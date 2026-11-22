# Keystone

A macOS desktop app that keeps a Swiss household in order: money, paperwork, deadlines, plans and retirement
provision. Built with .NET 10, Avalonia 12 and SQLite. Everything stays on the Mac: the database is local,
documents live in the owner's OneDrive folder, and the optional AI runs locally through Ollama.

The app was first called **Runway** and was renamed to **Keystone**. The code, folders and data paths are all
Keystone now. "Runway" survives only as the name of the Overview's headline figure ("Your runway": how long the
money lasts) and in code that moves data from the old names (see *Legacy names*).

## Commands

```bash
dotnet run --project src/Keystone            # debug build, uses its own database (keystone-dev.db)
dotnet test                                  # all tests (run from the repo root)
dotnet build Keystone.slnx -c Release        # release build; keep it at 0 warnings (use --no-incremental to be sure)
./scripts/package-mac.sh                     # build artifacts/Keystone.app
./scripts/package-mac.sh --install           # also install to /Applications (removes an old Runway.app)
cd src/Keystone && dotnet ef migrations add <Name> -o Data/Migrations
python3 scripts/make-logo.py                 # redraw Assets/keystone.png and keystone.ico (needs Pillow)
```

The owner installs new versions themselves with `./scripts/package-mac.sh --install` after quitting the app.
End a piece of work by giving them that command.

## Features

| Area | What it does |
| --- | --- |
| Overview | Runway (months until cash runs out, the cause), cash today per account, overdraft reach, daily earn/spend, saving this financial year, months covered without income, cash chart with projection, next 30 days. Header buttons for inbox scans, tasks due, bills to pay. |
| Transactions | Income/expense/transfer entries: one-time, monthly, quarterly, yearly, with end dates; per person and account; filters, sort, totals. Import bank statements (camt.053/054/052 XML and CSV). AI import of entries from documents. |
| Accounts | Several accounts (current, savings, credit card, cash) with IBAN, opening balance and overdraft/credit limit. Transfers move money between them without counting as income or spending. "Match my bank balance" adjusts the opening balance. |
| Documents | Bills, invoices, letters, notices, contracts, policies, statements, payslips per person, filed in OneDrive as `Person/Year/Type/yyyy-MM-dd Title.ext`. Due dates and paid status; bills can be added to expenses. Single and bulk import (drop many files). Swiss QR-bill reading. Inbox folder for phone scans. Search inside PDFs/text. Retention guide ("Past keeping time" filter). Deleted files go to `Deleted/`. |
| Tasks | Due dates, person, notes; repeating weekly/monthly/quarterly/yearly (ticking a repeating task creates the next one). |
| Appointments | Timed or all-day appointments with place, person, notes and a reminder. Two-way sync with one chosen calendar in the Calendar app through EventKit (`CalendarSync` logic, `MacCalendar` interop): compares 30 days back to a year ahead, the later change wins, deletions go both ways. Needs the app bundle (Info.plist calendar usage strings). |
| Notes | Notes with projects and pins, saved as you type. |
| Debts | Monthly, lump-sum and interest-only debts with extra repayments; payments feed the runway. |
| Retirement | AHV, pension fund (BVG), vested benefits, pillar 3a/3b accounts per person with yearly statements (balance, projected capital and pension at 65, buy-in). Pillar 3a paid this year vs the legal limit. AI reads pension letters. |
| Insurance | Policies with premium, term end and notice deadline (basic health insurance: by 30 November). |
| Contracts & dates | Contracts (mobile, internet, gym, leasing, rent…) with notice deadlines; important dates (passport, ID, driving licence, MFK, tyres, vignette) with lead times and repeats; warranties (2 years by default) with receipt. |
| Taxes | Per tax year: income, deductions (3a capped at the limit, pension buy-ins, childcare, medical costs, donations, debt interest, health premiums), assets and debts on 31 December, checklist of certificates found in Documents, copy as text. |
| Planning | Savings goals (by account or by hand, monthly amount needed, one-click monthly transfer, emergency fund preset); what-if scenarios saved in settings and compared with today (purple chart line); retirement gap with birth years and a 3a withdrawal plan (one account per year, before 65); net worth over 24 months. |
| Year | Month-by-month income and spending for a financial year (any start month). |
| Settings | Bank accounts, language (English / German), financial year, reminders, app lock, backup, household members, documents folder and inbox, local AI. |
| Everywhere | Search everything (⌘F), reminders as macOS notifications, daily backups, app lock with Touch ID (⌘L locks), English and German (Swiss) UI, light and dark mode. |

