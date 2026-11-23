using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>A choice of policy document; <see cref="Document"/> is null for "none".</summary>
public sealed record DocumentOption(HouseholdDocument? Document, string Name)
{
    public override string ToString() => Document is null ? Loc.T("None") : Name;
}

/// <summary>The Insurance page: every policy, what it costs a year and by when it can be cancelled.</summary>
public partial class MainViewModel
{
    /// <summary>Deadlines this close are highlighted and reminded of.</summary>
    public const int InsuranceDeadlineWarningDays = 60;

    private readonly List<InsurancePolicy> _policies = [];

    public IReadOnlyList<string> InsuranceKindNames => Enum.GetValues<InsuranceKind>().Select(InsurancePolicy.KindName).ToList();

    public IReadOnlyList<string> PremiumRecurrenceNames => Translate("Monthly", "Every 3 months", "Yearly");

    [ObservableProperty]
    public partial PersonOption InsurancePersonFilter { get; set; } = PersonOption.Everyone;

    partial void OnInsurancePersonFilterChanged(PersonOption value) => RefreshInsurance();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsuranceRows))]
    public partial IReadOnlyList<InsuranceRow> InsuranceRows { get; set; } = [];

    public bool HasInsuranceRows => InsuranceRows.Count > 0;

    public string InsuranceEmptyText => _policies.Count == 0
        ? Loc.T("No policies yet. Add your health, household, car and other insurance to see what they cost and when to cancel.")
        : Loc.T("No policies for this person.");

    [ObservableProperty]
    public partial string InsurancePremiumsText { get; set; } = "";

    [ObservableProperty]
    public partial string InsurancePremiumsDetail { get; set; } = "";

    [ObservableProperty]
    public partial string NextDeadlineText { get; set; } = "";

    [ObservableProperty]
    public partial string NextDeadlineDetail { get; set; } = "";

    [ObservableProperty]
    public partial bool IsNextDeadlineSoon { get; set; }

    private async Task LoadInsuranceAsync()
    {
        _policies.Clear();
        _policies.AddRange(await repository.GetInsurancePoliciesAsync());
        RefreshInsurance();
    }

    private void RefreshInsurance()
    {
        RefreshOwnPageLists();
        var filter = InsurancePersonFilter ?? PersonOption.Everyone;
        var policies = _policies.Where(p => filter.IsEveryone || p.PersonId == filter.Id).ToList();
        var documents = Documents.ToDictionary(d => d.Id);

        InsuranceRows = Sorted("policies", policies
            .OrderBy(p => p.HasEnded(Today))
            .ThenBy(p => p.NextDeadline(Today)?.CancelBy ?? DateOnly.MaxValue)
            .ThenBy(p => p.Insurer, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new InsuranceRow(p, PersonName(p.PersonId),
                p.DocumentId is { } id && documents.TryGetValue(id, out var document) ? document : null, Today)),
            r => r.Policy.SortOrder,
            new SortKey<InsuranceRow>("insurer", r => r.Policy.Insurer),
            new SortKey<InsuranceRow>("person", r => r.PersonName),
            new SortKey<InsuranceRow>("premium", r => r.Policy.YearlyPremium, DescendingFirst: true),
            new SortKey<InsuranceRow>("deadline", r => r.Policy.NextDeadline(Today)?.CancelBy));
        OnPropertyChanged(nameof(InsuranceEmptyText));

        var running = policies.Where(p => !p.HasEnded(Today)).ToList();
        var yearly = running.Sum(p => p.YearlyPremium);
        InsurancePremiumsText = Format.Money(yearly);
        InsurancePremiumsDetail = running.Count == 0
            ? Loc.T("Add a policy to see its premium")
            : Loc.F("{0} a month · {1}", Format.Money(yearly / 12),
                Loc.Plural(running.Count, "{0} policy", "{0} policies"));

        var next = running
            .Select(p => (Policy: p, Deadline: p.NextDeadline(Today)))
            .Where(x => x.Deadline is not null)
            .OrderBy(x => x.Deadline!.Value.CancelBy)
            .FirstOrDefault();
        if (next.Policy is null)
        {
            NextDeadlineText = "–";
            NextDeadlineDetail = Loc.T("Enter when a policy’s term ends to see its notice deadline");
            IsNextDeadlineSoon = false;
        }
        else
        {
            var cancelBy = next.Deadline!.Value.CancelBy;
            NextDeadlineText = Format.Date(cancelBy);
            NextDeadlineDetail = Loc.F("{0}, {1} · {2}", next.Policy.Insurer, InsurancePolicy.KindName(next.Policy.Kind),
                DaysLeftText(cancelBy));
            IsNextDeadlineSoon = cancelBy.DayNumber - Today.DayNumber <= InsuranceDeadlineWarningDays;
        }

        ScheduleReminders();
    }

    internal static string DaysLeftText(DateOnly date)
    {
        var days = date.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;
        return days switch
        {
            0 => Loc.T("today"),
            1 => Loc.T("tomorrow"),
            _ => Loc.Plural(days, "in {0} day", "in {0} days"),
        };
    }

    // ── Insurance panel ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsInsurancePanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InsuranceFormTitle), nameof(IsNewInsurance))]
    public partial InsurancePolicy? EditingPolicy { get; set; }

    public string InsuranceFormTitle => Loc.T(EditingPolicy is null ? "Add insurance" : "Edit insurance");

    public bool IsNewInsurance => EditingPolicy is null;

    [ObservableProperty]
    public partial string PolicyInsurer { get; set; } = "";

    [ObservableProperty]
    public partial int PolicyKindIndex { get; set; }

    [ObservableProperty]
    public partial PersonOption PolicyPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial string PolicyNumber { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyEntryText))]
    public partial decimal? PolicyPremium { get; set; }

    /// <summary>Index into <see cref="PremiumRecurrenceNames"/>: monthly, every 3 months, yearly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyEntryText))]
    public partial int PolicyRecurrenceIndex { get; set; } = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyCancelByText))]
    public partial DateTime? PolicyContractEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyCancelByText))]
    public partial bool PolicyRenews { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyCancelByText))]
    public partial decimal? PolicyNoticeMonths { get; set; } = 3;

    [ObservableProperty]
    public partial DocumentOption? PolicyDocument { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DocumentOption> PolicyDocumentOptions { get; set; } = [];

    [ObservableProperty]
    public partial string PolicyNotes { get; set; } = "";

    /// <summary>Also add the premium as a repeating expense, so the runway includes it.</summary>
    [ObservableProperty]
    public partial bool PolicyAddToExpenses { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPolicyError))]
    public partial string? PolicyError { get; set; }

    public bool HasPolicyError => !string.IsNullOrEmpty(PolicyError);

    private Recurrence PolicyRecurrence => PolicyRecurrenceIndex switch
    {
        0 => Recurrence.Monthly,
        1 => Recurrence.Quarterly,
        _ => Recurrence.Yearly,
    };

    /// <summary>When the first premium entry would be due.</summary>
    private DateOnly PolicyEntryStart =>
        PolicyRecurrence == Recurrence.Yearly && PolicyContractEnd is { } end
            ? DateOnly.FromDateTime(end).AddDays(1) is var renewal && renewal > Today ? renewal : new DateOnly(Today.Year, Today.Month, 1).AddMonths(1)
            : new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);

    public string PolicyEntryText => Loc.F("Add the premium to expenses from {0}, so your runway includes it", Format.Date(PolicyEntryStart));

    /// <summary>The deadline the form's values give, shown while editing.</summary>
    public string PolicyCancelByText
    {
        get
        {
            if (PolicyContractEnd is not { } end)
                return Loc.T("Enter when the term ends to see the notice deadline.");
            var policy = new InsurancePolicy
            {
                ContractEnd = DateOnly.FromDateTime(end), RenewsAutomatically = PolicyRenews,
                NoticeMonths = (int)(PolicyNoticeMonths ?? 0),
            };
            return policy.NextDeadline(Today) is { } deadline
                ? Loc.F("Cancel by {0} for the term ending {1}.", Format.Date(deadline.CancelBy), Format.Date(deadline.End))
                : Loc.T("This policy has ended.");
        }
    }

    partial void OnPolicyKindIndexChanged(int value)
    {
        // A new policy gets the usual notice period for its kind.
        if (EditingPolicy is null && Enum.IsDefined((InsuranceKind)value))
        {
            PolicyNoticeMonths = InsurancePolicy.DefaultNoticeMonths((InsuranceKind)value);
            if ((InsuranceKind)value == InsuranceKind.HealthBasic && PolicyContractEnd is null)
                PolicyContractEnd = new DateTime(Today.Year, 12, 31);
        }
    }

    [RelayCommand]
    private void NewInsurance()
    {
        CloseOtherPanels();
        EditingPolicy = null;
        PolicyInsurer = "";
        PolicyNumber = "";
        PolicyPremium = null;
        PolicyRecurrenceIndex = 2;
        PolicyContractEnd = null;
        PolicyRenews = true;
        PolicyKindIndex = 0;
        OnPolicyKindIndexChanged(0);
        PolicyPerson = InsurancePersonFilter is { IsEveryone: false } person ? person : PersonOption.Shared;
        PolicyNotes = "";
        PolicyAddToExpenses = false;
        PolicyError = null;
        RefreshPolicyDocumentOptions(null);
        IsInsurancePanelOpen = true;
    }

    [RelayCommand]
    private void EditInsurance(InsurancePolicy policy)
    {
        CloseOtherPanels();
        EditingPolicy = policy;
        PolicyInsurer = policy.Insurer;
        PolicyKindIndex = (int)policy.Kind;
        PolicyPerson = PersonOptions.FirstOrDefault(o => o.Id == policy.PersonId) ?? PersonOption.Shared;
        PolicyNumber = policy.PolicyNumber;
        PolicyPremium = policy.Premium;
        PolicyRecurrenceIndex = policy.PremiumRecurrence switch { Recurrence.Monthly => 0, Recurrence.Quarterly => 1, _ => 2 };
        PolicyContractEnd = policy.ContractEnd?.ToDateTime(TimeOnly.MinValue);
        PolicyRenews = policy.RenewsAutomatically;
        PolicyNoticeMonths = policy.NoticeMonths;
        PolicyNotes = policy.Notes;
        PolicyAddToExpenses = false;
        PolicyError = null;
        RefreshPolicyDocumentOptions(policy.DocumentId);
        IsInsurancePanelOpen = true;
    }

    private void RefreshPolicyDocumentOptions(int? selectedId)
    {
        PolicyDocumentOptions =
        [
            new DocumentOption(null, ""),
            .. Documents
                .Where(d => d.Kind is DocumentKind.Policy or DocumentKind.Contract || d.Id == selectedId)
                .OrderByDescending(d => d.Date)
                .Select(d => new DocumentOption(d, d.Sender == "" ? d.Title : $"{d.Sender}: {d.Title}")),
        ];
        PolicyDocument = PolicyDocumentOptions.FirstOrDefault(o => o.Document?.Id == selectedId) ?? PolicyDocumentOptions[0];
    }

    [RelayCommand]
    private async Task SaveInsuranceAsync()
    {
        PolicyError = null;
        if (string.IsNullOrWhiteSpace(PolicyInsurer))
        {
            PolicyError = Loc.T("Enter the insurer.");
            return;
        }
        if (PolicyPremium is not >= 0)
        {
            PolicyError = Loc.T("Enter the premium.");
            return;
        }

        var policy = EditingPolicy ?? new InsurancePolicy();
        var values = new InsurancePolicy
        {
            Id = policy.Id,
            Insurer = PolicyInsurer.Trim(),
            Kind = Enum.IsDefined((InsuranceKind)PolicyKindIndex) ? (InsuranceKind)PolicyKindIndex : InsuranceKind.Other,
            PersonId = PolicyPerson.Id,
            PolicyNumber = PolicyNumber.Trim(),
            Premium = PolicyPremium.Value,
            PremiumRecurrence = PolicyRecurrence,
            ContractEnd = PolicyContractEnd is { } end ? DateOnly.FromDateTime(end) : null,
            RenewsAutomatically = PolicyRenews,
            NoticeMonths = Math.Clamp((int)(PolicyNoticeMonths ?? 0), 0, 24),
            Notes = PolicyNotes.Trim(),
            DocumentId = PolicyDocument?.Document?.Id,
        };

        try
        {
            await repository.SaveInsurancePolicyAsync(values);
            if (EditingPolicy is null && PolicyAddToExpenses && values.Premium > 0)
            {
                var entry = await repository.AddTransactionAsync(new Transaction
                {
                    Date = PolicyEntryStart,
                    Description = $"{values.Insurer}: {InsurancePolicy.KindName(values.Kind)}",
                    Category = InsurancePolicy.CategoryFor(values.Kind),
                    Amount = values.Premium,
                    Type = TransactionType.Expense,
                    Recurrence = values.PremiumRecurrence,
                    PersonId = values.PersonId,
                });
                InsertSorted(entry);
                Recalculate();
            }
        }
        catch (Exception ex)
        {
            PolicyError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        _policies.Remove(policy);
        _policies.Add(values);
        CloseInsurancePanel();
        RefreshInsurance();
    }

    [RelayCommand]
    private async Task DeleteInsuranceAsync(InsurancePolicy policy)
    {
        try
        {
            await repository.DeleteInsurancePolicyAsync(policy.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        _policies.Remove(policy);
        if (EditingPolicy == policy)
            CloseInsurancePanel();
        RefreshInsurance();
    }

    [RelayCommand]
    private void OpenPolicyDocument(HouseholdDocument document) => OpenDocument(document);

    private void CloseInsurancePanel()
    {
        IsInsurancePanelOpen = false;
        EditingPolicy = null;
        PolicyError = null;
    }
}

/// <summary>One policy on the Insurance page.</summary>
public sealed record InsuranceRow(InsurancePolicy Policy, string PersonName, HouseholdDocument? Document, DateOnly Today)
{
    private (DateOnly End, DateOnly CancelBy)? Deadline => Policy.NextDeadline(Today);

    public string Insurer => Policy.Insurer;

    public string DetailText => Policy.PolicyNumber == ""
        ? InsurancePolicy.KindName(Policy.Kind)
        : $"{InsurancePolicy.KindName(Policy.Kind)} · {Policy.PolicyNumber}";

    public bool IsShared => Policy.PersonId is null;

    public string PremiumText => Format.Money(Policy.Premium) + Policy.PremiumRecurrence switch
    {
        Recurrence.Monthly => Loc.T(" / mo"),
        Recurrence.Quarterly => Loc.T(" / qtr"),
        _ => Loc.T(" / yr"),
    };

    public string YearlyText => Policy.PremiumRecurrence == Recurrence.Yearly ? "" : Loc.F("{0} a year", Format.Money(Policy.YearlyPremium));

    public bool HasYearly => YearlyText != "";

    public bool HasEnded => Policy.HasEnded(Today);

    public string TermText => HasEnded
        ? Loc.F("Ended {0}", Format.Date(Policy.ContractEnd!.Value))
        : Deadline is { } d
            ? Loc.F(Policy.RenewsAutomatically ? "Renews after {0}" : "Ends {0}", Format.Date(d.End))
            : Loc.T("No end date");

    public string CancelByText => Deadline is { } d && !HasEnded
        ? Loc.F("Cancel by {0}", Format.Date(d.CancelBy))
        : "";

    public bool HasCancelBy => CancelByText != "";

    public int DaysToDeadline => Deadline is { } d ? d.CancelBy.DayNumber - Today.DayNumber : int.MaxValue;

    public bool IsDeadlineUrgent => !HasEnded && DaysToDeadline <= 30;

    public bool IsDeadlineSoon => !HasEnded && !IsDeadlineUrgent && DaysToDeadline <= MainViewModel.InsuranceDeadlineWarningDays;

    public bool HasDocument => Document is not null;
}
