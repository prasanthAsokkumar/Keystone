using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>An entry the model found in a document, before the user reviews it.</summary>
public sealed record ExtractedEntry(
    string Description, decimal Amount, bool IsIncome, DateOnly Date, Recurrence Recurrence, string Category);

/// <summary>
/// Talks to a local Ollama server (https://ollama.com), which runs open-source models on this
/// Mac. Documents go to that local server only.
/// </summary>
public sealed class LocalAiClient
{
    // Local models can take a while on long documents, especially the first call that loads the model.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Keeps the model in memory between documents, so only the first one waits for it to load.
    /// </summary>
    private const string KeepModelLoaded = "30m";

    /// <summary>
    /// The context the model is asked to reserve. Memory grows with it, and on a machine without
    /// enough memory a large context makes the model page to disk and become very slow, so it is
    /// sized to the document: enough for the instructions, the text and the answer.
    /// </summary>
    public static int ContextSizeFor(DocumentContent document)
    {
        // Rough token counts: about 3 characters per token, an image of up to 1280 px about 2,100.
        var documentTokens = document.IsImage ? 2_100 : (document.Text?.Length ?? 0) / 3;
        // The answer can be nearly as long as a text full of entries (a statement lists every booking);
        // without room for it, the answer is cut off and can't be read.
        var answerTokens = document.IsImage ? 1_500 : Math.Max(1_500, documentTokens);
        var needed = documentTokens + answerTokens + 1_000; // plus the instructions
        var size = 4_096;
        while (size < needed && size < 32_768)
            size *= 2;
        return size;
    }

