using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

internal static class AnnualPackageFixture
{
    public static async Task EnsureAsync(ApiFactory factory)
    {
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = data.CreateCommand("""
            INSERT INTO identity.subscription_packages
                (id,name,monthly_fee,annual_fee,max_slots,walk_in_slots,preorder_slots,billing_cycle,status)
            VALUES('20000000-0000-0000-0000-00000000aa17','Annual integration test',0,4800000,3,2,1,'ANNUAL','ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        await command.ExecuteNonQueryAsync();
    }
}
