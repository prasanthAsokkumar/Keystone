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
    public int LockAfterMinutes { get; set; } = 5;

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
