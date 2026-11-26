# Keystone

A macOS desktop app that keeps a Swiss household in order: money, paperwork, deadlines, plans and retirement
provision. Built with .NET 10, Avalonia 12 and SQLite. Everything stays on the Mac: the database is local,
documents live in the owner's OneDrive folder, and the optional AI runs locally through Ollama.

The app was first called **Runway** and was renamed to **Keystone**. The code, folders and data paths are all
Keystone now. "Runway" survives in code names for how long the money lasts (`RunwayCalculator`, `RunwayText`; the
Overview shows it as "Money lasts") and in code that moves data from the old names (see *Legacy names*).

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
| Overview | `Views/Pages/OverviewPage.axaml`, `MainViewModel.Dashboard.cs`/`.Week.cs`: a greeting; "This week" (seven days with appointments, tasks, bills and payments, deadlines and medicines running out; overdue things on today); "Needs your attention" (overdue/this week's bills, tasks due, today's appointments, notice deadlines within 60 days, important dates in their lead time, warranties ending, inbox scans, medicines running out, someone off sick; each with "Open"; kinds can be hidden with the filter button, `AppSettings.HiddenAttention`); Money tiles (cash today per account, "Money lasts" (the runway as a date) with status and months without income, this month in/out, saving this financial year); "Coming up" for 3 weeks (payments, appointments, bills without an expense entry, deadlines, dates); cash chart. Movable and foldable sections like Insurance. Refreshed with reminders, on Recalculate and when the page is shown. |
| Transactions | Income/expense/transfer entries: one-time, monthly, quarterly, yearly, with end dates; per person and account. Income, Expenses (one-time only), Recurring (monthly/quarterly/yearly expenses) and Transfers are separate tabs (`TransactionsTab`: 0 income, 1 expenses, 2 transfers, 3 recurring, decided by `TabOf`; Transfers only with several accounts; dropping an entry on Expenses/Recurring makes it one-time/monthly), each with its own headline total, filters, sort, totals and CSV export; "New income/expense/transfer" follows the tab, and a saved or edited entry opens its tab. Change or skip a single time of a repeating entry (`EntryChange`, edited in the entry form). Import bank statements (camt.053/054/052 XML, CSV, and Revolut PDF statements via `PdfStatementParser`: CHF part only, direction from the balance change, reference = date+amount+balance). AI import of entries from documents. Export the listed entries as CSV. |
| Accounts | Several accounts (current, savings, credit card, cash) with IBAN, opening balance and overdraft/credit limit. Transfers move money between them without counting as income or spending. "Match my bank balance" adjusts the opening balance. |
| Documents | Bills, invoices, letters, notices, contracts, policies, statements, payslips per person, filed in OneDrive as `Person/Year/Type/yyyy-MM-dd Title.ext`. Due dates and paid status; bills can be added to expenses. A bill too big to pay at once can be paid in instalments (`BillInstalment`, split by `InstalmentPlan`: equal parts in whole 5 centimes, every N months, each part editable): each part has its own reminder, its own one-time expense (replacing the bill's single one, keeping its category) and is ticked off on its own; the bill is paid with its last part (`MainViewModel.Instalments.cs`). Use `NextDueDate`/`NextAmount`/`AmountLeft` for open bills, not `DueDate`/`Amount`. Single and bulk import (drop many files). Swiss QR-bill reading. Inbox folder for phone scans. Search inside PDFs/text. Scans (PDFs that are only page pictures, e.g. from the OneDrive app) and photos are read with macOS text recognition (`TextRecognizer`, Vision) for search, the AI import and the inbox. Retention guide ("Past keeping time" filter). Deleted files go to `Deleted/`. Duplicates are noticed (`DuplicateFinder`: same file by SHA-256 `FileHash`, same QR reference, or same sender + amount + date) with "Show it / Skip / File anyway"; in bulk they arrive unticked and inbox copies of filed scans go to `Deleted/`. Follow-ups: a filed insurance policy or premium letter is saved under Insurance straight away (`MainViewModel.PolicyAuto.cs`: policy added/updated, premium as a repeating expense from `PremiumStart`, an existing premium entry — matched by insurer name or old amount — ended the day before the new one, paper put in the Insurance section; one notice with Undo). "Check filed papers" on the Insurance page (`MainViewModel.CheckPapers.cs`) reads insurance papers filed earlier (AI on; without it only type Policy) oldest first and does the same, with one Undo; read papers are remembered in `AppSettings.CheckedPapers` so an old premium letter can't bring back an old premium. A contract, ID/passport/MFK notice or purchase receipt offers to add or update the record under Contracts & dates (AI fields `record`, `record_kind`… in `DocumentDetailsExtraction`; without AI the type Policy/Contract alone); one document opens the filled-in form, bulk saves records directly. |
| Tasks | Due dates, person, notes; repeating weekly/monthly/quarterly/yearly (ticking a repeating task creates the next one). |
| Appointments | Timed or all-day appointments with place, person, notes and a reminder. Two-way sync with one chosen calendar in the Calendar app through EventKit (`CalendarSync` logic, `MacCalendar` interop): compares 30 days back to a year ahead, the later change wins, deletions go both ways. Needs the app bundle (Info.plist calendar usage strings). |
| Notes | Notes with projects and pins, saved as you type. |
| Household | `Views/Pages/HouseholdPage`/`HouseholdPanels`, `MainViewModel.Household.cs`/`.Medicines.cs`/`.Work.cs`, `Models/Health.cs`: one profile per member (`Person`: birth date (sets `BirthYear`), phone, email, AHV number, blood type, allergies, conditions, family doctor, emergency contact; "Copy" gives the health summary as text). Medicines (`Medicine`: dose × morning/noon/evening/night or as needed, course end, stock counted on a day → `RunsOutOn`, pack size for one-click refill, prescription end; alerts 7 days / 14 days ahead on the Overview and as reminders). Jobs (`Employment`: manager/HR contacts, how and by when to report sick, doctor's certificate from day N, sick pay and accident insurer; Bern scale from `SwissSickPay` when no sick pay insurer). Sick days (`SickLeave`): "Ill today"/"Accident" starts one with steps (tell work, certificate due date, accident to the employer), a copyable message to work, "Back at work" ends it (a time off ending today is over); per-year totals. Deleting a member deletes their medicines, jobs and sick days (cascade). Members can also still be added in Settings. |
| Debts | Monthly, lump-sum and interest-only debts with extra repayments; borrowing more (a `DebtPayment` with a negative amount: the debt grows, the money comes in as income that isn't earnings); edit a debt's terms (the amount stays "owed on" its start date); payments feed the runway. |
| Assets | Securities, crypto, property, vehicles, valuables, money lent (`Asset`, `AssetValue` in `Models/Asset.cs`; `MainViewModel.Assets.cs`, `Views/Pages/AssetsPage`/`AssetPanel`): values over time (one per day), sold date, change since the last value, a nudge when a value is over a year old. `AssetsOn(date)` counts in `NetWorthOn` (Planning) and in the Taxes page's assets on 31 December (property: note to use the tax value). |
| Retirement | AHV, pension fund (BVG), vested benefits, pillar 3a/3b accounts per person with yearly statements (balance, projected capital and pension at 65, buy-in). Pillar 3a paid this year vs the legal limit. AI reads pension letters. |
| Insurance | Policies with premium, term end and notice deadline (basic health insurance: by 30 November). Premiums (expenses in Health insurance / Other insurance) and insurance papers (policies, papers from a policy's insurer, or naming insurance) are listed here, not on the Expenses tab (`MainViewModel.OwnPages.cs`, `HasOwnPage`). Open bills of insurers (or from a known Swiss insurer by sender, `SwissInsurers`) are listed under "Bills to pay" here and on Taxes (tax bills), and not again among the papers. "Health costs" (`HealthCosts`, `MainViewModel.HealthCosts.cs`): per calendar year and person, health premiums (category Health insurance, from occurrences) and cost-sharing bills (Leistungsabrechnungen: documents saying so, or non-premium bills from a health insurer), with the year's maximum (`InsurancePolicy.Deductible` + 10% share cap 700, children 350). "Your cover" (`SwissCover`, `MainViewModel.Cover.cs`) lists the insurances common in Switzerland (basic health per person, liability, contents, car, building, accident, legal protection, supplementary, life, income protection, travel) with what the household has, gaps, "+" to add one, and "doesn't apply" (saved in `AppSettings.HiddenCover`). |
| Contracts & dates | Contracts (mobile, internet, gym, leasing, rent…) with notice deadlines; the summary lists all notice deadlines of the next 12 months, contracts' and insurance policies' together (`AllDeadlineRows`); important dates (passport, ID, driving licence, MFK, tyres, vignette) with lead times and repeats; warranties (2 years by default) with receipt. |
| Taxes | Per tax year: income, deductions (3a capped at the limit, pension buy-ins, childcare, medical costs, donations, debt interest, health premiums), assets and debts on 31 December, checklist of certificates found in Documents, copy as text. Tax estimate (`MainViewModel.TaxEstimate.cs`): no tariffs; the household's rate = a past year's total tax (`AppSettings.TaxAssessments`, JSON per year) ÷ Keystone's income minus known deductions for that year, applied to this year's (or a hand rate, `AppSettings.TaxRate`), compared with the year's tax payments ("about CHF X more to pay / back"). Tax payments (expenses in Taxes) and tax papers (naming tax/Steuer/impôt) are listed here instead of the Expenses tab. |
| Planning | Savings goals (by account or by hand, monthly amount needed, one-click monthly transfer, emergency fund preset); what-if scenarios saved in settings and compared with today (purple chart line). The retirement gap with birth years and a 3a withdrawal plan (one account per year, before 65) is the Retirement place's "Plan for 65" tab (`Views/Pages/RetirementPlanPage`); net worth over 24 months is the "Net worth" tab of Debts & assets (`Views/Pages/NetWorthPage`). |
| Year | A tab of Transactions. Month-by-month income and spending for a financial year (any start month). Export every booking as CSV or a one-page PDF summary (`YearSummaryPdf`, SkiaSharp). |
| Widget | macOS desktop/Notification Centre widget (small, medium, large): open tasks, bills to pay, next appointments (14 days). Swift WidgetKit extension in `src/KeystoneWidget/` (sandboxed, may only read `~/Library/Application Support/Keystone/`), built and signed into `Keystone.app/Contents/PlugIns/KeystoneWidget.appex` by `package-mac.sh` (needs Xcode tools). Keystone writes `widget.json` next to the database with each reminder update (`WidgetSnapshot`, `MainViewModel.Widget.cs`): texts in the app's language, raw dates (the widget says Today/Overdue itself), titles left out while the app lock is on. The widget re-reads every 15 minutes and at midnight. |
| Settings | In tabs (`SettingsTab`: 0 general = language, financial year, your data; 1 accounts; 2 household; 3 reminders & calendar; 4 documents & AI; 5 backup & lock). |
| Everywhere | Search everything (⌘F), reminders as macOS notifications, daily backups, app lock with Touch ID (⌘L locks; by default it locks only when the Mac locks its screen or sleeps, through `Services/Mac/ScreenLock` and macOS's `com.apple.screenIsLocked`; `LockAfterMinutes` −1, time-away choices still in Settings), English and German (Swiss) UI, light and dark mode. Deleting an entry or a debt shows a notice with Undo (`ShowNotice`, 12 s); exports report there too. Every list sorts by clicking its column titles (or a "Sort by" row for card lists), remembered per list in `AppSettings.ListSorts` (`MainViewModel.Sorting.cs`, `Controls/SortHeader`). Drag and drop (`Controls/Drag`: `Drag.Item` on a row, `Drag.Target`/`Drag.TargetItem` on a drop place; decisions in `MainViewModel.DragDrop.cs`): entries onto Insurance/Taxes/Income/Expenses change category or type; documents onto Insurance/Taxes/Documents set `HouseholdDocument.Section`, onto a person (strip shown while dragging) change the person and refile; policies, contracts, tasks, goals and debts reorder by dropping on each other (`SortOrder`, list sort "manual"). |

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
                 Medicine/Employment/SickLeave (Health.cs),
                 PensionAccount/PensionStatement (Retirement.cs), TaskItem, Note, Debt, Person, AppSettings,
                 Categories, NoticeDeadline, EntryChange (Transaction.cs))
  Services/      Pure logic: RunwayCalculator, Schedule (expands repeating entries), DebtPlan, FinancialYearReport,
                 BankStatementParser, PdfStatementParser (Revolut PDF), CategoryGuesser, DocumentReader (PdfPig), DocumentStore (OneDrive filing),
                 LocalAi (Ollama), PersonMatcher, ReminderPlanner, RetentionGuide, RetirementPlanner, Scenario,
                 SwissPension, SwissQrBill, SwissSickPay, DatabaseBackup, DuplicateFinder, Csv (semicolons, BOM, 1234.50), YearSummaryPdf
  Services/Mac/  macOS integration through P/Invoke: ObjC (runtime, blocks), TouchId (LocalAuthentication),
                 MacNotifications (UserNotifications), MacCalendar (EventKit), QrBillScanner (CoreGraphics/ImageIO + ZXing),
                 TextRecognizer (Vision text recognition for scans and photos)
  ViewModels/    One MainViewModel split into partial files per area (MainViewModel.<Area>.cs), plus row records
  Views/         MainWindow.axaml (shell, older pages, side panels), Pages/*.axaml (newer pages and forms as
                 UserControls with x:DataType="vm:MainViewModel"), Styles.axaml; icons are StreamGeometry in App.axaml
  Controls/      RunwayChart (history, projection, optional comparison line), MonthlyBarChart
  Localization/  Loc, TExtension ({l:T '...'}), German.cs
  Data/          KeystoneDbContext, KeystoneRepository (short-lived contexts per call), Migrations,
                 SqliteLocking (one save at a time + busy_timeout, so background saves never hit "database is locked")
tests/Keystone.Tests/   xUnit
src/KeystoneWidget/     Swift WidgetKit extension (KeystoneWidget.swift, entitlements)
scripts/        package-mac.sh, make-logo.py
docs/screenshots/       README screenshots (made-up family, never real data)
```

- **MVVM**: CommunityToolkit.Mvvm with `[ObservableProperty] public partial ... { get; set; }` and
  `[RelayCommand]`. Avalonia compiled bindings everywhere (`x:DataType`). Rows are records built by
  `Refresh*()` methods; lists are reassigned, not mutated.
- **Movable sections**: a page's cards can be moved and folded (`PageSection`, `MainViewModel.PageLayout.cs`,
  `Controls/SectionTools`, saved in `AppSettings.PageLayout`). Each card has a `Tag` with its key; the page's
  code-behind puts them in order (see `InsurancePage.axaml.cs`). Used by Insurance and Overview.
- **Pages**: `AppPage` enum + `Is<Page>Page` properties; `PageTitle`/`PageSubtitle` switches in MainViewModel.cs.
  New pages go in `Views/Pages` and get a line in the page area and a place in the sidebar.
- **Sidebar places** (`MainViewModel.Navigation.cs`): Overview; Money (Transactions, Debts & assets, Retirement,
  Planning); Paperwork (Documents, Insurance, Taxes, Contracts & dates); Home (Household, Agenda, Notes); Settings.
  Pages that share a place are in `PageGroups` and shown as tabs under the title (`PageTabs`): Transactions = entries
  | Year, Debts & assets = Debts | Assets | Net worth, Retirement = accounts | Plan for 65, Agenda = Tasks |
  Appointments. A place's nav button binds `Is<Place>Place` and reopens the tab used last. The page title is the place's.
- **+ New** (sidebar button, ⌘N opens it): `NewMenuItems` lists everything that can be added, the current place's
  things first; `CreateNew` goes to the thing's page and opens its form (documents ask the window to pick files and
  tasks focus the task box, through events).
- **Person view** (`MainViewModel.PersonView.cs`): the sidebar's person picker sets every page's person filter and
  narrows lists without one through `InView(personId)`; a bar under the title says so ("Show everyone"). Shared
  items (no person) are left out, as the per-page filters do.
- **Shorter forms** (`MainViewModel.FormDetails.cs`): optional fields sit in an `Expander` bound to `<Form>MoreOpen`,
  which opens when the form opens with content in them or when one gets content (editing, the AI). Add new optional
  fields to both the open check and a change hook.
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
  `warranty-`, `pillar3a-`, `medicine-`, `sick-`). macOS keeps at most 64 pending; we schedule 60.
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

- **Background saves and "database is locked".** Indexing a filed document's text saves while the next document
  in a bulk import is saved. `SqliteLocking` (registered in `KeystoneDbContext.OnConfiguring`) serialises
  `SaveChanges` and sets `PRAGMA busy_timeout`; keep both. Tests delete their folders with `TestFolder.Delete`,
  which waits for those background saves.
- **Test files need different contents.** Documents are recognised by their SHA-256, so two test files that both
  contain "x" are the same document. Tests can reach `internal` members (`InternalsVisibleTo`).

- **A drag must carry something macOS can see.** In-process data isn't put on the drag pasteboard; a drag with only
  that makes AppKit throw in `NSDraggingSession` (0 pasteboard items, 1 drag image), which kills the app, and headless
  tests never show it. `Drag` therefore also sets the app-only marker format `Drag.Marker`; drops fall back to
  `MainViewModel.DraggedItem`.
- **Drags inside the app** use the in-process format `Drag.Format`; target handlers mark the event handled so the
  window's file-drop handler (Finder files) never sees them. A drag starts only after 6 px of movement, so clicks
  and double-clicks on rows still work.

- **Sum `Occurrence.Amount`/`SignedAmount`, never `o.Source.Amount`.** A single time of a repeating entry can have
  its own amount (`EntryChange`), and skipped times aren't returned by `Schedule.Expand` at all.
  `Schedule.RegularDates` gives the dates without changes (for the "change one time" picker).

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
- **Widget labels** are passed as a list (`WidgetLabels()`), which the translation test can't see: add their German
  texts by hand. The widget's date words come from these labels; only date formats are done in Swift.
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
