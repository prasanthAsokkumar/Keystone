using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>A choice of account; <see cref="Account"/> is null for "all accounts" in filters.</summary>
public sealed record AccountOption(BankAccount? Account)
{
    public static AccountOption All { get; } = new((BankAccount?)null);

    public override string ToString() => Account?.Name ?? Loc.T("All accounts");
}

/// <summary>An account with its balance today, for the Overview and Settings.</summary>
public sealed record AccountRow(BankAccount Account, decimal Balance, bool IsPrimary)
{
    public string Name => Account.Name;

    public string KindText => BankAccount.KindName(Account.Kind);

    public string DetailText => Account.Iban == "" ? KindText : $"{KindText} · {BankAccount.FormatIban(Account.Iban)}";

    public string BalanceText => Format.Money(Balance);

    public bool IsNegative => Balance < 0;

    public bool HasOverdraft => Account.OverdraftLimit > 0;

    public string OverdraftText => Account.Kind == BankAccountKind.CreditCard
        ? Loc.F("Limit {0}", Format.Money(Account.OverdraftLimit))
        : Loc.F("Overdraft {0}", Format.Money(Account.OverdraftLimit));
}

/// <summary>Bank accounts, transfers between them, and importing bank statements.</summary>
public partial class MainViewModel
{
    public ObservableCollection<BankAccount> BankAccounts { get; } = [];

    /// <summary>The account entries without an account belong to: the first one.</summary>
    public BankAccount? PrimaryAccount => BankAccounts.FirstOrDefault();

    public bool HasSeveralAccounts => BankAccounts.Count > 1;

    public IReadOnlyList<string> AccountKindNames => Enum.GetValues<BankAccountKind>().Select(BankAccount.KindName).ToList();

