using FloraBot.Api.Auth;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record Esp32Kiosk(Guid Id, string Code, string Status, string HardwareId, string MqttClientId);
public sealed record Esp32KioskAccess(Esp32Kiosk? Kiosk, Guid? AuditKioskId, bool ActorActive = true);

public interface IEsp32ConfigurationSession : IAsyncDisposable
{
    Task<Esp32KioskAccess> ResolveAsync(Guid resource, Guid actor, string role, CancellationToken ct);
    Task<bool> ReauthenticateAsync(Guid actor, string role, string? password, CancellationToken ct);
    Task AuditAndCommitAsync(Guid actor, Guid? kiosk, Guid? installation, bool download, string result, CancellationToken ct);
}

public interface IEsp32ConfigurationSessions
{
    Task<IEsp32ConfigurationSession> OpenAsync(CancellationToken ct);
}

public sealed class Esp32ConfigurationSessions(NpgsqlDataSource data) : IEsp32ConfigurationSessions
{
    public async Task<IEsp32ConfigurationSession> OpenAsync(CancellationToken ct)
    {
        var connection = await data.OpenConnectionAsync(ct);
        try { return new Session(connection, await connection.BeginTransactionAsync(ct)); }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction) : IEsp32ConfigurationSession
    {
        public Task<bool> ReauthenticateAsync(Guid actor, string role, string? password, CancellationToken ct) =>
            Esp32Reauthentication.VerifyAsync(connection, transaction, actor, role, password, ct);

        public async Task<Esp32KioskAccess> ResolveAsync(Guid resource, Guid actor, string role, CancellationToken ct)
        {
            var kioskId = resource;
            if (role == "TECHNICIAN")
            {
                // A completed handover is readable history, but no longer a live provisioning assignment.
                // Lock the installation before the kiosk, matching installation transition lock order.
                await using var application = new NpgsqlCommand("""
                    SELECT assigned_kiosk_id,assigned_technician_id,status
                    FROM kiosk_ops.kiosk_applications WHERE id=@id FOR SHARE
                    """, connection, transaction);
                application.Parameters.AddWithValue("id", resource);
                await using var reader = await application.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return new(null, null);
                Guid? assigned = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                var allowed = assigned.HasValue && !reader.IsDBNull(1) && reader.GetGuid(1) == actor
                    && reader.GetString(2) is "SCHEDULED" or "INSTALLING" or "AWAITING_HANDOVER";
                if (!allowed) return new(null, assigned);
                kioskId = assigned!.Value;
            }

            // Scheduling takes application -> technician user -> kiosk locks. Locking the actor
            // before the installation, or after the kiosk, can deadlock a concurrent reschedule.
            // Keep this check between installation resolution and the kiosk lock, and retain it
            // through the audit. Manager/Admin requests have no installation lock to acquire.
            if (!await Esp32Reauthentication.ActiveActorAsync(connection, transaction, actor, role, ct))
                return new(null, kioskId, false);

            Esp32Kiosk? kiosk;
            await using (var command = new NpgsqlCommand("""
                SELECT id,code,status,hardware_id,mqtt_client_id FROM kiosk_ops.kiosks
                WHERE id=@id FOR SHARE
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("id", kioskId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                kiosk = await reader.ReadAsync(ct)
                    ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
                    : null;
            }
            if (kiosk is null) return new(null, kioskId);
            if (role == "OPERATIONS_MANAGER")
            {
                // Manager reassignment locks the kiosk first. Hold both rows through audit/disclosure.
                await using var assignment = new NpgsqlCommand("""
                    SELECT 1 FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@id AND manager_id=@actor FOR SHARE
                    """, connection, transaction);
                assignment.Parameters.AddWithValue("id", kioskId);
                assignment.Parameters.AddWithValue("actor", actor);
                if (await assignment.ExecuteScalarAsync(ct) is null) return new(null, kioskId);
            }
            return new(kiosk, kioskId);
        }

        public async Task AuditAndCommitAsync(Guid actor, Guid? kiosk, Guid? installation, bool download, string result, CancellationToken ct)
        {
            await Esp32ConfigurationAudit.RecordAsync(connection, transaction, actor, kiosk, installation, download, result, ct);
            await transaction.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
