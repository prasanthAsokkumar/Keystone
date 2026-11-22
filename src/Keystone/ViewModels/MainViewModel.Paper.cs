using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.Services.Mac;

namespace Keystone.ViewModels;

/// <summary>
/// Getting paper in: QR-bills read exactly, the text of documents kept for searching, the inbox for phone
/// scans, and how long to keep each document.
/// </summary>
public partial class MainViewModel
{
    // ── QR-bills ────────────────────────────────────────────────────────────

    /// <summary>Finds the QR-bill in a file; replaceable in tests.</summary>
    public Func<string, QrBill?> ScanQrBill { get; set; } = QrBillScanner.Scan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaymentDetails))]
    public partial string DocumentPaymentIban { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaymentDetails))]
    public partial string DocumentPaymentReference { get; set; } = "";

    public bool HasPaymentDetails => DocumentPaymentIban != "" || DocumentPaymentReference != "";

    /// <summary>Fills the document form from the bill's QR code: exact amount, payee, account and reference.</summary>
    private void ApplyQrBill(QrBill bill)
    {
        DocumentKindIndex = (int)DocumentKind.Bill;
        if (DocumentSender == "")
            DocumentSender = bill.Creditor;
        if (bill.Amount is { } amount)
            DocumentAmount = amount;
        if (bill.InvoiceDate is { } invoiceDate)
            DocumentDate = invoiceDate.ToDateTime(TimeOnly.MinValue);
        if (bill.DueDate is { } due)
            DocumentDueDate = due.ToDateTime(TimeOnly.MinValue);
        if (DocumentNotes == "" && bill.Message != "")
            DocumentNotes = bill.Message;
        DocumentPaymentIban = bill.FormattedIban;
        DocumentPaymentReference = bill.FormattedReference;
    }

    /// <summary>Reads the QR-bill of a file being filed; true when there was one.</summary>
    private async Task<bool> ReadQrBillAsync(string path)
    {
        if (!QrBillScanner.CanScan(path))
            return false;
        var scan = ScanQrBill;
        var bill = await Task.Run(() => scan(path));
        if (bill is null)
            return false;
        ApplyQrBill(bill);
        DocumentAiStatus = Loc.T("Amount, account and reference read from the QR code.");
        return true;
    }

    [RelayCommand]
    private async Task CopyPaymentTextAsync(string? text)
    {
        if (CopyToClipboard is not null && !string.IsNullOrWhiteSpace(text))
            await CopyToClipboard(text.Replace(" ", ""));
    }

    /// <summary>Amount, account, reference and payee in one go, for pasting into e-banking notes.</summary>
    [RelayCommand]
    private async Task CopyAllPaymentDetailsAsync()
    {
        if (CopyToClipboard is null)
            return;
        var lines = new[]
        {
            DocumentSender,
            DocumentPaymentIban,
            DocumentPaymentReference,
            DocumentAmount is { } amount ? amount.ToString("N2", Loc.Instance.Culture) : "",
        };
        await CopyToClipboard(string.Join("\n", lines.Where(l => l != "")));
    }

    // ── Text inside documents ───────────────────────────────────────────────

    private Dictionary<int, string> _documentTexts = [];
    private bool _indexing;

    /// <summary>The words found inside a document, if its text has been read.</summary>
    public string? DocumentContent(int documentId) => _documentTexts.TryGetValue(documentId, out var text) ? text : null;

    private async Task LoadDocumentTextsAsync()
    {
        _documentTexts = await repository.GetDocumentTextsAsync();
        _ = IndexDocumentsAsync();
    }

    /// <summary>Reads the text of documents that haven't been read yet, one at a time in the background.</summary>
    private async Task IndexDocumentsAsync()
    {
        if (_indexing)
            return;
        _indexing = true;
        try
        {
            foreach (var document in Documents.Where(d => !_documentTexts.ContainsKey(d.Id)).ToList())
                await IndexDocumentAsync(document);
        }
        finally
        {
            _indexing = false;
        }
    }

    /// <summary>Reads and keeps one document's text. PDFs with text and text files; photos and scans have none.</summary>
    private async Task IndexDocumentAsync(HouseholdDocument document)
    {
        if (document.Id == 0 || !_documentStore.Exists(document))
            return;
        var path = _documentStore.FullPath(document.RelativePath);
        if (!DocumentReader.IsSupported(path) || DocumentReader.IsImage(path))
            return;

        string text;
        try
        {
            text = (await DocumentReader.ReadAsync(path)).Text ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            text = ""; // e.g. a scan without text: remembered as empty so it isn't read again
        }

        try
        {
            await repository.SaveDocumentTextAsync(document.Id, text);
            _documentTexts[document.Id] = text;
        }
        catch (Exception)
        {
            // Searching inside it is a convenience; the document itself is saved.
        }
    }

    /// <summary>A short passage around the first match, for search results.</summary>
    internal static string Snippet(string text, string query, int around = 40)
    {
        var at = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0)
            return "";
        var start = Math.Max(0, at - around);
        var end = Math.Min(text.Length, at + query.Length + around);
        var snippet = string.Join(" ", text[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : "");
    }

    // ── Inbox for phone scans ───────────────────────────────────────────────

    public const string InboxFolderName = "Inbox";

    private static readonly TimeSpan InboxCheckInterval = TimeSpan.FromMinutes(2);
    private bool _inboxLoopStarted;

    /// <summary>Scans saved here, e.g. with the OneDrive app on a phone, are offered for filing.</summary>
    public string InboxFolder => Path.Combine(DocumentsFolder, InboxFolderName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInboxFiles), nameof(InboxText))]
    public partial IReadOnlyList<string> InboxFiles { get; set; } = [];

    public bool HasInboxFiles => InboxFiles.Count > 0;

    public string InboxText => Loc.Plural(InboxFiles.Count, "{0} new scan in your inbox", "{0} new scans in your inbox");

    public string InboxNote => Loc.F("Scan letters with your phone into {0}; Keystone offers to file them here.", InboxFolder);

    /// <summary>Files in the inbox that have finished arriving: not hidden, not empty, untouched for a few seconds.</summary>
    public static IReadOnlyList<string> ReadyInboxFiles(string folder, DateTime now)
    {
        if (!Directory.Exists(folder))
            return [];
        try
        {
            return Directory.EnumerateFiles(folder)
                .Select(p => new FileInfo(p))
                .Where(f => !f.Name.StartsWith('.') && !f.Name.StartsWith("~$", StringComparison.Ordinal) &&
                            f.Extension.ToLowerInvariant() is not (".tmp" or ".part" or ".partial" or ".download" or ".crdownload") &&
                            f.Length > 0 && now - f.LastWriteTime > TimeSpan.FromSeconds(5))
                .OrderBy(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void CheckInbox()
    {
        try
        {
            Directory.CreateDirectory(InboxFolder); // so it shows up in the OneDrive app to scan into
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        // Files already in the import list aren't offered again.
        InboxFiles = ReadyInboxFiles(InboxFolder, DateTime.Now).Where(p => BulkItems.All(i => i.SourcePath != p)).ToList();
        OnPropertyChanged(nameof(InboxNote));
    }

    private void StartInbox()
    {
        CheckInbox();
        if (_inboxLoopStarted)
            return;
        _inboxLoopStarted = true;
        _ = InboxLoopAsync();
    }

    private async Task InboxLoopAsync()
    {
        using var timer = new PeriodicTimer(InboxCheckInterval);
        while (await timer.WaitForNextTickAsync())
            CheckInbox();
    }

    /// <summary>Opens the import list with the inbox's scans; saved ones leave the inbox.</summary>
    [RelayCommand]
    private async Task FileInboxAsync()
    {
        CheckInbox();
        if (InboxFiles.Count == 0)
            return;
        var files = InboxFiles;
        CurrentPage = AppPage.Documents;
        await StartBulkDocumentsAsync(files, fromInbox: true);
        CheckInbox();
    }

    [RelayCommand]
    private void OpenInboxFolder()
    {
        try
        {
            Directory.CreateDirectory(InboxFolder);
            System.Diagnostics.Process.Start("open", $"\"{InboxFolder}\"");
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not open the folder: {0}", ex.Message);
        }
    }

    // ── Keeping documents ───────────────────────────────────────────────────

    /// <summary>For the document being edited: how long to keep it, and why.</summary>
    public string DocumentRetentionText
    {
        get
        {
            if (EditingDocument is not { } document)
                return "";
            return RetentionGuide.KeepUntil(document) is { } until
                ? Loc.F("Keep until {0}. {1}.", Format.Date(until), RetentionGuide.Reason(document))
                : RetentionGuide.Reason(document) + ".";
        }
    }

    public int DocumentsToThrowAway => Documents.Count(d => RetentionGuide.CanBeThrownAway(d, Today));
}
