using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.ApiService.Features.Admin;

/// <summary>
/// Surfaces the exact values a SuperAdmin needs to paste into third-party
/// dashboards so platform webhooks land cleanly. Returns the configured
/// public webhook URL and the Shiplogic auth-key value (the same string
/// configured at Shiplogic:WebhookSecret) so the SuperAdmin doesn't have to
/// dig through env vars or guess the path.
/// </summary>
[ApiController]
[Route("api/admin/integrations")]
[Authorize(Roles = "SuperAdmin")]
public class IntegrationsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public IntegrationsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("shiplogic-webhook")]
    public ActionResult<ShiplogicWebhookInfo> GetShiplogicWebhook()
    {
        var apiBase = _configuration["PublicUrls:ApiBaseUrl"]?.TrimEnd('/')
                      ?? "https://api2.delicatecourier.co.za";
        var secret = _configuration["Shiplogic:WebhookSecret"];
        var hasSecret = !string.IsNullOrEmpty(secret);

        return Ok(new ShiplogicWebhookInfo
        {
            DeliveryUrl = $"{apiBase}/api/webhooks/shiplogic/tracking",
            Topic = "Shipment tracking event",
            AuthKey = secret ?? string.Empty,
            AuthKeyConfigured = hasSecret,
            AuthHeaderName = "Authorization",
            AlternateHeaderName = "X-Webhook-Secret",
            Notes = hasSecret
                ? "Paste the Auth key value into Shiplogic's webhook subscription. The platform accepts it on either Authorization or X-Webhook-Secret header."
                : "Shiplogic:WebhookSecret is not configured. Until it is, the platform accepts any Shiplogic webhook unauthenticated. Set the Shiplogic__WebhookSecret env var, then paste the same value into Shiplogic."
        });
    }
}

public class ShiplogicWebhookInfo
{
    public string DeliveryUrl { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string AuthKey { get; set; } = string.Empty;
    public bool AuthKeyConfigured { get; set; }
    public string AuthHeaderName { get; set; } = string.Empty;
    public string AlternateHeaderName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
