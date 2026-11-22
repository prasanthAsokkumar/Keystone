using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Keystone.Services;

/// <summary>
/// CSV files that open straight in Excel and Numbers with Swiss settings: semicolons between fields
/// (Excel in Switzerland expects them), a byte order mark so umlauts show, and amounts as 1234.50.
/// </summary>
public static class Csv
{
    public const char Separator = ';';

    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Build(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        var text = new StringBuilder();
        text.AppendLine(Line(header));
        foreach (var row in rows)
            text.AppendLine(Line(row));
        return text.ToString();
    }

    public static string Line(IEnumerable<string> fields) => string.Join(Separator, fields.Select(Field));

    /// <summary>Quotes a field when it holds a separator, quote or line break.</summary>
    public static string Field(string? value)
    {
        value ??= "";
        return value.IndexOfAny([Separator, '"', '\n', '\r']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Date(System.DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
