using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using StackExchange.Redis;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;

namespace FloraBot.Api.Jobs;

public sealed record JobResult(bool Executed, int Affected);

public sealed class JobRunner(NpgsqlDataSource data, IConnectionMultiplexer redis, ILogger<JobRunner> logger, IServiceScopeFactory scopes)
{
    public static readonly IReadOnlyList<string> Names = Array.AsReadOnly(new[]
    {
        "mark_offline_kiosks", "expire_pending_subscriptions", "roll_subscriptions",
        "expire_pending_payments", "expire_pickups", "expire_tokens", "expire_bouquets",
        "settle_due_orders", "reconcile_daily", "remind_subscriptions", "expire_web_orders"
    });
    private const string ReleaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
    public static string AdvisoryKey(string name) => $"florabot:jobs:{name}";

    public string LockKey(string name)
    {
        var connection = new NpgsqlConnectionStringBuilder(data.ConnectionString);
        var identity = $"{connection.Host}:{connection.Port}/{connection.Database}";
        return $"florabot:jobs:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16]}:{name}";
    }

    public async Task<JobResult> RunAsync(string name, CancellationToken ct)
    {
        if (!Names.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        ct.ThrowIfCancellationRequested();
        var key = LockKey(name);
        var owner = Guid.NewGuid().ToString("N");
        var cache = redis.GetDatabase();
        if (!await cache.StringSetAsync(key, owner, TimeSpan.FromMinutes(2), When.NotExists)) return new(false, 0);
        try
        {
            await using var connection = await data.OpenConnectionAsync(ct);
            using var scope = scopes.CreateScope();
            if (name == "remind_subscriptions" && scope.ServiceProvider.GetRequiredService<EmailSettings>().Provider == EmailProvider.Disabled)
                return new(false, 0);
            var publisher = scope.ServiceProvider.GetRequiredService<ICapPublisher>();
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            await using var setup = new NpgsqlCommand("SET LOCAL TIME ZONE 'Asia/Ho_Chi_Minh'; SET LOCAL statement_timeout='20s'; SELECT flow.reset_clock();", connection, transaction);
            await setup.ExecuteNonQueryAsync(ct);
            // The transaction lock prevents overlap even if a Valkey lease expires or Valkey restarts.
            await using var guard = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
            guard.Parameters.AddWithValue("key", AdvisoryKey(name));
            if (await guard.ExecuteScalarAsync(ct) is not true) return new(false, 0);

            var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
            var result = name == "remind_subscriptions"
                ? new JobResult(true, await Modules.Identity.SubscriptionReminders.EnqueueAsync(connection, transaction, publisher, ct))
                : name == "reconcile_daily"
                ? await ReconcileAsync(connection, transaction, ct)
                : await ExecuteFlowAsync(name, connection, transaction, ct);
            await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
            await outbox.CommitAsync(ct);
            return result;
        }
        finally
        {
            try { await cache.ScriptEvaluateAsync(ReleaseScript, [new RedisKey(key)], [new RedisValue(owner)]); }
            catch (RedisException ex) { logger.LogWarning(ex, "Could not release job lease {Job}; it will expire", name); }
        }
    }

    private static async Task<JobResult> ExecuteFlowAsync(string name, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        // name comes only from the allowlist; all clock and tenant arguments use SQL defaults.
        await using var command = new NpgsqlCommand($"SELECT flow.{name}()", connection, transaction) { CommandTimeout = 25 };
        return new(true, Convert.ToInt32(await command.ExecuteScalarAsync(ct)));
    }

    private async Task<JobResult> ReconcileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (await ReconciliationAudit.HasDailyJobAsync(connection, transaction, ct)) return new(false, 0);
        await using var query = new NpgsqlCommand("SELECT jsonb_build_object('date',CURRENT_DATE-1,'rows',coalesce(jsonb_agg(to_jsonb(r)),'[]'::jsonb)) FROM flow.reconcile_daily(CURRENT_DATE-1) r", connection, transaction) { CommandTimeout = 25 };
        var payload = (string)(await query.ExecuteScalarAsync(ct))!;
        using var report = JsonDocument.Parse(payload);
        var rows = report.RootElement.GetProperty("rows");
        if (rows.EnumerateArray().Any(row => row.GetProperty("lech").GetInt64() != 0))
            logger.LogWarning("Daily ledger reconciliation found differences for {Date}", report.RootElement.GetProperty("date").GetString());
        await ReconciliationAudit.RecordDailyJobAsync(connection, transaction, payload, ct);
        return new(true, rows.GetArrayLength());
    }
}
