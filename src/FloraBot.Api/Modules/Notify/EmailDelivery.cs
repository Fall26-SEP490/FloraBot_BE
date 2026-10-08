using System.Security.Cryptography;
using System.Text;
using Npgsql;
using StackExchange.Redis;

namespace FloraBot.Api.Modules.Notify;

// Best-effort duplicate suppression, not exactly-once delivery across provider/Valkey failures.
public sealed class EmailDelivery(IConnectionMultiplexer redis, NpgsqlDataSource data, TransactionalEmailSender sender)
{
    private const string Release = "if redis.call('get',KEYS[1])==ARGV[1] then return redis.call('del',KEYS[1]) else return 0 end";

    public string Key(string deliveryId)
    {
        var database = new NpgsqlConnectionStringBuilder(data.ConnectionString);
        var scope = $"{database.Host}:{database.Port}/{database.Database}/{deliveryId}";
        return "florabot:email:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    public async Task SendOnceAsync(string deliveryId, TransactionalEmail message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cache = redis.GetDatabase();
        var key = Key(deliveryId);
        if (await cache.KeyExistsAsync(key + ":accepted")) return;
        var owner = Guid.NewGuid().ToString("N");
        if (!await cache.StringSetAsync(key + ":lock", owner, TimeSpan.FromMinutes(1), When.NotExists))
            throw new InvalidOperationException("Email delivery is already being processed; retry later.");
        try
        {
            if (await cache.KeyExistsAsync(key + ":accepted")) return;
            await sender.SendAsync(message, ct);
            await cache.StringSetAsync(key + ":accepted", "1", TimeSpan.FromDays(90));
        }
        finally
        {
            await cache.ScriptEvaluateAsync(Release, [new RedisKey(key + ":lock")], [new RedisValue(owner)]);
        }
    }
}
