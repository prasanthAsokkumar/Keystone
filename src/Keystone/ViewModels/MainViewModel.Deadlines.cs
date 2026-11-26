using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>A notice deadline of a contract or an insurance policy, in the one list of all deadlines.</summary>
public sealed record NoticeDeadlineRow(DateOnly CancelBy, string Name, string Kind, string Person, bool IsInsurance, string DaysLeft,
    bool IsSoon, Action Open)
{
    public string DateText => Format.Date(CancelBy);

    public string DetailText => string.Join(" · ", new[] { Kind, Person }.Where(s => s != ""));

    public string SourceText => Loc.T(IsInsurance ? "Insurance" : "Contract");
}

/// <summary>The Contracts & dates page: contracts with notice deadlines, important dates and warranties.</summary>
public partial class MainViewModel
{
    private readonly List<Contract> _contracts = [];
    private readonly List<ImportantDate> _dates = [];
    private readonly List<Warranty> _warranties = [];

    /// <summary>0: contracts, 1: important dates, 2: warranties.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContractsTab), nameof(IsDatesTab), nameof(IsWarrantiesTab), nameof(AddDeadlineText))]
    public partial int DeadlinesTab { get; set; }

    public bool IsContractsTab { get => DeadlinesTab == 0; set { if (value) DeadlinesTab = 0; } }

    public bool IsDatesTab { get => DeadlinesTab == 1; set { if (value) DeadlinesTab = 1; } }

    public bool IsWarrantiesTab { get => DeadlinesTab == 2; set { if (value) DeadlinesTab = 2; } }

    public string AddDeadlineText => Loc.T(DeadlinesTab switch { 1 => "Add date", 2 => "Add warranty", _ => "Add contract" });

    [RelayCommand]
    private void AddDeadline()
    {
        switch (DeadlinesTab)
        {
            case 1: NewImportantDate(); break;
            case 2: NewWarranty(); break;
            default: NewContract(); break;
        }
    }

    private async Task LoadDeadlinesAsync()
    {
        _contracts.Clear();
        _contracts.AddRange(await repository.GetAllAsync<Contract>());
        _dates.Clear();
        _dates.AddRange(await repository.GetAllAsync<ImportantDate>());
        _warranties.Clear();
        _warranties.AddRange(await repository.GetAllAsync<Warranty>());
        RefreshDeadlines();
    }

    private void RefreshDeadlines()
    {
        RefreshContracts();
        RefreshImportantDates();
        RefreshWarranties();
        ScheduleReminders();
    }

    private IReadOnlyList<DocumentOption> DocumentOptionsFor(int? selectedId, params DocumentKind[] kinds) =>
    [
        new DocumentOption(null, ""),
        .. Documents
            .Where(d => kinds.Contains(d.Kind) || d.Id == selectedId)
            .OrderByDescending(d => d.Date)
            .Select(DocumentOptionOf),
    ];

    private DocumentOption DocumentOptionOf(HouseholdDocument document) => new(document,
        document.Sender == "" ? document.Title : $"{document.Sender}: {document.Title}",
        document.PersonId is null ? Loc.T(PersonOption.SharedName) : PersonName(document.PersonId), Format.Date(document.Date));

    [RelayCommand]
    private void OpenLinkedDocument(HouseholdDocument document) => OpenDocument(document);

    // ── Contracts ───────────────────────────────────────────────────────────

