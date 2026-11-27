using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class StatementPanel : UserControl
{
    public StatementPanel() => InitializeComponent();

    private void AttachStatementLetter_Click(object? sender, RoutedEventArgs e) => MainWindow.Of(this)?.AttachStatementLetter_Click(sender, e);
}
