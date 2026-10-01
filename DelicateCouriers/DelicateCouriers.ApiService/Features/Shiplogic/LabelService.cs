using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Features.Shiplogic;

/// <summary>
/// Service for managing shipment label PDFs.
/// Stores labels in the database as base64 data URLs today; can be migrated to an
/// object-storage backend (e.g. S3-compatible bucket) later without schema changes.
/// </summary>
public class LabelService
{
    private readonly AppDbContext _context;
    private readonly ILogger<LabelService> _logger;

    public LabelService(AppDbContext context, ILogger<LabelService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Save a label PDF to the database.
    /// Stores as base64 data URL for now (can migrate to object storage later).
    /// </summary>
    /// <param name="shipmentId">Shipment ID from our database</param>
    /// <param name="pdfBytes">The PDF file as byte array</param>
    /// <param name="shiplogicShipmentId">Shiplogic's shipment ID</param>
    /// <returns>The saved Label entity</returns>
    public async Task<Label> SaveLabelAsync(int shipmentId, byte[] pdfBytes, int shiplogicShipmentId)
    {
        _logger.LogInformation("Saving label for ShipmentID: {ShipmentId}, Size: {Size} bytes", shipmentId, pdfBytes.Length);

        // Check if label already exists
        var existingLabel = await _context.Labels.FirstOrDefaultAsync(l => l.ShipmentID == shipmentId);

        var fileName = $"label_{shiplogicShipmentId}.pdf";
        var base64Data = Convert.ToBase64String(pdfBytes);

        if (existingLabel != null)
        {
            _logger.LogInformation("Label already exists for ShipmentID: {ShipmentId}, updating...", shipmentId);

            // Update existing label
            existingLabel.BlobURL = $"data:application/pdf;base64,{base64Data}";
            existingLabel.BlobFileName = fileName;
            existingLabel.FileSizeBytes = pdfBytes.Length;
            existingLabel.ChangedOn = DateTime.UtcNow;
            existingLabel.ChangedBy = "LabelService";

            await _context.SaveChangesAsync();

            return existingLabel;
        }

        // Create new label
        var label = new Label
        {
            ShipmentID = shipmentId,
            BlobURL = $"data:application/pdf;base64,{base64Data}",
            BlobFileName = fileName,
            ContentType = "application/pdf",
            FileSizeBytes = pdfBytes.Length,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = "LabelService"
        };

        _context.Labels.Add(label);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Label saved successfully. LabelID: {LabelId}, ShipmentID: {ShipmentId}", label.LabelID, shipmentId);

        return label;
    }

    /// <summary>
    /// Get label PDF bytes for a shipment
    /// Converts from base64 back to bytes
    /// </summary>
    /// <param name="shipmentId">Shipment ID from our database</param>
    /// <returns>PDF bytes, or null if not found</returns>
    public async Task<byte[]?> GetLabelBytesAsync(int shipmentId)
    {
        _logger.LogInformation("Retrieving label bytes for ShipmentID: {ShipmentId}", shipmentId);

        var label = await _context.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.ShipmentID == shipmentId);

        if (label == null)
        {
            _logger.LogWarning("Label not found for ShipmentID: {ShipmentId}", shipmentId);

            return null;
        }

        // Extract base64 from data URL: "data:application/pdf;base64,XXXXX"
        var base64Data = label.BlobURL.Split(',')[1];
        var pdfBytes = Convert.FromBase64String(base64Data);

        return pdfBytes;
    }

    /// <summary>
    /// Get label entity for a shipment
    /// </summary>
    public async Task<Label?> GetLabelByShipmentIdAsync(int shipmentId)
    {
        _logger.LogInformation("Retrieving label for ShipmentID: {ShipmentId}", shipmentId);

        var label = await _context.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.ShipmentID == shipmentId);

        if (label == null)
        {
            _logger.LogWarning("Label not found for ShipmentID: {ShipmentId}", shipmentId);
        }

        return label;
    }

    /// <summary>
    /// Check if a shipment has a label
    /// </summary>
    public async Task<bool> HasLabelAsync(int shipmentId)
    {
        return await _context.Labels.AnyAsync(l => l.ShipmentID == shipmentId);
    }

    /// <summary>
    /// Delete a label (if needed)
    /// </summary>
    public async Task<bool> DeleteLabelAsync(int shipmentId)
    {
        var label = await _context.Labels.FirstOrDefaultAsync(l => l.ShipmentID == shipmentId);

        if (label == null)
        {
            return false;
        }

        _context.Labels.Remove(label);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Label deleted for ShipmentID: {ShipmentId}", shipmentId);

        return true;
    }
}