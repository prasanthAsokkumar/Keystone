using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Runway.Controls;

/// <summary>Drawing helpers shared by the chart controls.</summary>
internal static class ChartDrawing
{
    public const double LabelFontSize = 11;

    public static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128));

    /// <summary>Horizontal grid lines with value labels to the left of the plot.</summary>
    public static void DrawValueAxis(
        DrawingContext context, Rect plot, double yMin, double yMax, double yStep, IBrush foreground,
        Func<double, double> y)
    {
        var gridPen = new Pen(GridBrush, 1);
        for (var value = yMin; value <= yMax + yStep / 2; value += yStep)
        {
            var py = y(value);
            context.DrawLine(gridPen, new Point(plot.Left, py), new Point(plot.Right, py));

            var label = CreateText(FormatCompact(value), foreground, LabelFontSize);
            context.DrawText(label, new Point(plot.Left - label.Width - 8, py - label.Height / 2));
        }
    }

    /// <summary>Rounds the axis range out to "nice" tick values (1, 2 or 5 × 10ⁿ).</summary>
    public static (double Min, double Max, double Step) NiceScale(double min, double max, int targetTicks = 5)
    {
        if (max - min < 1e-9)
            max = min + 1;

        var rough = (max - min) / targetTicks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var normalized = rough / magnitude;
        var nice = normalized < 1.5 ? 1 : normalized < 3 ? 2 : normalized < 7 ? 5 : 10;
        var step = nice * magnitude;

        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    public static string FormatCompact(double value)
    {
        var abs = Math.Abs(value);
        var culture = CultureInfo.CurrentCulture;
        if (abs >= 1_000_000)
            return (value / 1_000_000).ToString("0.#", culture) + "M";
        if (abs >= 1_000)
            return (value / 1_000).ToString("0.#", culture) + "k";
        return value.ToString("0", culture);
    }

    public static FormattedText CreateText(string text, IBrush brush, double fontSize) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, fontSize, brush);
}
