using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Keystone.ViewModels;

namespace Keystone.Controls;

/// <summary>
/// A column title that sorts its list when clicked, and reverses the order when clicked again.
/// ▲ or ▼ shows the column the list is sorted by.
/// </summary>
public sealed class SortHeader : Button
{
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<SortHeader, string>(nameof(Label), "");

    /// <summary>The list's name, e.g. "transactions".</summary>
    public static readonly StyledProperty<string> ListProperty = AvaloniaProperty.Register<SortHeader, string>(nameof(List), "");

    /// <summary>The column, e.g. "amount".</summary>
    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<SortHeader, string>(nameof(Key), "");

    /// <summary>Bound to <see cref="MainViewModel.ListSorts"/>, so the arrow follows the sort.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> SortsProperty =
        AvaloniaProperty.Register<SortHeader, IReadOnlyDictionary<string, string>?>(nameof(Sorts));

    private readonly TextBlock _label = new() { Classes = { "columnHeader" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _arrow = new() { Classes = { "columnHeader" }, FontSize = 9, VerticalAlignment = VerticalAlignment.Center };

    public SortHeader()
    {
        Classes.Add("sortHeader");
        Cursor = new Cursor(StandardCursorType.Hand);
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _label, _arrow } };
    }

    protected override System.Type StyleKeyOverride => typeof(Button);

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string List { get => GetValue(ListProperty); set => SetValue(ListProperty, value); }

    public string Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    public IReadOnlyDictionary<string, string>? Sorts { get => GetValue(SortsProperty); set => SetValue(SortsProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty)
            _label.Text = Label;
        if (change.Property == SortsProperty || change.Property == KeyProperty || change.Property == ListProperty)
        {
            var sort = Sorts is not null && Sorts.TryGetValue(List, out var s) ? s : null;
            _arrow.Text = sort == Key ? "▲" : sort == "-" + Key ? "▼" : "";
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (DataContext is MainViewModel vm)
            vm.SortListCommand.Execute($"{List}:{Key}");
    }
}
