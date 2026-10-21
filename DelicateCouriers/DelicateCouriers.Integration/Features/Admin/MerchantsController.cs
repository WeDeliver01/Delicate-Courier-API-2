using System.Security.Cryptography;
using System.Text.Json;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Integration.Features.Admin;

/// <summary>
/// Managing merchants without a deploy.
///
/// Everything here used to be a developer editing a settings file. The point of moving it is
/// that whoever runs the business can onboard a bakery, correct an address or switch a
/// merchant off, and that each of those leaves a record saying when it happened.
/// </summary>
[ApiController]
[Route("api/admin/merchants")]
[RequiresAdminKey]
public class MerchantsController : ControllerBase
{
    private readonly MerchantDbContext _db;
    private readonly DbMerchantDirectory _directory;
    private readonly ILogger<MerchantsController> _logger;

    public MerchantsController(
        MerchantDbContext db,
        DbMerchantDirectory directory,
        ILogger<MerchantsController> logger)
    {
        _db = db;
        _directory = directory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<MerchantView>>> List(CancellationToken cancellationToken)
    {
        var rows = await _db.Merchants.AsNoTracking()
            .OrderBy(m => m.StoreId)
            .ToListAsync(cancellationToken);
        return Ok(rows.Select(MerchantView.Of));
    }

    [HttpGet("{storeId:int}")]
    public async Task<ActionResult<MerchantView>> Get(int storeId, CancellationToken cancellationToken)
    {
        var row = await _db.Merchants.AsNoTracking()
            .FirstOrDefaultAsync(m => m.StoreId == storeId, cancellationToken);
        return row is null ? NotFound(new { error = "Merchant not found" }) : Ok(MerchantView.Of(row));
    }

    /// <summary>
    /// Onboard a merchant. The response carries the generated webhook secret, which is what
    /// goes into their plugin, and it is not readable afterwards.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MerchantCreated>> Create(
        [FromBody] MerchantUpsert body, CancellationToken cancellationToken)
    {
        if (await _db.Merchants.AnyAsync(m => m.StoreId == body.StoreId, cancellationToken))
        {
            return Conflict(new { error = $"Store {body.StoreId} already exists" });
        }

        var record = new MerchantRecord { StoreId = body.StoreId };
        body.ApplyTo(record);

        // Generated rather than chosen: a secret somebody types is a secret somebody reuses.
        var secret = $"whk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=')}";
        record.WebhookSecret = secret;

        _db.Merchants.Add(record);
        Record(record.StoreId, "merchant.create", new { body.Name, body.EngineAccountRef });
        await _db.SaveChangesAsync(cancellationToken);
        _directory.Invalidate();

        _logger.LogInformation("Merchant {StoreId} created ({Name})", record.StoreId, record.Name);
        return CreatedAtAction(nameof(Get), new { storeId = record.StoreId },
            new MerchantCreated(MerchantView.Of(record), secret));
    }

    [HttpPut("{storeId:int}")]
    public async Task<ActionResult<MerchantView>> Update(
        int storeId, [FromBody] MerchantUpsert body, CancellationToken cancellationToken)
    {
        var record = await _db.Merchants.FirstOrDefaultAsync(m => m.StoreId == storeId, cancellationToken);
        if (record is null) return NotFound(new { error = "Merchant not found" });

        body.ApplyTo(record);
        record.UpdatedAt = DateTimeOffset.UtcNow;
        Record(storeId, "merchant.update", new { body.Name, body.EngineAccountRef, body.IsActive });
        await _db.SaveChangesAsync(cancellationToken);
        _directory.Invalidate();

        return Ok(MerchantView.Of(record));
    }

    /// <summary>
    /// Issue a new webhook secret. The old one stops working immediately, so this is a
    /// coordinated change with the merchant rather than routine housekeeping.
    /// </summary>
    [HttpPost("{storeId:int}/rotate-secret")]
    public async Task<ActionResult<MerchantCreated>> RotateSecret(
        int storeId, CancellationToken cancellationToken)
    {
        var record = await _db.Merchants.FirstOrDefaultAsync(m => m.StoreId == storeId, cancellationToken);
        if (record is null) return NotFound(new { error = "Merchant not found" });

        var secret = $"whk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=')}";
        record.WebhookSecret = secret;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        Record(storeId, "merchant.rotate_secret", null);
        await _db.SaveChangesAsync(cancellationToken);
        _directory.Invalidate();

        return Ok(new MerchantCreated(MerchantView.Of(record), secret));
    }

    /// <summary>
    /// Switch a merchant off. Deliberately not a delete: their past orders still reference
    /// this row, and an inactive merchant can be switched back on after a misunderstanding.
    /// </summary>
    [HttpPost("{storeId:int}/deactivate")]
    public async Task<ActionResult<MerchantView>> Deactivate(
        int storeId, CancellationToken cancellationToken)
    {
        var record = await _db.Merchants.FirstOrDefaultAsync(m => m.StoreId == storeId, cancellationToken);
        if (record is null) return NotFound(new { error = "Merchant not found" });

        record.IsActive = false;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        Record(storeId, "merchant.deactivate", null);
        await _db.SaveChangesAsync(cancellationToken);
        _directory.Invalidate();

        return Ok(MerchantView.Of(record));
    }

