using DotNetCore.CAP;
using Npgsql;

namespace FloraBot.Api.Modules.Notify;

// Recovery covers broker bindings that were absent during first startup and exhausted CAP retries.
public sealed class CommunicationRecovery(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<CommunicationRecovery> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("COMMUNICATION_BROKER_ENABLED", false)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RecoverAsync(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>(),
                    scope.ServiceProvider.GetRequiredService<ICapPublisher>(), config, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Communication queue recovery postponed; storage or broker unavailable."); }
        }
    }

    public static async Task RecoverAsync(NpgsqlDataSource data, ICapPublisher publisher,
        IConfiguration config, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        await using var query = new NpgsqlCommand("""
            UPDATE notify.communication_deliveries SET updated_at=now() WHERE delivery_id IN (
              SELECT delivery_id FROM notify.communication_deliveries
              WHERE state='QUEUED' AND updated_at<now()-interval '2 minutes'
              ORDER BY updated_at LIMIT 100 FOR UPDATE SKIP LOCKED
            ) RETURNING delivery_id
            """, connection, transaction);
        var ids = new List<string>();
        await using (var rows = await query.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct)) ids.Add(rows.GetString(0));
        foreach (var id in ids)
            await publisher.PublishAsync(CommunicationEvents.Requested, new QueuedEmailDelivery(id,
                CommunicationEvents.SignRequest(id, config["COMMUNICATION_SERVICE_TOKEN"]!)), cancellationToken: ct);
        await outbox.CommitAsync(ct);
    }
}
