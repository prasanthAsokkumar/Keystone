using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class TasksPage : UserControl
{
    public TasksPage() => InitializeComponent();

    /// <summary>Double-clicking a task opens it for editing.</summary>
    private void TaskRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TaskRow row } && DataContext is MainViewModel vm)
            vm.Tasks.EditTaskCommand.Execute(row.Task);
    }
}
