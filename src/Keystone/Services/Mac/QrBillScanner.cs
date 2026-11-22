using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Keystone.Services.Mac;

/// <summary>
/// Finds the Swiss QR-bill in a PDF or photo. Pages and images are drawn with macOS's own PDF and image
/// engines (CoreGraphics, ImageIO) into a grey bitmap, which ZXing searches for a QR code.
/// </summary>
public static class QrBillScanner
{
    /// <summary>The payment part sits at the bottom of the last page, so pages are searched from the end.</summary>
    private const int MaxPages = 6;

    /// <summary>Pixels across a page: enough for about 10 pixels per QR module on A4.</summary>
    private const double PageWidthPixels = 2400;

    /// <summary>Photos are scaled down to this size first; phones take much larger ones.</summary>
    private const int MaxImageSide = 2600;

    public static bool CanScan(string path) =>
        OperatingSystem.IsMacOS() && Path.GetExtension(path).ToLowerInvariant() is
            ".pdf" or ".png" or ".jpg" or ".jpeg" or ".heic" or ".heif" or ".tif" or ".tiff";

    /// <summary>The QR-bill in the file, or null when there is none or the file can't be read.</summary>
    public static QrBill? Scan(string path)
    {
        if (!CanScan(path) || !File.Exists(path))
            return null;
        try
        {
            foreach (var image in Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                         ? RenderPdfPages(path)
                         : [RenderImage(path)])
            {
                if (image is { } gray && Decode(gray) is { } bill)
                    return bill;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
        }
        return null;
    }

    /// <summary>Decodes the first QR code that is a QR-bill, trying the bottom of the page first.</summary>
    internal static QrBill? Decode(GrayImage image)
    {
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
        };
        var reader = new QRCodeReader();

        // The bottom 45% holds the payment part; searching it alone is faster and avoids other codes.
        var bottom = (int)(image.Height * 0.55);
        foreach (var top in new[] { bottom, 0 })
        {
            var source = new RGBLuminanceSource(Crop(image, top), image.Width, image.Height - top,
                RGBLuminanceSource.BitmapFormat.Gray8);
            try
            {
                var result = reader.decode(new BinaryBitmap(new HybridBinarizer(source)), hints);
                if (SwissQrBillParser.Parse(result?.Text) is { } bill)
                    return bill;
            }
            catch (ReaderException)
            {
            }
        }
        return null;
    }

    private static byte[] Crop(GrayImage image, int top)
    {
        if (top == 0)
            return image.Pixels;
        var pixels = new byte[image.Width * (image.Height - top)];
        Array.Copy(image.Pixels, top * image.Width, pixels, 0, pixels.Length);
        return pixels;
    }

    public sealed record GrayImage(byte[] Pixels, int Width, int Height);

    // ── PDF pages ───────────────────────────────────────────────────────────

    private static IEnumerable<GrayImage?> RenderPdfPages(string path)
    {
        var url = FileUrl(path);
        if (url == IntPtr.Zero)
            yield break;
        var document = CGPDFDocumentCreateWithURL(url);
        CFRelease(url);
        if (document == IntPtr.Zero)
            yield break;

        try
        {
            var count = (int)CGPDFDocumentGetNumberOfPages(document);
            for (var number = count; number >= 1 && number > count - MaxPages; number--)
                yield return RenderPage(CGPDFDocumentGetPage(document, (nuint)number));
        }
        finally
        {
            CGPDFDocumentRelease(document);
        }
    }

    private static GrayImage? RenderPage(IntPtr page)
    {
        if (page == IntPtr.Zero)
            return null;
        var box = CGPDFPageGetBoxRect(page, 1 /* crop box */);
        if (box.Width <= 0 || box.Height <= 0)
            return null;

        var scale = PageWidthPixels / box.Width;
        var width = (int)Math.Ceiling(box.Width * scale);
        var height = (int)Math.Ceiling(box.Height * scale);
        return Draw(width, height, context =>
        {
            CGContextScaleCTM(context, scale, scale);
            CGContextTranslateCTM(context, -box.X, -box.Y);
            CGContextDrawPDFPage(context, page);
        });
    }