    public IReadOnlyList<string> ContractKindNames => Enum.GetValues<ContractKind>().Select(Contract.KindName).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContractRows))]
    public partial IReadOnlyList<ContractRow> ContractRows { get; set; } = [];

    public bool HasContractRows => ContractRows.Count > 0;

    [ObservableProperty]
    public partial string ContractCostText { get; set; } = "";

    [ObservableProperty]
    public partial string ContractCostDetail { get; set; } = "";

    [ObservableProperty]
    public partial string ContractDeadlineText { get; set; } = "";

    [ObservableProperty]
    public partial string ContractDeadlineDetail { get; set; } = "";

    [ObservableProperty]
    public partial bool IsContractDeadlineSoon { get; set; }

    private void RefreshContracts()
    {
        var documents = Documents.ToDictionary(d => d.Id);
        ContractRows = Sorted("contracts", _contracts
            .Where(c => InView(c.PersonId))
            .OrderBy(c => c.HasEnded(Today))
            .ThenBy(c => c.NextDeadline(Today)?.CancelBy ?? DateOnly.MaxValue)
            .ThenBy(c => c.Provider, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ContractRow(c, PersonName(c.PersonId),
                c.DocumentId is { } id && documents.TryGetValue(id, out var d) ? d : null, Today)),
            r => r.Contract.SortOrder,
            new SortKey<ContractRow>("provider", r => r.Contract.Provider),
            new SortKey<ContractRow>("person", r => r.PersonName),
            new SortKey<ContractRow>("cost", r => r.Contract.YearlyCost, DescendingFirst: true),
            new SortKey<ContractRow>("deadline", r => r.Contract.NextDeadline(Today)?.CancelBy));

        var running = _contracts.Where(c => !c.HasEnded(Today)).ToList();
        var yearly = running.Sum(c => c.YearlyCost);
        ContractCostText = Format.Money(yearly);
        ContractCostDetail = running.Count == 0
            ? Loc.T("Add phone, internet, gym or leasing contracts")
            : Loc.F("{0} a month · {1}", Format.Money(yearly / 12), Loc.Plural(running.Count, "{0} contract", "{0} contracts"));

        // Contracts and insurance policies together: every notice deadline in one place.
        AllDeadlineRows = AllNoticeDeadlines();
        var next = AllDeadlineRows.FirstOrDefault();
        if (next is null)
        {
            ContractDeadlineText = "–";
            ContractDeadlineDetail = Loc.T("Enter when a contract’s minimum term ends to see its notice deadline");
            IsContractDeadlineSoon = false;
        }
        else
        {
            ContractDeadlineText = Format.Date(next.CancelBy);
            ContractDeadlineDetail = $"{next.Name}, {next.Kind} · {next.DaysLeft}";
            IsContractDeadlineSoon = next.IsSoon;
        }
    }

    /// <summary>The notice deadlines of the next 12 months, of contracts and insurance policies, soonest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAllDeadlineRows))]
    public partial IReadOnlyList<NoticeDeadlineRow> AllDeadlineRows { get; set; } = [];

    public bool HasAllDeadlineRows => AllDeadlineRows.Count > 0;

    /// <summary>The first few, for the summary card.</summary>
    public IReadOnlyList<NoticeDeadlineRow> TopDeadlineRows => AllDeadlineRows.Take(5).ToList();

    partial void OnAllDeadlineRowsChanged(IReadOnlyList<NoticeDeadlineRow> value) => OnPropertyChanged(nameof(TopDeadlineRows));

    private List<NoticeDeadlineRow> AllNoticeDeadlines()
    {
        var until = Today.AddYears(1);
        var rows = new List<NoticeDeadlineRow>();
        foreach (var c in _contracts.Where(c => !c.HasEnded(Today) && InView(c.PersonId)))
            if (c.NextDeadline(Today) is { } d && d.CancelBy <= until)
                rows.Add(new NoticeDeadlineRow(d.CancelBy, c.Provider, Contract.KindName(c.Kind), PersonNameOrEmpty(c.PersonId), false,
                    DaysLeftText(d.CancelBy), d.CancelBy.DayNumber - Today.DayNumber <= InsuranceDeadlineWarningDays,
                    () => EditContractCommand.Execute(c)));
        foreach (var p in _policies.Where(p => !p.HasEnded(Today) && InView(p.PersonId)))
            if (p.NextDeadline(Today) is { } d && d.CancelBy <= until)
                rows.Add(new NoticeDeadlineRow(d.CancelBy, p.Insurer, InsurancePolicy.KindName(p.Kind), PersonNameOrEmpty(p.PersonId), true,
                    DaysLeftText(d.CancelBy), d.CancelBy.DayNumber - Today.DayNumber <= InsuranceDeadlineWarningDays,
                    () => { CurrentPage = AppPage.Insurance; EditInsuranceCommand.Execute(p); }));
        return rows.OrderBy(r => r.CancelBy).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private string PersonNameOrEmpty(int? id) => id is null ? "" : PersonName(id);

    [RelayCommand]
    private void OpenNoticeDeadline(NoticeDeadlineRow row) => row.Open();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsContractPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContractFormTitle))]
    public partial Contract? EditingContract { get; set; }

    public string ContractFormTitle => Loc.T(EditingContract is null ? "Add contract" : "Edit contract");

    [ObservableProperty]
    public partial string ContractProvider { get; set; } = "";

    [ObservableProperty]
    public partial int ContractKindIndex { get; set; }

    [ObservableProperty]
    public partial PersonOption ContractPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial string ContractCustomerNumber { get; set; } = "";

    [ObservableProperty]
    public partial decimal? ContractCost { get; set; }

    /// <summary>Index into <see cref="PremiumRecurrenceNames"/>: monthly, every 3 months, yearly.</summary>
    [ObservableProperty]
    public partial int ContractRecurrenceIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContractCancelByText))]
    public partial DateTime? ContractEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContractCancelByText))]
    public partial bool ContractRenews { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContractCancelByText))]
    public partial decimal? ContractNoticeMonths { get; set; } = 2;

    [ObservableProperty]
    public partial IReadOnlyList<DocumentOption> ContractDocumentOptions { get; set; } = [];

    [ObservableProperty]
    public partial DocumentOption? ContractDocument { get; set; }

    [ObservableProperty]
    public partial string ContractNotes { get; set; } = "";

    [ObservableProperty]
    public partial bool ContractAddToExpenses { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContractError))]
    public partial string? ContractError { get; set; }

    public bool HasContractError => !string.IsNullOrEmpty(ContractError);

    public string ContractCancelByText => ContractEnd is not { } end
        ? Loc.T("Without an end date, it can be cancelled any time with its notice period.")
        : NoticeDeadline.Next(DateOnly.FromDateTime(end), ContractRenews, (int)(ContractNoticeMonths ?? 0), Today) is { } d
            ? Loc.F("Cancel by {0} for the term ending {1}.", Format.Date(d.CancelBy), Format.Date(d.End))
            : Loc.T("This contract has ended.");

    partial void OnContractKindIndexChanged(int value)
    {
        if (EditingContract is null && Enum.IsDefined((ContractKind)value))
            ContractNoticeMonths = Contract.DefaultNoticeMonths((ContractKind)value);
    }

    private Recurrence ContractRecurrence => ContractRecurrenceIndex switch { 1 => Recurrence.Quarterly, 2 => Recurrence.Yearly, _ => Recurrence.Monthly };

    [RelayCommand]
    private void NewContract()
    {
        CloseOtherPanels();
        EditingContract = null;
        ContractProvider = "";
        ContractKindIndex = 0;
        OnContractKindIndexChanged(0);
        ContractPerson = PersonOption.Shared;
        ContractCustomerNumber = "";
        ContractCost = null;
        ContractRecurrenceIndex = 0;
        ContractEnd = null;
        ContractRenews = true;
        ContractNotes = "";
        ContractAddToExpenses = false;
        ContractError = null;
        ContractDocumentOptions = DocumentOptionsFor(null, DocumentKind.Contract);
        ContractDocument = ContractDocumentOptions[0];
        IsContractPanelOpen = true;
    }

    [RelayCommand]
    private void EditContract(Contract contract)
    {
        CloseOtherPanels();
        EditingContract = contract;
        ContractProvider = contract.Provider;
        ContractKindIndex = (int)contract.Kind;
        ContractPerson = PersonOptions.FirstOrDefault(o => o.Id == contract.PersonId) ?? PersonOption.Shared;
        ContractCustomerNumber = contract.CustomerNumber;
        ContractCost = contract.Cost;
        ContractRecurrenceIndex = contract.CostRecurrence switch { Recurrence.Quarterly => 1, Recurrence.Yearly => 2, _ => 0 };
        ContractEnd = contract.ContractEnd?.ToDateTime(TimeOnly.MinValue);
        ContractRenews = contract.RenewsAutomatically;
        ContractNoticeMonths = contract.NoticeMonths;
        ContractNotes = contract.Notes;
        ContractAddToExpenses = false;
        ContractError = null;
        ContractDocumentOptions = DocumentOptionsFor(contract.DocumentId, DocumentKind.Contract);
        ContractDocument = ContractDocumentOptions.FirstOrDefault(o => o.Document?.Id == contract.DocumentId) ?? ContractDocumentOptions[0];
        IsContractPanelOpen = true;
    }

    [RelayCommand]
    private async Task SaveContractAsync()
    {
        ContractError = null;
        if (string.IsNullOrWhiteSpace(ContractProvider))
        {
            ContractError = Loc.T("Enter the provider.");
            return;
        }

        var existing = EditingContract;
        var contract = new Contract
        {
            Id = existing?.Id ?? 0,
            Provider = ContractProvider.Trim(),
            Kind = Enum.IsDefined((ContractKind)ContractKindIndex) ? (ContractKind)ContractKindIndex : ContractKind.Other,
            PersonId = ContractPerson.Id,
            CustomerNumber = ContractCustomerNumber.Trim(),
            Cost = Math.Max(0, ContractCost ?? 0),
            CostRecurrence = ContractRecurrence,
            ContractEnd = ContractEnd is { } end ? DateOnly.FromDateTime(end) : null,
            RenewsAutomatically = ContractRenews,
            NoticeMonths = Math.Clamp((int)(ContractNoticeMonths ?? 0), 0, 24),
            Notes = ContractNotes.Trim(),
            DocumentId = ContractDocument?.Document?.Id,
        };

        try
        {
            await repository.SaveAsync(contract, existing is null);
            if (existing is null && ContractAddToExpenses && contract.Cost > 0)
            {
                var entry = await repository.AddTransactionAsync(new Transaction
                {
                    Date = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1),
                    Description = $"{contract.Provider}: {Contract.KindName(contract.Kind)}",
                    Category = Contract.CategoryFor(contract.Kind),
                    Amount = contract.Cost,
                    Type = TransactionType.Expense,
                    Recurrence = contract.CostRecurrence,
                    PersonId = contract.PersonId,
                });
                InsertSorted(entry);
                Recalculate();
            }
        }
        catch (Exception ex)
        {
            ContractError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (existing is not null)
            _contracts.Remove(existing);
        _contracts.Add(contract);
        CloseDeadlinePanels();
        RefreshDeadlines();
    }

    [RelayCommand]
    private async Task DeleteContractAsync(Contract contract)
    {
        if (await DeleteRecordAsync<Contract>(c => c.Id == contract.Id))
        {
            _contracts.Remove(contract);
            RefreshDeadlines();
        }
    }

    // ── Important dates ─────────────────────────────────────────────────────

    public IReadOnlyList<string> ImportantDateKindNames => Enum.GetValues<ImportantDateKind>().Select(ImportantDate.KindName).ToList();

    public IReadOnlyList<string> RepeatMonthNames => Translate("Once", "Every 6 months", "Every year", "Every 2 years", "Every 3 years");

    private static readonly int[] RepeatMonthValues = [0, 6, 12, 24, 36];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportantDateRows))]
    public partial IReadOnlyList<ImportantDateRow> ImportantDateRows { get; set; } = [];

    public bool HasImportantDateRows => ImportantDateRows.Count > 0;

    [ObservableProperty]
    public partial string NextImportantDateText { get; set; } = "";

    [ObservableProperty]
    public partial string NextImportantDateDetail { get; set; } = "";

    private void RefreshImportantDates()
    {
        ImportantDateRows = Sorted("dates", _dates
            .Where(d => InView(d.PersonId))
            .OrderBy(d => d.HasPassed(Today))
            .ThenBy(d => d.NextDate(Today))
            .Select(d => new ImportantDateRow(d, PersonName(d.PersonId), Today)), null,
            new SortKey<ImportantDateRow>("title", r => r.Date.Title),
            new SortKey<ImportantDateRow>("person", r => r.PersonName),
            new SortKey<ImportantDateRow>("date", r => r.Date.NextDate(Today)));
        var next = _dates.Where(d => !d.HasPassed(Today)).OrderBy(d => d.NextDate(Today)).FirstOrDefault();
        NextImportantDateText = next is null ? "–" : Format.Date(next.NextDate(Today));
        NextImportantDateDetail = next is null
            ? Loc.T("Add passports, the car inspection or the vignette")
            : Loc.F("{0} · {1}", next.Title, DaysLeftText(next.NextDate(Today)));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsImportantDatePanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportantDateFormTitle))]
    public partial ImportantDate? EditingImportantDate { get; set; }

    public string ImportantDateFormTitle => Loc.T(EditingImportantDate is null ? "Add date" : "Edit date");

    [ObservableProperty]
    public partial string ImportantDateTitle { get; set; } = "";

    [ObservableProperty]
    public partial int ImportantDateKindIndex { get; set; }

    [ObservableProperty]
    public partial PersonOption ImportantDatePerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial DateTime? ImportantDateDate { get; set; }

    [ObservableProperty]
    public partial int ImportantDateRepeatIndex { get; set; }

    [ObservableProperty]
    public partial decimal? ImportantDateRemindDays { get; set; } = 30;

    [ObservableProperty]
    public partial string ImportantDateNotes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportantDateError))]
    public partial string? ImportantDateError { get; set; }

    public bool HasImportantDateError => !string.IsNullOrEmpty(ImportantDateError);

    partial void OnImportantDateKindIndexChanged(int value)
    {
        if (EditingImportantDate is not null || !Enum.IsDefined((ImportantDateKind)value))
            return;
        var kind = (ImportantDateKind)value;
        var (remind, repeat) = ImportantDate.Defaults(kind);
        ImportantDateRemindDays = remind;
        ImportantDateRepeatIndex = Math.Max(0, Array.IndexOf(RepeatMonthValues, repeat));
        if (kind != ImportantDateKind.Other && (ImportantDateTitle == "" || Enum.GetValues<ImportantDateKind>().Any(k => ImportantDate.KindName(k) == ImportantDateTitle)))
            ImportantDateTitle = ImportantDate.KindName(kind);
        // The vignette is due every January.
        if (kind == ImportantDateKind.Vignette && ImportantDateDate is null)
            ImportantDateDate = new DateTime(Today.Year + 1, 1, 31);
    }

    [RelayCommand]
    private void NewImportantDate()
    {
        CloseOtherPanels();
        EditingImportantDate = null;
        ImportantDateTitle = "";
        ImportantDateDate = null;
        ImportantDatePerson = PersonOption.Shared;
        ImportantDateNotes = "";
        ImportantDateError = null;
        ImportantDateKindIndex = 0;
        OnImportantDateKindIndexChanged(0);
        IsImportantDatePanelOpen = true;
    }

    [RelayCommand]
    private void EditImportantDate(ImportantDate date)
    {
        CloseOtherPanels();
        EditingImportantDate = date;
        ImportantDateTitle = date.Title;
        ImportantDateKindIndex = (int)date.Kind;
        ImportantDatePerson = PersonOptions.FirstOrDefault(o => o.Id == date.PersonId) ?? PersonOption.Shared;
        ImportantDateDate = date.Date.ToDateTime(TimeOnly.MinValue);
        ImportantDateRepeatIndex = Math.Max(0, Array.IndexOf(RepeatMonthValues, date.RepeatMonths));
        ImportantDateRemindDays = date.RemindDaysBefore;
        ImportantDateNotes = date.Notes;
        ImportantDateError = null;
        IsImportantDatePanelOpen = true;
    }

    [RelayCommand]
    private async Task SaveImportantDateAsync()
    {
        ImportantDateError = null;
        if (string.IsNullOrWhiteSpace(ImportantDateTitle) || ImportantDateDate is null)
        {
            ImportantDateError = Loc.T("Enter what it is and the date.");
            return;
        }

        var existing = EditingImportantDate;
        var date = new ImportantDate
        {
            Id = existing?.Id ?? 0,
            Title = ImportantDateTitle.Trim(),
            Kind = Enum.IsDefined((ImportantDateKind)ImportantDateKindIndex) ? (ImportantDateKind)ImportantDateKindIndex : ImportantDateKind.Other,
            PersonId = ImportantDatePerson.Id,
            Date = DateOnly.FromDateTime(ImportantDateDate.Value),
            RepeatMonths = RepeatMonthValues[Math.Clamp(ImportantDateRepeatIndex, 0, RepeatMonthValues.Length - 1)],
            RemindDaysBefore = Math.Clamp((int)(ImportantDateRemindDays ?? 0), 0, 365),
            Notes = ImportantDateNotes.Trim(),
        };
        try
        {
            await repository.SaveAsync(date, existing is null);
        }
        catch (Exception ex)
        {
            ImportantDateError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (existing is not null)
            _dates.Remove(existing);
        _dates.Add(date);
        CloseDeadlinePanels();
        RefreshDeadlines();
    }

    [RelayCommand]
    private async Task DeleteImportantDateAsync(ImportantDate date)
    {
        if (await DeleteRecordAsync<ImportantDate>(d => d.Id == date.Id))
        {
            _dates.Remove(date);
            RefreshDeadlines();
        }
    }

    // ── Warranties ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarrantyRows))]
    public partial IReadOnlyList<WarrantyRow> WarrantyRows { get; set; } = [];

    public bool HasWarrantyRows => WarrantyRows.Count > 0;

    [ObservableProperty]
    public partial string WarrantySummaryText { get; set; } = "";

    [ObservableProperty]
    public partial string WarrantySummaryDetail { get; set; } = "";

    private void RefreshWarranties()
    {
        var documents = Documents.ToDictionary(d => d.Id);
        WarrantyRows = Sorted("warranties", _warranties
            .Where(w => InView(w.PersonId))
            .OrderBy(w => w.HasExpired(Today))
            .ThenBy(w => w.ExpiresOn)
            .Select(w => new WarrantyRow(w, PersonName(w.PersonId),
                w.DocumentId is { } id && documents.TryGetValue(id, out var d) ? d : null, Today)), null,
            new SortKey<WarrantyRow>("item", r => r.Warranty.Item),
            new SortKey<WarrantyRow>("person", r => r.PersonName),
            new SortKey<WarrantyRow>("price", r => r.Warranty.Price, DescendingFirst: true),
            new SortKey<WarrantyRow>("expires", r => r.Warranty.ExpiresOn));
        var running = _warranties.Count(w => !w.HasExpired(Today));
        var soon = _warranties.Count(w => !w.HasExpired(Today) && w.ExpiresOn.DayNumber - Today.DayNumber <= 60);
        WarrantySummaryText = running.ToString(Loc.Instance.Culture);
        WarrantySummaryDetail = _warranties.Count == 0
            ? Loc.T("Add purchases with their receipt")
            : soon > 0 ? Loc.Plural(soon, "{0} ends within 2 months", "{0} end within 2 months") : Loc.T("None ends soon");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsWarrantyPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarrantyFormTitle))]
    public partial Warranty? EditingWarranty { get; set; }

    public string WarrantyFormTitle => Loc.T(EditingWarranty is null ? "Add warranty" : "Edit warranty");

    [ObservableProperty]
    public partial string WarrantyItem { get; set; } = "";

    [ObservableProperty]
    public partial string WarrantyStore { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption WarrantyPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarrantyEndText))]
    public partial DateTime? WarrantyPurchaseDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial decimal? WarrantyPrice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarrantyEndText))]
    public partial decimal? WarrantyMonths { get; set; } = Warranty.DefaultMonths;

    [ObservableProperty]
    public partial IReadOnlyList<DocumentOption> WarrantyDocumentOptions { get; set; } = [];

    [ObservableProperty]
    public partial DocumentOption? WarrantyDocument { get; set; }

    [ObservableProperty]
    public partial string WarrantyNotes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarrantyError))]
    public partial string? WarrantyError { get; set; }

    public bool HasWarrantyError => !string.IsNullOrEmpty(WarrantyError);

    public string WarrantyEndText => WarrantyPurchaseDate is { } bought
        ? Loc.F("Warranty until {0}.", Format.Date(DateOnly.FromDateTime(bought).AddMonths((int)(WarrantyMonths ?? 0)).AddDays(-1)))
        : "";

    [RelayCommand]
    private void NewWarranty()
    {
        CloseOtherPanels();
        EditingWarranty = null;
        WarrantyItem = "";
        WarrantyStore = "";
        WarrantyPerson = PersonOption.Shared;
        WarrantyPurchaseDate = DateTime.Today;
        WarrantyPrice = null;
        WarrantyMonths = Warranty.DefaultMonths;
        WarrantyNotes = "";
        WarrantyError = null;
        WarrantyDocumentOptions = DocumentOptionsFor(null, DocumentKind.Invoice, DocumentKind.Bill);
        WarrantyDocument = WarrantyDocumentOptions[0];
        IsWarrantyPanelOpen = true;
    }

    [RelayCommand]
    private void EditWarranty(Warranty warranty)
    {
        CloseOtherPanels();
        EditingWarranty = warranty;
        WarrantyItem = warranty.Item;
        WarrantyStore = warranty.Store;
        WarrantyPerson = PersonOptions.FirstOrDefault(o => o.Id == warranty.PersonId) ?? PersonOption.Shared;
        WarrantyPurchaseDate = warranty.PurchaseDate.ToDateTime(TimeOnly.MinValue);
        WarrantyPrice = warranty.Price;
        WarrantyMonths = warranty.Months;
        WarrantyNotes = warranty.Notes;
        WarrantyError = null;
        WarrantyDocumentOptions = DocumentOptionsFor(warranty.DocumentId, DocumentKind.Invoice, DocumentKind.Bill);
        WarrantyDocument = WarrantyDocumentOptions.FirstOrDefault(o => o.Document?.Id == warranty.DocumentId) ?? WarrantyDocumentOptions[0];
        IsWarrantyPanelOpen = true;
    }

    [RelayCommand]
    private async Task SaveWarrantyAsync()
    {
        WarrantyError = null;
        if (string.IsNullOrWhiteSpace(WarrantyItem) || WarrantyPurchaseDate is null)
        {
            WarrantyError = Loc.T("Enter what you bought and when.");
            return;
        }

        var existing = EditingWarranty;
        var warranty = new Warranty
        {
            Id = existing?.Id ?? 0,
            Item = WarrantyItem.Trim(),
            Store = WarrantyStore.Trim(),
            PersonId = WarrantyPerson.Id,
            PurchaseDate = DateOnly.FromDateTime(WarrantyPurchaseDate.Value),
            Price = WarrantyPrice is > 0 ? WarrantyPrice : null,
            Months = Math.Clamp((int)(WarrantyMonths ?? Warranty.DefaultMonths), 1, 240),
            DocumentId = WarrantyDocument?.Document?.Id,
            Notes = WarrantyNotes.Trim(),
        };
        try
        {
            await repository.SaveAsync(warranty, existing is null);
        }
        catch (Exception ex)
        {
            WarrantyError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (existing is not null)
            _warranties.Remove(existing);
        _warranties.Add(warranty);
        CloseDeadlinePanels();
        RefreshDeadlines();
    }

    [RelayCommand]
    private async Task DeleteWarrantyAsync(Warranty warranty)
    {
        if (await DeleteRecordAsync<Warranty>(w => w.Id == warranty.Id))
        {
            _warranties.Remove(warranty);
            RefreshDeadlines();
        }
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    private async Task<bool> DeleteRecordAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> match) where T : class
    {
        try
        {
            await repository.DeleteAsync(match);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return false;
        }
    }

    private void CloseDeadlinePanels()
    {
        IsContractPanelOpen = false;
        IsImportantDatePanelOpen = false;
        IsWarrantyPanelOpen = false;
        EditingContract = null;
        EditingImportantDate = null;
        EditingWarranty = null;
    }
}

