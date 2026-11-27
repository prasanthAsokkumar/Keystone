using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private void MatchAccount_Click(object? sender, RoutedEventArgs e) => MainWindow.Of(this)?.MatchAccount_Click(sender, e);

    private void RestoreBackup_Click(object? sender, RoutedEventArgs e) => MainWindow.Of(this)?.RestoreBackup_Click(sender, e);

    private void ChangeDocumentsFolder_Click(object? sender, RoutedEventArgs e) => MainWindow.Of(this)?.ChangeDocumentsFolder_Click(sender, e);
}
