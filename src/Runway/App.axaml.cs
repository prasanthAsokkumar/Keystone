using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Runway.Data;
using Runway.ViewModels;
using Runway.Views;

namespace Runway;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var repository = new RunwayRepository(RunwayDbContext.DefaultDatabasePath());
            repository.Migrate();

            var viewModel = new MainViewModel(repository);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            _ = viewModel.LoadAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void About_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            new AboutWindow().ShowDialog(owner);
    }
}