/// <summary>One contract in the list.</summary>
public sealed record ContractRow(Contract Contract, string PersonName, HouseholdDocument? Document, DateOnly Today)
{
    private (DateOnly End, DateOnly CancelBy)? Deadline => Contract.NextDeadline(Today);

    public string Provider => Contract.Provider;

    public string DetailText => Contract.CustomerNumber == ""
        ? Contract.KindName(Contract.Kind)
        : $"{Contract.KindName(Contract.Kind)} · {Contract.CustomerNumber}";

    public bool IsShared => Contract.PersonId is null;

    public string CostText => Format.Money(Contract.Cost) + Contract.CostRecurrence switch
    {
        Recurrence.Monthly => Loc.T(" / mo"),
        Recurrence.Quarterly => Loc.T(" / qtr"),
        _ => Loc.T(" / yr"),
    };

    public string YearlyText => Contract.CostRecurrence == Recurrence.Yearly ? "" : Loc.F("{0} a year", Format.Money(Contract.YearlyCost));

    public bool HasYearly => YearlyText != "";

    public bool HasEnded => Contract.HasEnded(Today);

    public string TermText => HasEnded
        ? Loc.F("Ended {0}", Format.Date(Contract.ContractEnd!.Value))
        : Deadline is { } d
            ? Loc.F(Contract.RenewsAutomatically ? "Renews after {0}" : "Ends {0}", Format.Date(d.End))
            : Contract.NoticeMonths == 0
                ? Loc.T("Cancel any time")
                : Loc.Plural(Contract.NoticeMonths, "Cancel any time · {0} month’s notice", "Cancel any time · {0} months’ notice");

