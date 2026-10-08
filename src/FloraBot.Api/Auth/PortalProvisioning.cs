using System.Net.Mail;
using System.Text;
using Npgsql;

namespace FloraBot.Api.Auth;

public static class PortalProvisioning
{
    public static async Task<int> RunAsync(string[] args, IConfiguration config, TextReader input, TextWriter output)
    {
        if (args.Length != 3 || args[0] != "provision-user" || !Guid.TryParse(args[1], out var id)
            || !MailAddress.TryCreate(args[2], out var address) || address.Address != args[2] || args[2].Length > 254)
        {
            await output.WriteLineAsync("Usage: provision-user <user-uuid> <email>. Supply the password on standard input.");
            return 2;
        }
        var password = await input.ReadLineAsync();
        if (password is null || password.Length < 12 || Encoding.UTF8.GetByteCount(password) > 72)
        {
            await output.WriteLineAsync("Password must have at least 12 characters and at most 72 UTF-8 bytes.");
            return 2;
        }
        var connection = config["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(connection))
        {
            await output.WriteLineAsync("DATABASE_URL is required.");
            return 2;
        }
        try
        {
            await using var data = NpgsqlDataSource.Create(connection);
            await using var command = data.CreateCommand("SELECT flow.provision_portal_user(@id,@email,@hash,@demo)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("email", args[2].ToLowerInvariant());
            command.Parameters.AddWithValue("hash", BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12));
            command.Parameters.AddWithValue("demo", config["ASPNETCORE_ENVIRONMENT"] == "Development");
            await command.ExecuteNonQueryAsync();
            await output.WriteLineAsync($"Provisioned portal account {id}. Role and seller membership are unchanged.");
            return 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException)
        {
            await output.WriteLineAsync(ex.MessageText);
            return 1;
        }
        catch (NpgsqlException)
        {
            // Connection strings and PostgreSQL details can contain credentials or personal data.
            await output.WriteLineAsync("Provisioning failed. Check database connectivity and runtime migrations.");
            return 1;
        }
    }
}
