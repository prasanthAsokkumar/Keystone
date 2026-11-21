using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Runway.Data;

namespace Runway.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "";
        VersionText.Text = $"Version {version.Split('+')[0]}"; // Drop the build metadata suffix.
        DatabaseText.Text = RunwayDbContext.DefaultDatabasePath();
        CopyrightText.Text = $"© {DateTime.Today.Year} Prasanth Asokkumar";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
