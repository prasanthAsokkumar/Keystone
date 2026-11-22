using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// Filing many documents at once: each file gets its details filled in by the local AI, one after
/// another, while the ones already read can be checked and saved.
/// </summary>
public partial class MainViewModel
{
    private CancellationTokenSource? _bulkCancellation;
    private bool _isBulkQueueRunning;

    public ObservableCollection<BulkDocumentItem> BulkItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsBulkPanelOpen { get; set; }

    [ObservableProperty]
    public partial string BulkStatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBulkBusy { get; set; }

    /// <summary>Share of the files read so far, 0 to 100.</summary>
    [ObservableProperty]
    public partial double BulkProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBulkError))]
    public partial string? BulkError { get; set; }

    public bool HasBulkError => !string.IsNullOrEmpty(BulkError);

    public string BulkTitle => Loc.Plural(BulkItems.Count, "Import {0} document", "Import {0} documents");

    public string BulkSaveText => Loc.Plural(SavableBulkItems.Count(), "Save {0} document", "Save {0} documents");

    public bool CanSaveBulk => SavableBulkItems.Any();

    /// <summary>The ticked files that aren't still being read.</summary>
    private IEnumerable<BulkDocumentItem> SavableBulkItems => BulkItems.Where(i => i.IsSelected && !i.IsBusy);

    /// <summary>Opens the import list for several files, or adds them to it, and starts reading them.</summary>
    /// <param name="fromInbox">The files come from the inbox: once filed, they leave it.</param>
    public async Task StartBulkDocumentsAsync(IEnumerable<string> paths, bool fromInbox = false)
    {
        if (!IsBulkPanelOpen)
        {
            CloseOtherPanels();
            ClearBulkItems();
            IsBulkPanelOpen = true;
        }

        var person = DocumentPersonFilter is { IsEveryone: false } filter ? filter : PersonOption.Shared;
        foreach (var path in paths.Where(p => BulkItems.All(i => i.SourcePath != p)))
        {
            var item = new BulkDocumentItem(path, person, IsImportEnabled && DocumentReader.IsSupported(path),
                QrBillScanner.CanScan(path)) { FromInbox = fromInbox };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(BulkDocumentItem.IsSelected) or nameof(BulkDocumentItem.Status))
                    RefreshBulkTexts();
            };
            BulkItems.Add(item);
            try
            {
                item.Hash = await Task.Run(() => DuplicateFinder.Hash(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            CheckBulkDuplicate(item);
        }

        RefreshBulkTexts();
        await ProcessBulkQueueAsync();
    }

    /// <summary>Reads the waiting files one at a time; the local model handles one document at once anyway.</summary>
    private async Task ProcessBulkQueueAsync()
    {
        if (_isBulkQueueRunning)
            return;

        _isBulkQueueRunning = true;
        var cancellation = _bulkCancellation ??= new CancellationTokenSource();
        try
        {
            while (!cancellation.IsCancellationRequested &&
                   BulkItems.FirstOrDefault(i => i.Status == BulkStatus.Waiting) is { } item)
            {
                item.Status = BulkStatus.Reading;
                RefreshBulkTexts();
                try
                {
                    // The QR code first: exact, and quick even without the AI.
                    if (item.QrWanted)
                    {
                        var scan = ScanQrBill;
                        var path = item.SourcePath;
                        if (await Task.Run(() => scan(path)) is { } bill)
                            item.ApplyQrBill(bill);
                        CheckBulkDuplicate(item);
                    }
                    if (item.AiWanted)
                    {
                        var content = await DocumentReader.ReadAsync(item.SourcePath);
                        var details = await _ai.ExtractDetailsAsync(content, AiEndpoint, AiModel, Today, HouseholdNames, cancellation.Token);
                        if (cancellation.IsCancellationRequested)
                            break;
                        item.Apply(details, MatchPersonOption(details.Person));
                        CheckBulkDuplicate(item);
                        if (details.FollowUp is { } followUp)
                        {
                            item.FollowUp = followUp;
                            item.FollowUpText = FollowUpText(item.ToDocument(Today), followUp);
                        }
                    }
                    item.Status = BulkStatus.Ready;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    item.Error = ex.Message;
                    item.Status = BulkStatus.Failed;
                }
            }
        }
        finally
        {
            _isBulkQueueRunning = false;
            RefreshBulkTexts();
        }

        // Files added after the list was closed and opened again while this was stopping.
        if (IsBulkPanelOpen && BulkItems.Any(i => i.Status == BulkStatus.Waiting))
            await ProcessBulkQueueAsync();
    }

    /// <summary>
    /// Marks a file already filed (or in the list twice) and unticks it, so saving all doesn't file copies.
    /// It can still be ticked to file it anyway.
    /// </summary>
    private void CheckBulkDuplicate(BulkDocumentItem item)
    {
        var found = DuplicateFinder.Find(Documents, item.Hash, item.Sender, item.Amount,
            item.Date is { } date ? DateOnly.FromDateTime(date) : null, item.PaymentReference);
        var text = found is null ? null : DuplicateText(found);
        var sameFile = found?.Match == DuplicateMatch.SameFile;
        if (found is null && item.Hash != "" && BulkItems.TakeWhile(i => i != item).Any(i => i.Hash == item.Hash))
        {
            text = Loc.T("This file is in the list twice.");
            sameFile = true;
        }
        if (text == item.DuplicateText)
            return;

        var first = item.DuplicateText is null;
        item.DuplicateText = text;
        item.IsSameFile = sameFile;
        if (text is not null && first)
        {
            item.IsSelected = false;
            if (found?.Document.TransactionId is not null)
                item.CreateEntry = false;
        }
    }

    private void RefreshBulkTexts()
    {
        var reading = BulkItems.Where(i => i.AiWanted || i.QrWanted).ToList();
        var done = reading.Count(i => !i.IsBusy);
        IsBulkBusy = BulkItems.Any(i => i.IsBusy);
        BulkProgress = reading.Count == 0 ? 100 : 100.0 * done / reading.Count;
        BulkStatusText = IsBulkBusy
            ? Loc.F("Reading {0} of {1} with {2}. Files already read can be checked and saved.", done + 1, reading.Count, AiModel)
            : BulkItems.Any(i => i.AiWanted)
                ? Loc.T("All files read. Check the details, then save.")
                : IsImportEnabled
                    ? Loc.T("These files can’t be read by the AI. Fill in the details, then save.")
                    : Loc.T("Local AI is off, so fill in the details yourself. Turn it on in Settings to have them filled in.");
        OnPropertyChanged(nameof(BulkTitle));
        OnPropertyChanged(nameof(BulkSaveText));
        OnPropertyChanged(nameof(CanSaveBulk));
    }

    [RelayCommand]
    private void RemoveBulkItem(BulkDocumentItem item)
    {
        BulkItems.Remove(item);
        if (BulkItems.Count == 0)
            CloseBulkPanel();
        else
            RefreshBulkTexts();
    }

    /// <summary>Files every ticked document that has been read. Ones still being read stay in the list.</summary>
    [RelayCommand]
    private async Task SaveBulkAsync()
    {
        BulkError = null;
        var failed = 0;
        var followed = new List<string>();
        foreach (var item in SavableBulkItems.ToList())
        {
            item.Error = null;
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                item.Error = Loc.T("Enter a title.");
                failed++;
                continue;
            }
            if (item.CanCreateEntry && item.CreateEntry && item.Amount is not > 0)
            {
                item.Error = Loc.T("Enter the amount to pay, or untick “Add to expenses”.");
                failed++;
                continue;
            }

            var document = item.ToDocument(Today);
            document.FileHash = item.Hash;
            try
            {
                var personName = item.Person.Id is null ? null : item.Person.Name;
                document.RelativePath = await Task.Run(() => _documentStore.Add(item.SourcePath, document, personName));
                if (item.CanCreateEntry && item.CreateEntry)
                    await AddBillEntryAsync(document);
                await repository.AddDocumentAsync(document);
            }
            catch (Exception ex)
            {
                item.Error = Loc.F("Could not save: {0}", ex.GetBaseException().Message);
                failed++;
                continue;
            }

            Documents.Add(document);
            BulkItems.Remove(item);
            _ = IndexDocumentAsync(document);
            if (item is { FollowUp: { } followUp, AddFollowUp: true })
            {
                try
                {
                    if (await ApplyFollowUpAsync(document, followUp) is { } name)
                        followed.Add(name);
                }
                catch (Exception)
                {
                    // The document is filed; the record can still be added by hand.
                }
            }
            if (item.FromInbox)
            {
                try
                {
                    File.Delete(item.SourcePath); // filed now, so it leaves the inbox
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        // Scans already filed before don't stay in the inbox to be offered again; they go to Deleted.
        var discarded = 0;
        foreach (var copy in BulkItems.Where(i => i is { FromInbox: true, IsSameFile: true, IsSelected: false, IsBusy: false }).ToList())
        {
            try
            {
                _documentStore.Discard(copy.SourcePath);
                BulkItems.Remove(copy);
                discarded++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (BulkItems.All(i => !i.FromInbox))
            CheckInbox();

        var notes = new List<string>();
        if (followed.Count > 0)
            notes.Add(Loc.F("Also added to Insurance or Contracts & dates: {0}.", string.Join(", ", followed)));
        if (discarded > 0)
            notes.Add(Loc.Plural(discarded, "{0} scan already filed was taken out of the inbox.", "{0} scans already filed were taken out of the inbox."));
        if (notes.Count > 0)
            ShowNotice(string.Join(" ", notes));

        RefreshDocumentRows();
        RefreshBulkTexts();
        if (failed > 0)
            BulkError = Loc.Plural(failed, "{0} document wasn’t saved. See the note on it.", "{0} documents weren’t saved. See the notes on them.");
        else if (BulkItems.Count == 0)
        {
            CloseBulkPanel();
            CurrentPage = AppPage.Documents;
        }
    }

    private void CloseBulkPanel()
    {
        IsBulkPanelOpen = false;
        ClearBulkItems();
    }

    private void ClearBulkItems()
    {
        _bulkCancellation?.Cancel();
        _bulkCancellation = null;
        BulkItems.Clear();
        BulkError = null;
        RefreshBulkTexts();
    }
}

public enum BulkStatus
{
    Waiting,
    Reading,
    Ready,
    Failed,
}

/// <summary>One file in the bulk import list, with the details it will be filed with.</summary>
public sealed partial class BulkDocumentItem : ObservableObject
{
    public BulkDocumentItem(string sourcePath, PersonOption person, bool aiWanted, bool qrWanted = false)
    {
        SourcePath = sourcePath;
        AiWanted = aiWanted;
        QrWanted = qrWanted;
        Title = Path.GetFileNameWithoutExtension(sourcePath);
        Person = person;
        // Unknown until the QR code or the AI says otherwise; "Other" needs no amount to be saved.
        KindIndex = (int)DocumentKind.Other;
        Status = aiWanted || qrWanted ? BulkStatus.Waiting : BulkStatus.Ready;
    }

    /// <summary>Whether the file is searched for a QR-bill.</summary>
    public bool QrWanted { get; }

    /// <summary>The file's fingerprint, to notice it was filed before.</summary>
    public string Hash { get; set; } = "";

    /// <summary>Why this looks filed already, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicate))]
    public partial string? DuplicateText { get; set; }

    public bool HasDuplicate => DuplicateText is not null;

    /// <summary>Byte for byte a file already filed (or listed twice), not just a similar one.</summary>
    public bool IsSameFile { get; set; }

    /// <summary>Insurance, contract, date or warranty details the AI found in the document.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFollowUp))]
    public partial FollowUp? FollowUp { get; set; }

    public bool HasFollowUp => FollowUp is not null;

    [ObservableProperty]
    public partial string FollowUpText { get; set; } = "";

    [ObservableProperty]
    public partial bool AddFollowUp { get; set; } = true;

    /// <summary>The file came from the inbox and leaves it once filed.</summary>
    public bool FromInbox { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaymentDetails))]
    public partial string PaymentIban { get; set; } = "";

    [ObservableProperty]
    public partial string PaymentReference { get; set; } = "";

    public bool HasPaymentDetails => PaymentIban != "";

    /// <summary>Takes the exact payment details from the bill's QR code.</summary>
    public void ApplyQrBill(QrBill bill)
    {
        KindIndex = (int)DocumentKind.Bill;
        _hasQrBill = true;
        if (Sender == "")
            Sender = bill.Creditor;
        Amount = bill.Amount;
        if (bill.InvoiceDate is { } invoiceDate)
            Date = invoiceDate.ToDateTime(TimeOnly.MinValue);
        if (bill.DueDate is { } due)
            DueDate = due.ToDateTime(TimeOnly.MinValue);
        PaymentIban = bill.FormattedIban;
        PaymentReference = bill.FormattedReference;
    }

    private bool _hasQrBill;

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>Whether the local AI reads this file.</summary>
    public bool AiWanted { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsEditable), nameof(IsReading), nameof(StatusText), nameof(HasStatus),
        nameof(IsFailed))]
    public partial BulkStatus Status { get; set; }

    public bool IsBusy => Status is BulkStatus.Waiting or BulkStatus.Reading;

    public bool IsEditable => !IsBusy;

    public bool IsReading => Status == BulkStatus.Reading;

    public bool IsFailed => Status == BulkStatus.Failed || Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatus), nameof(IsFailed))]
    public partial string? Error { get; set; }

    public string StatusText => Error is { } error && Status != BulkStatus.Failed
        ? error
        : Status switch
        {
            BulkStatus.Waiting => Loc.T("Waiting…"),
            BulkStatus.Reading => Loc.T("Reading…"),
            BulkStatus.Failed => Loc.F("Couldn’t fill in the details automatically: {0}", Error),
            _ when _hasQrBill => Loc.T("Payment details from the QR code. Check before saving."),
            _ => AiWanted ? Loc.T("Filled in by AI. Check before saving.") : "",
        };

    public bool HasStatus => StatusText != "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPayable), nameof(CanCreateEntry))]
    public partial int KindIndex { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Sender { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption Person { get; set; }

    [ObservableProperty]
    public partial DateTime? Date { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial decimal? Amount { get; set; }

    [ObservableProperty]
    public partial DateTime? DueDate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateEntry))]
    public partial bool IsPaid { get; set; }

    [ObservableProperty]
    public partial bool CreateEntry { get; set; } = true;

    private DocumentKind Kind => Enum.IsDefined((DocumentKind)KindIndex) ? (DocumentKind)KindIndex : DocumentKind.Other;

    public bool IsPayable => Kind is DocumentKind.Bill or DocumentKind.Invoice;

    public bool CanCreateEntry => IsPayable && !IsPaid;

    /// <summary>Takes what the model read; the fields can't be edited while it reads.</summary>
    public void Apply(DocumentDetails details, PersonOption? person)
    {
        // The QR code's payment details are exact; the AI only adds what it doesn't give.
        if (details.Kind is { } kind && !_hasQrBill)
            KindIndex = (int)kind;
        if (details.Title != "")
            Title = details.Title;
        if (details.Sender != "")
            Sender = details.Sender;
        if (person is not null)
            Person = person;
        if (details.Date is { } date)
            Date = date.ToDateTime(TimeOnly.MinValue);
        if (!_hasQrBill || Amount is null)
            Amount = details.Amount;
        if (DueDate is null)
            DueDate = details.DueDate?.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>Texts made in code follow a language change.</summary>
    public void RefreshTexts() => OnPropertyChanged(nameof(StatusText));

    public HouseholdDocument ToDocument(DateOnly today) => new()
    {
        Kind = Kind,
        Title = Title.Trim(),
        Sender = Sender.Trim(),
        PersonId = Person.Id,
        Date = DateOnly.FromDateTime(Date ?? DateTime.Today),
        Amount = IsPayable && Amount is > 0 ? Amount : null,
        DueDate = IsPayable && DueDate is { } due ? DateOnly.FromDateTime(due) : null,
        PaidOn = IsPayable && IsPaid ? today : null,
        PaymentIban = IsPayable ? PaymentIban.Replace(" ", "") : "",
        PaymentReference = IsPayable ? PaymentReference : "",
        AddedAt = DateTime.Now,
    };
}
