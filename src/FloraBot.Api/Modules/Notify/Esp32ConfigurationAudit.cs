using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.Notify;

public static class Esp32ConfigurationAudit
{
    // Only bounded operation/result codes and identifiers are accepted. Never serialize a request,
    // exception, configuration object, password, signing key, or generated file into audit payloads.
    public static async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actor, Guid? kiosk, Guid? installation, bool download, string result, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT flow.audit(@actor,@action,'kiosks',@kiosk,
              jsonb_build_object('result',@result::text,'installationId',@installation::uuid))
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("action", download
            ? result == "SUCCESS" ? "ESP32_CONFIG_DOWNLOADED" : "ESP32_CONFIG_DOWNLOAD_DENIED"
            : result == "SUCCESS" ? "ESP32_CONFIG_VIEWED" : "ESP32_CONFIG_VIEW_DENIED");
        command.Parameters.Add("kiosk", NpgsqlDbType.Uuid).Value = (object?)kiosk ?? DBNull.Value;
        command.Parameters.Add("installation", NpgsqlDbType.Uuid).Value = (object?)installation ?? DBNull.Value;
        command.Parameters.AddWithValue("result", result);
        await command.ExecuteNonQueryAsync(ct);
    }
}
