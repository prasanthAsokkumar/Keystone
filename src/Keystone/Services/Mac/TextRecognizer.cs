using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Keystone.Services.Mac;

/// <summary>
/// Reads the text in scans and photos with macOS's own text recognition (the Vision framework, the same
/// one behind Live Text). Runs on this Mac only; nothing is sent anywhere.
/// Scanner apps such as the OneDrive app save a letter as a PDF holding only a picture of each page,
/// so there is no text to read without this.
/// </summary>
public static class TextRecognizer
{
    /// <summary>Pages read at most; a long scanned contract still gets its first pages searchable.</summary>
    public const int MaxPages = 12;

    /// <summary>Pixels across a page: plenty for small print on A4, still quick to recognise.</summary>
    private const double PageWidthPixels = 2200;

    /// <summary>Photos are scaled down to this size first; phones take much larger ones.</summary>
    private const int MaxImageSide = 3000;

    /// <summary>Swiss letters come in German, French, Italian and English.</summary>
    private static readonly string[] Languages = ["de-DE", "fr-FR", "it-IT", "en-US"];

    private static readonly Lazy<bool> Available = new(() =>
        OperatingSystem.IsMacOS() && ObjC.LoadFramework("Vision") && ObjC.Class("VNRecognizeTextRequest") != IntPtr.Zero);

    public static bool IsSupported => Available.Value;

    /// <summary>The text of every page of a scanned PDF, page by page; empty when nothing could be read.</summary>
    public static string RecognizePdf(string path)
    {
        if (!IsSupported || !File.Exists(path))
            return "";
        var url = FileUrl(path);
        if (url == IntPtr.Zero)
            return "";
        var document = CGPDFDocumentCreateWithURL(url);
        CFRelease(url);
        if (document == IntPtr.Zero)
            return "";

        try
        {
            var text = new StringBuilder();
            var count = (int)Math.Min(CGPDFDocumentGetNumberOfPages(document), MaxPages);
            for (var number = 1; number <= count; number++)
            {
                var image = RenderPage(CGPDFDocumentGetPage(document, (nuint)number));
                if (image == IntPtr.Zero)
                    continue;
                try
                {
                    var page = Recognize(image);
                    if (page.Length > 0)
                        text.AppendLine($"--- Page {number} ---").AppendLine(page);
                }
                finally
                {
                    CGImageRelease(image);
                }
            }
            return text.ToString();
        }
        finally
        {
            CGPDFDocumentRelease(document);
        }
    }

    /// <summary>The text in a photo (PNG, JPG, HEIC…); empty when nothing could be read.</summary>
    public static string RecognizeImage(string path)
    {
        if (!IsSupported || !File.Exists(path))
            return "";
        var url = FileUrl(path);
        if (url == IntPtr.Zero)
            return "";
        var source = CGImageSourceCreateWithURL(url, IntPtr.Zero);
        CFRelease(url);
        if (source == IntPtr.Zero)
            return "";

        try
        {
            var original = CGImageSourceCreateImageAtIndex(source, 0, IntPtr.Zero);
            if (original == IntPtr.Zero)
                return "";
            try
            {
                double width = CGImageGetWidth(original), height = CGImageGetHeight(original);
                var scale = Math.Min(1, MaxImageSide / Math.Max(width, height));
                var w = Math.Max(1, (int)(width * scale));
                var h = Math.Max(1, (int)(height * scale));
                var image = Draw(w, h, context => CGContextDrawImage(context, new CGRect(0, 0, w, h), original));
                if (image == IntPtr.Zero)
                    return "";
                try
                {
                    return Recognize(image);
                }
                finally
                {
                    CGImageRelease(image);
                }
            }
            finally
            {
                CFRelease(original);
            }
        }
        finally
        {
            CFRelease(source);
        }
    }

    // ── Vision ──────────────────────────────────────────────────────────────

    private readonly record struct Line(string Text, CGRect Box);

