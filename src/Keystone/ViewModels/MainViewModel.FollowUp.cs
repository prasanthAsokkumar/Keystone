using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// A filed paper often belongs on another list too: an insurance policy under Insurance, a mobile contract under
/// Contracts, a passport under Important dates, a receipt under Warranties. Those lists give the reminders
/// (notice deadlines, expiry dates), so filing the paper offers to add or update the record as well.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Shown at the top of a form that was filled in from a document, so it is checked before saving.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFollowUpHint))]
    public partial string FollowUpHint { get; set; } = "";

    public bool HasFollowUpHint => FollowUpHint != "";

    /// <summary>A follow-up for a document whose type says what it is, when the local AI didn't read it.</summary>
    internal static FollowUp? FollowUpForKind(DocumentKind kind) => kind switch
    {
        DocumentKind.Policy => new FollowUp(FollowUpKind.Insurance),
        DocumentKind.Contract => new FollowUp(FollowUpKind.Contract),
        _ => null,
    };

    private static T ParseKind<T>(string name, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(name, ignoreCase: true, out var kind) && Enum.IsDefined(kind) ? kind : fallback;

    private static string CleanNumber(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());

    private static string NameOf(HouseholdDocument document) => document.Sender != "" ? document.Sender : document.Title;

    // ── Finding what is already tracked ─────────────────────────────────────

    /// <summary>The policy this paper is about: same insurer, and the same policy number, or else the same kind and person.</summary>
    private InsurancePolicy? MatchingPolicy(HouseholdDocument document, FollowUp followUp)
    {
        var kind = ParseKind(followUp.Subkind, InsuranceKind.Other);
        return _policies.FirstOrDefault(p => DuplicateFinder.SameSender(p.Insurer, NameOf(document)) &&
            (followUp.Reference != "" && p.PolicyNumber != ""
                ? CleanNumber(p.PolicyNumber) == CleanNumber(followUp.Reference)
                : (followUp.Subkind == "" || p.Kind == kind) && p.PersonId == document.PersonId));
    }

    private Contract? MatchingContract(HouseholdDocument document, FollowUp followUp)
    {
        var kind = ParseKind(followUp.Subkind, ContractKind.Other);
        return Deadlines.Contracts.FirstOrDefault(c => DuplicateFinder.SameSender(c.Provider, NameOf(document)) &&
            (followUp.Reference != "" && c.CustomerNumber != ""
                ? CleanNumber(c.CustomerNumber) == CleanNumber(followUp.Reference)
                : (followUp.Subkind == "" || c.Kind == kind) && c.PersonId == document.PersonId));
    }

    /// <summary>A new passport replaces the date of the old one for the same person.</summary>
    private ImportantDate? MatchingDate(HouseholdDocument document, FollowUp followUp)
    {
        var kind = ParseKind(followUp.Subkind, ImportantDateKind.Other);
        return kind == ImportantDateKind.Other ? null : Deadlines.Dates.FirstOrDefault(d => d.Kind == kind && d.PersonId == document.PersonId);
    }

    // ── The record's values ─────────────────────────────────────────────────

    /// <summary>The policy as the paper describes it; what the paper doesn't say is kept from the existing one.</summary>
    internal InsurancePolicy PolicyFrom(HouseholdDocument document, FollowUp followUp, InsurancePolicy? existing)
    {
        var kind = followUp.Subkind != "" ? ParseKind(followUp.Subkind, InsuranceKind.Other) : existing?.Kind ?? InsuranceKind.Other;
        return new InsurancePolicy
        {
            Id = existing?.Id ?? 0,
            Insurer = existing?.Insurer ?? NameOf(document),
            Kind = kind,
            PersonId = existing?.PersonId ?? document.PersonId,
            PolicyNumber = followUp.Reference != "" ? followUp.Reference : existing?.PolicyNumber ?? "",
            Premium = followUp.Cost ?? existing?.Premium ?? 0,
            PremiumRecurrence = followUp.Frequency is Recurrence.Monthly or Recurrence.Quarterly or Recurrence.Yearly
                ? followUp.Frequency.Value
                : existing?.PremiumRecurrence ?? Recurrence.Yearly,
            // Basic health insurance always runs to the end of the year.
            ContractEnd = followUp.EndDate ?? existing?.ContractEnd ??
                          (kind == InsuranceKind.HealthBasic ? new DateOnly(Today.Year, 12, 31) : null),
            RenewsAutomatically = existing?.RenewsAutomatically ?? true,
            NoticeMonths = followUp.NoticeMonths ?? existing?.NoticeMonths ?? InsurancePolicy.DefaultNoticeMonths(kind),
            Notes = existing?.Notes ?? "",
            Deductible = existing?.Deductible,
            DocumentId = document.Id,
        };
    }

    internal Contract ContractFrom(HouseholdDocument document, FollowUp followUp, Contract? existing)
    {
        var kind = followUp.Subkind != "" ? ParseKind(followUp.Subkind, ContractKind.Other) : existing?.Kind ?? ContractKind.Other;
        return new Contract
        {
            Id = existing?.Id ?? 0,
            Provider = existing?.Provider ?? NameOf(document),
            Kind = kind,
            PersonId = existing?.PersonId ?? document.PersonId,
            CustomerNumber = followUp.Reference != "" ? followUp.Reference : existing?.CustomerNumber ?? "",
            Cost = followUp.Cost ?? existing?.Cost ?? 0,
            CostRecurrence = followUp.Frequency is Recurrence.Monthly or Recurrence.Quarterly or Recurrence.Yearly
                ? followUp.Frequency.Value
                : existing?.CostRecurrence ?? Recurrence.Monthly,
            ContractEnd = followUp.EndDate ?? existing?.ContractEnd,
            RenewsAutomatically = existing?.RenewsAutomatically ?? true,
            NoticeMonths = followUp.NoticeMonths ?? existing?.NoticeMonths ?? Contract.DefaultNoticeMonths(kind),
            Notes = existing?.Notes ?? "",
            DocumentId = document.Id,
        };
    }

    internal ImportantDate? DateFrom(HouseholdDocument document, FollowUp followUp, ImportantDate? existing)
    {
        if (followUp.EndDate is not { } date)
            return null;
        var kind = ParseKind(followUp.Subkind, ImportantDateKind.Other);
        var (remind, repeat) = ImportantDate.Defaults(kind);
        return new ImportantDate
        {
            Id = existing?.Id ?? 0,
            Title = existing?.Title ?? (kind == ImportantDateKind.Other ? document.Title : ImportantDate.KindName(kind)),
            Kind = kind,
            PersonId = document.PersonId,
            Date = date,
            RepeatMonths = existing?.RepeatMonths ?? repeat,
            RemindDaysBefore = existing?.RemindDaysBefore ?? remind,
            Notes = Loc.F("From “{0}”", document.Title),
        };
    }

    internal static Warranty WarrantyFrom(HouseholdDocument document, FollowUp followUp) => new()
    {
        Item = followUp.Item != "" ? followUp.Item : document.Title,
        Store = document.Sender,
        PersonId = document.PersonId,
        PurchaseDate = document.Date,
        Price = followUp.Cost ?? document.Amount,
        Months = Warranty.DefaultMonths,
        DocumentId = document.Id,
    };

    // ── Describing it ───────────────────────────────────────────────────────

    /// <summary>The checkbox text, e.g. "Also add to Insurance: Helsana · CHF 412 / month · ends 31 Dec 2026".</summary>
    internal string FollowUpText(HouseholdDocument document, FollowUp followUp)
    {
        static string Every(Recurrence? frequency) => frequency switch
        {
            Recurrence.Monthly => Loc.T(" / month"),
            Recurrence.Quarterly => Loc.T(" / quarter"),
            Recurrence.Yearly => Loc.T(" / year"),
            _ => "",
        };

        var parts = new List<string>();
        string label;
        switch (followUp.Kind)
        {
            case FollowUpKind.Insurance:
            {
                var existing = MatchingPolicy(document, followUp);
                var policy = PolicyFrom(document, followUp, existing);
                label = existing is null ? Loc.T("Also add to Insurance") : Loc.F("Also update “{0}” in Insurance", existing.Insurer);
                parts.Add(existing is null ? policy.Insurer : InsurancePolicy.KindName(policy.Kind));
                if (existing is null && followUp.Subkind != "")
                    parts.Add(InsurancePolicy.KindName(policy.Kind));
                if (followUp.Cost is { } premium)
                    parts.Add(Format.Money(premium) + Every(policy.PremiumRecurrence));
                if (policy.NextDeadline(Today) is { } deadline)
                    parts.Add(Loc.F("cancel by {0}", Format.Date(deadline.CancelBy)));
                break;
            }
            case FollowUpKind.Contract:
            {
                var existing = MatchingContract(document, followUp);
                var contract = ContractFrom(document, followUp, existing);
                label = existing is null ? Loc.T("Also add to Contracts") : Loc.F("Also update “{0}” in Contracts", existing.Provider);
                parts.Add(existing is null ? contract.Provider : Contract.KindName(contract.Kind));
                if (existing is null && followUp.Subkind != "")
                    parts.Add(Contract.KindName(contract.Kind));
                if (followUp.Cost is { } cost)
                    parts.Add(Format.Money(cost) + Every(contract.CostRecurrence));
                if (contract.NextDeadline(Today) is { } deadline)
                    parts.Add(Loc.F("cancel by {0}", Format.Date(deadline.CancelBy)));
                break;
            }
            case FollowUpKind.Date:
            {
                var existing = MatchingDate(document, followUp);
                label = existing is null ? Loc.T("Also add to Important dates") : Loc.F("Also update “{0}” in Important dates", existing.Title);
                var kind = ParseKind(followUp.Subkind, ImportantDateKind.Other);
                parts.Add(kind == ImportantDateKind.Other ? document.Title : ImportantDate.KindName(kind));
                parts.Add(followUp.EndDate is { } date ? Format.Date(date) : Loc.T("enter the date there"));
                break;
            }
            default:
            {
                var warranty = WarrantyFrom(document, followUp);
                label = Loc.T("Also add to Warranties");
                parts.Add(warranty.Item);
                parts.Add(Loc.F("until {0}", Format.Date(warranty.ExpiresOn)));
                break;
            }
        }
        return $"{label}: {string.Join(" · ", parts.Where(p => p != ""))}";
    }

    // ── Saving it ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds or updates the record straight away, for documents filed several at once. Returns what was saved,
    /// e.g. "Helsana", or null when the paper didn't say enough (an important date without a date).
    /// </summary>
    internal async Task<string?> ApplyFollowUpAsync(HouseholdDocument document, FollowUp followUp)
    {
        switch (followUp.Kind)
        {
            case FollowUpKind.Insurance:
            {
                var insurer = PolicyFrom(document, followUp, MatchingPolicy(document, followUp)).Insurer;
                await SavePolicyFromDocumentAsync(document, followUp);
                return insurer;
            }
            case FollowUpKind.Contract:
            {
                var existing = MatchingContract(document, followUp);
                var contract = ContractFrom(document, followUp, existing);
                await repository.SaveAsync(contract, existing is null);
                Replace(Deadlines.Contracts, existing, contract);
                Deadlines.RefreshDeadlines();
                return contract.Provider;
            }
            case FollowUpKind.Date:
            {
                var existing = MatchingDate(document, followUp);
                if (DateFrom(document, followUp, existing) is not { } date)
                    return null;
                await repository.SaveAsync(date, existing is null);
                Replace(Deadlines.Dates, existing, date);
                Deadlines.RefreshDeadlines();
                return date.Title;
            }
            default:
            {
                var warranty = WarrantyFrom(document, followUp);
                await repository.SaveAsync(warranty, isNew: true);
                Deadlines.Warranties.Add(warranty);
                Deadlines.RefreshDeadlines();
                return warranty.Item;
            }
        }
    }

    private static void Replace<T>(List<T> list, T? existing, T updated) where T : class
    {
        var index = existing is null ? -1 : list.IndexOf(existing);
        if (index >= 0)
            list[index] = updated;
        else
            list.Add(updated);
    }

    /// <summary>
    /// Opens the Insurance or Contracts &amp; dates form filled in from the document just filed, on its page,
    /// so it is checked and saved by hand. An existing record is opened for editing instead of adding a second one.
    /// </summary>
    internal void OpenFollowUp(HouseholdDocument document, FollowUp followUp)
    {
        var person = PersonOptions.FirstOrDefault(o => o.Id == document.PersonId) ?? PersonOption.Shared;
        bool updates;
        switch (followUp.Kind)
        {
            case FollowUpKind.Insurance:
            {
                var existing = MatchingPolicy(document, followUp);
                var policy = PolicyFrom(document, followUp, existing);
                if (existing is null)
                    NewInsurance();
                else
                    EditInsurance(existing);
                PolicyInsurer = policy.Insurer;
                PolicyKindIndex = (int)policy.Kind; // sets the usual notice period for a new one…
                PolicyPerson = PersonOptions.FirstOrDefault(o => o.Id == policy.PersonId) ?? PersonOption.Shared;
                PolicyNumber = policy.PolicyNumber;
                PolicyPremium = policy.Premium > 0 ? policy.Premium : null;
                PolicyRecurrenceIndex = policy.PremiumRecurrence switch { Recurrence.Monthly => 0, Recurrence.Quarterly => 1, _ => 2 };
                PolicyContractEnd = policy.ContractEnd?.ToDateTime(TimeOnly.MinValue);
                PolicyNoticeMonths = policy.NoticeMonths; // …so the paper's own notice period comes after
                RefreshPolicyDocumentOptions(document.Id);
                CurrentPage = AppPage.Insurance;
                updates = existing is not null;
                break;
            }
            case FollowUpKind.Contract:
            {
                var existing = MatchingContract(document, followUp);
                var contract = ContractFrom(document, followUp, existing);
                if (existing is null)
                    Deadlines.NewContract();
                else
                    Deadlines.EditContract(existing);
                Deadlines.ContractProvider = contract.Provider;
                Deadlines.ContractKindIndex = (int)contract.Kind;
                Deadlines.ContractPerson = PersonOptions.FirstOrDefault(o => o.Id == contract.PersonId) ?? PersonOption.Shared;
                Deadlines.ContractCustomerNumber = contract.CustomerNumber;
                Deadlines.ContractCost = contract.Cost > 0 ? contract.Cost : null;
                Deadlines.ContractRecurrenceIndex = contract.CostRecurrence switch { Recurrence.Quarterly => 1, Recurrence.Yearly => 2, _ => 0 };
                Deadlines.ContractEnd = contract.ContractEnd?.ToDateTime(TimeOnly.MinValue);
                Deadlines.ContractNoticeMonths = contract.NoticeMonths;
                Deadlines.ContractDocumentOptions = DocumentOptionsFor(document.Id, DocumentKind.Contract);
                Deadlines.ContractDocument = Deadlines.ContractDocumentOptions.FirstOrDefault(o => o.Document?.Id == document.Id) ?? Deadlines.ContractDocumentOptions[0];
                CurrentPage = AppPage.Contracts;
                Deadlines.DeadlinesTab = 0;
                updates = existing is not null;
                break;
            }
            case FollowUpKind.Date:
            {
                var existing = MatchingDate(document, followUp);
                if (existing is null)
                    Deadlines.NewImportantDate();
                else
                    Deadlines.EditImportantDate(existing);
                Deadlines.ImportantDateKindIndex = (int)ParseKind(followUp.Subkind, ImportantDateKind.Other);
                Deadlines.ImportantDatePerson = person;
                if (followUp.EndDate is { } date)
                    Deadlines.ImportantDateDate = date.ToDateTime(TimeOnly.MinValue);
                if (Deadlines.ImportantDateTitle == "")
                    Deadlines.ImportantDateTitle = document.Title;
                CurrentPage = AppPage.Contracts;
                Deadlines.DeadlinesTab = 1;
                updates = existing is not null;
                break;
            }
            default:
            {
                var warranty = WarrantyFrom(document, followUp);
                Deadlines.NewWarranty();
                Deadlines.WarrantyItem = warranty.Item;
                Deadlines.WarrantyStore = warranty.Store;
                Deadlines.WarrantyPerson = person;
                Deadlines.WarrantyPurchaseDate = warranty.PurchaseDate.ToDateTime(TimeOnly.MinValue);
                Deadlines.WarrantyPrice = warranty.Price;
                Deadlines.WarrantyDocumentOptions = DocumentOptionsFor(document.Id, DocumentKind.Invoice, DocumentKind.Bill);
                Deadlines.WarrantyDocument = Deadlines.WarrantyDocumentOptions.FirstOrDefault(o => o.Document?.Id == document.Id) ?? Deadlines.WarrantyDocumentOptions[0];
                CurrentPage = AppPage.Contracts;
                Deadlines.DeadlinesTab = 2;
                updates = false;
                break;
            }
        }

        FollowUpHint = updates
            ? Loc.F("Updated from “{0}”. Check the changes, then save to keep the reminders right.", document.Title)
            : Loc.F("Filled in from “{0}”. Check it, then save to get the reminders.", document.Title);
    }
}
