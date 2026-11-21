using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Keystone.Services.Mac;

public enum NotificationPermission
{
    /// <summary>Not running as a Mac app bundle, so macOS can't deliver notifications (e.g. during development).</summary>
    Unsupported,
    NotAsked,
    Denied,
    Allowed,
}

/// <summary>
/// macOS notifications through UserNotifications. Reminders are handed to macOS with their time, so they
/// arrive even when Keystone isn't running.
/// </summary>
public static class MacNotifications
{
    /// <summary>macOS keeps at most 64 pending notifications per app.</summary>
    public const int MaxPending = 60;

    private static readonly object Gate = new();
    private static TaskCompletionSource<bool>? _authorization;
    private static TaskCompletionSource<long>? _settings;
    private static IntPtr _authorizationBlock;
    private static IntPtr _settingsBlock;
    private static IntPtr _delegate;
    private static bool? _supported;

    private static IntPtr Center => ObjC.Send(ObjC.Class("UNUserNotificationCenter"), ObjC.Sel("currentNotificationCenter"));

    /// <summary>
    /// Notifications need a real app bundle: without one, macOS raises an exception that would end the app.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            _supported ??= OperatingSystem.IsMacOS() && ObjC.BundleIdentifier() is { Length: > 0 } &&
                           ObjC.LoadFramework("UserNotifications");
            return _supported.Value;
        }
    }

    /// <summary>Lets reminders show as banners while Keystone is the active app, too.</summary>
    public static void ShowWhileActive()
    {
        if (!IsSupported || _delegate != IntPtr.Zero)
            return;

        var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "KeystoneNotificationPresenter", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
            WillPresentPointer(), "v@:@@@?");
        ObjC.objc_registerClassPair(cls);
        _delegate = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(Center, ObjC.Sel("setDelegate:"), _delegate);
    }

    public static async Task<NotificationPermission> GetPermissionAsync()
    {
        if (!IsSupported)
            return NotificationPermission.Unsupported;

        TaskCompletionSource<long> pending;
        lock (Gate)
        {
            pending = _settings = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_settingsBlock == IntPtr.Zero)
                _settingsBlock = ObjC.GlobalBlock(SettingsReceivedPointer(), "v@?@");
            ObjC.Send(Center, ObjC.Sel("getNotificationSettingsWithCompletionHandler:"), _settingsBlock);
        }

        return await pending.Task switch
        {
            0 => NotificationPermission.NotAsked,
            1 => NotificationPermission.Denied,
            _ => NotificationPermission.Allowed, // authorized, provisional or ephemeral
        };
    }

    /// <summary>Asks macOS for permission; the system shows its prompt only the first time.</summary>
    public static Task<bool> RequestPermissionAsync()
    {
        if (!IsSupported)
            return Task.FromResult(false);

        lock (Gate)
        {
            if (_authorization is { Task.IsCompleted: false } running)
                return running.Task;
            _authorization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_authorizationBlock == IntPtr.Zero)
                _authorizationBlock = ObjC.GlobalBlock(AuthorizedPointer(), "v@?B@");

            const long alertSoundBadge = 4 | 2 | 1;
            ObjC.SendLong(Center, ObjC.Sel("requestAuthorizationWithOptions:completionHandler:"), alertSoundBadge, _authorizationBlock);
            return _authorization.Task;
        }
    }

    /// <summary>Replaces every reminder Keystone has scheduled with these.</summary>
    public static void Replace(IEnumerable<Reminder> reminders)
    {
        if (!IsSupported)
            return;

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var center = Center;
            ObjC.Send(center, ObjC.Sel("removeAllPendingNotificationRequests"));
            foreach (var reminder in reminders.Take(MaxPending))
                Add(center, reminder.Id, reminder.Title, reminder.Body, CalendarTrigger(reminder.At));
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Shows a notification right away, e.g. to try reminders out.</summary>
    public static void ShowNow(string title, string body)
    {
        if (!IsSupported)
            return;

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            Add(Center, $"keystone-now-{Guid.NewGuid():N}", title, body, trigger: IntPtr.Zero);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private static void Add(IntPtr center, string id, string title, string body, IntPtr trigger)
    {
        var content = ObjC.New("UNMutableNotificationContent");
        ObjC.Send(content, ObjC.Sel("setTitle:"), ObjC.NSString(title));
        ObjC.Send(content, ObjC.Sel("setBody:"), ObjC.NSString(body));
        ObjC.Send(content, ObjC.Sel("setSound:"), ObjC.Send(ObjC.Class("UNNotificationSound"), ObjC.Sel("defaultSound")));

        var request = ObjC.Send(ObjC.Class("UNNotificationRequest"), ObjC.Sel("requestWithIdentifier:content:trigger:"),
            ObjC.NSString(id), content, trigger);
        ObjC.Send(center, ObjC.Sel("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
    }

    private static IntPtr CalendarTrigger(DateTime at)
    {
        var components = ObjC.New("NSDateComponents");
        ObjC.SendLong(components, ObjC.Sel("setYear:"), at.Year);
        ObjC.SendLong(components, ObjC.Sel("setMonth:"), at.Month);
        ObjC.SendLong(components, ObjC.Sel("setDay:"), at.Day);
        ObjC.SendLong(components, ObjC.Sel("setHour:"), at.Hour);
        ObjC.SendLong(components, ObjC.Sel("setMinute:"), at.Minute);
        return ObjC.Send(ObjC.Class("UNCalendarNotificationTrigger"), ObjC.Sel("triggerWithDateMatchingComponents:repeats:"),
            components, (byte)0);
    }

    /// <summary>Opens the Notifications pane of System Settings.</summary>
    public static void OpenSystemSettings() =>
        System.Diagnostics.Process.Start("open", "x-apple.systempreferences:com.apple.Notifications-Settings.extension");

    private static unsafe IntPtr AuthorizedPointer() => (IntPtr)(delegate* unmanaged<IntPtr, byte, IntPtr, void>)&Authorized;

    private static unsafe IntPtr SettingsReceivedPointer() => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&SettingsReceived;

    private static unsafe IntPtr WillPresentPointer() =>
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&WillPresent;

    [UnmanagedCallersOnly]
    private static void Authorized(IntPtr block, byte granted, IntPtr error) => _authorization?.TrySetResult(granted != 0);

    [UnmanagedCallersOnly]
    private static void SettingsReceived(IntPtr block, IntPtr settings) =>
        _settings?.TrySetResult(settings == IntPtr.Zero ? 0 : ObjC.SendReturnLong(settings, ObjC.Sel("authorizationStatus")));

    [UnmanagedCallersOnly]
    private static void WillPresent(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification, IntPtr completion)
    {
        const long listBannerSound = 8 | 16 | 2;
        ObjC.CallBlock(completion, listBannerSound);
    }
}

/// <summary>A notification to show at a given time. <see cref="Id"/> identifies it, so it is never shown twice.</summary>
public sealed record Reminder(string Id, DateTime At, string Title, string Body);
