using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class BulkPanel : UserControl
{
    public BulkPanel() => InitializeComponent();

    private void AddDocument_Click(object? sender, RoutedEventArgs e) => MainWindow.Of(this)?.AddDocument_Click(sender, e);
}
