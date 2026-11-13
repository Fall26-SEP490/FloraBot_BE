using System.Collections.Concurrent;
using System.Reflection;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace FloraBot.Api.Tests;

public class RedisProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handle = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handle(targetMethod!, args);
}

public sealed class MobileRefreshTests
{
    private static TokenService Service()
    {
        var values = new ConcurrentDictionary<string, RedisValue>();
        var db = DispatchProxy.Create<IDatabase, RedisProxy>();
        ((RedisProxy)(object)db).Handle = (method, args) => method.Name switch
        {
            "StringSetAsync" => Task.FromResult(values.TryAdd(args![0]!.ToString()!, (RedisValue)args[1]!)),
            "StringGetDeleteAsync" => Task.FromResult(values.TryRemove(args![0]!.ToString()!, out var value) ? value : RedisValue.Null),
            _ => throw new NotSupportedException(method.Name)
        };
        var redis = DispatchProxy.Create<IConnectionMultiplexer, RedisProxy>();
        ((RedisProxy)(object)redis).Handle = (method, _) => method.Name == "GetDatabase" ? db : throw new NotSupportedException(method.Name);
        return new TokenService(new ConfigurationBuilder().Build(), redis);
    }

    [Fact]
    public async Task WebAndMobileRefreshTokensCannotCrossRealms()
    {
        var service = Service();
        var user = new User { Id = Guid.NewGuid(), PasswordHash = "credential", Role = "CUSTOMER" };
        var mobile = await service.CreateMobileRefresh(user);
        var web = await service.CreateRefresh(user);
        Assert.Null(await service.ConsumeRefresh(mobile));
        Assert.Null(await service.ConsumeMobileRefresh(web));
        Assert.Equal(user.Id, (await service.ConsumeMobileRefresh(mobile))!.Value.Id);
        Assert.Equal(user.Id, (await service.ConsumeRefresh(web))!.Value.Id);
    }

    [Fact]
    public async Task MobileRefreshIsAtomicSingleUseAndBindsCredentialVersion()
    {
        var service = Service();
        var user = new User { Id = Guid.NewGuid(), PasswordHash = "old hash", Role = "SELLER_STAFF" };
        var token = await service.CreateMobileRefresh(user);
        var oldVersion = TokenService.CredentialVersion(user);
        user.PasswordHash = "new hash";
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ConsumeMobileRefresh(token)));
        var consumed = Assert.Single(attempts, x => x.HasValue);
        Assert.Equal(oldVersion, consumed!.Value.Version);
        Assert.NotEqual(TokenService.CredentialVersion(user), consumed.Value.Version);
    }
}