    public string CancelByText => Deadline is { } d && !HasEnded ? Loc.F("Cancel by {0}", Format.Date(d.CancelBy)) : "";

    public bool HasCancelBy => CancelByText != "";

    private int DaysToDeadline => Deadline is { } d ? d.CancelBy.DayNumber - Today.DayNumber : int.MaxValue;

    public bool IsDeadlineUrgent => !HasEnded && DaysToDeadline <= 30;

    public bool IsDeadlineSoon => !HasEnded && !IsDeadlineUrgent && DaysToDeadline <= MainViewModel.InsuranceDeadlineWarningDays;

    public bool HasDocument => Document is not null;
}

/// <summary>One important date in the list.</summary>
public sealed record ImportantDateRow(ImportantDate Date, string PersonName, DateOnly Today)
{
    public string Title => Date.Title;

    /// <summary>The kind (unless the title already says it) and how often it comes round.</summary>
    public string DetailText => string.Join(" · ", new[]
    {
        Date.Title == ImportantDate.KindName(Date.Kind) ? "" : ImportantDate.KindName(Date.Kind),
        Date.RepeatMonths switch
        {
            0 => "",
            12 => Loc.T("every year"),
            _ when Date.RepeatMonths % 12 == 0 => Loc.F("every {0} years", Date.RepeatMonths / 12),
            _ => Loc.F("every {0} months", Date.RepeatMonths),
        },
    }.Where(p => p != ""));