    /// <summary>The models installed in Ollama, or an explanation of why it couldn't be reached.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => Http.GetAsync(Url(endpoint, "api/tags"), cancellationToken), endpoint);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n != "").ToList()
            ?? [];
    }

    /// <summary>
    /// Loads the model in the background, at the context size most documents need, so the first
    /// document doesn't wait minutes for it. Failures are ignored; a real request reports them.
    /// </summary>
    public async Task WarmUpAsync(string endpoint, string model)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["keep_alive"] = KeepModelLoaded,
            ["options"] = new JsonObject { ["num_ctx"] = ContextSizeFor(new DocumentContent("", "", null, false)) },
        };
        try
        {
            using var response = await Http.PostAsJsonAsync(Url(endpoint, "api/generate"), request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
        }
    }

    /// <summary>Lists the income and expenses in a document, and who in the household it is for.</summary>
    public async Task<EntryImport> ExtractEntriesAsync(
        DocumentContent document, string endpoint, string model, DateOnly today, IReadOnlyList<string> household,
        CancellationToken cancellationToken = default)
    {
        var answer = await AskAsync(document, "List its entries.", EntryExtraction.Instructions(today) + PersonPrompt.Instructions(household),
            PersonPrompt.AddTo(EntryExtraction.Schema()), endpoint, model, cancellationToken);
        return new EntryImport(EntryExtraction.Parse(answer, today), PersonPrompt.Read(answer));
    }

    /// <summary>Suggests the type, title, sender, person, dates and amount of a document being filed.</summary>
    public async Task<DocumentDetails> ExtractDetailsAsync(
        DocumentContent document, string endpoint, string model, DateOnly today, IReadOnlyList<string> household,
        CancellationToken cancellationToken = default)
    {
        var answer = await AskAsync(document, "Describe this document.",
            DocumentDetailsExtraction.Instructions(today) + PersonPrompt.Instructions(household),
            PersonPrompt.AddTo(DocumentDetailsExtraction.Schema()), endpoint, model, cancellationToken);
        return DocumentDetailsExtraction.Parse(answer) with { Person = PersonPrompt.Read(answer) };
    }

    /// <summary>Reads the figures from a yearly pension, vested benefits, 3a or AHV letter.</summary>
    public async Task<PensionStatementDetails> ExtractPensionStatementAsync(
        DocumentContent document, string endpoint, string model, IReadOnlyList<string> household,
        CancellationToken cancellationToken = default)
    {
        var answer = await AskAsync(document, "Read the figures from this pension statement.",
            PensionStatementExtraction.Instructions + PersonPrompt.Instructions(household),
            PersonPrompt.AddTo(PensionStatementExtraction.Schema()), endpoint, model, cancellationToken);
        return PensionStatementExtraction.Parse(answer) with { Person = PersonPrompt.Read(answer) };
    }

    /// <summary>Sends the document with instructions and returns the model's JSON answer.</summary>
    private static async Task<string> AskAsync(
        DocumentContent document, string imageRequest, string instructions, JsonObject schema, string endpoint,
        string model, CancellationToken cancellationToken)
    {
        var user = new JsonObject
        {
            ["role"] = "user",
            ["content"] = document.IsImage
                ? $"The attached image is \"{document.FileName}\". {imageRequest}"
                : $"Document \"{document.FileName}\":\n\n{document.Text}",
        };
        if (document.ImageBase64 is { } image)
            user["images"] = new JsonArray(image);

        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["format"] = schema,
            ["keep_alive"] = KeepModelLoaded,
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = ContextSizeFor(document) },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = instructions }, user),
        };

        using var response = await SendAsync(
            () => Http.PostAsJsonAsync(Url(endpoint, "api/chat"), request, cancellationToken), endpoint, model);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return json?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException(Loc.T("The model sent back an empty answer."));
    }

    private static Uri Url(string endpoint, string path) => new(new Uri(endpoint.TrimEnd('/') + "/"), path);

    /// <summary>Turns connection and Ollama errors into messages a user can act on.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send, string endpoint, string? model = null)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                Loc.F("Can’t reach Ollama at {0}. Make sure Ollama is running (Docker Desktop or the Ollama app).", endpoint));
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new InvalidOperationException(Loc.T("The local model took too long to answer. Try a smaller document."));
        }

        if (response.IsSuccessStatusCode)
            return response;

        var body = await response.Content.ReadAsStringAsync();
        response.Dispose();
        var error = TryReadError(body) ?? Loc.F("Ollama answered {0}.", (int)response.StatusCode);
        if (model is not null && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            error = Loc.F("The model “{0}” isn’t installed. Run: ollama pull {0}", model);
        throw new InvalidOperationException(error);
    }

    private static string? TryReadError(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The prompt, answer format and answer parsing for reading entries out of a document.</summary>
public static class EntryExtraction
{
    public static string Instructions(DateOnly today) =>
        $"""
        You read household finance documents for a budgeting app and list the money that comes in or goes out.
        Documents may be in English, German, French or Italian. Amounts are in CHF unless the document says otherwise.
        Today is {today:yyyy-MM-dd}.

        - Bill or invoice: one expense for the total amount due, dated on the due date (or the invoice date if there is none).
        - Payslip: one income for the net pay that is paid out.
        - Bank or card statement: one entry per transaction, with money in as income and money out as expense.
        - Insurance policy, contract or subscription: one entry for the regular premium, with its frequency.
        Frequency is "once" unless the document clearly says the amount repeats (monthly, every 3 months, yearly / jährlich / annuel).
        Date: when the money actually moves, i.e. the due date ("zahlbar bis", "fällig", "échéance", "due") or the
        booking date on a statement. Use the invoice date only if there is no other date. If a document gives a separate
        due date for each amount, use each amount's own due date.
        Amounts are always positive numbers; "type" says whether it is income or an expense.
        Description: short and clear, 2 to 5 words: the company and what it's for ("Swisscom mobile", "Helsana basic insurance").
        Category: one of {string.Join(", ", Categories.Expense.Concat(Categories.Income).Distinct())}.
        Skip totals, balances, subtotals, VAT lines and anything that isn't a payment. If there are none, return an empty list.
        """;

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["entries"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["amount"] = new JsonObject { ["type"] = "number" },
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("expense", "income") },
                        ["date"] = new JsonObject { ["type"] = "string", ["description"] = "YYYY-MM-DD" },
                        ["frequency"] = new JsonObject
                        {
                            ["type"] = "string", ["enum"] = new JsonArray("once", "monthly", "quarterly", "yearly"),
                        },
                        ["category"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("description", "amount", "type", "date", "frequency", "category"),
                },
            },
        },
        ["required"] = new JsonArray("entries"),
    };

    /// <summary>Reads the model's JSON answer, skipping entries that make no sense.</summary>
    /// <exception cref="InvalidOperationException">The answer isn't the expected JSON.</exception>
    public static IReadOnlyList<ExtractedEntry> Parse(string json, DateOnly today)
    {
        JsonArray? items;
        try
        {
            items = JsonNode.Parse(json)?["entries"]?.AsArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(Loc.T("The model’s answer couldn’t be read. Try again or use another model."));
        }

        var entries = new List<ExtractedEntry>();
        foreach (var item in items ?? [])
        {
            if (item is not JsonObject entry)
                continue;

            var amount = Math.Round(Math.Abs(ReadDecimal(entry["amount"])), 2);
            var description = ReadString(entry["description"]).Trim();
            if (amount == 0 || description == "")
                continue;

            var date = DateOnly.TryParseExact(ReadString(entry["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) ? parsed : today;
            var recurrence = ReadString(entry["frequency"]).ToLowerInvariant() switch
            {
                "monthly" => Recurrence.Monthly,
                "quarterly" => Recurrence.Quarterly,
                "yearly" or "annual" or "annually" => Recurrence.Yearly,
                _ => Recurrence.OneTime,
            };
            var isIncome = ReadString(entry["type"]).Equals("income", StringComparison.OrdinalIgnoreCase);

            entries.Add(new ExtractedEntry(
                description.Length > 200 ? description[..200] : description, amount, isIncome, date, recurrence,
                ReadString(entry["category"]).Trim()));
        }

        return entries;
    }

    internal static string ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>Accepts numbers and numeric strings such as "1'234.50" or "1,234.50".</summary>
    internal static decimal ReadDecimal(JsonNode? node)
    {
        if (node is not JsonValue value)
            return 0;
        if (value.TryGetValue<decimal>(out var number))
            return number;
        if (value.TryGetValue<double>(out var real))
            return (decimal)real;

        var text = ReadString(node).Replace("'", "").Replace("’", "").Replace(",", "").Replace("CHF", "").Trim();
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}

/// <summary>What the model suggests for a document being filed; every field may be missing.</summary>
public sealed record DocumentDetails(
    DocumentKind? Kind, string Title, string Sender, DateOnly? Date, decimal? Amount, DateOnly? DueDate)
{
    /// <summary>Who the document is for, as the model read it; match it with <see cref="PersonMatcher"/>.</summary>
    public string Person { get; init; } = "";

    /// <summary>What else the paper should be kept track of as, e.g. an insurance policy with a notice deadline.</summary>
    public FollowUp? FollowUp { get; init; }
}

/// <summary>Where a filed paper belongs besides Documents: Insurance, Contracts, Important dates or Warranties.</summary>
public enum FollowUpKind { Insurance, Contract, Date, Warranty }

/// <summary>
/// The details of an insurance policy, contract, important date or purchase found in a document, so it can be
/// tracked with its reminders. Every field may be missing; <see cref="Subkind"/> is the English enum name, e.g. "HealthBasic".
/// </summary>
public sealed record FollowUp(
    FollowUpKind Kind,
    string Subkind = "",
    string Reference = "",
    decimal? Cost = null,
    Recurrence? Frequency = null,
    DateOnly? EndDate = null,
    int? NoticeMonths = null,
    string Item = "");

/// <summary>The entries found in a document, and who it is for.</summary>
public sealed record EntryImport(IReadOnlyList<ExtractedEntry> Entries, string Person);

/// <summary>Asks the model who in the household a document is for.</summary>
public static class PersonPrompt
{
    public static string Instructions(IReadOnlyList<string> household) => household.Count == 0
        ? "\n- person: always empty."
        : $"""

        - person: the one person the document is about, written as on the document: the insured person, patient,
          employee, taxpayer, account or policy holder, or student. If there is no such person, the addressee.
          The household members are: {string.Join(", ", household)}. Prefer their names as written there.
          Leave it empty if the document is for several of them together (e.g. "Familie …", a joint account),
          or if you can't tell.
        """;

    /// <summary>Adds a required "person" field to an answer format.</summary>
    public static JsonObject AddTo(JsonObject schema)
    {
        ((JsonObject)schema["properties"]!)["person"] = new JsonObject { ["type"] = "string" };
        ((JsonArray)schema["required"]!).Add("person");
        return schema;
    }

    public static string Read(string json)
    {
        try
        {
            return EntryExtraction.ReadString(JsonNode.Parse(json)?["person"]).Trim();
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

/// <summary>The prompt, answer format and parsing for describing a document being filed.</summary>
public static class DocumentDetailsExtraction
{
    private static readonly string[] KindNames = Enum.GetNames<DocumentKind>().Select(n => n.ToLowerInvariant()).ToArray();

    public static string Instructions(DateOnly today) =>
        $"""
        You file household paperwork. Describe the document so it can be filed and found again.
        Documents may be in English, German, French or Italian. Today is {today:yyyy-MM-dd}.

        - type: bill (something to pay, incl. premium invoices), invoice (from a shop or service), letter, notice
          (official notices, e.g. tax, authorities, rent changes), contract, policy (the insurance policy itself: "Police",
          "Versicherungspolice", "police d'assurance", setting out cover and terms), statement (bank, card or
          pension account statements, incl. a pension fund certificate "Vorsorgeausweis" and 3a statements), payslip, other.
        - title: short and specific, 2 to 6 words, in English, e.g. "Health insurance premium 2027", "Tax assessment 2025".
        - sender: the company, office or person who sent it, e.g. "Helsana", "Steuerverwaltung Basel-Stadt".
        - date: the date printed on the document (YYYY-MM-DD).
        - amount: for bills and invoices, the total to pay ("Total", "Gesamtbetrag", "zu bezahlen", "Montant") as a
          plain number without thousands separators, e.g. 1'272.35 becomes 1272.35. Otherwise 0.
        - due_date: when a bill or invoice must be paid (YYYY-MM-DD), otherwise empty.

        Whether the household should also keep track of this paper elsewhere:
        - record: "insurance" for an insurance policy or a yearly policy or premium overview ("Police",
          "Versicherungsausweis", "Prämienübersicht"); not for a single premium bill. "contract" for a contract or
          contract confirmation for a service paid regularly (mobile, internet, TV, streaming, gym, leasing, rent,
          electricity or gas). "date" for an identity document, passport, driving licence, or a car inspection notice
          ("MFK", "Aufgebot zur Fahrzeugprüfung"). "warranty" for a purchase receipt or invoice for a durable item
          (electronics, an appliance, furniture, a bike) worth at least 50. Otherwise "none".
        - record_kind: for insurance one of health_basic (KVG/LAMal basic health insurance), health_supplementary (VVG),
          accident, household_liability (household contents and private liability together), household_contents
          (Hausrat only), private_liability (Privathaftpflicht only), building, car (car or motorbike), legal_protection,
          life, income_protection (Erwerbsunfähigkeit), travel, pet, other; for a contract one of mobile,
          internet, tv_and_streaming, gym, leasing, rent, subscription, energy, other; for a date one of passport,
          identity_card, driving_licence, car_inspection, vignette, other. Otherwise empty.
        - reference: the policy, customer or contract number, otherwise empty.
        - cost: the premium or price for one period (monthly, quarterly or yearly) as a plain number; for a warranty
          the item's price. Otherwise 0.
        - frequency: how often the cost is paid: monthly, quarterly, yearly, or once.
        - end_date: when the current term ends ("Vertragsende", "Ablauf", "gültig bis", "Laufzeit bis"), or for a date
          the expiry or appointment date (YYYY-MM-DD). Otherwise empty.
        - notice_months: the notice period in months ("Kündigungsfrist 3 Monate"), otherwise 0.
        - item: for a warranty, the product bought, e.g. "Dishwasher Bosch Serie 4". Otherwise empty.
        """;

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(KindNames.Select(n => (JsonNode)n).ToArray()) },
            ["title"] = new JsonObject { ["type"] = "string" },
            ["sender"] = new JsonObject { ["type"] = "string" },
            ["date"] = new JsonObject { ["type"] = "string" },
            ["amount"] = new JsonObject { ["type"] = "number" },
            ["due_date"] = new JsonObject { ["type"] = "string" },
            ["record"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("none", "insurance", "contract", "date", "warranty") },
            ["record_kind"] = new JsonObject { ["type"] = "string" },
            ["reference"] = new JsonObject { ["type"] = "string" },
            ["cost"] = new JsonObject { ["type"] = "number" },
            ["frequency"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("monthly", "quarterly", "yearly", "once") },
            ["end_date"] = new JsonObject { ["type"] = "string" },
            ["notice_months"] = new JsonObject { ["type"] = "number" },
            ["item"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("type", "title", "sender", "date", "amount", "due_date",
            "record", "record_kind", "reference", "cost", "frequency", "end_date", "notice_months", "item"),
    };

    /// <exception cref="InvalidOperationException">The answer isn't the expected JSON.</exception>
    public static DocumentDetails Parse(string json)
    {
        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is null)
            throw new InvalidOperationException(Loc.T("The model’s answer couldn’t be read. Try again or use another model."));

        DocumentKind? kind = Enum.TryParse<DocumentKind>(EntryExtraction.ReadString(answer["type"]), ignoreCase: true, out var k)
            ? k
            : null;
        var amount = Math.Round(Math.Abs(EntryExtraction.ReadDecimal(answer["amount"])), 2);
        return new DocumentDetails(
            kind,
            Limit(EntryExtraction.ReadString(answer["title"]).Trim(), 200),
            Limit(EntryExtraction.ReadString(answer["sender"]).Trim(), 200),
            ReadDate(answer["date"]),
            amount > 0 ? amount : null,
            ReadDate(answer["due_date"]))
        {
            FollowUp = ReadFollowUp(answer),
        };
    }

    /// <summary>The record the paper also belongs to, or null for "none" or an answer without one.</summary>
    internal static FollowUp? ReadFollowUp(JsonObject answer)
    {
        FollowUpKind? kind = EntryExtraction.ReadString(answer["record"]).Trim().ToLowerInvariant() switch
        {
            "insurance" => FollowUpKind.Insurance,
            "contract" => FollowUpKind.Contract,
            "date" => FollowUpKind.Date,
            "warranty" => FollowUpKind.Warranty,
            _ => null,
        };
        if (kind is null)
            return null;

        var cost = Math.Round(Math.Abs(EntryExtraction.ReadDecimal(answer["cost"])), 2);
        var notice = (int)Math.Round(EntryExtraction.ReadDecimal(answer["notice_months"]));
        Recurrence? frequency = EntryExtraction.ReadString(answer["frequency"]).Trim().ToLowerInvariant() switch
        {
            "monthly" => Recurrence.Monthly,
            "quarterly" => Recurrence.Quarterly,
            "yearly" => Recurrence.Yearly,
            "once" => Recurrence.OneTime,
            _ => null,
        };
        return new FollowUp(
            kind.Value,
            // "health_basic" → "HealthBasic", the enum's name
            string.Concat(EntryExtraction.ReadString(answer["record_kind"]).Trim().Split('_', ' ', '-')
                .Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant())),
            Limit(EntryExtraction.ReadString(answer["reference"]).Trim(), 100),
            cost > 0 ? cost : null,
            frequency,
            ReadDate(answer["end_date"]),
            notice is > 0 and <= 24 ? notice : null,
            Limit(EntryExtraction.ReadString(answer["item"]).Trim(), 200));
    }

    private static DateOnly? ReadDate(JsonNode? node) =>
        DateOnly.TryParseExact(EntryExtraction.ReadString(node).Trim(), ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string Limit(string text, int length) => text.Length > length ? text[..length] : text;
}

/// <summary>What the model read from a pension letter; every figure may be missing.</summary>
public sealed record PensionStatementDetails(
    PensionPillar? Pillar, string Provider, DateOnly? Date, decimal? Balance, decimal? PaidIn,
    decimal? ProjectedCapital, decimal? ProjectedPension, decimal? BuyInPotential)
{
    /// <summary>The insured person, as the model read it.</summary>
    public string Person { get; init; } = "";
}

/// <summary>The prompt, answer format and parsing for Swiss retirement statements.</summary>
public static class PensionStatementExtraction
{
    private static readonly Dictionary<string, PensionPillar> Pillars = new()
    {
        ["ahv"] = PensionPillar.Ahv,
        ["pension_fund"] = PensionPillar.PensionFund,
        ["vested_benefits"] = PensionPillar.VestedBenefits,
        ["pillar_3a"] = PensionPillar.Pillar3a,
        ["pillar_3b"] = PensionPillar.Pillar3b,
    };

    public const string Instructions =
        """
        You read Swiss retirement provision statements for a household budgeting app.
        Letters may be in German, French, Italian or English. Amounts are in CHF.

        - pillar: pension_fund for a pension fund certificate ("Vorsorgeausweis", "Pensionskasse", "certificat de
          prévoyance", BVG/LPP); vested_benefits for a vested benefits account ("Freizügigkeitskonto/-police");
          pillar_3a for a 3a account or policy ("Säule 3a", "Vorsorgekonto 3a", "pilier 3a"); pillar_3b for other
          life insurance or savings; ahv for AHV/AVS letters ("Individuelles Konto", "Rentenvorausberechnung").
        - provider: the pension fund, bank or insurer, e.g. "Pensionskasse Basel-Stadt", "VIAC", "Swiss Life".
        - date: the date the figures are valid on ("per", "Stichtag", "au"), YYYY-MM-DD.
        - balance: the current retirement savings: "Altersguthaben", "vorhandenes Sparkapital", "avoir de vieillesse",
          or the account balance ("Kontostand", "Saldo"). For AHV letters 0.
        - paid_in: contributions paid in during the year, if shown ("Beiträge", "Einzahlungen", "Sparbeiträge"
          per year, employee plus employer). Otherwise 0.
        - projected_capital: projected retirement savings at age 65 ("voraussichtliches Altersguthaben mit 65",
          "Alterskapital"), using the interest rate the letter projects with. Otherwise 0.
        - projected_pension: projected YEARLY old-age pension at 65 ("Altersrente", "rente de vieillesse"). If the letter
          only shows a monthly pension, multiply it by 12. Otherwise 0.
        - buy_in: the maximum voluntary buy-in still possible ("Einkaufspotenzial", "möglicher Einkauf", "rachat
          possible"). Otherwise 0.
        All amounts are plain numbers without thousands separators, e.g. 123'456.70 becomes 123456.70.
        """;

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pillar"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Pillars.Keys.Select(k => (JsonNode)k).ToArray()) },
            ["provider"] = new JsonObject { ["type"] = "string" },
            ["date"] = new JsonObject { ["type"] = "string" },
            ["balance"] = new JsonObject { ["type"] = "number" },
            ["paid_in"] = new JsonObject { ["type"] = "number" },
            ["projected_capital"] = new JsonObject { ["type"] = "number" },
            ["projected_pension"] = new JsonObject { ["type"] = "number" },
            ["buy_in"] = new JsonObject { ["type"] = "number" },
        },
        ["required"] = new JsonArray("pillar", "provider", "date", "balance", "paid_in", "projected_capital",
            "projected_pension", "buy_in"),
    };

    /// <exception cref="InvalidOperationException">The answer isn't the expected JSON.</exception>
    public static PensionStatementDetails Parse(string json)
    {
        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is null)
            throw new InvalidOperationException(Loc.T("The model’s answer couldn’t be read. Try again or use another model."));

        decimal? Amount(string name)
        {
            var value = Math.Round(Math.Abs(EntryExtraction.ReadDecimal(answer[name])), 2);
            return value > 0 ? value : null;
        }

        var provider = EntryExtraction.ReadString(answer["provider"]).Trim();
        return new PensionStatementDetails(
            Pillars.TryGetValue(EntryExtraction.ReadString(answer["pillar"]).Trim().ToLowerInvariant(), out var pillar) ? pillar : null,
            provider.Length > 200 ? provider[..200] : provider,
            // Models sometimes answer with the date as printed on Swiss letters, e.g. 01.01.2026.
            DateOnly.TryParseExact(EntryExtraction.ReadString(answer["date"]).Trim(), ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
            Amount("balance"), Amount("paid_in"), Amount("projected_capital"), Amount("projected_pension"), Amount("buy_in"));
    }
}
