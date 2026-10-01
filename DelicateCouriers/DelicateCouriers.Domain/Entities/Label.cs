namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a shipping label PDF file for a shipment.
/// Stored in the database as a base64 data URL today; can be migrated to an
/// object-storage backend (e.g. S3-compatible bucket) later without schema changes.
/// Each label corresponds to one shipment.
/// Example: Label for Aramex shipment SL123456, file name "labels/SL123456.pdf"
/// </summary>
public class Label
{
    /// <summary>
    /// Unique identifier for the label
    /// </summary>
    public int LabelID { get; set; }

    /// <summary>
    /// Foreign key to the shipment this label is for
    /// </summary>
    public int ShipmentID { get; set; }

    /// <summary>
    /// URL or data URL for the PDF file. Currently a base64 data URL stored inline;
    /// can become a real object-storage URL once labels are externalised.
    /// Example: "data:application/pdf;base64,JVBERi0xLjQKJ..."
    /// </summary>
    public string BlobURL { get; set; } = string.Empty;

    /// <summary>
    /// Filename in blob storage
    /// Example: "SL123456.pdf" or "labels/2025/01/SL123456.pdf"
    /// </summary>
    public string BlobFileName { get; set; } = string.Empty;

    /// <summary>
    /// Size of the PDF file in bytes
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// MIME content type (always "application/pdf" for labels)
    /// </summary>
    public string ContentType { get; set; } = "application/pdf";

    /// <summary>
    /// When this label was retrieved and stored
    /// </summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who/what created this label record (usually "System")
    /// </summary>
    public string CreatedBy { get; set; } = "System";

    /// <summary>
    /// When this label was last modified
    /// </summary>
    public DateTime? ChangedOn { get; set; }

    /// <summary>
    /// Who last modified this label
    /// </summary>
    public string? ChangedBy { get; set; }

    // Navigation property
    public Shipment Shipment { get; set; } = null!;
}