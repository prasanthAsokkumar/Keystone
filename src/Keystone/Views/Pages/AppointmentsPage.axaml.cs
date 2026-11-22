using Avalonia.Controls;
using Avalonia.Input;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class AppointmentsPage : UserControl
{
    public AppointmentsPage() => InitializeComponent();

    /// <summary>Double-clicking an appointment opens it.</summary>
    private void AppointmentRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: AppointmentRow row } && DataContext is MainViewModel vm)
            vm.EditAppointmentCommand.Execute(row.Appointment);
    }
}
