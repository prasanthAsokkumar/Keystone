namespace Keystone.Models;

/// <summary>Single-row table holding user settings.</summary>
public class AppSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Cash on hand before the first recorded transaction.</summary>
    public decimal StartingBalance { get; set; }

    /// <summary>Month the financial year starts in (1 = January).</summary>
    public int FinancialYearStartMonth { get; set; } = 1;

    /// <summary>The app's language code ("en", "de"). Empty means the Mac's language.</summary>
    public string Language { get; set; } = "";

    /// <summary>How each list is sorted, e.g. {"transactions":"-amount"}: the column, and "-" for descending.</summary>
    public string ListSorts { get; set; } = "";

    /// <summary>Kinds of insurance marked "doesn't apply" in the Insurance page's cover overview, e.g. "Car,Building".</summary>
    public string HiddenCover { get; set; } = "";

    /// <summary>Kinds left out of the Overview's "Needs your attention", e.g. "inbox,health".</summary>
    public string HiddenAttention { get; set; } = "";

    /// <summary>Ids of insurance papers "Check filed papers" has read, e.g. "12,40", so a later check skips them.</summary>
    public string CheckedPapers { get; set; } = "";

    /// <summary>
    /// How the owner arranged the sections of a page, e.g. {"insurance":{"order":["policies","bills"],"closed":["cover"]}}.
    /// </summary>
    public string PageLayout { get; set; } = "";

    /// <summary>
    /// Total tax per year from the assessment or final bill (canton, commune and federal together), e.g.
    /// {"2025":18400}. With Keystone's own figures for that year it gives the rate the estimate uses.
    /// </summary>
    public string TaxAssessments { get; set; } = "";

    /// <summary>A tax rate set by hand (percent of income after deductions), used when no assessment is entered.</summary>
    public decimal? TaxRate { get; set; }

    /// <summary>Where household documents are filed. Empty means the default OneDrive folder.</summary>
    public string DocumentsFolder { get; set; } = "";

    /// <summary>Document import is off until the user turns it on, since it needs a local model.</summary>
    public bool AiImportEnabled { get; set; }

    /// <summary>Address of the local Ollama server that reads imported documents.</summary>
    public string AiEndpoint { get; set; } = DefaultAiEndpoint;

    /// <summary>Ollama model used for document import. Needs vision support to read photos.</summary>
    public string AiModel { get; set; } = DefaultAiModel;

    /// <summary>Notifications for bills, tasks and deadlines. Asks macOS for permission the first time.</summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>Hour of the day reminders arrive at.</summary>
    public int ReminderHour { get; set; } = 8;

    /// <summary>Asks for Touch ID or the Mac's password before showing anything.</summary>
    public bool LockEnabled { get; set; }

    /// <summary>Minutes away from Keystone after which it locks again; 0 locks as soon as you switch away.</summary>
    public int LockAfterMinutes { get; set; } = -1;

    /// <summary>The what-if changes being tried, as JSON.</summary>
    public string ScenarioJson { get; set; } = "";

    /// <summary>Keeps appointments in step with a calendar in the Mac's Calendar app.</summary>
    public bool CalendarSyncEnabled { get; set; }

    /// <summary>The calendar appointments are kept in; empty until one is chosen.</summary>
    public string CalendarId { get; set; } = "";

    /// <summary>Share of today's living costs needed in retirement, in percent.</summary>
    public int RetirementNeedPercent { get; set; } = 80;

    public const string DefaultAiEndpoint = "http://localhost:11434";

    public const string DefaultAiModel = "qwen2.5vl:7b";
}