    public bool IsShared => Date.PersonId is null;

    public bool HasPassed => Date.HasPassed(Today);

    public string DateText => Format.Date(Date.NextDate(Today));

    public string WhenText => HasPassed ? Loc.T("Passed") : MainViewModel.DaysLeftText(Date.NextDate(Today));

    /// <summary>Within the time to act, e.g. 6 months before a passport expires.</summary>
    public bool IsSoon => !HasPassed && Date.NextDate(Today).DayNumber - Today.DayNumber <= Date.RemindDaysBefore;
}

/// <summary>One warranty in the list.</summary>
public sealed record WarrantyRow(Warranty Warranty, string PersonName, HouseholdDocument? Document, DateOnly Today)
{
    public string Item => Warranty.Item;

    public string DetailText => string.Join(" · ", new[]
    {
        Warranty.Store,
        Loc.F("bought {0}", Format.Date(Warranty.PurchaseDate)),
        Warranty.Price is { } price ? Format.Money(price) : "",
    }.Where(p => p != ""));

    public bool IsShared => Warranty.PersonId is null;

    public bool HasExpired => Warranty.HasExpired(Today);

    public string EndText => HasExpired
        ? Loc.F("Ended {0}", Format.Date(Warranty.ExpiresOn))
        : Loc.F("Until {0}", Format.Date(Warranty.ExpiresOn));

    public bool IsEndingSoon => !HasExpired && Warranty.ExpiresOn.DayNumber - Today.DayNumber <= 60;

    public bool HasDocument => Document is not null;
}
