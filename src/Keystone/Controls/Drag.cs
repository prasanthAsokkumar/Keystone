using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Controls;

/// <summary>
/// Dragging rows inside Keystone: an entry onto Insurance or Taxes, a document onto a person, a policy above
/// another. <c>Drag.Item</c> makes a control draggable; <c>Drag.Target</c> (with an optional
/// <c>Drag.TargetItem</c>) makes it accept drops. What a drop does is decided by <see cref="MainViewModel"/>.
/// </summary>
public static class Drag
{
    /// <summary>Only Keystone itself can read what is dragged; it never leaves the app.</summary>
    public static readonly DataFormat<object> Format = DataFormat.CreateInProcessFormat<object>("keystone-item");

    /// <summary>How far the pointer moves before a press becomes a drag, so clicks and double-clicks still work.</summary>
    private const double Threshold = 6;

    public static readonly AttachedProperty<object?> ItemProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("Item", typeof(Drag));

    public static readonly AttachedProperty<string?> TargetProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Target", typeof(Drag));

    public static readonly AttachedProperty<object?> TargetItemProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("TargetItem", typeof(Drag));

    public static object? GetItem(Control control) => control.GetValue(ItemProperty);
    public static void SetItem(Control control, object? value) => control.SetValue(ItemProperty, value);
    public static string? GetTarget(Control control) => control.GetValue(TargetProperty);
    public static void SetTarget(Control control, string? value) => control.SetValue(TargetProperty, value);
    public static object? GetTargetItem(Control control) => control.GetValue(TargetItemProperty);
    public static void SetTargetItem(Control control, object? value) => control.SetValue(TargetItemProperty, value);

    private static PointerPressedEventArgs? _pressed;
    private static Control? _pressedOn;

    static Drag()
    {
        ItemProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            control.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            if (e.NewValue is not null)
            {
                control.AddHandler(InputElement.PointerPressedEvent, OnPressed);
                control.AddHandler(InputElement.PointerMovedEvent, OnMoved);
            }
        });
        TargetProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            DragDrop.SetAllowDrop(control, e.NewValue is not null);
            control.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            control.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            control.RemoveHandler(DragDrop.DropEvent, OnDrop);
            if (e.NewValue is not null)
            {
                control.AddHandler(DragDrop.DragOverEvent, OnDragOver);
                control.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
                control.AddHandler(DragDrop.DropEvent, OnDrop);
            }
        });
    }

    private static MainViewModel? ViewModel(Control control) =>
        TopLevel.GetTopLevel(control)?.DataContext as MainViewModel;

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            _pressed = e;
            _pressedOn = control;
        }
    }

    private static async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || sender != _pressedOn || sender is not Control control)
            return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            _pressed = null;
            return;
        }
        var delta = e.GetPosition(control) - pressed.GetPosition(control);
        if (Math.Abs(delta.X) < Threshold && Math.Abs(delta.Y) < Threshold)
            return;

        _pressed = null;
        if (GetItem(control) is not { } item || ViewModel(control) is not { } vm || vm.IsLocked)
            return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Format, item));
        vm.DraggedItem = item;
        try
        {
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
        }
        finally
        {
            vm.DraggedItem = null;
        }
    }

    private static object? Dragged(DragEventArgs e) => e.DataTransfer.TryGetValue(Format);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control control || Dragged(e) is not { } item)
            return;
        var allowed = ViewModel(control)?.CanDrop(GetTarget(control)!, GetTargetItem(control), item) == true;
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        control.Classes.Set("dropHover", allowed);
        e.Handled = true;
    }

    private static void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control control)
            control.Classes.Set("dropHover", false);
    }

    private static async void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control || Dragged(e) is not { } item)
            return;
        control.Classes.Set("dropHover", false);
        e.Handled = true;
        if (ViewModel(control) is { } vm && vm.CanDrop(GetTarget(control)!, GetTargetItem(control), item))
            await vm.DropAsync(GetTarget(control)!, GetTargetItem(control), item);
    }
}
