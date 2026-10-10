using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace DelicateCouriers.Integration.Labels;

public interface ILabelRenderer
{
    byte[] Render(WaybillDocument waybill);
}

/// <summary>
/// Draws a waybill onto an A6 label.
///
/// A6 because that is what thermal label printers in this trade take, and because a label
/// bigger than the parcel is no use. The layout puts the waybill number first and largest: it
/// is the one thing a driver reads aloud on the phone, writes on a manifest and types into a
/// search box, so it has to survive a bad photocopy and a dim van.
///
/// No barcode yet, deliberately — see <see cref="BarcodeNotice"/>.
/// </summary>
public sealed class LabelRenderer : ILabelRenderer
{
    /// <summary>
    /// Why there is no barcode on this label.
    ///
    /// A barcode that renders but does not scan is worse than none: a driver trusts it, the
    /// scan fails at the door, and the failure surfaces as a delivery problem rather than a
    /// printing one. Code 128 is easy to draw and easy to get subtly wrong, and nothing here
    /// can verify it against a real scanner and real thermal stock — which the migration plan
    /// already requires before the label is signed off.
    ///
    /// So: the number is printed large and clear, and the barcode waits for someone with a
    /// scanner. <see cref="LabelRenderer"/> is the only place that needs to change.
    /// </summary>
    public const string BarcodeNotice =
        "Barcode pending verification against a real scanner and label stock.";

    // A6 at 72dpi: 105mm x 148mm.
    private const double PageWidth = 297.6;
    private const double PageHeight = 419.5;
    private const double Margin = 18;

    public byte[] Render(WaybillDocument waybill)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"Waybill {waybill.Waybill}";

        var page = document.AddPage();
        page.Width = PageWidth;
        page.Height = PageHeight;

        using var gfx = XGraphics.FromPdfPage(page);

        var heading = new XFont("Arial", 8, XFontStyle.Bold);
        var body = new XFont("Arial", 7.5);
        var bodyBold = new XFont("Arial", 7.5, XFontStyle.Bold);
        var waybillFont = new XFont("Arial", 19, XFontStyle.Bold);
        var fine = new XFont("Arial", 5.5);

        var width = PageWidth - (Margin * 2);
        double y = Margin;

        // Carrier
        var carrier = waybill.Carrier.TradingName ?? waybill.Carrier.LegalName ?? "Delicate Courier";
        gfx.DrawString(carrier, heading, XBrushes.Black, new XRect(Margin, y, width, 12), XStringFormats.TopLeft);
        y += 13;

        if (!string.IsNullOrWhiteSpace(waybill.Carrier.Phone))
        {
            gfx.DrawString(waybill.Carrier.Phone, fine, XBrushes.Black,
                new XRect(Margin, y, width, 8), XStringFormats.TopLeft);
            y += 9;
        }

        y = Rule(gfx, y, width);

        // The waybill number, as large as the page allows.
        gfx.DrawString(waybill.Waybill, waybillFont, XBrushes.Black,
            new XRect(Margin, y, width, 24), XStringFormats.TopLeft);
        y += 26;

        gfx.DrawString(BarcodeNotice, fine, XBrushes.Gray,
            new XRect(Margin, y, width, 8), XStringFormats.TopLeft);
        y += 10;

        y = Rule(gfx, y, width);

        // Service and slot
        var service = waybill.Service.Name ?? waybill.Service.Code ?? "Delivery";
        if (!string.IsNullOrWhiteSpace(waybill.Service.SlotDate))
        {
            service += $"   {waybill.Service.SlotDate}";
            if (!string.IsNullOrWhiteSpace(waybill.Service.SlotWindow))
            {
                service += $" ({waybill.Service.SlotWindow})";
            }
        }
        gfx.DrawString(service, bodyBold, XBrushes.Black, new XRect(Margin, y, width, 10), XStringFormats.TopLeft);
        y += 11;

        var parcels = waybill.ParcelCount == 1 ? "1 parcel" : $"{waybill.ParcelCount} parcels";
        gfx.DrawString(parcels, body, XBrushes.Black, new XRect(Margin, y, width, 10), XStringFormats.TopLeft);
        y += 12;

        y = Rule(gfx, y, width);

        y = Party(gfx, "TO", waybill.Recipient, y, width, heading, body, bodyBold);
        y = Rule(gfx, y, width);
        y = Party(gfx, "FROM", waybill.Sender, y, width, heading, body, bodyBold);

        // References, anchored to the foot so the blocks above can grow.
        double footer = PageHeight - Margin - 26;
        Rule(gfx, footer - 4, width);
        var references = $"Booking {waybill.BookingReference}";
        if (!string.IsNullOrWhiteSpace(waybill.CustomerReference))
        {
            references += $"    Order {waybill.CustomerReference}";
        }
        gfx.DrawString(references, body, XBrushes.Black,
            new XRect(Margin, footer, width, 10), XStringFormats.TopLeft);

        if (!string.IsNullOrWhiteSpace(waybill.Terms))
        {
            gfx.DrawString(Truncate(waybill.Terms, 150), fine, XBrushes.Gray,
                new XRect(Margin, footer + 11, width, 14), XStringFormats.TopLeft);
        }

        using var buffer = new MemoryStream();
        document.Save(buffer, false);
        return buffer.ToArray();
    }

    private static double Party(
        XGraphics gfx, string label, WaybillParty party, double y, double width,
        XFont heading, XFont body, XFont bodyBold)
    {
        gfx.DrawString(label, heading, XBrushes.Black, new XRect(Margin, y, width, 10), XStringFormats.TopLeft);
        y += 11;

        var name = party.Contact?.Name ?? party.AccountName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            gfx.DrawString(name, bodyBold, XBrushes.Black, new XRect(Margin, y, width, 10), XStringFormats.TopLeft);
            y += 10;
        }

        foreach (var line in AddressLines(party))
        {
            gfx.DrawString(line, body, XBrushes.Black, new XRect(Margin, y, width, 10), XStringFormats.TopLeft);
            y += 9;
        }

        if (!string.IsNullOrWhiteSpace(party.Contact?.Phone))
        {
            gfx.DrawString(party.Contact!.Phone, body, XBrushes.Black,
                new XRect(Margin, y, width, 10), XStringFormats.TopLeft);
            y += 9;
        }

        if (!string.IsNullOrWhiteSpace(party.Instructions))
        {
            gfx.DrawString(Truncate(party.Instructions!, 90), body, XBrushes.Black,
                new XRect(Margin, y, width, 10), XStringFormats.TopLeft);
            y += 9;
        }

        return y + 3;
    }

    /// <summary>
    /// Address lines for a label, skipping the empties.
    ///
    /// Prefers the parts over <c>formatted</c>: a single formatted string wraps badly at this
    /// width, and a driver standing at a gate reads a street and a suburb, not a sentence.
    /// </summary>
    private static IEnumerable<string> AddressLines(WaybillParty party)
    {
        var address = party.Address;
        if (address is null) yield break;

        var parts = new[] { address.Line1, address.Suburb, address.City, address.PostalCode }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (parts.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(address.Formatted)) yield return address.Formatted;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(address.Line1)) yield return address.Line1!;

        var locality = string.Join(", ", new[] { address.Suburb, address.City, address.PostalCode }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        if (locality.Length > 0) yield return locality;
    }

    private static double Rule(XGraphics gfx, double y, double width)
    {
        gfx.DrawLine(new XPen(XColors.Black, 0.4), Margin, y + 2, Margin + width, y + 2);
        return y + 7;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
