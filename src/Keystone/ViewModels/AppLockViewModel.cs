using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Data;
using Keystone.Localization;
using Keystone.Services.Mac;

namespace Keystone.ViewModels;

/// <summary>
/// Locking Keystone behind Touch ID (or the Mac's password) at start and after time away. Its own view model:
/// the rest of the app only asks whether it is locked and tells it when the window loses or gains focus.
/// </summary>
/// <param name="lockChanged">The lock was turned on or off (the widget hides titles while it is on).</param>
/// <param name="returned">The owner came back to the window (check the inbox, sync the calendar).</param>
public sealed partial class AppLockViewModel(
    KeystoneRepository repository, Func<bool> isLoading, Action<string> reportError, Action lockChanged, Action returned)
    : ViewModelBase
{
    /// <summary>Checks the user; replaceable in tests.</summary>
    public Func<string, Task<bool>> Authenticate { get; set; } = TouchId.AuthenticateAsync;

    public bool CanLock => TouchId.IsAvailable;

    /// <summary>Minutes away before locking; <see cref="WhenMacLocks"/> locks only with the Mac's own screen lock.</summary>
    private static readonly int[] LockDelays = [WhenMacLocks, 0, 1, 5, 15, 60];

    /// <summary>Stored as the lock's minutes: lock only when the Mac locks its screen or goes to sleep.</summary>
    public const int WhenMacLocks = -1;

    private int LockMinutes => LockDelays[Math.Clamp(LockDelayIndex, 0, LockDelays.Length - 1)];

    public IReadOnlyList<string> LockDelayNames =>
    [
        Loc.T("When my Mac locks or sleeps"),
        Loc.T("As soon as I switch away"),
        Loc.T("After 1 minute away"),
        Loc.T("After 5 minutes away"),
        Loc.T("After 15 minutes away"),
        Loc.T("After 1 hour away"),
    ];

    [ObservableProperty]
    public partial bool IsLockEnabled { get; set; }

    [ObservableProperty]
    public partial int LockDelayIndex { get; set; }

    /// <summary>Everything is hidden until the user unlocks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcceptDrop))]
    public partial bool IsLocked { get; set; }

    [ObservableProperty]
    public partial string LockMessage { get; set; } = "";

    private DateTime? _leftAt;
    private bool _isUnlocking;
    private bool _isChangingLock;

    /// <summary>Windows Keystone opened itself (the Touch ID prompt, file pickers) that are open now.</summary>
    private int _ownWindows;

    /// <summary>Focus went to one of Keystone's own windows; coming back from it isn't returning from elsewhere.</summary>
    private bool _leftForOwnWindow;

    /// <summary>
    /// Marks a window Keystone itself opens, such as a file picker, so the focus it takes isn't counted as
    /// time away: the lock neither closes nor asks again because of it. Dispose when the window has closed.
    /// </summary>
    public IDisposable OwnWindow()
    {
        _ownWindows++;
        return new OwnWindowScope(this);
    }

    private sealed class OwnWindowScope(AppLockViewModel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._ownWindows--;
        }
    }

    /// <summary>After the language changed: the timing choices, keeping the one chosen.</summary>
    internal override void RefreshTexts()
    {
        var delay = LockDelayIndex;
        _isChangingLock = true;
        OnPropertyChanged(string.Empty);
        LockDelayIndex = delay;
        _isChangingLock = false;
    }

    /// <summary>Files can be dropped on the window only while it is unlocked.</summary>
    public bool CanAcceptDrop => !IsLocked;

    /// <summary>Asks for Touch ID; the system prompt counts as one of Keystone's own windows.</summary>
    private async Task<bool> AuthenticateInOwnWindowAsync(string reason)
    {
        using (OwnWindow())
            return await Authenticate(reason);
    }

    internal void Apply(bool enabled, int minutes)
    {
        _isChangingLock = true;
        IsLockEnabled = enabled && CanLock;
        LockDelayIndex = Math.Max(0, Array.IndexOf(LockDelays, minutes));
        _isChangingLock = false;
        if (IsLockEnabled)
            IsLocked = true;
    }

    /// <summary>Turning the lock on first checks that unlocking works, so nobody locks themselves out.</summary>
    partial void OnIsLockEnabledChanged(bool value)
    {
        lockChanged();
        if (_isChangingLock || isLoading())
            return;
        _ = ChangeLockAsync(value);
    }

    private async Task ChangeLockAsync(bool enable)
    {
        if (enable && !await AuthenticateInOwnWindowAsync(Loc.T("turn on the app lock")))
        {
            _isChangingLock = true;
            IsLockEnabled = false;
            _isChangingLock = false;
            return;
        }
        await SaveLockSettingsAsync();
    }

    partial void OnLockDelayIndexChanged(int value)
    {
        if (!_isChangingLock && !isLoading())
            _ = SaveLockSettingsAsync();
    }

    private async Task SaveLockSettingsAsync()
    {
        try
        {
            await repository.SetLockAsync(IsLockEnabled, LockMinutes);
        }
        catch (Exception ex)
        {
            reportError(Loc.F("Could not save: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private void LockNow()
    {
        if (IsLockEnabled)
            IsLocked = true;
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (!IsLocked || _isUnlocking)
            return;
        _isUnlocking = true;
        LockMessage = "";
        try
        {
            if (await AuthenticateInOwnWindowAsync(Loc.T("unlock your household data")))
                IsLocked = false;
            else
                LockMessage = Loc.T("Not unlocked. Try again.");
        }
        finally
        {
            _isUnlocking = false;
        }
    }

    /// <summary>The window lost focus: lock now, or note the time for later.</summary>
    public void OnLeftApp(DateTime now)
    {
        if (_ownWindows > 0)
        {
            // Focus went to Touch ID or a file picker: the user is still here.
            _leftForOwnWindow = true;
            return;
        }

        _leftAt = now;
        if (IsLockEnabled && LockMinutes == 0)
            IsLocked = true;
    }

    /// <summary>The window is active again: lock if the user was away long enough, then ask to unlock.</summary>
    public void OnReturnedToApp(DateTime now)
    {
        if (_leftForOwnWindow)
        {
            // Back from Keystone's own window. Not a return from elsewhere, so no new lock, and no new
            // Touch ID prompt either: after a cancelled prompt the user clicks Unlock when ready.
            _leftForOwnWindow = false;
            return;
        }

        // Switching to another app and back never locks with "When my Mac locks": only the Mac's lock does.
        if (IsLockEnabled && LockMinutes != WhenMacLocks && _leftAt is { } left && now - left >= TimeSpan.FromMinutes(LockMinutes))
            IsLocked = true;
        _leftAt = null;
        returned(); // scans may have arrived and appointments changed while away
        if (IsLocked)
            _ = UnlockAsync();
    }

    /// <summary>The Mac locked its screen or is going to sleep: lock, whatever the lock's timing.</summary>
    public void OnMacLocked()
    {
        if (IsLockEnabled)
            IsLocked = true;
    }

    /// <summary>The Mac was unlocked: ask for Touch ID right away if Keystone is the app in front.</summary>
    public void OnMacUnlocked(bool keystoneInFront)
    {
        if (IsLocked && keystoneInFront)
            _ = UnlockAsync();
    }
}
