using System;
using System.Runtime.InteropServices;

namespace Keystone.Services.Mac;

/// <summary>
/// Tells when the Mac locks its screen (lock screen, screen saver with password, fast user switching) or goes to
/// sleep, and when it is unlocked again, so the app lock can follow the Mac's own lock instead of every switch to
/// another app. macOS posts these as notifications: the screen lock ones to the distributed notification centre,
/// sleep to the workspace's. Both are delivered on the main queue, which is the UI thread.
/// </summary>
public static unsafe class ScreenLock
{
    public static event Action? Locked;

    public static event Action? Unlocked;

    private static bool _started;
    private static IntPtr _lockedBlock, _unlockedBlock;

    public static void Start()
    {
        if (_started || !OperatingSystem.IsMacOS())
            return;
        _started = true;
        try
        {
            _lockedBlock = ObjC.GlobalBlock((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnLocked, "v@?@");
            _unlockedBlock = ObjC.GlobalBlock((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnUnlocked, "v@?@");
            var mainQueue = ObjC.Send(ObjC.Class("NSOperationQueue"), ObjC.Sel("mainQueue"));

            var distributed = ObjC.Send(ObjC.Class("NSDistributedNotificationCenter"), ObjC.Sel("defaultCenter"));
            Observe(distributed, "com.apple.screenIsLocked", mainQueue, _lockedBlock);
            Observe(distributed, "com.apple.screenIsUnlocked", mainQueue, _unlockedBlock);

            var workspace = ObjC.Send(ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace")), ObjC.Sel("notificationCenter"));
            Observe(workspace, "NSWorkspaceWillSleepNotification", mainQueue, _lockedBlock);
            Observe(workspace, "NSWorkspaceSessionDidResignActiveNotification", mainQueue, _lockedBlock); // fast user switching
        }
        catch (Exception)
        {
            // Without these the lock still works with its time-based choices.
        }
    }

    // The centre keeps the observer it returns until the app ends, which is how long Keystone listens.
    private static void Observe(IntPtr center, string name, IntPtr queue, IntPtr block) =>
        ObjC.Send(center, ObjC.Sel("addObserverForName:object:queue:usingBlock:"), ObjC.NSString(name), IntPtr.Zero, queue, block);

    [UnmanagedCallersOnly]
    private static void OnLocked(IntPtr block, IntPtr notification) => Raise(Locked);

    [UnmanagedCallersOnly]
    private static void OnUnlocked(IntPtr block, IntPtr notification) => Raise(Unlocked);

    /// <summary>An exception must not get back into macOS: that would end the app.</summary>
    private static void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception)
        {
        }
    }
}