### Swiss rules built in

- `SwissPension`: retirement age 65, 3a withdrawal from 60, BVG minimum conversion rate 6.8%, pillar 3a limits
  per year (with/without pension fund; **update yearly** when the Federal Council publishes new figures — the
  latest known year is reused for later years).
- `NoticeDeadline`: cancellation must arrive by `termEnd + 1 day − notice months − 1 day`; auto-renewing terms
  roll to next year once a deadline has passed.
- `RetentionGuide`: bills/receipts 5 years after paying, tax papers (statements, notices) 10 years, letters 2
  years, payslips/contracts/policies kept.
- `SwissQrBillParser`: SPC 0200 payload; Swico S1 bill information gives invoice date (/11/) and payment term
  (/40/ "0:30" → due 30 days later).
- Every Swiss rule shown in the UI carries a "guide, not advice" note.

## Architecture

```
src/Keystone/
  Models/        EF Core entities and small domain rules (Transaction, BankAccount, HouseholdDocument,
                 InsurancePolicy, Contract/ImportantDate/Warranty/SavingsGoal/DocumentText (Contract.cs),
                 PensionAccount/PensionStatement (Retirement.cs), TaskItem, Note, Debt, Person, AppSettings,
                 Categories, NoticeDeadline)
  Services/      Pure logic: RunwayCalculator, Schedule (expands repeating entries), DebtPlan, FinancialYearReport,
                 BankStatementParser, CategoryGuesser, DocumentReader (PdfPig), DocumentStore (OneDrive filing),
                 LocalAi (Ollama), PersonMatcher, ReminderPlanner, RetentionGuide, RetirementPlanner, Scenario,
                 SwissPension, SwissQrBill, DatabaseBackup
  Services/Mac/  macOS integration through P/Invoke: ObjC (runtime, blocks), TouchId (LocalAuthentication),
                 MacNotifications (UserNotifications), MacCalendar (EventKit), QrBillScanner (CoreGraphics/ImageIO + ZXing)
  ViewModels/    One MainViewModel split into partial files per area (MainViewModel.<Area>.cs), plus row records
  Views/         MainWindow.axaml (shell, older pages, side panels), Pages/*.axaml (newer pages and forms as
                 UserControls with x:DataType="vm:MainViewModel"), Styles.axaml; icons are StreamGeometry in App.axaml
  Controls/      RunwayChart (history, projection, optional comparison line), MonthlyBarChart
  Localization/  Loc, TExtension ({l:T '...'}), German.cs
  Data/          KeystoneDbContext, KeystoneRepository (short-lived contexts per call), Migrations
tests/Keystone.Tests/   xUnit
scripts/        package-mac.sh, make-logo.py
docs/screenshots/       README screenshots (made-up family, never real data)
```

- **MVVM**: CommunityToolkit.Mvvm with `[ObservableProperty] public partial ... { get; set; }` and
  `[RelayCommand]`. Avalonia compiled bindings everywhere (`x:DataType`). Rows are records built by
  `Refresh*()` methods; lists are reassigned, not mutated.
- **Pages**: `AppPage` enum + `Is<Page>Page` properties; `PageTitle`/`PageSubtitle` switches in MainViewModel.cs.
  New pages go in `Views/Pages` and get a nav `RadioButton` in the sidebar and a line in the page area.
