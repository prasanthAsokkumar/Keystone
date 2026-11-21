using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Keystone.Services;
using static Keystone.Controls.ChartDrawing;

namespace Keystone.Controls;

/// <summary>Grouped bars of income and expenses per month. Planned (future) months are faded.</summary>
public class MonthlyBarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<MonthTotals>?> MonthsProperty =
        AvaloniaProperty.Register<MonthlyBarChart, IReadOnlyList<MonthTotals>?>(nameof(Months));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<MonthlyBarChart>();

    public static readonly Color IncomeColor = Color.Parse("#10B981");
    public static readonly Color ExpenseColor = Color.Parse("#F97066");

    private const byte PlannedAlpha = 80;
    private const double MarginLeft = 64;
    private const double MarginRight = 8;
    private const double MarginTop = 8;
    private const double MarginBottom = 28;

    static MonthlyBarChart()
    {
        AffectsRender<MonthlyBarChart>(MonthsProperty, ForegroundProperty);
    }

    public IReadOnlyList<MonthTotals>? Months
    {
        get => GetValue(MonthsProperty);
        set => SetValue(MonthsProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var months = Months ?? [];
        var foreground = Foreground ?? Brushes.Gray;
        var plot = new Rect(
            MarginLeft,
            MarginTop,
            Math.Max(0, Bounds.Width - MarginLeft - MarginRight),
            Math.Max(0, Bounds.Height - MarginTop - MarginBottom));
        if (months.Count == 0 || plot.Width < 20 || plot.Height < 20)
            return;

        var max = (double)months.Max(m => Math.Max(m.Income, m.Expenses));
        var (yMin, yMax, yStep) = NiceScale(0, Math.Max(max, 1));
        double Y(double value) => plot.Bottom - (value - yMin) / (yMax - yMin) * plot.Height;

        DrawValueAxis(context, plot, yMin, yMax, yStep, foreground, Y);

        var slot = plot.Width / months.Count;
        var barWidth = Math.Min(14, slot * 0.26);
        var gap = Math.Min(4, slot * 0.05);

        for (var i = 0; i < months.Count; i++)
        {
            var month = months[i];
            var center = plot.Left + slot * (i + 0.5);
            var alpha = month.IsPlanned ? PlannedAlpha : (byte)255;

            DrawBar(context, center - gap / 2 - barWidth, barWidth, Y((double)month.Income), plot.Bottom,
                new SolidColorBrush(IncomeColor, alpha / 255.0));
            DrawBar(context, center + gap / 2, barWidth, Y((double)month.Expenses), plot.Bottom,
                new SolidColorBrush(ExpenseColor, alpha / 255.0));

            var text = month.Month.Month == 1 || i == 0
                ? month.Month.ToString("MMM yy", CultureInfo.CurrentCulture)
                : month.Month.ToString("MMM", CultureInfo.CurrentCulture);
            var label = CreateText(text, foreground, LabelFontSize);
            context.DrawText(label, new Point(center - label.Width / 2, plot.Bottom + 8));
        }
    }

    private static void DrawBar(DrawingContext context, double x, double width, double top, double bottom, IBrush brush)
    {
        var height = bottom - top;
        if (height < 0.5)
            return;

        var radius = Math.Min(width / 2, height);
        context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, top, width, height), radius, radius, 0, 0));
    }
}
