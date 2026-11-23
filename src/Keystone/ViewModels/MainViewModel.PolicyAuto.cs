using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// A filed insurance policy (or a premium letter for one) is put to work straight away: the policy is added under
/// Insurance or updated there, its premium becomes a repeating expense (or the one already there changes from the
/// new term on), and the paper is listed with the insurance papers. A notice says what was done and can undo it.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// When a premium starts counting in the expenses: a new term that hasn't begun yet starts on its first day;
    /// otherwise the next payment after today in the current term (or the first of next month, without a term end).
    /// Earlier payments are left out, as the bank balance already has them.
    /// </summary>
    internal static DateOnly PremiumStart(Recurrence recurrence, DateOnly? termEnd, DateOnly today)
    {
        var nextMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        if (termEnd is not { } end)
            return nextMonth;
        var termStart = end.AddYears(-1).AddDays(1);
        if (termStart > today)
            return termStart;
        var months = recurrence switch { Recurrence.Monthly => 1, Recurrence.Quarterly => 3, _ => 12 };
        var date = termStart;
        for (var i = 1; date <= today; i++)
            date = termStart.AddMonths(i * months);
        return date;
    }

    private static readonly string[] GenericInsurerWords =
        ["versicherung", "versicherungen", "insurance", "assurance", "assurances", "assicurazioni", "krankenkasse", "krankenversicherung", "ag", "sa", "gmbh", "die", "la", "le", "group", "gruppe", "schweiz", "suisse"];

    /// <summary>The insurer's distinctive words, e.g. "Helsana" of "Helsana Versicherungen AG".</summary>
    private static bool NamesInsurer(string text, string insurer) =>
        Regex.Split(insurer, @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3 && !GenericInsurerWords.Contains(w.ToLowerInvariant()))
            .Any(w => Regex.IsMatch(text, $@"(?<![\p{{L}}]){Regex.Escape(w)}(?![\p{{L}}])", RegexOptions.IgnoreCase));

    /// <summary>
    /// The repeating expense that pays this policy's premium: a running insurance expense naming the insurer, or,
    /// failing that, one with the premium the policy had (or has), which is the same payment under another name.
    /// </summary>
    private Transaction? PremiumEntryOf(InsurancePolicy policy, InsurancePolicy? before)
    {
        var running = Transactions.Where(t => t is { Type: TransactionType.Expense, IsRecurring: true } &&
                                              Categories.IsInsurance(t.Category) && (t.EndDate is null || t.EndDate >= Today))
            .OrderByDescending(t => t.Date)
            .ToList();
        var kindName = InsurancePolicy.KindName(policy.Kind);
        return running.Where(t => NamesInsurer(t.Description, policy.Insurer))
                   .OrderByDescending(t => t.Description.Contains(kindName, StringComparison.CurrentCultureIgnoreCase))
                   .ThenByDescending(t => t.PersonId == policy.PersonId)
                   .FirstOrDefault() ??
               running.FirstOrDefault(t => t.PersonId == policy.PersonId && t.Recurrence == (before ?? policy).PremiumRecurrence &&
                                           t.Amount == (before ?? policy).Premium);
    }

    /// <summary>Adds or updates the policy a filed paper describes, with its premium in the expenses. Returns what was done and how to undo it.</summary>
    internal async Task<(string Text, Func<Task> Undo)> SavePolicyFromDocumentAsync(HouseholdDocument document, FollowUp followUp)
    {
        var existing = MatchingPolicy(document, followUp);
        var policy = PolicyFrom(document, followUp, existing);
        await repository.SaveInsurancePolicyAsync(policy);
        Replace(_policies, existing, policy);

        // The premium: added, changed from the new term on, or already there.
        Transaction? added = null, stopped = null;
        DateOnly? stoppedEnd = null;
        var current = PremiumEntryOf(policy, existing);
        var start = PremiumStart(policy.PremiumRecurrence, policy.ContractEnd, Today);
        // Without the AI, or when the paper doesn't say, the premium is entered under Insurance.
        var premiumText = policy.Premium > 0 ? "" : Loc.T("add its premium there");
        if (policy.Premium > 0 && current is { } same && same.Amount == policy.Premium && same.Recurrence == policy.PremiumRecurrence)
            premiumText = Loc.T("premium already in your expenses");
        else if (policy.Premium > 0)
        {
            if (current is not null)
            {
                (stopped, stoppedEnd) = (current, current.EndDate);
                current.EndDate = start.AddDays(-1);
                await repository.SetEndDateAsync(current.Id, current.EndDate);
            }
            added = await repository.AddTransactionAsync(new Transaction
            {
                Date = start,
                Description = current?.Description ?? $"{policy.Insurer}: {InsurancePolicy.KindName(policy.Kind)}",
                Category = current?.Category ?? InsurancePolicy.CategoryFor(policy.Kind),
                Amount = policy.Premium,
                Type = TransactionType.Expense,
                Recurrence = policy.PremiumRecurrence,
                PersonId = policy.PersonId,
                AccountId = current?.AccountId,
            });
            InsertSorted(added);
            premiumText = Loc.F(current is null ? "premium {0} in your expenses from {1}" : "new premium {0} in your expenses from {1}",
                Format.Money(policy.Premium) + Every(policy.PremiumRecurrence), Format.Date(start));
        }

        // The paper is one of the insurance papers from now on.
        var section = document.Section;
        if (!IsInsuranceDocument(document))
        {
            document.Section = "Insurance";
            await repository.UpdateDocumentAsync(document);
        }

        Recalculate();
        RefreshInsurance();
        RefreshDocumentRows();
        ScheduleReminders();

        var text = Loc.F(existing is null ? "Added {0} to Insurance" : "Updated {0} in Insurance", policy.Insurer) +
                   (premiumText == "" ? "" : " · " + premiumText);
        return (text, async () =>
        {
            if (existing is null)
            {
                await repository.DeleteInsurancePolicyAsync(policy.Id);
                _policies.Remove(policy);
            }
            else
            {
                await repository.SaveInsurancePolicyAsync(existing);
                Replace(_policies, policy, existing);
            }
            if (added is not null)
            {
                await repository.DeleteTransactionAsync(added.Id);
                Transactions.Remove(added);
            }
            if (stopped is not null)
            {
                stopped.EndDate = stoppedEnd;
                await repository.SetEndDateAsync(stopped.Id, stoppedEnd);
            }
            if (document.Section != section)
            {
                document.Section = section;
                await repository.UpdateDocumentAsync(document);
            }
            Recalculate();
            RefreshInsurance();
            RefreshDocumentRows();
            ScheduleReminders();
        });
    }

    private static string Every(Recurrence? frequency) => frequency switch
    {
        Recurrence.Monthly => Loc.T(" / month"),
        Recurrence.Quarterly => Loc.T(" / quarter"),
        Recurrence.Yearly => Loc.T(" / year"),
        _ => "",
    };

    /// <summary>For tests: what the local AI would have read from the paper.</summary>
    internal void SetDocumentFollowUpForTest(FollowUp followUp)
    {
        _documentAiFollowUp = followUp;
        RefreshDocumentFollowUp();
    }

    /// <summary>Under the follow-up checkbox of the document form: what saving does.</summary>
    public string DocumentFollowUpNote => DocumentFollowUp?.Kind == FollowUpKind.Insurance
        ? Loc.T("Saved under Insurance when you save this document, with its premium in your expenses. You can undo it from the notice.")
        : Loc.T("After saving, its form opens filled in, so you can check it and get the reminders.");
}
