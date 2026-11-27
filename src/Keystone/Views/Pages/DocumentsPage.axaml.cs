using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class DocumentsPage : UserControl
{
    public DocumentsPage() => InitializeComponent();

    /// <summary>Double-clicking a document opens the file.</summary>
    private void DocumentRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentRow row } && DataContext is MainViewModel vm)
            vm.OpenDocumentCommand.Execute(row.Document);
    }
}
