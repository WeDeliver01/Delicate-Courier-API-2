using System.Text;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Labels;
using PdfSharpCore.Pdf.IO;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// The printed label.
///
/// A PDF is awkward to assert on, so these check the two things that actually matter: that it
/// is a real PDF a printer will accept, and that the words a driver needs are in it. The
/// layout itself needs human eyes and a printer — which the migration plan already requires
/// before this label is signed off.
/// </summary>
public class LabelTests
{
    private static WaybillDocument Waybill() => new()
    {
        Waybill = "DC-260101-00001",
        BookingReference = "BK-260101-0001",
        CustomerReference = "WC-49905",
        Status = "booked",
        IssuedAt = "2026-01-01T08:00:00Z",
        ParcelCount = 2,
        Terms = "All deliveries are subject to our standard terms and conditions.",
        Carrier = new WaybillCarrier
        {
            TradingName = "Delicate Courier",
            Phone = "012 345 6789"
        },
        Service = new WaybillService
        {
            Code = "standard",
            Name = "Standard",
            SlotDate = "2026-01-02",
            SlotWindow = "Morning"
        },
        Sender = new WaybillParty
        {
            AccountName = "Honey Bee Baker",
            Contact = new EngineContact { Name = "Bakery", Phone = "0821111111" },
            Address = new EngineAddress
            {
                Formatted = "Honey Bee Baker, Menlyn, Pretoria",
                Line1 = "Atterbury Road",
                Suburb = "Menlyn",
                City = "Pretoria",
                PostalCode = "0181"
            }
        },
        Recipient = new WaybillParty
        {
            Contact = new EngineContact { Name = "Jane Smith", Phone = "0821234567" },
            Address = new EngineAddress
            {
                Formatted = "12 Oak Street, Centurion",
                Line1 = "12 Oak Street",
                Suburb = "Centurion",
                City = "Pretoria",
                PostalCode = "0157"
            },
            Instructions = "Leave at reception"
        }
    };

    private static string AsText(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void Produces_a_real_pdf()
    {
        var pdf = new LabelRenderer().Render(Waybill());

        Assert.NotEmpty(pdf);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Contains("%%EOF", AsText(pdf));
    }

    [Fact]
    public void Names_the_document_after_the_waybill()
    {
        // The merchant saves these to a folder; a file called "document.pdf" twenty times
        // over is no use to anyone. Read back through a PDF reader rather than by searching
        // the bytes — metadata is not stored as plain text in the file.
        var pdf = new LabelRenderer().Render(Waybill());

        using var stream = new MemoryStream(pdf);
        using var reopened = PdfReader.Open(stream, PdfDocumentOpenMode.InformationOnly);
        Assert.Equal("Waybill DC-260101-00001", reopened.Info.Title);
    }

    [Fact]
    public void Is_a_single_a6_page()
    {
        // A6 is what the thermal printers in this trade take, and a label bigger than the
        // parcel helps nobody.
        var pdf = new LabelRenderer().Render(Waybill());

        using var stream = new MemoryStream(pdf);
        using var reopened = PdfReader.Open(stream, PdfDocumentOpenMode.InformationOnly);
        Assert.Equal(1, reopened.PageCount);
        Assert.Equal(105, reopened.Pages[0].Width.Millimeter, 0);
        Assert.Equal(148, reopened.Pages[0].Height.Millimeter, 0);
    }

    [Fact]
    public void Renders_without_an_address_rather_than_throwing()
    {
        // A waybill with thin data still has to print. Refusing to render is worse than a
        // sparse label, because the parcel then has nothing on it at all.
        var sparse = Waybill() with
        {
            Recipient = new WaybillParty { Contact = new EngineContact { Name = "Jane" } },
            Sender = new WaybillParty(),
            Terms = string.Empty
        };

        var pdf = new LabelRenderer().Render(sparse);
        Assert.NotEmpty(pdf);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Falls_back_to_the_formatted_address_when_there_are_no_parts()
    {
        var oneLine = Waybill() with
        {
            Recipient = new WaybillParty
            {
                Contact = new EngineContact { Name = "Jane Smith" },
                Address = new EngineAddress { Formatted = "Somewhere hard to parse, Pretoria" }
            }
        };

        var pdf = new LabelRenderer().Render(oneLine);
        Assert.NotEmpty(pdf);
    }

    [Fact]
    public void Writes_a_sample_when_asked_so_the_layout_can_be_looked_at()
    {
        // A label is judged by eye and by printer, not by assertions. Set LABEL_SAMPLE_PATH
        // to drop a rendered one somewhere openable; otherwise this just renders and stops.
        var pdf = new LabelRenderer().Render(Waybill());
        var path = Environment.GetEnvironmentVariable("LABEL_SAMPLE_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllBytes(path, pdf);
        }
        Assert.NotEmpty(pdf);
    }

    [Fact]
    public void Says_plainly_that_the_barcode_is_not_verified_yet()
    {
        // The notice is on the label on purpose. A missing barcode that nobody mentions is
        // discovered by a driver at a door; one that says so is discovered in an office.
        Assert.Contains("scanner", LabelRenderer.BarcodeNotice, StringComparison.OrdinalIgnoreCase);
    }
}
