using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class TransactionsPage : UserControl
{
    public TransactionsPage() => InitializeComponent();

    /// <summary>Double-clicking a row in the Entries table opens it for editing.</summary>
    private void EntryRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: EntryRow row } && DataContext is MainViewModel vm)
            vm.EditTransactionCommand.Execute(row.Transaction);
    }
}
