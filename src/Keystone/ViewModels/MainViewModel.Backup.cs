using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>Daily copies of the database next to the documents, and restoring one.</summary>
public partial class MainViewModel
{
    /// <summary>How often Keystone checks whether a backup is due while it is open.</summary>
    private static readonly TimeSpan BackupCheckInterval = TimeSpan.FromHours(1);

    private bool _backupLoopStarted;

    /// <summary>One backup at a time; a second request waits for the first, then makes its own copy.</summary>
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    public string BackupFolder => Path.Combine(DocumentsFolder, DatabaseBackup.FolderName);

    public string BackupNote => Loc.F("Once a day Keystone saves a copy of all its data to {0} and keeps the last {1}.",
        BackupFolder, DatabaseBackup.Keep);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupError))]
    public partial string? BackupError { get; set; }

    public bool HasBackupError => !string.IsNullOrEmpty(BackupError);

    [ObservableProperty]
    public partial string BackupStatusText { get; set; } = "";

    /// <summary>A backup chosen to restore, waiting for the user to confirm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestoreCandidate), nameof(RestoreQuestion))]
    public partial DatabaseBackup.Backup? RestoreCandidate { get; set; }

    public bool HasRestoreCandidate => RestoreCandidate is not null;

    public string RestoreQuestion => RestoreCandidate is { } backup
        ? Loc.F("Replace all data in Keystone with the backup from {0}? Your data as it is now is backed up first, so you can go back.",
            FormatBackupTime(backup.CreatedAt))
        : "";

    private static string FormatBackupTime(DateTime time) =>
        time.Date == DateTime.Today
            ? Loc.F("today at {0:t}", time)
            : time.Date == DateTime.Today.AddDays(-1)
                ? Loc.F("yesterday at {0:t}", time)
                : $"{Format.Date(DateOnly.FromDateTime(time))} {time:t}";

    private void RefreshBackupStatus()
    {
        OnPropertyChanged(nameof(BackupNote));
        var latest = DatabaseBackup.List(BackupFolder).FirstOrDefault();
        BackupStatusText = latest is null
            ? Loc.T("No backup yet.")
            : Loc.F("Last backup: {0}", FormatBackupTime(latest.CreatedAt));
    }

    /// <summary>Backs up when the last copy is a day old, now and then every hour while Keystone is open.</summary>
    private void StartBackups()
    {
        RefreshBackupStatus();
        if (_backupLoopStarted)
            return;
        _backupLoopStarted = true;
        _ = BackupLoopAsync();
    }

    private async Task BackupLoopAsync()
    {
        using var timer = new PeriodicTimer(BackupCheckInterval);
        do
        {
            try
            {
                if (DatabaseBackup.IsDue(BackupFolder, DateTime.Now))
                    await BackUpAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BackupError = Loc.F("Could not back up: {0}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync());
    }

    [RelayCommand]
    private async Task BackUpNowAsync() => await BackUpAsync();

    private async Task BackUpAsync()
    {
        await _backupGate.WaitAsync();
        BackupError = null;
        try
        {
            var databasePath = repository.DatabasePath;
            var folder = BackupFolder;
            await Task.Run(() => DatabaseBackup.Create(databasePath, folder, DateTime.Now));
        }
        catch (Exception ex)
        {
            BackupError = Loc.F("Could not back up: {0}", ex.Message);
        }
        finally
        {
            _backupGate.Release();
            RefreshBackupStatus();
        }
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            System.Diagnostics.Process.Start("open", $"\"{BackupFolder}\"");
        }
        catch (Exception ex)
        {
            BackupError = Loc.F("Could not open the folder: {0}", ex.Message);
        }
    }

    /// <summary>A backup file picked in Settings; restoring waits for confirmation.</summary>
    public void ChooseBackupToRestore(string path)
    {
        BackupError = null;
        if (!DatabaseBackup.IsKeystoneDatabase(path))
        {
            BackupError = Loc.T("This file isn’t a Keystone backup.");
            return;
        }

        var created = DatabaseBackup.List(Path.GetDirectoryName(path) ?? "").FirstOrDefault(b => b.Path == path)?.CreatedAt
                      ?? File.GetLastWriteTime(path);
        RestoreCandidate = new DatabaseBackup.Backup(path, created);
    }

    [RelayCommand]
    private void CancelRestore() => RestoreCandidate = null;

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (RestoreCandidate is not { } backup)
            return;

        BackupError = null;
        await _backupGate.WaitAsync();
        try
        {
            var databasePath = repository.DatabasePath;
            var folder = BackupFolder;
            await Task.Run(() =>
            {
                DatabaseBackup.Restore(backup.Path, databasePath, folder, DateTime.Now);
                repository.Migrate(); // an older backup gets the newest tables
            });
        }
        catch (Exception ex)
        {
            BackupError = Loc.F("Could not restore the backup: {0}", ex.Message);
            return;
        }
        finally
        {
            _backupGate.Release();
            RestoreCandidate = null;
        }

        CloseOtherPanels();
        await LoadAsync();
        BackupStatusText = Loc.F("Restored the backup from {0}.", FormatBackupTime(backup.CreatedAt));
    }
}