    [HttpGet("{storeId:int}/changes")]
    public async Task<ActionResult<IEnumerable<MerchantChange>>> Changes(
        int storeId, CancellationToken cancellationToken)
    {
        var rows = await _db.MerchantChanges.AsNoTracking()
            .Where(c => c.StoreId == storeId)
            .OrderByDescending(c => c.OccurredAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        return Ok(rows);
    }

    /// <summary>
    /// Who did this.
    ///
    /// The admin key identifies the console, not a person, which makes "who turned this
    /// merchant off on Friday" unanswerable. The console knows — it signed the operator in —
    /// so it says, and that is what gets recorded. It is self-reported and only as
    /// trustworthy as the thing holding the key, which is the point of keeping that key on a
    /// server rather than in a browser.
    /// </summary>
    private void Record(int storeId, string action, object? detail)
    {
        var actor = Request.Headers[ActorHeader].ToString();
        _db.MerchantChanges.Add(new MerchantChange
        {
            StoreId = storeId,
            Action = action,
            ActorLabel = string.IsNullOrWhiteSpace(actor) ? "admin-key" : Trim(actor, 200),
            Detail = detail is null ? null : JsonSerializer.Serialize(detail)
        });
    }

    public const string ActorHeader = "X-Admin-Actor";

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>
/// A merchant as the admin API shows it.
///
/// No secrets. The webhook secret and the WooCommerce credentials are write-only: they are
/// needed to verify and to call, never to display, and a console that renders them puts them
/// in a browser, a screenshot and a support ticket.
/// </summary>
public sealed record MerchantView(
    int StoreId,
    string Name,
    string EngineAccountRef,
    string? EngineAccountId,
    MerchantAddress Collection,
    string CheckoutServiceLevelCode,
    string CheckoutRateLabel,
    string EngineServiceLevelCode,
    string DefaultPackageTypeId,
    int EstimatedDeliveryDays,
    List<string> BookingTriggerStatuses,
    string? WooCommerceUrl,
    bool HasWooCredentials,
    bool IsActive,
    DateTimeOffset UpdatedAt)
{
    public static MerchantView Of(MerchantRecord r) => new(
        r.StoreId, r.Name, r.EngineAccountRef, r.EngineAccountId, r.Collection,
        r.CheckoutServiceLevelCode, r.CheckoutRateLabel, r.EngineServiceLevelCode,
        r.DefaultPackageTypeId, r.EstimatedDeliveryDays, r.BookingTriggerStatuses,
        r.WooCommerceUrl,
        !string.IsNullOrWhiteSpace(r.WooConsumerKey) && !string.IsNullOrWhiteSpace(r.WooConsumerSecret),
        r.IsActive, r.UpdatedAt);
}

public sealed record MerchantCreated(MerchantView Merchant, string WebhookSecret);

public sealed class MerchantUpsert
{
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EngineAccountRef { get; set; } = string.Empty;
    public string? EngineAccountId { get; set; }
    public MerchantAddress Collection { get; set; } = new();
    public string CheckoutServiceLevelCode { get; set; } = "STD";
    public string CheckoutRateLabel { get; set; } = "Delicate Courier Delivery";
    public string EngineServiceLevelCode { get; set; } = "standard";
    public string DefaultPackageTypeId { get; set; } = string.Empty;
    public int EstimatedDeliveryDays { get; set; } = 1;
    public List<string> BookingTriggerStatuses { get; set; } = new();
    public string? WooCommerceUrl { get; set; }

    /// <summary>Left null to keep whatever is stored; set to replace it.</summary>
    public string? WooConsumerKey { get; set; }
    public string? WooConsumerSecret { get; set; }

    public bool IsActive { get; set; } = true;

    public void ApplyTo(MerchantRecord record)
    {
        record.Name = Name;
        record.EngineAccountRef = EngineAccountRef;
        record.EngineAccountId = EngineAccountId;
        record.Collection = Collection;
        record.CheckoutServiceLevelCode = CheckoutServiceLevelCode;
        record.CheckoutRateLabel = CheckoutRateLabel;
        record.EngineServiceLevelCode = EngineServiceLevelCode;
        record.DefaultPackageTypeId = DefaultPackageTypeId;
        record.EstimatedDeliveryDays = EstimatedDeliveryDays;
        record.BookingTriggerStatuses = BookingTriggerStatuses;
        record.WooCommerceUrl = WooCommerceUrl;
        record.IsActive = IsActive;

        // Credentials are only replaced when supplied. An edit form that does not render them
        // would otherwise blank them every time somebody corrected a phone number.
        if (!string.IsNullOrWhiteSpace(WooConsumerKey)) record.WooConsumerKey = WooConsumerKey;
        if (!string.IsNullOrWhiteSpace(WooConsumerSecret)) record.WooConsumerSecret = WooConsumerSecret;
    }
}