    [ObservableProperty]
    public partial IReadOnlyList<AccountOption> AccountOptions { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AccountOption> AccountFilters { get; set; } = [AccountOption.All];

    [ObservableProperty]
    public partial AccountOption AccountFilter { get; set; } = AccountOption.All;

    partial void OnAccountFilterChanged(AccountOption value) => RefreshEntryRows();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountRows))]
    public partial IReadOnlyList<AccountRow> AccountRows { get; set; } = [];

    public bool HasAccountRows => AccountRows.Count > 0;

    /// <summary>The household's cash before any entry: every account's opening balance.</summary>
    private decimal TotalOpeningBalance => BankAccounts.Sum(a => a.OpeningBalance);

    public decimal TotalOverdraft => BankAccounts.Sum(a => a.OverdraftLimit);

    public bool HasOverdraft => TotalOverdraft > 0;

    public string OverdraftText => Loc.F("Overdraft available: {0}", Format.Money(TotalOverdraft));

    /// <summary>How far the overdraft would stretch the runway, shown under it.</summary>
    [ObservableProperty]
    public partial string OverdraftRunwayText { get; set; } = "";

    public bool HasOverdraftRunway => OverdraftRunwayText != "";

    partial void OnOverdraftRunwayTextChanged(string value) => OnPropertyChanged(nameof(HasOverdraftRunway));

    private int AccountIdOf(Transaction transaction) => transaction.AccountId ?? PrimaryAccount?.Id ?? 0;

    /// <summary>Whether an entry moves money in or out of <paramref name="accountId"/>.</summary>
    private bool Touches(Transaction transaction, int accountId) =>
        AccountIdOf(transaction) == accountId || (transaction.IsTransfer && transaction.ToAccountId == accountId);

    /// <summary>What an entry does to one account's balance.</summary>
    private decimal EffectOn(Transaction transaction, int accountId) =>
        EffectOn(new Occurrence(transaction.Date, transaction), accountId);

    /// <summary>What one occurrence does to one account's balance, with its own amount.</summary>
    private decimal EffectOn(Occurrence occurrence, int accountId)
    {
        var transaction = occurrence.Source;
        var effect = 0m;
        if (AccountIdOf(transaction) == accountId)
            effect += transaction.IsTransfer ? -occurrence.Amount : occurrence.SignedAmount;
        if (transaction.IsTransfer && transaction.ToAccountId == accountId)
            effect += occurrence.Amount;
        return effect;
    }

    /// <summary>An account's balance at the end of <paramref name="date"/>, debt payments included.</summary>
    public decimal AccountBalance(BankAccount account, DateOnly date)
    {
        var entries = _entries.Where(t => Touches(t, account.Id)).ToList();
        if (entries.Count == 0)
            return account.OpeningBalance;
        var first = entries.Min(t => t.Date);
        return account.OpeningBalance +
               Schedule.Expand(entries, first, date).Sum(o => EffectOn(o, account.Id));
    }

    private async Task LoadAccountsAsync(AppSettings settings)
    {
        BankAccounts.Clear();
        var accounts = await repository.GetBankAccountsAsync();
        if (accounts.Count == 0)
        {
            // Before accounts existed, there was one starting balance; it becomes the main account's.
            accounts.Add(await repository.SaveBankAccountAsync(new BankAccount
            {
                Name = Loc.T("Main account"),
                Kind = BankAccountKind.Current,
                OpeningBalance = settings.StartingBalance,
            }));
        }
        foreach (var account in accounts)
            BankAccounts.Add(account);
        RefreshAccountOptions();
    }

    private void RefreshAccountOptions()
    {
        var filterId = AccountFilter.Account?.Id;
        var newId = NewAccount?.Account?.Id;
        var toId = NewToAccount?.Account?.Id;
        var balanceId = BalanceAccount?.Account?.Id;
        var importId = ImportAccount?.Account?.Id;

        AccountOptions = BankAccounts.Select(a => new AccountOption(a)).ToList();
        AccountFilters = [AccountOption.All, .. AccountOptions];
        AccountOption Pick(int? id) => AccountOptions.FirstOrDefault(o => o.Account!.Id == id) ?? AccountOptions.FirstOrDefault()!;

        AccountFilter = AccountFilters.FirstOrDefault(o => o.Account?.Id == filterId) ?? AccountOption.All;
        NewAccount = Pick(newId);
        NewToAccount = toId is null ? AccountOptions.Skip(1).FirstOrDefault() ?? Pick(null) : Pick(toId);
        BalanceAccount = Pick(balanceId);
        ImportAccount = Pick(importId);
        OnPropertyChanged(nameof(HasSeveralAccounts));
        OnPropertyChanged(nameof(PrimaryAccount));
    }

    /// <summary>Balances per account and what the overdraft adds to the runway.</summary>
    private void RefreshAccounts()
    {
        AccountRows = BankAccounts.Select(a => new AccountRow(a, AccountBalance(a, Today), a == PrimaryAccount)).ToList();
        OnPropertyChanged(nameof(HasOverdraft));
        OnPropertyChanged(nameof(OverdraftText));
        OnPropertyChanged(nameof(TotalOverdraft));

        OverdraftRunwayText = "";
        if (TotalOverdraft > 0 && Summary.ZeroCashDate is not null)
        {
            var withOverdraft = RunwayCalculator.Calculate(
                TotalOpeningBalance + TotalOverdraft, _entries, Today, financialYearStartMonth: FinancialYearStartIndex + 1);
            OverdraftRunwayText = withOverdraft.ZeroCashDate is { } date
                ? Loc.F("With your overdraft of {0}: until {1}", Format.Money(TotalOverdraft), Format.Date(date))
                : Loc.F("Your overdraft of {0} would cover the gap", Format.Money(TotalOverdraft));
        }
    }

    // ── Matching the bank ───────────────────────────────────────────────────

    /// <summary>The account "Match my bank balance" applies to.</summary>
    [ObservableProperty]
    public partial AccountOption? BalanceAccount { get; set; }

    /// <summary>Sets the account's opening balance so that its balance today matches the bank.</summary>
    [RelayCommand]
    private async Task MatchBankBalanceAsync()
    {
        if (BankBalance is not { } bank || (BalanceAccount?.Account ?? PrimaryAccount) is not { } account)
            return;

        await SetBalanceOnAsync(account, bank, Today);
        BankBalance = null;
    }

    /// <summary>Changes an account's opening balance so it has <paramref name="balance"/> at the end of <paramref name="date"/>.</summary>
    private async Task SetBalanceOnAsync(BankAccount account, decimal balance, DateOnly date)
    {
        var previous = account.OpeningBalance;
        account.OpeningBalance += balance - AccountBalance(account, date);
        try
        {
            await repository.SaveBankAccountAsync(account);
        }
        catch (Exception ex)
        {
            account.OpeningBalance = previous;
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        Recalculate();
    }

    // ── Account panel ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsAccountPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountFormTitle), nameof(IsNewAccount))]
    public partial BankAccount? EditingAccount { get; set; }

    public string AccountFormTitle => Loc.T(EditingAccount is null ? "Add account" : "Edit account");

    public bool IsNewAccount => EditingAccount is null;

    [ObservableProperty]
    public partial string AccountName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverdraftLabel))]
    public partial int AccountKindIndex { get; set; }

    [ObservableProperty]
    public partial string AccountIban { get; set; } = "";

    /// <summary>For a new account: what the bank shows today.</summary>
    [ObservableProperty]
    public partial decimal? AccountBalanceToday { get; set; }

    [ObservableProperty]
    public partial decimal? AccountOverdraft { get; set; }

    public string OverdraftLabel => Loc.T((BankAccountKind)AccountKindIndex == BankAccountKind.CreditCard
        ? "Credit limit"
        : "Overdraft limit (optional)");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountError))]
    public partial string? AccountError { get; set; }

    public bool HasAccountError => !string.IsNullOrEmpty(AccountError);

    [RelayCommand]
    private void NewBankAccount()
    {
        CloseOtherPanels();
        EditingAccount = null;
        AccountName = "";
        AccountKindIndex = 0;
        AccountIban = "";
        AccountBalanceToday = null;
        AccountOverdraft = null;
        AccountError = null;
        IsAccountPanelOpen = true;
    }

    [RelayCommand]
    private void EditBankAccount(BankAccount account)
    {
        CloseOtherPanels();
        EditingAccount = account;
        AccountName = account.Name;
        AccountKindIndex = (int)account.Kind;
        AccountIban = account.Iban == "" ? "" : BankAccount.FormatIban(account.Iban);
        AccountOverdraft = account.OverdraftLimit == 0 ? null : account.OverdraftLimit;
        AccountError = null;
        IsAccountPanelOpen = true;
    }

    [RelayCommand]
    private async Task SaveBankAccountAsync()
    {
        AccountError = null;
        var name = AccountName.Trim();
        if (name == "")
        {
            AccountError = Loc.T("Enter a name, e.g. the bank.");
            return;
        }
        if (BankAccounts.Any(a => a != EditingAccount && a.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            AccountError = Loc.F("“{0}” is already in the list.", name);
            return;
        }

        var account = EditingAccount ?? new BankAccount { OpeningBalance = AccountBalanceToday ?? 0 };
        var before = (account.Name, account.Kind, account.Iban, account.OverdraftLimit);
        account.Name = name;
        account.Kind = Enum.IsDefined((BankAccountKind)AccountKindIndex) ? (BankAccountKind)AccountKindIndex : BankAccountKind.Current;
        account.Iban = BankAccount.NormalizeIban(AccountIban);
        account.OverdraftLimit = Math.Max(0, AccountOverdraft ?? 0);
        try
        {
            await repository.SaveBankAccountAsync(account);
        }
        catch (Exception ex)
        {
            (account.Name, account.Kind, account.Iban, account.OverdraftLimit) = before;
            AccountError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (EditingAccount is null)
            BankAccounts.Add(account);
        CloseAccountPanel();
        RefreshAccountOptions();
        Recalculate();
    }

    /// <summary>Removes an account that has no entries; the main account always stays.</summary>
    [RelayCommand]
    private async Task DeleteBankAccountAsync(BankAccount account)
    {
        ErrorMessage = null;
        if (account == PrimaryAccount)
        {
            ErrorMessage = Loc.T("The main account can’t be removed.");
            return;
        }
        if (Transactions.Any(t => Touches(t, account.Id)))
        {
            ErrorMessage = Loc.F("“{0}” still has entries. Move or delete them first.", account.Name);
            return;
        }

        try
        {
            await repository.DeleteBankAccountAsync(account.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        BankAccounts.Remove(account);
        RefreshAccountOptions();
        Recalculate();
    }

    private void CloseAccountPanel()
    {
        IsAccountPanelOpen = false;
        EditingAccount = null;
        AccountError = null;
    }

    // ── Entry form ──────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial AccountOption? NewAccount { get; set; }

    [ObservableProperty]
    public partial AccountOption? NewToAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewIsExpense), nameof(AccountLabel))]
    public partial bool NewIsTransfer { get; set; }

    public bool NewIsExpense
    {
        get => !NewIsIncome && !NewIsTransfer;
        set
        {
            if (!value)
                return;
            NewIsIncome = false;
            NewIsTransfer = false;
        }
    }

    public string AccountLabel => Loc.T(NewIsTransfer ? "From account" : "Account");

    partial void OnNewIsIncomeChanged(bool value)
    {
        if (value)
            NewIsTransfer = false;
        OnPropertyChanged(nameof(NewIsExpense));
    }

    partial void OnNewIsTransferChanged(bool value)
    {
        if (value)
            NewIsIncome = false;
    }

    // ── Bank statement import ───────────────────────────────────────────────

    /// <summary>The import panel shows a bank statement rather than entries read by the AI.</summary>
    [ObservableProperty]
    public partial bool IsBankImport { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportBalanceText))]
    public partial AccountOption? ImportAccount { get; set; }

    private BankStatement? _importStatement;

    /// <summary>Also set the account's balance to the statement's closing balance.</summary>
    [ObservableProperty]
    public partial bool ImportMatchBalance { get; set; } = true;

    public bool HasImportBalance => IsBankImport && _importStatement?.ClosingBalance is not null;

    public string ImportBalanceText => _importStatement is { ClosingBalance: { } balance, ClosingDate: { } date }
        ? Loc.F("Set {0} to the statement’s balance: {1} on {2}", ImportAccount?.Account?.Name ?? "", Format.Money(balance), Format.Date(date))
        : "";

    public static bool IsBankStatement(string path) => BankStatementParser.LooksLikeStatement(path);

    /// <summary>Reads a camt or CSV bank statement and lists its bookings for review.</summary>
    public async Task ImportBankStatementAsync(string path)
    {
        CloseOtherPanels();
        SetImportItems([]);
        ImportError = null;
        ImportNote = "";
        ImportFileName = Path.GetFileName(path);
        IsBankImport = true;
        IsImportPanelOpen = true;
        IsImporting = true;
        ImportStatus = Loc.T("Reading the bank statement…");

        BankStatement statement;
        try
        {
            statement = await Task.Run(() => BankStatementParser.Parse(path));
        }
        catch (Exception ex)
        {
            ImportError = ex is InvalidOperationException ? ex.Message : Loc.F("Could not read {0}: {1}", ImportFileName, ex.Message);
            IsImporting = false;
            return;
        }

        _importStatement = statement;
        var account = BankAccounts.FirstOrDefault(a => a.Iban != "" && a.Iban == statement.Iban) ?? PrimaryAccount;
        ImportAccount = AccountOptions.FirstOrDefault(o => o.Account == account) ?? ImportAccount;
        ImportMatchBalance = statement.ClosingBalance is not null;

        var items = statement.Lines.Select(line => ToImportItem(line, account!.Id)).ToList();
        SetImportItems(items);
        IsImporting = false;

        var duplicates = items.Count(i => i.IsDuplicate);
        var transfers = items.Count(i => i.IsTransfer);
        ImportNote = string.Join(" ", new[]
        {
            items.Count == 0 ? Loc.T("This statement has no bookings.") : "",
            duplicates > 0
                ? Loc.Plural(duplicates, "{0} booking is already in Keystone and is unticked.",
                    "{0} bookings are already in Keystone and are unticked.")
                : "",
            transfers > 0
                ? Loc.Plural(transfers, "{0} booking goes to one of your own accounts and is marked as a transfer.",
                    "{0} bookings go to your own accounts and are marked as transfers.")
                : "",
            statement.Iban is { } iban && account?.Iban != iban
                ? Loc.F("The statement is for {0}; choose the account it belongs to.", BankAccount.FormatIban(iban))
                : "",
        }.Where(n => n != ""));
        OnPropertyChanged(nameof(HasImportBalance));
        OnPropertyChanged(nameof(ImportBalanceText));
    }

    private ImportItem ToImportItem(BankLine line, int accountId)
    {
        var isIncome = line.Amount > 0;
        var amount = Math.Abs(line.Amount);
        var entry = new ExtractedEntry(line.Description == "" ? Loc.T("Bank booking") : line.Description, amount, isIncome,
            line.Date, Recurrence.OneTime, Categories.Display(CategoryGuesser.Guess(line.Description, isIncome, Transactions)));
        var counter = line.CounterpartyIban is { } iban
            ? BankAccounts.FirstOrDefault(a => a.Id != accountId && a.Iban == iban)
            : null;

        return new ImportItem(entry, IsAlreadyBooked(line, accountId))
        {
            ExternalId = line.Reference,
            IsOutgoing = line.Amount < 0,
            AllowsTransfer = HasSeveralAccounts,
            CounterAccount = AccountOptions.FirstOrDefault(o => o.Account == counter)
                             ?? AccountOptions.FirstOrDefault(o => o.Account!.Id != accountId),
            IsTransfer = counter is not null,
        };
    }

    /// <summary>
    /// Already in Keystone: imported before with the same bank reference, or entered by hand with the same
    /// amount within a few days, including an occurrence of a repeating entry such as the rent.
    /// </summary>
    public bool IsAlreadyBooked(BankLine line, int accountId)
    {
        var amount = Math.Abs(line.Amount);
        foreach (var t in Transactions.Where(t => Touches(t, accountId)))
        {
            if (line.Reference != "" && t.ExternalId == line.Reference)
                return true;
            if (t.Amount != amount || Math.Sign(EffectOn(t, accountId)) != Math.Sign(line.Amount))
                continue;
            var window = t.IsRecurring ? 5 : 3;
            if (Schedule.Expand([t], line.Date.AddDays(-window), line.Date.AddDays(window)).Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>After adding, sets the account to the statement's closing balance when asked to.</summary>
    private async Task FinishBankImportAsync()
    {
        if (IsBankImport && ImportMatchBalance && _importStatement is { ClosingBalance: { } balance, ClosingDate: { } date } &&
            ImportAccount?.Account is { } account)
            await SetBalanceOnAsync(account, balance, date);
    }
}
