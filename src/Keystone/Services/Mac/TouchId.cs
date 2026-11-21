using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Keystone.Services.Mac;

/// <summary>Asks the user to prove it's them with Touch ID, or their Mac password when there is no Touch ID.</summary>
public static unsafe class TouchId
{
    /// <summary>LAPolicyDeviceOwnerAuthentication: biometrics, falling back to the account password.</summary>
    private const long DeviceOwnerAuthentication = 2;

    private static readonly object Gate = new();
    private static TaskCompletionSource<bool>? _pending;
    private static IntPtr _replyBlock;
    private static IntPtr _context;

    /// <summary>Whether this Mac can check the user at all.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsMacOS() || !ObjC.LoadFramework("LocalAuthentication"))
                return false;
            try
            {
                var context = ObjC.New("LAContext");
                return ObjC.SendReturnBool(context, ObjC.Sel("canEvaluatePolicy:error:"), DeviceOwnerAuthentication, IntPtr.Zero) != 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Shows the system prompt "Keystone is trying to <paramref name="reason"/>" and returns whether the user
    /// passed. Only one prompt runs at a time.
    /// </summary>
    public static Task<bool> AuthenticateAsync(string reason)
    {
        if (!IsAvailable)
            return Task.FromResult(false);

        lock (Gate)
        {
            if (_pending is { Task.IsCompleted: false } running)
                return running.Task;

            _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_replyBlock == IntPtr.Zero)
                _replyBlock = ObjC.GlobalBlock((IntPtr)(delegate* unmanaged<IntPtr, byte, IntPtr, void>)&Reply, "v@?B@");

            // A fresh context each time, so an earlier success isn't reused; kept alive until the reply.
            _context = ObjC.New("LAContext");
            ObjC.SendLong(_context, ObjC.Sel("evaluatePolicy:localizedReason:reply:"), DeviceOwnerAuthentication,
                ObjC.NSString(reason), _replyBlock);
            return _pending.Task;
        }
    }

    [UnmanagedCallersOnly]
    private static void Reply(IntPtr block, byte success, IntPtr error) => _pending?.TrySetResult(success != 0);
}
