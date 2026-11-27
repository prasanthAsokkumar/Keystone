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

namespace Keystone.ViewModels;

/// <summary>The Documents page: bills, letters and notices filed per household member.</summary>
public partial class MainViewModel
{
    /// <summary>Documents to link to a contract, policy or warranty: the kinds that fit, newest first.</summary>
    internal IReadOnlyList<DocumentOption> DocumentOptionsFor(int? selectedId, params DocumentKind[] kinds) =>
    [
        new DocumentOption(null, ""),
        .. Documents
            .Where(d => kinds.Contains(d.Kind) || d.Id == selectedId)
            .OrderByDescending(d => d.Date)
            .Select(DocumentOptionOf),
    ];

    internal DocumentOption DocumentOptionOf(HouseholdDocument document) => new(document,
        document.Sender == "" ? document.Title : $"{document.Sender}: {document.Title}",
        document.PersonId is null ? Loc.T(PersonOption.SharedName) : PersonName(document.PersonId), Format.Date(document.Date));

    [RelayCommand]
    private void OpenLinkedDocument(HouseholdDocument document) => OpenDocument(document);

    internal DocumentStore _documentStore = new(DocumentStore.DefaultRootFolder());
    private CancellationTokenSource? _detailsCancellation;

    public ObservableCollection<HouseholdDocument> Documents { get; } = [];

    public IReadOnlyList<string> DocumentKindNames =>
        Enum.GetValues<DocumentKind>().Select(HouseholdDocument.KindName).ToList();

    public IReadOnlyList<string> DocumentKindFilters =>
        [Loc.T("All types"), .. Enum.GetValues<DocumentKind>().Select(HouseholdDocument.KindName)];

    public IReadOnlyList<string> DocumentStatusFilters => Translate("Any status", "To pay", "Paid", "Past keeping time");

    /// <summary>Insurance or tax papers: the Insurance and Taxes pages link here instead of listing them again.</summary>
    public IReadOnlyList<string> DocumentTopicFilters => Translate("Any topic", "Insurance", "Taxes");

    [ObservableProperty]
    public partial int DocumentTopicFilterIndex { get; set; }

    partial void OnDocumentTopicFilterIndexChanged(int value) => RefreshDocumentRows();

    /// <summary>Opens Documents on one topic's papers, e.g. from the Insurance page.</summary>
    [RelayCommand]
    private void ShowPapers(string topic)
    {
        DocumentSearch = "";
        DocumentPersonFilter = PersonOption.Everyone;
        DocumentKindFilterIndex = 0;
        DocumentStatusFilterIndex = 0;
        DocumentTopicFilterIndex = topic == "taxes" ? 2 : 1;
        CurrentPage = AppPage.Documents;
    }

    // ── List and filters ────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string DocumentSearch { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption DocumentPersonFilter { get; set; } = PersonOption.Everyone;

    [ObservableProperty]
    public partial int DocumentKindFilterIndex { get; set; }