- **Side panels**: each form is a `Is<X>PanelOpen` property. Add it to `IsPanelOpen` and close it in
  `CloseOtherPanels`/`NewEntry`/`NewDebt`/`ClosePanel`/`EditTransaction` (they share the
  `CloseRetirementPanels(); CloseInsurancePanel(); CloseAccountPanel(); CloseDeadlinePanels(); ClosePlanningPanels();` block).
- **Loading**: `MainViewModel.LoadAsync` reads settings, people, accounts, documents (+ text index), insurance,
  contracts/dates/warranties, goals, tasks, notes, retirement, transactions, debts, then `Recalculate()`. After
  loading: backups, inbox, taxes, deadlines, planning, reminders. Guard side effects with `_isLoading`.
- **Recalculate()**: debt payments become generated transactions (`_entries`), then runway summary, accounts,
  year report, entry rows, upcoming, taxes, planning. Transfers are excluded from the calculator and reports.
- **Accounts**: `Transaction.AccountId` null means the primary (first) account; `ToAccountId` for transfers;
  `ExternalId` holds the bank reference so a statement can't be imported twice. The old single
  `AppSettings.StartingBalance` became the main account's opening balance on first load (setting kept, unused).
- **Language change**: `OnLanguageChanged` saves every `*Index` selection, raises `OnPropertyChanged(string.Empty)`,
  restores them and refreshes code-built texts. New ComboBox indexes must be added to that tuple.
- **Reminders**: `PlanReminders` → `ReminderPlanner.Plan` → `MacNotifications.Replace` (debounced 1.5 s via
  `ScheduleReminders()`). Ids must be stable and prefixed (`bill-`, `task-`, `insurance-`, `contract-`, `date-`,
  `warranty-`, `pillar3a-`). macOS keeps at most 64 pending; we schedule 60.
- **Local AI**: Ollama at `http://localhost:11434`, model `qwen2.5vl:7b` (vision). JSON-schema answers, `keep_alive`
  30m, context size sized to the document. The household's names are sent so the model can say whom a
  document is for; `PersonMatcher` maps that to a member (distinctive words decide; a shared family name alone
  means shared). The AI is off by default (Settings → Local AI).
- **QR-bills** are read before the AI and win over it for amount, payee, IBAN and reference.

## Conventions

- **Every UI text is translated.** Views use `{l:T 'English text'}` (XAML: write `&amp;` for &, `’` not `'`);
  code uses `Loc.T("…")`, `Loc.F("… {0}", x)`, `Loc.Plural(n, "one", "many")`, or `Translate("a","b")` for
  ComboBox lists. Add the German text to `Localization/German.cs` (Swiss spelling: ss not ß, «Anführungszeichen»,
  informal "du"). `LocalizationTests.EveryTextHasAGermanTranslation` fails otherwise — but it only finds
  literal keys; texts chosen by `Loc.T(x ? "a" : "b")` or `Loc.T(kind switch …)` must be added by hand.
- Built-in categories are stored in English and shown translated (`Categories.Display/Normalize`). Deduction
  categories: Pillar 3a, Pension fund buy-in, Childcare, Medical costs, Donations.
- Enums are stored as strings; new enum values need no migration. New non-null columns need a sensible
  default in the migration (e.g. `TaskRepeat.Never`, not "").
