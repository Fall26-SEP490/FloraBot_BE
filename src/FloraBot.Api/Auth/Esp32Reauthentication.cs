using System.Text;
using Npgsql;

namespace FloraBot.Api.Auth;

/// <summary>Identity-owned checks, using the caller's transaction until disclosure is audited.</summary>
public static class Esp32Reauthentication
{
    public static async Task<bool> ActiveActorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actor, string role, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE id=@actor AND role=@role AND status='ACTIVE' FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("role", role);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public static async Task<bool> VerifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actor, string role, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || Encoding.UTF8.GetByteCount(password) > 72) return false;
        await using var command = new NpgsqlCommand("SELECT password_hash FROM identity.users WHERE id=@actor AND role=@role AND status='ACTIVE' FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("role", role);
        var hash = await command.ExecuteScalarAsync(ct) as string;
        if (hash is null) return false;
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }
}