    [ObservableProperty]
    public partial int DocumentStatusFilterIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentRows), nameof(DocumentsEmptyText))]
    public partial IReadOnlyList<DocumentRow> DocumentRows { get; set; } = [];

    public bool HasDocumentRows => DocumentRows.Count > 0;

    public string DocumentsEmptyText => Documents.Count == 0
        ? Loc.T("No documents yet. Drop a bill, letter or notice here, or click Add document.")
        : Loc.T("No documents match these filters.");

    public string DocumentsFolderNote => Loc.F("Files are kept in {0}", DocumentsFolder);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenBills))]
    public partial string OpenBillsText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasOverdueBills { get; set; }

    public bool HasOpenBills => OpenBillsText != "";

    partial void OnDocumentSearchChanged(string value) => RefreshDocumentRows();

    partial void OnDocumentPersonFilterChanged(PersonOption value) => RefreshDocumentRows();

    partial void OnDocumentKindFilterIndexChanged(int value) => RefreshDocumentRows();

    partial void OnDocumentStatusFilterIndexChanged(int value) => RefreshDocumentRows();

    [RelayCommand]
    private void ShowOpenBills()
    {
        DocumentSearch = "";
        DocumentKindFilterIndex = 0;
        DocumentPersonFilter = PersonOption.Everyone;
        DocumentStatusFilterIndex = 1;
        CurrentPage = AppPage.Documents;
    }

    internal void RefreshDocumentRows()
    {
        RefreshOwnPageLists();
        var names = People.ToDictionary(p => p.Id, p => p.Name);
        var search = DocumentSearch.Trim();
        var person = DocumentPersonFilter ?? PersonOption.Everyone;

        DocumentRows = Sorted("documents", Documents
            .Where(d => person.IsEveryone || d.PersonId == person.Id)
            .Where(d => DocumentKindFilterIndex <= 0 || (int)d.Kind == DocumentKindFilterIndex - 1)
            .Where(d => DocumentTopicFilterIndex switch
            {
                1 => IsInsuranceDocument(d),
                2 => IsTaxDocument(d),
                _ => true,
            })
            .Where(d => DocumentStatusFilterIndex switch
            {
                1 => d.IsOpenBill,
                2 => d.IsPayable && d.IsPaid,
                3 => RetentionGuide.CanBeThrownAway(d, Today),
                _ => true,
            })
            .Where(d => search == "" ||
                        d.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        d.Sender.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        d.Notes.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        DocumentContent(d.Id)?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true)
            .OrderBy(d => d.IsOpenBill ? 0 : 1)
            .ThenBy(d => d.IsOpenBill ? d.NextDueDate ?? DateOnly.MaxValue : DateOnly.MaxValue)
            .ThenByDescending(d => d.Date)
            .ThenByDescending(d => d.Id)
            .Select(d => new DocumentRow(
                d, d.PersonId is { } id && names.TryGetValue(id, out var name) ? name : Loc.T(DocumentStore.HouseholdFolder),
                _documentStore.Exists(d), Today))
            .ToList(), null, DocumentSortKeys);

        ScheduleReminders();
        var open = Documents.Where(d => d.IsOpenBill).ToList();
        var overdue = open.Count(d => d.IsOverdue(Today));
        HasOverdueBills = overdue > 0;
        OpenBillsText = open.Count == 0
            ? ""
            : Loc.Plural(open.Count, "{0} bill to pay · {1}", "{0} bills to pay · {1}", Format.Money(open.Sum(d => d.AmountLeft))) +
              (overdue > 0 ? Loc.F(" · {0} overdue", overdue) : "");
    }

    private async Task LoadDocumentsAsync(AppSettings settings)
    {
        await MoveLegacyDocumentsFolderAsync(settings);
        SetDocumentsFolder(settings.DocumentsFolder);
        Documents.Clear();
        foreach (var document in await repository.GetDocumentsAsync())
            Documents.Add(document);
        RefreshDocumentRows();
        await LoadDocumentTextsAsync();
    }

    // ── Documents folder ────────────────────────────────────────────────────

    /// <summary>
    /// Earlier versions kept documents in OneDrive/&lt;a folder&gt;/KeystoneApp (RunwayApp before the app was renamed)
    /// without saving that as a choice. Such a folder is kept in use and saved as the chosen folder, so a household
    /// that started then never loses its documents to the new default. A RunwayApp folder is renamed to KeystoneApp;
    /// if that isn't possible it stays in use, so documents are never split.
    /// </summary>
    private async Task MoveLegacyDocumentsFolderAsync(AppSettings settings)
    {
        var configured = settings.DocumentsFolder;
        string? folder;
        if (configured != "")
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            if (Path.GetFileName(full) != DocumentStore.LegacyFolderName)
                return; // a folder the user picked; leave it alone
            folder = full;
        }
        else
        {
            folder = await Task.Run(DocumentStore.FindEarlierFolder);
            if (folder is null)
                return; // a new household: the default folder
        }

        string? problem = null;
        if (Path.GetFileName(folder) == DocumentStore.LegacyFolderName)
        {
            var renamed = Path.Combine(Path.GetDirectoryName(folder)!, DocumentStore.EarlierFolderName);
            (folder, problem) = await Task.Run(() => DocumentStore.MoveLegacyFolder(renamed));
        }
        if (folder != configured)
        {
            try
            {
                await repository.SetDocumentsFolderAsync(folder);
                settings.DocumentsFolder = folder;
            }
            catch (Exception ex)
            {
                ErrorMessage = Loc.F("Could not save the documents folder: {0}", ex.Message);
            }
        }
        if (problem is not null)
            ErrorMessage = Loc.F("Couldn’t rename the documents folder to {0}, so {1} is still used: {2}",
                DocumentStore.EarlierFolderName, DocumentStore.LegacyFolderName, problem);
    }

    [ObservableProperty]
    public partial string DocumentsFolder { get; set; } = "";

    [ObservableProperty]
    public partial bool IsDefaultDocumentsFolder { get; set; } = true;

    private void SetDocumentsFolder(string configured)
    {
        IsDefaultDocumentsFolder = string.IsNullOrWhiteSpace(configured);
        _documentStore = new DocumentStore(IsDefaultDocumentsFolder ? DocumentStore.DefaultRootFolder() : configured);
        DocumentsFolder = _documentStore.RootFolder;
        OnPropertyChanged(nameof(DocumentsFolderNote));
        OnPropertyChanged(nameof(DropDetailText));
        if (!_isLoading)
            Backup.RefreshBackupStatus();
    }

    /// <summary>
    /// Points Keystone at another folder. Files already filed stay where they are, so this is for
    /// when the folder itself moved, e.g. to another OneDrive.
    /// </summary>
    public async Task ChangeDocumentsFolderAsync(string? folder)
    {
        try
        {
            await repository.SetDocumentsFolderAsync(folder ?? "");
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save the documents folder: {0}", ex.Message);
            return;
        }

        SetDocumentsFolder(folder ?? "");
        RefreshDocumentRows();
    }

    [RelayCommand]
    private Task ResetDocumentsFolderAsync() => ChangeDocumentsFolderAsync(null);

    [RelayCommand]
    private void OpenDocumentsFolder()
    {
        try
        {
            _documentStore.OpenRoot();
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not open the documents folder: {0}", ex.Message);
        }
    }

    // ── Document panel ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsDocumentPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingDocument), nameof(DocumentFormTitle), nameof(DocumentSubmitText),
        nameof(CanCreateDocumentEntry))]
    public partial HouseholdDocument? EditingDocument { get; set; }

    public bool IsEditingDocument => EditingDocument is not null;

    public string DocumentFormTitle => Loc.T(IsEditingDocument ? "Edit document" : "Add document");

    public string DocumentSubmitText => Loc.T(IsEditingDocument ? "Save changes" : HasDocumentDuplicate ? "File anyway" : "Save to documents");

    /// <summary>The file being added; only set for a new document.</summary>
    private string? _documentSourcePath;

    [ObservableProperty]
    public partial string DocumentFileName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentIsPayable), nameof(CanCreateDocumentEntry))]
    public partial int DocumentKindIndex { get; set; }

    [ObservableProperty]
    public partial string DocumentTitle { get; set; } = "";

    [ObservableProperty]
    public partial string DocumentSender { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption DocumentPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial DateTime? DocumentDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateDocumentEntry))]
    public partial decimal? DocumentAmount { get; set; }

    [ObservableProperty]
    public partial DateTime? DocumentDueDate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateDocumentEntry), nameof(CanPayDocumentInInstalments))]
    public partial bool DocumentIsPaid { get; set; }

    [ObservableProperty]
    public partial string DocumentNotes { get; set; } = "";

    /// <summary>Also add the bill as a one-time expense on its due date, so the runway includes it.</summary>
    [ObservableProperty]
    public partial bool DocumentCreateEntry { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentError))]
    public partial string? DocumentError { get; set; }

    public bool HasDocumentError => !string.IsNullOrEmpty(DocumentError);

    /// <summary>Progress or outcome of the local AI filling in the form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentAiStatus))]
    public partial string DocumentAiStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsDocumentAiBusy { get; set; }

    public bool HasDocumentAiStatus => DocumentAiStatus != "";

    private DocumentKind SelectedDocumentKind =>
        DocumentKindIndex >= 0 && DocumentKindIndex < DocumentKindNames.Count ? (DocumentKind)DocumentKindIndex : DocumentKind.Other;

    public bool DocumentIsPayable => SelectedDocumentKind is DocumentKind.Bill or DocumentKind.Invoice;

    public bool CanCreateDocumentEntry =>
        DocumentIsPayable && !DocumentIsPaid && EditingDocument?.TransactionId is null && !HasInstalmentEntries(EditingDocument);

    /// <summary>Opens the panel to file a dropped or picked file, and lets the local AI fill in what it can.</summary>
    public async Task StartNewDocumentAsync(string path)
    {
        CloseOtherPanels();
        ResetDocumentForm();
        _documentSourcePath = path;
        DocumentFileName = Path.GetFileName(path);
        DocumentTitle = Path.GetFileNameWithoutExtension(path);
        DocumentPerson = DocumentPersonFilter is { IsEveryone: false } person ? person : PersonOption.Shared;
        IsDocumentPanelOpen = true;
        await CheckDocumentFileAsync(path);

        // The QR code gives exact payment details; the AI fills in the rest.
        var hasQrBill = await ReadQrBillAsync(path);
        RefreshDocumentDuplicate();
        if (IsImportEnabled && DocumentReader.IsSupported(path))
            await FillDocumentDetailsAsync(path, hasQrBill);
    }

    private async Task FillDocumentDetailsAsync(string path, bool hasQrBill = false)
    {
        var cancellation = _detailsCancellation = new CancellationTokenSource();
        var fileTitle = DocumentTitle;
        var personBefore = DocumentPerson;
        IsDocumentAiBusy = true;
        DocumentAiStatus = Loc.F("Filling in the details with {0}. You can type while it works…", AiModel);
        try
        {
            var content = await DocumentReader.ReadAsync(path);
            var details = await _ai.ExtractDetailsAsync(content, AiEndpoint, AiModel, Today, HouseholdNames, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            // Only fill fields the user hasn't changed in the meantime.
            if (details.Kind is { } kind && DocumentKindIndex == 0 && !hasQrBill)
                DocumentKindIndex = (int)kind;
            if (details.Title != "" && DocumentTitle == fileTitle)
                DocumentTitle = details.Title;
            if (details.Sender != "" && DocumentSender == "")
                DocumentSender = details.Sender;
            if (DocumentPerson == personBefore && MatchPersonOption(details.Person) is { } person)
                DocumentPerson = person;
            if (details.Date is { } date && DocumentDate == DateTime.Today)
                DocumentDate = date.ToDateTime(TimeOnly.MinValue);
            if (details.Amount is { } amount && DocumentAmount is null)
                DocumentAmount = amount;
            if (details.DueDate is { } due && DocumentDueDate is null)
                DocumentDueDate = due.ToDateTime(TimeOnly.MinValue);
            _documentAiFollowUp = details.FollowUp;
            RefreshDocumentFollowUp();
            RefreshDocumentDuplicate();
            DocumentAiStatus = hasQrBill
                ? Loc.T("Payment details from the QR code, the rest by your local AI. Check them before saving.")
                : Loc.T("Details filled in by your local AI. Check them before saving.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            DocumentAiStatus = Loc.F("Couldn’t fill in the details automatically: {0}", ex.Message);
        }
        finally
        {
            if (_detailsCancellation == cancellation)
                IsDocumentAiBusy = false;
        }
    }

    [RelayCommand]
    private void EditDocument(HouseholdDocument document)
    {
        CloseOtherPanels();
        ResetDocumentForm();
        EditingDocument = document;
        DocumentFileName = Path.GetFileName(document.RelativePath);
        DocumentKindIndex = (int)document.Kind;
        DocumentTitle = document.Title;
        DocumentSender = document.Sender;
        DocumentPerson = PersonOptions.FirstOrDefault(o => o.Id == document.PersonId) ?? PersonOption.Shared;
        DocumentDate = document.Date.ToDateTime(TimeOnly.MinValue);
        DocumentAmount = document.Amount;
        DocumentDueDate = document.DueDate?.ToDateTime(TimeOnly.MinValue);
        DocumentIsPaid = document.IsPaid;
        DocumentNotes = document.Notes;
        DocumentPaymentIban = document.PaymentIban == "" ? "" : QrBill.FormatIban(document.PaymentIban);
        DocumentPaymentReference = document.PaymentReference;
        LoadInstalmentForm(document);
        OnPropertyChanged(nameof(DocumentRetentionText));
        IsDocumentPanelOpen = true;
    }

    [RelayCommand]
    private async Task SaveDocumentAsync()
    {
        DocumentError = null;
        if (string.IsNullOrWhiteSpace(DocumentTitle))
        {
            DocumentError = Loc.T("Enter a title.");
            return;
        }

        var payable = DocumentIsPayable;
        var inParts = payable && DocumentPaysInInstalments;
        var existingBill = EditingDocument;
        // Taking a plan away puts the bill back in the expenses as one entry, if its parts were there.
        var createEntry = !inParts && (CanCreateDocumentEntry && DocumentCreateEntry || HasInstalmentEntries(existingBill));
        var partEntries = inParts && (existingBill?.TransactionId is not null || HasInstalmentEntries(existingBill) ||
                                      CanCreateDocumentEntry && DocumentCreateEntry);
        if (createEntry && DocumentAmount is not > 0)
        {
            DocumentError = Loc.T("Enter the amount to pay, or untick “Add to expenses”.");
            return;
        }

        var existing = EditingDocument;
        var document = new HouseholdDocument
        {
            Id = existing?.Id ?? 0,
            Kind = SelectedDocumentKind,
            Title = DocumentTitle.Trim(),
            Sender = DocumentSender.Trim(),
            PersonId = DocumentPerson.Id,
            Date = DateOnly.FromDateTime(DocumentDate ?? DateTime.Today),
            Amount = payable && DocumentAmount is > 0 ? DocumentAmount : null,
            DueDate = payable && DocumentDueDate is { } due ? DateOnly.FromDateTime(due) : null,
            PaidOn = payable && DocumentIsPaid ? existing?.PaidOn ?? Today : null,
            Notes = DocumentNotes.Trim(),
            PaymentIban = payable ? DocumentPaymentIban.Replace(" ", "").ToUpperInvariant() : "",
            PaymentReference = payable ? DocumentPaymentReference.Trim() : "",
            RelativePath = existing?.RelativePath ?? "",
            AddedAt = existing?.AddedAt ?? DateTime.Now,
            TransactionId = existing?.TransactionId,
            FileHash = existing?.FileHash ?? _documentSourceHash,
        };
        if (inParts)
        {
            var (parts, error) = InstalmentsFromForm(document.Id);
            if (error is not null)
            {
                DocumentError = error;
                return;
            }
            document.Instalments = parts;
            document.PaidOn = parts.All(p => p.IsPaid) ? parts.Max(p => p.PaidOn) : null;
        }
        var followUp = existing is null && DocumentAddFollowUp ? DocumentFollowUp : null;
        var personName = DocumentPerson.Id is null ? null : DocumentPerson.Name;

        try
        {
            // File first: if OneDrive isn't reachable, nothing is saved in Keystone either.
            document.RelativePath = existing is null
                ? await Task.Run(() => _documentStore.Add(_documentSourcePath!, document, personName))
                : await Task.Run(() => _documentStore.Refile(document, personName));
        }
        catch (Exception ex)
        {
            DocumentError = Loc.F("Could not save the file to {0}: {1}", _documentStore.RootFolder, ex.Message);
            return;
        }

        try
        {
            var category = BillCategory(existing, document);
            if (inParts || existing?.HasInstalments == true)
                await SyncInstalmentEntriesAsync(existing, document, partEntries);
            if (createEntry)
                await AddBillEntryAsync(document, category);

            if (existing is null)
                await repository.AddDocumentAsync(document);
            else
                await repository.UpdateDocumentAsync(document);
        }
        catch (Exception ex)
        {
            DocumentError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (existing is not null)
            Documents.Remove(existing);
        Documents.Add(document);
        RefreshDocumentRows();
        CloseDocumentPanel();
        _ = IndexDocumentAsync(document);
        // An insurance policy is saved under Insurance straight away, with its premium; a contract, passport or
        // receipt opens its form next, filled in, for the reminders.
        if (followUp is { Kind: FollowUpKind.Insurance })
        {
            try
            {
                var (text, undo) = await SavePolicyFromDocumentAsync(document, followUp);
                CurrentPage = AppPage.Insurance;
                ShowNotice(text, undo);
            }
            catch (Exception ex)
            {
                // The document is filed; the policy can be checked and saved by hand.
                OpenFollowUp(document, followUp);
                PolicyError = Loc.F("Could not save: {0}", ex.GetBaseException().Message);
            }
        }
        else if (followUp is not null)
            OpenFollowUp(document, followUp);
        else
            CurrentPage = AppPage.Documents;
    }

    /// <summary>Adds an open bill as a one-time expense on its due date, so the runway includes it.</summary>
    private async Task AddBillEntryAsync(HouseholdDocument document, (string Category, int? AccountId)? like = null)
    {
        var entry = await repository.AddTransactionAsync(new Transaction
        {
            Date = document.DueDate ?? document.Date,
            Description = BillDescription(document),
            Category = like?.Category ?? "Bills",
            AccountId = like?.AccountId,
            Amount = document.Amount!.Value,
            Type = TransactionType.Expense,
            PersonId = document.PersonId,
        });
        InsertSorted(entry);
        document.TransactionId = entry.Id;
        Recalculate();
    }

    [RelayCommand]
    private async Task MarkDocumentPaidAsync(HouseholdDocument document)
    {
        if (document.HasInstalments)
        {
            await MarkNextInstalmentPaidAsync(document);
            return;
        }
        var paidOn = document.PaidOn;
        document.PaidOn = Today;
        try
        {
            await repository.UpdateDocumentAsync(document);
        }
        catch (Exception ex)
        {
            document.PaidOn = paidOn;
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        RefreshDocumentRows();
    }

    /// <summary>Removes the document from Keystone and moves its file into the Deleted folder.</summary>
    [RelayCommand]
    private async Task DeleteDocumentAsync(HouseholdDocument document)
    {
        try
        {
            await Task.Run(() => _documentStore.Delete(document));
            await repository.DeleteDocumentAsync(document.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        Documents.Remove(document);
        if (EditingDocument == document)
            CloseDocumentPanel();
        RefreshDocumentRows();
    }

    [RelayCommand]
    internal void OpenDocument(HouseholdDocument document) => RunFileAction(() => _documentStore.Open(document));

    [RelayCommand]
    private void RevealDocument(HouseholdDocument document) => RunFileAction(() => _documentStore.RevealInFinder(document));

    private void RunFileAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not open the file: {0}", ex.Message);
        }
    }

    /// <summary>An entry was deleted; bills that pointed at it no longer have an expense entry.</summary>
    private void UnlinkDocumentsFrom(int transactionId)
    {
        foreach (var document in Documents.Where(d => d.TransactionId == transactionId))
            document.TransactionId = null;
        foreach (var part in Documents.SelectMany(d => d.Instalments).Where(i => i.TransactionId == transactionId))
            part.TransactionId = null;
    }

    private void CloseDocumentPanel()
    {
        _detailsCancellation?.Cancel();
        _detailsCancellation = null;
        IsDocumentAiBusy = false;
        IsDocumentPanelOpen = false;
        ResetDocumentForm();
    }

    private void ResetDocumentForm()
    {
        EditingDocument = null;
        _documentSourcePath = null;
        DocumentFileName = "";
        DocumentKindIndex = 0;
        DocumentTitle = "";
        DocumentSender = "";
        DocumentDate = DateTime.Today;
        DocumentAmount = null;
        DocumentDueDate = null;
        DocumentIsPaid = false;
        DocumentNotes = "";
        DocumentPaymentIban = "";
        DocumentPaymentReference = "";
        DocumentCreateEntry = true;
        ResetInstalmentForm();
        DocumentError = null;
        DocumentAiStatus = "";
        ResetDocumentChecks();
    }
}
