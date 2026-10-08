using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public static class FinancialEvidence
{
    public static async Task<bool> ExistsAsync(NpgsqlDataSource source, string purpose, Guid resourceId, CancellationToken ct)
    {
        var sql = purpose switch
        {
            "confirm_refund" => "SELECT EXISTS(SELECT 1 FROM payment.payments WHERE id=@id AND kind='REFUND')",
            "pay_withdrawal" => "SELECT EXISTS(SELECT 1 FROM payment.withdrawal_requests WHERE id=@id)",
            _ => null
        };
        if (sql is null) return false;
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("id", resourceId);
        return Equals(await command.ExecuteScalarAsync(ct), true);
    }
}
