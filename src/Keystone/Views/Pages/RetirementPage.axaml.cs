using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class RetirementPage : UserControl
{
    public RetirementPage() => InitializeComponent();

    /// <summary>Double-clicking a retirement account shows its details and history.</summary>
    private void PensionAccountRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PensionAccountRow row } && DataContext is MainViewModel vm)
            vm.Retirement.EditPensionAccountCommand.Execute(row.Account);
    }
}
