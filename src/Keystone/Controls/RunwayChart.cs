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
using Keystone.Localization;

namespace Keystone.Controls;

/// <summary>Line chart of past cash balance (solid) and projected balance (dashed).</summary>
public class RunwayChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<BalancePoint>?> HistoryProperty =
        AvaloniaProperty.Register<RunwayChart, IReadOnlyList<BalancePoint>?>(nameof(History));

    public static readonly StyledProperty<IReadOnlyList<BalancePoint>?> ProjectionProperty =
        AvaloniaProperty.Register<RunwayChart, IReadOnlyList<BalancePoint>?>(nameof(Projection));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<RunwayChart>();

    public static readonly Color HistoryColor = Color.Parse("#3B82F6");
    public static readonly Color ProjectionColor = Color.Parse("#F59E0B");
    public static readonly IBrush HistoryBrush = new SolidColorBrush(HistoryColor);
    public static readonly IBrush ProjectionBrush = new SolidColorBrush(ProjectionColor);
    public static readonly IBrush ZeroBrush = new SolidColorBrush(Color.Parse("#F04438"));

    private const double MarginLeft = 64;
    private const double MarginRight = 24;
    private const double MarginTop = 16;
    private const double MarginBottom = 32;

    static RunwayChart()
    {
        AffectsRender<RunwayChart>(HistoryProperty, ProjectionProperty, ForegroundProperty);
    }

    public IReadOnlyList<BalancePoint>? History
    {
        get => GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    public IReadOnlyList<BalancePoint>? Projection
    {
        get => GetValue(ProjectionProperty);
        set => SetValue(ProjectionProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var history = History ?? [];
        var projection = Projection ?? [];
        var all = history.Concat(projection).ToList();
        var foreground = Foreground ?? Brushes.Gray;
        var size = Bounds.Size;

        if (all.Count == 0)
        {
            var empty = CreateText(Loc.T("Add transactions to see your runway"), foreground, 14);
            context.DrawText(empty, new Point((size.Width - empty.Width) / 2, (size.Height - empty.Height) / 2));
            return;
        }

        var plot = new Rect(
            MarginLeft,
            MarginTop,
            Math.Max(0, size.Width - MarginLeft - MarginRight),
            Math.Max(0, size.Height - MarginTop - MarginBottom));
        if (plot.Width < 20 || plot.Height < 20)
            return;

        var minDay = all.Min(p => p.Date.DayNumber);
        var maxDay = all.Max(p => p.Date.DayNumber);
        if (maxDay == minDay)
            maxDay = minDay + 30;

        var (yMin, yMax, yStep) = NiceScale(
            Math.Min(0, (double)all.Min(p => p.Balance)),
            Math.Max(0, (double)all.Max(p => p.Balance)));

        double X(DateOnly date) => plot.Left + (date.DayNumber - minDay) / (double)(maxDay - minDay) * plot.Width;
        double Y(decimal value) => plot.Bottom - ((double)value - yMin) / (yMax - yMin) * plot.Height;

        DrawValueAxis(context, plot, yMin, yMax, yStep, foreground, v => Y((decimal)v));
        DrawDateAxis(context, plot, minDay, maxDay, foreground, X);

        // Zero line: where the money runs out.
        context.DrawLine(new Pen(ZeroBrush, 1, DashStyle.Dash), new Point(plot.Left, Y(0)), new Point(plot.Right, Y(0)));

        DrawArea(context, history, HistoryColor, X, Y, Y(0));
        DrawArea(context, projection, ProjectionColor, X, Y, Y(0));

        if (projection.Count > 0)
        {
            var todayX = X(projection[0].Date);
            context.DrawLine(
                new Pen(GridBrush, 1, DashStyle.Dash),
                new Point(todayX, plot.Top),
                new Point(todayX, plot.Bottom));
            var todayLabel = CreateText(Loc.T("Today"), foreground, LabelFontSize);
            context.DrawText(todayLabel, new Point(todayX + 4, plot.Top));
        }

        DrawSeries(context, projection, new Pen(ProjectionBrush, 2, DashStyle.Dash), ProjectionBrush, X, Y);
        DrawSeries(context, history, new Pen(HistoryBrush, 2.5), HistoryBrush, X, Y);

        if (projection.Count > 1 && projection[^1].Balance <= 0)
        {
            var zero = projection[^1];
            var point = new Point(X(zero.Date), Y(zero.Balance));
            context.DrawEllipse(ZeroBrush, null, point, 5, 5);
            var label = CreateText(Loc.F("Out of cash · {0:MMM yyyy}", zero.Date), ZeroBrush, 12);
            var labelX = Math.Min(point.X - label.Width / 2, plot.Right - label.Width);
            context.DrawText(label, new Point(Math.Max(plot.Left, labelX), point.Y - label.Height - 8));
        }
    }

    private static void DrawDateAxis(
        DrawingContext context, Rect plot, int minDay, int maxDay, IBrush foreground, Func<DateOnly, double> x)
    {
        var first = DateOnly.FromDayNumber(minDay);
        var last = DateOnly.FromDayNumber(maxDay);
        var month = new DateOnly(first.Year, first.Month, 1);
        if (month < first)
            month = month.AddMonths(1);

        var monthCount = (last.Year - month.Year) * 12 + last.Month - month.Month + 1;
        var maxLabels = Math.Max(1, (int)(plot.Width / 70));
        var step = Math.Max(1, (int)Math.Ceiling(monthCount / (double)maxLabels));

        for (var i = 0; month <= last; month = month.AddMonths(1), i++)
        {
            if (i % step != 0)
                continue;

            var px = x(month);
            context.DrawLine(new Pen(GridBrush, 1), new Point(px, plot.Bottom), new Point(px, plot.Bottom + 4));

            var text = month.Month == 1 || i == 0 ? month.ToString("MMM yy", CultureInfo.CurrentCulture)
                : month.ToString("MMM", CultureInfo.CurrentCulture);
            var label = CreateText(text, foreground, LabelFontSize);
            context.DrawText(label, new Point(px - label.Width / 2, plot.Bottom + 8));
        }
    }

    private static void DrawSeries(
        DrawingContext context, IReadOnlyList<BalancePoint> points, Pen pen, IBrush markerBrush,
        Func<DateOnly, double> x, Func<decimal, double> y)
    {
        if (points.Count == 0)
            return;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x(points[0].Date), y(points[0].Balance)), false);
            foreach (var p in points.Skip(1))
                g.LineTo(new Point(x(p.Date), y(p.Balance)));
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);

        var last = points[^1];
        context.DrawEllipse(markerBrush, null, new Point(x(last.Date), y(last.Balance)), 4, 4);
    }

    /// <summary>Soft fill between a series and the zero line, fading away from the line.</summary>
    private static void DrawArea(
        DrawingContext context, IReadOnlyList<BalancePoint> points, Color color,
        Func<DateOnly, double> x, Func<decimal, double> y, double baseline)
    {
        if (points.Count < 2)
            return;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x(points[0].Date), baseline), true);
            foreach (var p in points)
                g.LineTo(new Point(x(p.Date), y(p.Balance)));
            g.LineTo(new Point(x(points[^1].Date), baseline));
            g.EndFigure(true);
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(56, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(4, color.R, color.G, color.B), 1),
            },
        };
        context.DrawGeometry(brush, null, geometry);
    }
}