- Dates: `DateOnly` in models; `Format.Date` ("03 Jan 2027" / "3. Jan. 2027"), `Format.Money` (Swiss: CHF 1'234).
- Comments explain why, in plain language. Messages to the user are plain and specific.
- Every feature gets tests. View-model tests construct `MainViewModel` with a temp `KeystoneRepository` and call
  `LoadAsync()`.

## Pitfalls already hit (don't repeat them)

- **`Dictionary<int?, …>` with a null key throws.** Shared items have `PersonId == null`. Group with
  `GroupBy(...).Select(...)` into lists or use a lookup function, never `ToDictionary` keyed by `PersonId`.
- **Tests must never touch real data.** `TestSetup` points `KEYSTONE_ONEDRIVE_PATH` at a temp folder because
  `LoadAsync` renames/creates folders in OneDrive and writes backups there. Any test that loads the view model
  inherits that; tests that change the documents folder use their own temp folder. The debug build uses
  `keystone-dev.db` and backups named `keystone-dev-*` so they never mix with real ones.
- Tests that format text or change the language belong to `[Collection(nameof(LanguageCollection))]` (no
  parallel runs); `Loc` is global.
- Avalonia `CalendarDatePicker` keeps old text when its date is cleared from code — handled globally in
  `MainWindow` (deferred clear). `&` must be `&amp;` in XAML. `async` methods can't be in an `unsafe` class:
  keep function-pointer code in small `unsafe` helper methods.
- macOS interop: notifications need a real app bundle (`MacNotifications.IsSupported` checks the bundle id;
  without one macOS throws an Objective-C exception that kills the process). Blocks are global blocks built in
  `ObjC.GlobalBlock`; callbacks are `[UnmanagedCallersOnly]`. Declare a separate `objc_msgSend` signature per use.
- App lock: Keystone's own windows (Touch ID prompt, file pickers, About) take focus. Wrap them in
  `vm.OwnWindow()` (pickers go through `PickFilesAsync`/`PickFolderAsync` in MainWindow) so the lock neither
  relocks nor re-prompts. Drops are refused while locked (`CanAcceptDrop`).
- Backups: one at a time (`_backupGate`); `Restore` backs up the current data first.
- Pushing large files over HTTPS needed `git config http.postBuffer 157286400` (set in this repo).

## Data and files

| What | Where |
| --- | --- |
| Database | `~/Library/Application Support/Keystone/keystone.db` (debug: `keystone-dev.db`); override with `KEYSTONE_DB_PATH` |
| Documents | `~/OneDrive/PRAS/KeystoneApp` by default (Settings can change it); override OneDrive with `KEYSTONE_ONEDRIVE_PATH` |
| Inbox | `<documents>/Inbox` — files older than 5 s are offered for filing and deleted from the inbox once filed |
| Backups | `<documents>/Backups/keystone-yyyy-MM-dd-HHmm.db`, daily, last 30 kept |
| Deleted documents | `<documents>/Deleted` |
| App bundle | `/Applications/Keystone.app`, bundle id `ch.keystone.app` (ad-hoc signed, not notarized) |

### Legacy names

On start, Keystone moves `Application Support/Runway/runway.db` to `Application Support/Keystone/keystone.db`
(`KeystoneDbContext.ResolveDatabasePath`) and renames `OneDrive/PRAS/RunwayApp` to `KeystoneApp`
(`DocumentStore.MoveLegacyFolder`); the package script removes `/Applications/Runway.app`. Keep these; they
are why "Runway" still appears in a few strings.

## Checking the UI

There is no UI test project in the repo. To see screens, build a throwaway console app (outside the repo, e.g.
in a scratch folder) that references `src/Keystone/Keystone.csproj` plus `Avalonia.Headless` and `Avalonia.Skia`,
creates `MainWindow` with a `MainViewModel` over a **new, empty** database, seeds made-up data (the README uses
the fictional Meier family in Basel), sets `CurrentPage`, and saves `window.CaptureRenderedFrame()`. Wait for
async work by pumping `Dispatcher.UIThread.RunJobs()` — never block with `.GetResult()` on the UI thread.
Set `KEYSTONE_ONEDRIVE_PATH` to a temp folder first and never copy the owner's real database.

## Working with the owner

- Privacy matters: no real names, family members, emails or financial data in code, tests, screenshots or
  commits. Use made-up people (Lukas, Anna, Sofia Meier). Scan for personal data before every push.
- Commit and push only when asked. GitHub: `prasanthAsokkumar/Keystone`, branch `main`.
- Explain results in plain language: what changed, what was verified, what wasn't.
- The owner reads the app in English and German; check German texts fit (they are longer).