    // ── Images ──────────────────────────────────────────────────────────────

    private static GrayImage? RenderImage(string path)
    {
        var url = FileUrl(path);
        if (url == IntPtr.Zero)
            return null;
        var source = CGImageSourceCreateWithURL(url, IntPtr.Zero);
        CFRelease(url);
        if (source == IntPtr.Zero)
            return null;

        try
        {
            var image = CGImageSourceCreateImageAtIndex(source, 0, IntPtr.Zero);
            if (image == IntPtr.Zero)
                return null;
            try
            {
                double width = CGImageGetWidth(image), height = CGImageGetHeight(image);
                var scale = Math.Min(1, MaxImageSide / Math.Max(width, height));
                var w = Math.Max(1, (int)(width * scale));
                var h = Math.Max(1, (int)(height * scale));
                return Draw(w, h, context => CGContextDrawImage(context, new CGRect(0, 0, w, h), image));
            }
            finally
            {
                CFRelease(image);
            }
        }
        finally
        {
            CFRelease(source);
        }
    }

    /// <summary>Draws into a white 8-bit grey bitmap; its rows run from the top of the image down.</summary>
    private static GrayImage? Draw(int width, int height, Action<IntPtr> draw)
    {
        var pixels = new byte[width * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var space = CGColorSpaceCreateDeviceGray();
        try
        {
            var context = CGBitmapContextCreate(handle.AddrOfPinnedObject(), (nuint)width, (nuint)height, 8, (nuint)width, space, 0);
            if (context == IntPtr.Zero)
                return null;
            try
            {
                CGContextSetGrayFillColor(context, 1, 1);
                CGContextFillRect(context, new CGRect(0, 0, width, height));
                draw(context);
            }
            finally
            {
                CGContextRelease(context);
            }
        }
        finally
        {
            CGColorSpaceRelease(space);
            handle.Free();
        }
        return new GrayImage(pixels, width, height);
    }

    private static IntPtr FileUrl(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        return CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
    }

    // ── CoreGraphics, ImageIO and CoreFoundation ────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, nint length,
        [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CoreGraphics)] private static extern IntPtr CGPDFDocumentCreateWithURL(IntPtr url);
    [DllImport(CoreGraphics)] private static extern nuint CGPDFDocumentGetNumberOfPages(IntPtr document);
    [DllImport(CoreGraphics)] private static extern IntPtr CGPDFDocumentGetPage(IntPtr document, nuint pageNumber);
    [DllImport(CoreGraphics)] private static extern void CGPDFDocumentRelease(IntPtr document);
    [DllImport(CoreGraphics)] private static extern CGRect CGPDFPageGetBoxRect(IntPtr page, int box);
    [DllImport(CoreGraphics)] private static extern IntPtr CGColorSpaceCreateDeviceGray();
    [DllImport(CoreGraphics)] private static extern void CGColorSpaceRelease(IntPtr space);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent,
        nuint bytesPerRow, IntPtr space, uint bitmapInfo);

    [DllImport(CoreGraphics)] private static extern void CGContextRelease(IntPtr context);
    [DllImport(CoreGraphics)] private static extern void CGContextSetGrayFillColor(IntPtr context, double gray, double alpha);
    [DllImport(CoreGraphics)] private static extern void CGContextFillRect(IntPtr context, CGRect rect);
    [DllImport(CoreGraphics)] private static extern void CGContextScaleCTM(IntPtr context, double sx, double sy);
    [DllImport(CoreGraphics)] private static extern void CGContextTranslateCTM(IntPtr context, double tx, double ty);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawPDFPage(IntPtr context, IntPtr page);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetWidth(IntPtr image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetHeight(IntPtr image);
    [DllImport(ImageIO)] private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);
    [DllImport(ImageIO)] private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nuint index, IntPtr options);
}
