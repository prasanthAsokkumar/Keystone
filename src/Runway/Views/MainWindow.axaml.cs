using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Runway.ViewModels;

namespace Runway.Views;

public partial class MainWindow : Window
{
    static MainWindow()
    {
        // A date picker keeps showing its old text when its date is cleared from code; clear the text too.
        // The picker updates its text after the change, so clear it once that has happened.
        CalendarDatePicker.SelectedDateProperty.Changed.AddClassHandler<CalendarDatePicker>((picker, _) =>
        {
            if (picker.SelectedDate is null)
                Dispatcher.UIThread.Post(() =>
                {
                    if (picker.SelectedDate is null && !string.IsNullOrEmpty(picker.Text))
                        picker.Text = "";
                });
        });
    }

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        Title += " (development data)";
#endif
        AddHandler(DragDrop.DragEnterEvent, Window_DragOver);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged += ViewModel_PropertyChanged;
        };
    }

    private bool _closingAfterSave;

    /// <summary>A note edit that hasn't been written yet is saved before the window closes.</summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingAfterSave || e.Cancel || DataContext is not MainViewModel { HasUnsavedNote: true } vm)
            return;

        e.Cancel = true;
        await vm.SaveNoteNowAsync();
        _closingAfterSave = true;
        Close();
    }

    /// <summary>A new, empty note gets the cursor in its title.</summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedNoteItem) &&
            sender is MainViewModel { SelectedNoteItem: { IsUntitled: true, HasPreview: false } })
            Dispatcher.UIThread.Post(() => NoteTitleBox.Focus(), DispatcherPriority.Background);
    }

    /// <summary>Any file can be filed as a document; reading entries needs a type the AI can read.</summary>
    private string? DroppedPath(DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return null;
        return e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && File.Exists(p) && (vm.DropFilesAsDocument || MainViewModel.CanImport(p)));
    }

    private static List<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null && File.Exists(p))
            .Select(p => p!)
            .ToList() ?? [];

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        var canDrop = DroppedPath(e) is not null;
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = canDrop;
    }

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (DroppedPath(e) is not { } path || DataContext is not MainViewModel vm)
            return;

        // Several files at once are filed together, each read by the local AI.
        var paths = DroppedPaths(e);
        if (paths.Count > 1 && vm.DropFilesAsDocument && !vm.IsRetirementPage)
        {
            await vm.StartBulkDocumentsAsync(paths);
            return;
        }

        if (vm.IsRetirementPage)
            await vm.StartNewStatementAsync(path);
        else if (vm.DropFilesAsDocument)
            await vm.StartNewDocumentAsync(path);
        else
            await vm.ImportDocumentAsync(path);
    }

    /// <summary>One file opens the document form; several go to the import list.</summary>
    private async void AddDocument_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add bills, letters or notices",
            AllowMultiple = true,
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (DataContext is not MainViewModel vm || paths.Count == 0)
            return;

        if (paths.Count > 1 || vm.IsBulkPanelOpen)
            await vm.StartBulkDocumentsAsync(paths);
        else
            await vm.StartNewDocumentAsync(paths[0]);
    }

    private async void AttachStatementLetter_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the letter",
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && DataContext is MainViewModel vm)
            await vm.AttachStatementLetterAsync(path);
    }

    private async void ChangeDocumentsFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to keep documents",
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path && DataContext is MainViewModel vm)
            await vm.ChangeDocumentsFolderAsync(path);
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a bill, payslip or statement",
            FileTypeFilter =
            [
                new FilePickerFileType("Documents and photos")
                {
                    Patterns = ["*.pdf", "*.csv", "*.tsv", "*.txt", "*.png", "*.jpg", "*.jpeg", "*.heic", "*.heif"],
                },
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && DataContext is MainViewModel vm)
            await vm.ImportDocumentAsync(path);
    }

    /// <summary>Clicking outside the side panel closes it.</summary>
    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ClosePanelCommand.Execute(null);
    }

    /// <summary>Double-clicking a document opens the file.</summary>
    private void DocumentRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentRow row } && DataContext is MainViewModel vm)
            vm.OpenDocumentCommand.Execute(row.Document);
    }

    /// <summary>Double-clicking a retirement account shows its details and history.</summary>
    private void PensionAccountRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PensionAccountRow row } && DataContext is MainViewModel vm)
            vm.EditPensionAccountCommand.Execute(row.Account);
    }

    /// <summary>Double-clicking a task opens it for editing.</summary>
    private void TaskRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TaskRow row } && DataContext is MainViewModel vm)
            vm.EditTaskCommand.Execute(row.Task);
    }

    /// <summary>Double-clicking a row in the Entries table opens it for editing.</summary>
    private void EntryRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: EntryRow row } && DataContext is MainViewModel vm)
            vm.EditTransactionCommand.Execute(row.Transaction);
    }
}
