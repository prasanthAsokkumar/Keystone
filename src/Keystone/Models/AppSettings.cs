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

    public const string DefaultAiEndpoint = "http://localhost:11434";

    public const string DefaultAiModel = "qwen2.5vl:7b";
}
