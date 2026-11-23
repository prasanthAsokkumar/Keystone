using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keystone.Services;

/// <summary>
/// What the Keystone widget on the Mac's desktop shows: open tasks, bills to pay and the next appointments.
/// Keystone writes it next to its database whenever these change; the widget (a small Swift extension inside
/// Keystone.app) reads it. It never leaves this Mac. With the app lock on, only counts are written, no titles.
/// Dates are written as dates, so the widget can say "today" or "overdue" correctly on later days.
/// </summary>
public sealed record WidgetSnapshot(
    int Version,
    DateTime UpdatedAt,
    string Language,
    bool DetailsHidden,
    IReadOnlyDictionary<string, string> Labels,
    WidgetSection Tasks,
    WidgetSection Bills,
    WidgetSection Appointments)
{
    public const int CurrentVersion = 1;

    /// <summary>How many items of each kind are written; the large widget shows up to five.</summary>
    public const int MaxItems = 8;

    /// <summary>Appointments this many days ahead are shown.</summary>
    public const int AppointmentDays = 14;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Umlauts and "+" as they are; the file only stays on this Mac.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>"widget.json" next to the database; "widget-dev.json" for the debug build's own database.</summary>
    public static string PathFor(string databasePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        var name = Path.GetFileName(databasePath).StartsWith("keystone-dev", StringComparison.OrdinalIgnoreCase)
            ? "widget-dev.json"
            : "widget.json";
        return Path.Combine(folder, name);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Writes the file in one go (a temporary file renamed over it), so the widget never reads half of it.</summary>
    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A count, how many are overdue, an optional total, and the first items.</summary>
public sealed record WidgetSection(int Count, int Overdue, string? Total, IReadOnlyList<WidgetItem> Items);

/// <summary>One line: a title and when (a date, or a date and time). Title and detail are empty when details are hidden.</summary>
public sealed record WidgetItem(string Title, string? Detail, DateOnly? Date, DateTime? Start, bool AllDay = false);
