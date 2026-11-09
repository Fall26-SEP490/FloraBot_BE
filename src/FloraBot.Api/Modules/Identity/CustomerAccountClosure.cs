using System.Security.Claims;
using System.Text;
using FloraBot.Api.Auth;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record CloseCustomerAccountRequest(string Password);

public static class CustomerAccountClosure
{
    public static void MapCustomerAccountClosure(this WebApplication app)
    {
        app.MapPost("/api/account/close", async (CloseCustomerAccountRequest input,
            NpgsqlDataSource data, TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            if (string.IsNullOrEmpty(input.Password) || Encoding.UTF8.GetByteCount(input.Password) > 72)
                return Results.Problem(statusCode: 400, detail: "Cần xác nhận mật khẩu hiện tại.");

            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string? storedHash;
            await using (var user = new NpgsqlCommand("""
                SELECT password_hash FROM identity.users
                WHERE id=@id AND role='CUSTOMER' AND status='ACTIVE' FOR UPDATE
                """, conn, tx))
            {
                user.Parameters.AddWithValue("id", actor);
                var value = await user.ExecuteScalarAsync(ct);
                if (value is null) return Results.Forbid();
                storedHash = value as string;
            }
            if (storedHash is null)
                return Results.Problem(statusCode: 409, detail: "Tài khoản chưa có mật khẩu. Hãy thiết lập mật khẩu trước khi đóng tài khoản.");
            var valid = false;
            try { valid = BCrypt.Net.BCrypt.Verify(input.Password, storedHash); }
            catch (BCrypt.Net.SaltParseException) { }
            if (!valid) return Results.Problem(statusCode: 400, detail: "Mật khẩu hiện tại chưa đúng.");

            // Retain user ID for historical orders/payments, while removing login and profile PII.
            await using (var update = new NpgsqlCommand("""
                UPDATE identity.users SET email='deleted-' || id || '@florabot.invalid',
                  phone=NULL,password_hash=NULL,full_name='Khách đã đóng tài khoản',
                  status='DISABLED',loyalty_points=0,updated_at=public.app_now()
                WHERE id=@id
                """, conn, tx))
            {
                update.Parameters.AddWithValue("id", actor);
                await update.ExecuteNonQueryAsync(ct);
            }
            await using (var removeProfile = new NpgsqlCommand(
                "DELETE FROM identity.user_profiles WHERE user_id=@id", conn, tx))
            {
                removeProfile.Parameters.AddWithValue("id", actor);
                await removeProfile.ExecuteNonQueryAsync(ct);
            }
            await using (var unlink = new NpgsqlCommand(
                "DELETE FROM identity.external_identities WHERE user_id=@id", conn, tx))
            {
                unlink.Parameters.AddWithValue("id", actor);
                await unlink.ExecuteNonQueryAsync(ct);
            }
            await using (var audit = new NpgsqlCommand(
                "SELECT flow.audit(@id,'CUSTOMER_ACCOUNT_CLOSED','users',@id)", conn, tx))
            {
                audit.Parameters.AddWithValue("id", actor);
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            // The account is already disabled. Redis may be unavailable, but the
            // refresh token can no longer authenticate because the user is inactive.
            if (http.Request.Cookies.TryGetValue("florabot_refresh", out var refresh))
            {
                try { await tokens.ConsumeRefresh(refresh); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    http.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(CustomerAccountClosure))
                        .LogWarning(ex, "Could not remove refresh token after customer account closure {UserId}", actor);
                }
            }
            var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
            var cookie = new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps || !(environment.IsDevelopment() || environment.IsEnvironment("Testing")),
                SameSite = SameSiteMode.Strict,
                IsEssential = true
            };
            cookie.Path = "/";
            http.Response.Cookies.Delete("florabot_access", cookie);
            cookie.Path = "/api/auth";
            http.Response.Cookies.Delete("florabot_refresh", cookie);
            return Results.NoContent();
        }).RequireAuthorization("PreorderCustomer").RequireRateLimiting("auth")
          .WithTags("Account").Produces(204).ProducesProblem(400)
          .ProducesProblem(409);
    }
}
