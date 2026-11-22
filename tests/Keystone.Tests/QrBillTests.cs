using System.Globalization;
using System.Text;
using Keystone.Services;
using Keystone.Services.Mac;
using ZXing;
using ZXing.QrCode;

namespace Keystone.Tests;

public sealed class QrBillTests : IDisposable
{
    /// <summary>The example from the Swiss Payment Standards' QR-bill guidelines.</summary>
    private const string Example = """
        SPC
        0200
        1
        CH4431999123000889012
        S
        Robert Schneider AG
        Rue du Lac
        1268
        2501
        Biel
        CH







        1949.75
        CHF
        S
        Pia-Maria Rutschmann-Schnyder
        Grosse Marktgasse
        28
        9400
        Rorschach
        CH
        QRR
        210000000003139471430009017
        Auftrag vom 15.06.2020
        EPD
        //S1/10/10201409/11/200701/20/140.000-53/30/102673831/31/200615/32/7.7/33/7.7:10/40/0:30
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-qr-{Guid.NewGuid():N}");

    public QrBillTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TestFolder.Delete(_folder);

    [Fact]
    public void ReadsThePaymentPart()
    {
        var bill = SwissQrBillParser.Parse(Example)!;

        Assert.Equal("CH4431999123000889012", bill.Iban);
        Assert.Equal("CH44 3199 9123 0008 8901 2", bill.FormattedIban);
        Assert.Equal("Robert Schneider AG", bill.Creditor);
        Assert.Equal(1949.75m, bill.Amount);
        Assert.Equal("QRR", bill.ReferenceType);
        Assert.Equal("21 00000 00003 13947 14300 09017", bill.FormattedReference);
        Assert.Equal("Auftrag vom 15.06.2020", bill.Message);
        Assert.Equal(new DateOnly(2020, 7, 1), bill.InvoiceDate);
        Assert.Equal(new DateOnly(2020, 7, 31), bill.DueDate); // 30 days, no discount
    }

    [Theory]
    [InlineData("")]
    [InlineData("BCD\n002\n1\nSCT")] // a European payment QR, not a Swiss one
    [InlineData("SPC\n0200\n1\nDE89370400440532013000")] // not a Swiss IBAN
    public void IgnoresOtherCodes(string payload) => Assert.Null(SwissQrBillParser.Parse(payload));

    [Fact]
    public void AnOpenAmountStaysEmpty()
    {
        var lines = Example.Split('\n');
        lines[18] = "";
        Assert.Null(SwissQrBillParser.Parse(string.Join('\n', lines))!.Amount);
    }

    [Fact]
    public void FindsTheQrCodeOnAPdfPage()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var path = Path.Combine(_folder, "bill.pdf");
        File.WriteAllBytes(path, PdfWithQrCode(Example));

        var bill = QrBillScanner.Scan(path);

        Assert.NotNull(bill);
        Assert.Equal(1949.75m, bill.Amount);
        Assert.Equal("CH4431999123000889012", bill.Iban);
    }

    [Fact]
    public void FindsTheQrCodeInAPhoto()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var path = Path.Combine(_folder, "photo.bmp");
        File.WriteAllBytes(path, BitmapWithQrCode(Example, moduleSize: 7, margin: 300));
        // BMP isn't offered for scanning; it stands in for a photo here.
        var renamed = Path.ChangeExtension(path, ".png");
        File.Move(path, renamed);

        Assert.Equal("Robert Schneider AG", QrBillScanner.Scan(renamed)?.Creditor);
    }

    [Fact]
    public void APdfWithoutAQrBillHasNone()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var path = Path.Combine(_folder, "letter.pdf");
        File.WriteAllBytes(path, PdfWithQrCode(null));
        Assert.Null(QrBillScanner.Scan(path));
    }

    private static ZXing.Common.BitMatrix Encode(string text) =>
        new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            [EncodeHintType.MARGIN] = 0,
        });

    /// <summary>An A4 page with a heading and, at the bottom left like a payment part, a 46 mm QR code drawn as vector squares.</summary>
    private static byte[] PdfWithQrCode(string? payload)
    {
        var content = new StringBuilder("BT /F1 18 Tf 60 780 Td (Rechnung 10201409) Tj ET\n");
        if (payload is not null)
        {
            var matrix = Encode(payload);
            var module = 130.4 / matrix.Width; // 46 mm
            for (var y = 0; y < matrix.Height; y++)
                for (var x = 0; x < matrix.Width; x++)
                    if (matrix[x, y])
                        content.Append(string.Create(CultureInfo.InvariantCulture,
                            $"{190 + x * module:0.###} {170 - y * module:0.###} {module:0.###} {module:0.###} re\n"));
            content.Append("f\n");
        }

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>A 24-bit BMP with the QR code on a light-grey background, like a photographed slip.</summary>
    private static byte[] BitmapWithQrCode(string payload, int moduleSize, int margin)
    {
        var matrix = Encode(payload);
        var size = matrix.Width * moduleSize + 2 * margin;
        var rowBytes = (size * 3 + 3) / 4 * 4;
        var bmp = new byte[54 + rowBytes * size];
        void Int(int at, int value) => BitConverter.GetBytes(value).CopyTo(bmp, at);
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        Int(2, bmp.Length); Int(10, 54); Int(14, 40); Int(18, size); Int(22, size);
        bmp[26] = 1; bmp[28] = 24; Int(34, rowBytes * size);
        for (var row = 0; row < size; row++)
        {
            var y = size - 1 - row; // BMP rows run bottom-up
            for (var x = 0; x < size; x++)
            {
                var mx = (x - margin) / moduleSize;
                var my = (y - margin) / moduleSize;
                var dark = x >= margin && y >= margin && mx < matrix.Width && my < matrix.Height && matrix[mx, my];
                var shade = (byte)(dark ? 30 : 225);
                var at = 54 + row * rowBytes + x * 3;
                bmp[at] = bmp[at + 1] = bmp[at + 2] = shade;
            }
        }
        return bmp;
    }
}
