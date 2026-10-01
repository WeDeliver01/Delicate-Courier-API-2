using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.Notifications;

/// <summary>
/// Sends operational notification emails to the platform admin.
/// Provider is chosen from environment configuration at send time:
///   * RESEND_API_KEY set          → Resend HTTP API (https://resend.com)
///   * SMTP_HOST set               → classic SMTP (SMTP_PORT, SMTP_USER,
///                                   SMTP_PASS, SMTP_FROM)
///   * neither                     → logs an error and returns false so the
///                                   caller can surface "email not configured"
///                                   instead of failing silently.
/// Recipient defaults to ADMIN_NOTIFY_EMAIL, falling back to
/// admin@delicatecourier.co.za.
/// </summary>
public interface IAdminEmailSender
{
    /// <returns>true when a provider accepted the message.</returns>
    Task<bool> SendAsync(string subject, string htmlBody, CancellationToken ct = default);

    /// <summary>True when at least one provider is configured.</summary>
    bool IsConfigured { get; }

    string RecipientAddress { get; }
}

public class AdminEmailSender : IAdminEmailSender
{
    public const string DefaultRecipient = "admin@delicatecourier.co.za";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AdminEmailSender> _logger;

    public AdminEmailSender(IHttpClientFactory httpClientFactory, ILogger<AdminEmailSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string RecipientAddress =>
        Environment.GetEnvironmentVariable("ADMIN_NOTIFY_EMAIL") is { Length: > 0 } r ? r : DefaultRecipient;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RESEND_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMTP_HOST"));

    public async Task<bool> SendAsync(string subject, string htmlBody, CancellationToken ct = default)
    {
        var resendKey = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        if (!string.IsNullOrWhiteSpace(resendKey))
        {
            return await SendViaResendAsync(resendKey, subject, htmlBody, ct);
        }

        var smtpHost = Environment.GetEnvironmentVariable("SMTP_HOST");
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            return await SendViaSmtpAsync(smtpHost, subject, htmlBody, ct);
        }

        _logger.LogError(
            "Admin email NOT sent (subject: {Subject}) — no email provider configured. " +
            "Set RESEND_API_KEY or SMTP_HOST/SMTP_PORT/SMTP_USER/SMTP_PASS/SMTP_FROM.",
            subject);
        return false;
    }

    private async Task<bool> SendViaResendAsync(string apiKey, string subject, string htmlBody, CancellationToken ct)
    {
        try
        {
            var from = Environment.GetEnvironmentVariable("RESEND_FROM") is { Length: > 0 } f
                ? f
                : "Delicate Couriers Platform <onboarding@resend.dev>";

            var client = _httpClientFactory.CreateClient(nameof(AdminEmailSender));
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                from,
                to = new[] { RecipientAddress },
                subject,
                html = htmlBody,
            }), Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Admin email sent via Resend. Subject: {Subject}", subject);
                return true;
            }

            _logger.LogError("Resend rejected admin email ({Status}): {Body}", (int)resp.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending admin email via Resend. Subject: {Subject}", subject);
            return false;
        }
    }

    private async Task<bool> SendViaSmtpAsync(string host, string subject, string htmlBody, CancellationToken ct)
    {
        try
        {
            var port = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;
            var user = Environment.GetEnvironmentVariable("SMTP_USER");
            var pass = Environment.GetEnvironmentVariable("SMTP_PASS");
            var from = Environment.GetEnvironmentVariable("SMTP_FROM") is { Length: > 0 } f ? f : user;

            if (string.IsNullOrWhiteSpace(from))
            {
                _logger.LogError("SMTP_FROM/SMTP_USER not set — cannot send admin email.");
                return false;
            }

            using var smtp = new SmtpClient(host, port)
            {
                EnableSsl = true,
                Credentials = string.IsNullOrEmpty(user) ? null : new NetworkCredential(user, pass),
            };
            using var msg = new MailMessage(from, RecipientAddress, subject, htmlBody) { IsBodyHtml = true };
            await smtp.SendMailAsync(msg, ct);
            _logger.LogInformation("Admin email sent via SMTP ({Host}). Subject: {Subject}", host, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending admin email via SMTP. Subject: {Subject}", subject);
            return false;
        }
    }
}
