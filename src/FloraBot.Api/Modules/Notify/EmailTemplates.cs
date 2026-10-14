using System.Text;

namespace FloraBot.Api.Modules.Notify;

public static class EmailTemplates
{
    public static string Encode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    public static string WrapLayout(string title, string contentHtml, string? previewText = null)
    {
        var encodedTitle = Encode(title);
        var encodedPreview = Encode(previewText ?? title);

        return $"""
            <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">
            <html xmlns="http://www.w3.org/1999/xhtml" lang="vi">
            <head>
              <meta http-equiv="Content-Type" content="text/html; charset=UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="color-scheme" content="light" />
              <meta name="supported-color-schemes" content="light" />
              <title>{encodedTitle}</title>
            </head>
            <body style="margin: 0; padding: 0; background-color: #F6F8F5; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; -webkit-font-smoothing: antialiased; word-break: break-word;">
              <div style="display: none; font-size: 1px; color: #F6F8F5; line-height: 1px; max-height: 0px; max-width: 0px; opacity: 0; overflow: hidden; mso-hide: all;">
                {encodedPreview}
              </div>
              <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="background-color: #F6F8F5; table-layout: fixed;">
                <tr>
                  <td align="center" style="padding: 24px 16px;">
                    <!--[if (gte mso 9)|(IE)]>
                    <table align="center" border="0" cellspacing="0" cellpadding="0" width="600">
                    <tr>
                    <td align="center" valign="top" width="600">
                    <![endif]-->
                    <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="max-width: 600px; margin: 0 auto; text-align: left;">
                      <!-- Header -->
                      <tr>
                        <td style="background-color: #1B4332; padding: 24px 32px; border-radius: 12px 12px 0 0; text-align: center;">
                          <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%">
                            <tr>
                              <td align="center">
                                <span style="display: inline-block; font-size: 24px; font-weight: 700; color: #FFFFFF; letter-spacing: 0.5px;">🌿 FloraBot</span>
                                <div style="font-size: 13px; color: #D8F3DC; margin-top: 4px; font-weight: 400; letter-spacing: 0.2px;">Trao hoa, gửi yêu thương</div>
                              </td>
                            </tr>
                          </table>
                        </td>
                      </tr>
                      <!-- Body Card -->
                      <tr>
                        <td style="background-color: #FFFFFF; padding: 24px; border-left: 1px solid #E5E7EB; border-right: 1px solid #E5E7EB; border-bottom: 1px solid #E5E7EB; border-radius: 0 0 12px 12px; box-shadow: 0 1px 3px rgba(0, 0, 0, 0.05);">
                          {contentHtml}
                        </td>
                      </tr>
                      <!-- Footer -->
                      <tr>
                        <td style="padding: 24px 16px; text-align: center; font-size: 12px; line-height: 1.6; color: #6B7280;">
                          <div style="font-size: 14px; margin-bottom: 8px;">🌿 ❀ 🌿</div>
                          <p style="margin: 0 0 4px 0; font-weight: 600; color: #4B5563;">FloraBot — Hoa giả, quà tặng và kiosk thông minh</p>
                          <p style="margin: 0; color: #6B7280;">Email tự động, vui lòng không trả lời thư này.</p>
                        </td>
                      </tr>
                    </table>
                    <!--[if (gte mso 9)|(IE)]>
                    </td>
                    </tr>
                    </table>
                    <![endif]-->
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    public static string CreateDefaultHtml(string subject, string text)
    {
        var encodedSubject = Encode(subject);
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = normalized.Split(["\n\n"], StringSplitOptions.None);

        var sb = new StringBuilder();
        sb.Append($"<h2 style=\"margin: 0 0 20px 0; font-size: 18px; font-weight: 700; color: #1B4332; line-height: 1.4;\">{encodedSubject}</h2>");

        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph)) continue;
            var lines = paragraph.Split('\n');
            var encodedLines = lines.Select(Encode);
            var content = string.Join("<br/>", encodedLines);
            sb.Append($"<p style=\"margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #374151;\">{content}</p>");
        }

        return WrapLayout(subject, sb.ToString(), subject);
    }

    public static string RegistrationOtp(string code, string? fullName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var trimmedCode = code.Trim();
        var encodedCode = Encode(trimmedCode);
        var encodedName = string.IsNullOrWhiteSpace(fullName) ? null : Encode(fullName.Trim());

        var greeting = encodedName is not null
            ? $"Xin chào <strong>{encodedName}</strong>,"
            : "Xin chào bạn,";

        var bodyHtml = $"""
            <h2 style="margin: 0 0 20px 0; font-size: 20px; font-weight: 700; color: #1B4332; line-height: 1.4;">Xác thực tài khoản FloraBot</h2>
            <p style="margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #374151;">{greeting}</p>
            <p style="margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #374151;">Cảm ơn bạn đã đăng ký tài khoản tại <strong>FloraBot</strong>. Dưới đây là mã xác thực của bạn:</p>

            <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="margin: 24px 0;">
              <tr>
                <td align="center">
                  <div style="background-color: #F0FDF4; border: 2px dashed #2D6A4F; border-radius: 8px; padding: 18px 16px; display: inline-block;">
                    <span style="font-family: 'Courier New', Courier, Consolas, monospace; font-size: 32px; font-weight: 700; letter-spacing: 8px; color: #1B4332; line-height: 1.2; white-space: nowrap;">{encodedCode}</span>
                  </div>
                </td>
              </tr>
            </table>

            <div style="background-color: #FEF9C3; border-left: 4px solid #CA8A04; padding: 12px 16px; margin: 20px 0; border-radius: 0 6px 6px 0; font-size: 14px; color: #713F12; line-height: 1.5;">
              <strong>Lưu ý:</strong> Mã có hiệu lực trong 10 phút. Không chia sẻ mã này với bất kỳ ai.
            </div>

            <p style="margin: 20px 0 0 0; font-size: 14px; color: #6B7280; line-height: 1.5;">Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email.</p>
            """;

        return WrapLayout("Mã xác thực đăng ký tài khoản FloraBot", bodyHtml, $"Mã xác thực FloraBot của bạn là: {trimmedCode}");
    }

    public static string PasswordReset(string resetUrl, string? fullName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resetUrl);
        if (!Uri.TryCreate(resetUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Password reset link must be a valid HTTP or HTTPS URL.", nameof(resetUrl));

        var encodedUrl = Encode(resetUrl);
        var encodedName = string.IsNullOrWhiteSpace(fullName) ? null : Encode(fullName.Trim());

        var greeting = encodedName is not null
            ? $"Xin chào <strong>{encodedName}</strong>,"
            : "Xin chào bạn,";

        var bodyHtml = $"""
            <h2 style="margin: 0 0 20px 0; font-size: 20px; font-weight: 700; color: #1B4332; line-height: 1.4;">Đặt lại mật khẩu FloraBot</h2>
            <p style="margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #374151;">{greeting}</p>
            <p style="margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #374151;">Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn. Nhấn vào nút bên dưới để tiến hành tạo mật khẩu mới:</p>

            <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="margin: 28px 0;">
              <tr>
                <td align="center">
                  <a href="{encodedUrl}" target="_blank" style="background-color: #2D6A4F; color: #FFFFFF; font-size: 16px; font-weight: 600; text-decoration: none; padding: 14px 32px; border-radius: 6px; display: inline-block; text-align: center; letter-spacing: 0.3px;">Đặt lại mật khẩu</a>
                </td>
              </tr>
            </table>

            <div style="background-color: #FEF9C3; border-left: 4px solid #CA8A04; padding: 12px 16px; margin: 20px 0; border-radius: 0 6px 6px 0; font-size: 14px; color: #713F12; line-height: 1.5;">
              Mở liên kết để đặt lại mật khẩu trong 20 phút. Nếu bạn không yêu cầu, hãy bỏ qua email này.
            </div>

            <div style="margin-top: 20px; font-size: 13px; color: #6B7280; line-height: 1.5;">
              Nếu nút trên không hoạt động, bạn có thể sao chép liên kết sau vào trình duyệt:<br/>
              <a href="{encodedUrl}" style="color: #2D6A4F; word-break: break-all; text-decoration: underline;">{encodedUrl}</a>
            </div>
            """;

        return WrapLayout("Đặt lại mật khẩu FloraBot", bodyHtml, "Mở liên kết để đặt lại mật khẩu trong 20 phút");
    }

    public static string ContactEmailOtp(string code, string? fullName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var trimmedCode = code.Trim();
        var encodedCode = Encode(trimmedCode);
        var encodedName = string.IsNullOrWhiteSpace(fullName) ? null : Encode(fullName.Trim());

        var greeting = encodedName is not null
            ? $"Xin chào <strong>{encodedName}</strong>,"
            : "Xin chào bạn,";

        var bodyHtml = $"""
            <h2 style="margin: 0 0 20px 0; font-size: 20px; font-weight: 700; color: #1B4332; line-height: 1.4;">Xác thực email liên hệ FloraBot</h2>
            <p style="margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #374151;">{greeting}</p>
            <p style="margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #374151;">Chúng tôi nhận được yêu cầu cập nhật email liên hệ cho tài khoản của bạn tại <strong>FloraBot</strong>. Dưới đây là mã xác thực của bạn:</p>

            <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="margin: 24px 0;">
              <tr>
                <td align="center">
                  <div style="background-color: #F0FDF4; border: 2px dashed #2D6A4F; border-radius: 8px; padding: 18px 16px; display: inline-block;">
                    <span style="font-family: 'Courier New', Courier, Consolas, monospace; font-size: 32px; font-weight: 700; letter-spacing: 8px; color: #1B4332; line-height: 1.2; white-space: nowrap;">{encodedCode}</span>
                  </div>
                </td>
              </tr>
            </table>

            <div style="background-color: #FEF9C3; border-left: 4px solid #CA8A04; padding: 12px 16px; margin: 20px 0; border-radius: 0 6px 6px 0; font-size: 14px; color: #713F12; line-height: 1.5;">
              <strong>Lưu ý:</strong> Mã có hiệu lực trong 10 phút. Không chia sẻ mã này với bất kỳ ai.
            </div>

            <p style="margin: 20px 0 0 0; font-size: 14px; color: #6B7280; line-height: 1.5;">Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email.</p>
            """;

        return WrapLayout("Xác thực email liên hệ FloraBot", bodyHtml, $"Mã xác thực email liên hệ FloraBot của bạn là: {trimmedCode}");
    }
}
