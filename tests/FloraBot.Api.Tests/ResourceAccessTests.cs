using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ResourceAccessTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly ClaimsPrincipal Seller = new(new ClaimsIdentity(
        [new Claim("role", "SELLER"), new Claim("seller_id", "20000000-0000-0000-0000-000000000001")], "test", "sub", "role"));

    [Theory]
    [InlineData("30000000-0000-0000-0000-000000000003")]
    [InlineData("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")]
    public async Task MissingAndForeignProductsRemainIndistinguishable(string product)
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await ResourceAccess.CheckAsync(connection, transaction, Seller, "update_product_price",
            new Dictionary<string, JsonElement> { ["p_product"] = JsonSerializer.SerializeToElement(product) }, CancellationToken.None);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task OwnershipLockLivesUntilTheCallingTransactionEnds()
    {
        const string product = "30000000-0000-0000-0000-000000000001";
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await ResourceAccess.CheckAsync(connection, transaction, Seller, "update_product_price",
            new Dictionary<string, JsonElement> { ["p_product"] = JsonSerializer.SerializeToElement(product) }, CancellationToken.None);
        Assert.Null(result);

        await using var contender = await source.OpenConnectionAsync();
        await using (var competingTransaction = await contender.BeginTransactionAsync())
        {
            await using var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '100ms'", contender, competingTransaction);
            await timeout.ExecuteNonQueryAsync();
            await using var lockedRead = new NpgsqlCommand("SELECT id FROM catalog.flower_products WHERE id=@id FOR UPDATE", contender, competingTransaction);
            lockedRead.Parameters.AddWithValue("id", Guid.Parse(product));
            var error = await Assert.ThrowsAsync<PostgresException>(() => lockedRead.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            await competingTransaction.RollbackAsync();
        }

        await transaction.RollbackAsync();
        await using var after = new NpgsqlCommand("SELECT id FROM catalog.flower_products WHERE id=@id FOR UPDATE NOWAIT", contender);
        after.Parameters.AddWithValue("id", Guid.Parse(product));
        Assert.Equal(Guid.Parse(product), await after.ExecuteScalarAsync());
    }
}
