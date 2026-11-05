using Npgsql;

namespace FloraBot.Api.Tests;

public static class CurrentMainKioskFixture
{
    public const string AdminUserId = "10000000-0000-0000-0000-000000000001";
    public const string DefaultPackageId = "20000000-0000-0000-0000-00000000000a";

    /// <summary>
    /// Seeds a coherent paid subscription charge in payment.payments and an active annual lease in
    /// identity.subscriptions, then binds the whole kiosk to the single seller via flow.assign_kiosk.
    /// Expects parameters: @seller (uuid), @kiosk (uuid).
    /// </summary>
    public const string AssignKioskWithLeaseSql = """
        WITH inserted_lease AS (
            INSERT INTO identity.subscriptions (
                id, seller_id, kiosk_id, package_id, status, period_from, period_to,
                period_from_ts, period_to_ts, price, payment_id
            ) VALUES (
                public.uuid_v7(), @seller, @kiosk, '20000000-0000-0000-0000-00000000000a', 'ACTIVE',
                CURRENT_DATE, CURRENT_DATE + 365,
                public.app_now(), public.app_now() + interval '365 days', 600000, public.uuid_v7()
            ) RETURNING id, payment_id, seller_id
        )
        INSERT INTO payment.payments (
            id, kind, purpose, subscription_id, seller_id, gateway, gateway_txn_id,
            idempotency_key, amount, status, paid_at
        )
        SELECT
            payment_id, 'CHARGE', 'SUBSCRIPTION', id, seller_id, 'PAYOS', 'sub-txn-' || id::text,
            'sub-pay-' || id::text, 600000, 'SUCCEEDED', public.app_now()
        FROM inserted_lease;
        SELECT flow.assign_kiosk(@kiosk, @seller, '10000000-0000-0000-0000-000000000001');
        """;

    public static async Task AssignKioskWithLeaseAsync(
        NpgsqlDataSource data,
        Guid kioskId,
        Guid sellerId,
        Guid? packageId = null,
        Guid? adminId = null,
        long price = 600000)
    {
        var pkg = packageId ?? Guid.Parse(DefaultPackageId);
        var admin = adminId ?? Guid.Parse(AdminUserId);
        await using var cmd = data.CreateCommand("""
            WITH inserted_lease AS (
                INSERT INTO identity.subscriptions (
                    id, seller_id, kiosk_id, package_id, status, period_from, period_to,
                    period_from_ts, period_to_ts, price, payment_id
                ) VALUES (
                    public.uuid_v7(), @seller, @kiosk, @package, 'ACTIVE',
                    CURRENT_DATE, CURRENT_DATE + 365,
                    public.app_now(), public.app_now() + interval '365 days', @price, public.uuid_v7()
                ) RETURNING id, payment_id, seller_id
            )
            INSERT INTO payment.payments (
                id, kind, purpose, subscription_id, seller_id, gateway, gateway_txn_id,
                idempotency_key, amount, status, paid_at
            )
            SELECT
                payment_id, 'CHARGE', 'SUBSCRIPTION', id, seller_id, 'PAYOS', 'sub-txn-' || id::text,
                'sub-pay-' || id::text, @price, 'SUCCEEDED', public.app_now()
            FROM inserted_lease;
            SELECT flow.assign_kiosk(@kiosk, @seller, @admin);
            """);
        cmd.Parameters.AddWithValue("seller", sellerId);
        cmd.Parameters.AddWithValue("kiosk", kioskId);
        cmd.Parameters.AddWithValue("package", pkg);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("admin", admin);
        await cmd.ExecuteNonQueryAsync();
    }
}