    /// <summary>Recognises the text in one picture and puts it back into lines, top to bottom.</summary>
    private static string Recognize(IntPtr image)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        var request = IntPtr.Zero;
        var handler = IntPtr.Zero;
        try
        {
            request = ObjC.New("VNRecognizeTextRequest");
            ObjC.SendLong(request, ObjC.Sel("setRecognitionLevel:"), 0); // accurate
            ObjC.SendBool(request, ObjC.Sel("setUsesLanguageCorrection:"), 1);
            var languages = ObjC.Send(ObjC.Class("NSMutableArray"), ObjC.Sel("array"));
            foreach (var language in Languages)
                ObjC.Send(languages, ObjC.Sel("addObject:"), ObjC.NSString(language));
            ObjC.Send(request, ObjC.Sel("setRecognitionLanguages:"), languages);

            var options = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Sel("dictionary"));
            handler = ObjC.Send(ObjC.Send(ObjC.Class("VNImageRequestHandler"), ObjC.Sel("alloc")),
                ObjC.Sel("initWithCGImage:options:"), image, options);
            var requests = ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObject:"), request);
            if (ObjC.SendWithError(handler, ObjC.Sel("performRequests:error:"), requests, out _) == 0)
                return "";

            var results = ObjC.Send(request, ObjC.Sel("results"));
            if (results == IntPtr.Zero)
                return "";
            var lines = new List<Line>();
            var count = ObjC.SendReturnLong(results, ObjC.Sel("count"));
            for (long i = 0; i < count; i++)
            {
                var observation = ObjC.SendWithLong(results, ObjC.Sel("objectAtIndex:"), i);
                var candidate = ObjC.Send(ObjC.SendWithLong(observation, ObjC.Sel("topCandidates:"), 1), ObjC.Sel("firstObject"));
                if (candidate == IntPtr.Zero || ObjC.ToManagedString(ObjC.Send(candidate, ObjC.Sel("string"))) is not { Length: > 0 } text)
                    continue;
                lines.Add(new Line(text, SendReturnRect(observation, ObjC.Sel("boundingBox"))));
            }
            return Arrange(lines);
        }
        finally
        {
            if (handler != IntPtr.Zero)
                ObjC.Send(handler, ObjC.Sel("release"));
            if (request != IntPtr.Zero)
                ObjC.Send(request, ObjC.Sel("release"));
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Vision finds separate pieces of text; on a receipt the item and its price are two pieces on one line.
    /// Pieces whose middles are at the same height become one line, left to right, so "Total 23.50" stays together.
    /// Boxes are in 0–1 page coordinates with the origin at the bottom left.
    /// </summary>
    private static string Arrange(List<Line> pieces)
    {
        var rows = new List<List<Line>>();
        foreach (var piece in pieces.OrderByDescending(p => p.Box.Y + p.Box.Height / 2))
        {
            var middle = piece.Box.Y + piece.Box.Height / 2;
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row.Average(p => p.Box.Y + p.Box.Height / 2) - middle) < Math.Min(row[0].Box.Height, piece.Box.Height) / 2)
                row.Add(piece);
            else
                rows.Add([piece]);
        }
        return string.Join(Environment.NewLine,
            rows.Select(row => string.Join("   ", row.OrderBy(p => p.Box.X).Select(p => p.Text))));
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    private static IntPtr RenderPage(IntPtr page)
    {
        if (page == IntPtr.Zero)
            return IntPtr.Zero;
        var box = CGPDFPageGetBoxRect(page, 1 /* crop box */);
        if (box.Width <= 0 || box.Height <= 0)
            return IntPtr.Zero;
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

    /// <summary>Draws on white into a grey bitmap and returns it as an image (to be released).</summary>
    private static IntPtr Draw(int width, int height, Action<IntPtr> draw)
    {
        var space = CGColorSpaceCreateDeviceGray();
        var context = CGBitmapContextCreate(IntPtr.Zero, (nuint)width, (nuint)height, 8, 0, space, 0);
        CGColorSpaceRelease(space);
        if (context == IntPtr.Zero)
            return IntPtr.Zero;
        try
        {
            CGContextSetGrayFillColor(context, 1, 1);
            CGContextFillRect(context, new CGRect(0, 0, width, height));
            draw(context);
            return CGBitmapContextCreateImage(context);
        }
        finally
        {
            CGContextRelease(context);
        }
    }

    private static IntPtr FileUrl(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        return CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
    }

    // ── Native ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    /// <summary>For -[VNObservation boundingBox]; arm64 returns the rectangle in registers.</summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect SendReturnRect(IntPtr receiver, IntPtr selector);

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

    [DllImport(CoreGraphics)] private static extern IntPtr CGBitmapContextCreateImage(IntPtr context);
    [DllImport(CoreGraphics)] private static extern void CGImageRelease(IntPtr image);
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
