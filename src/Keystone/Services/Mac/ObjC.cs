using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Keystone.Services.Mac;

/// <summary>
/// Minimal access to macOS frameworks through the Objective-C runtime, for Touch ID and notifications.
/// Every message signature used is declared separately, as arm64 requires.
/// </summary>
internal static unsafe class ObjC
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string SystemLib = "/usr/lib/libSystem.dylib";

    [DllImport(Objc)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Objc)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Objc)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Objc)] public static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Objc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);
    [DllImport(Objc)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Objc)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, byte b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendLong(IntPtr receiver, IntPtr selector, long a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendLong(IntPtr receiver, IntPtr selector, long a, IntPtr b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendLong(IntPtr receiver, IntPtr selector, long a, IntPtr b, IntPtr c);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern long SendReturnLong(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern byte SendReturnBool(IntPtr receiver, IntPtr selector, long a, IntPtr b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern byte SendReturnBool(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern byte SendReturnBool(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendWithLong(IntPtr receiver, IntPtr selector, long a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendWithLong(IntPtr receiver, IntPtr selector, long a, IntPtr b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern IntPtr SendDouble(IntPtr receiver, IntPtr selector, double a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern double SendReturnDouble(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendBool(IntPtr receiver, IntPtr selector, byte a);
    /// <summary>For saveEvent:span:commit:error: and removeEvent:span:commit:error:.</summary>
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    public static extern byte SendSpanCommit(IntPtr receiver, IntPtr selector, IntPtr item, long span, byte commit, out IntPtr error);
    /// <summary>For saveCalendar:commit:error:.</summary>
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    public static extern byte SendCommit(IntPtr receiver, IntPtr selector, IntPtr item, byte commit, out IntPtr error);
    /// <summary>For methods taking one object and an error out-parameter, e.g. performRequests:error:.</summary>
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    public static extern byte SendWithError(IntPtr receiver, IntPtr selector, IntPtr item, out IntPtr error);
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(SystemLib)] private static extern IntPtr dlopen(string path, int mode);
    [DllImport(SystemLib)] private static extern IntPtr dlsym(IntPtr handle, string symbol);

    private static readonly ConcurrentDictionary<string, IntPtr> Selectors = new();
    private static readonly ConcurrentDictionary<string, IntPtr> Classes = new();

    public static IntPtr Sel(string name) => Selectors.GetOrAdd(name, sel_registerName);

    public static IntPtr Class(string name) => Classes.GetOrAdd(name, objc_getClass);

    /// <summary>Loads a system framework so its classes can be found.</summary>
    public static bool LoadFramework(string name) =>
        dlopen($"/System/Library/Frameworks/{name}.framework/{name}", 2 /* RTLD_NOW */) != IntPtr.Zero;

    public static IntPtr NSString(string text) => SendString(Class("NSString"), Sel("stringWithUTF8String:"), text);

    public static string? ToManagedString(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    public static IntPtr New(string className) => Send(Send(Class(className), Sel("alloc")), Sel("init"));

    /// <summary>
    /// The app's bundle identifier; null when not running from an app bundle (during development and in tests).
    /// A process outside a .app can still report an identifier (one embedded in its executable, as on build
    /// servers); macOS frameworks that need a real bundle then throw and end the process, so that counts as none.
    /// </summary>
    public static string? BundleIdentifier()
    {
        var bundle = Send(Class("NSBundle"), Sel("mainBundle"));
        if (bundle == IntPtr.Zero)
            return null;
        var path = ToManagedString(Send(bundle, Sel("bundlePath")));
        if (path is null || !path.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return null;
        return ToManagedString(Send(bundle, Sel("bundleIdentifier")));
    }

    // ── Blocks ────────────────────────────────────────────────────────────────

    private const int BlockIsGlobal = 1 << 28;
    private const int BlockHasSignature = 1 << 30;

    /// <summary>
    /// A block that calls <paramref name="invoke"/>, which must be a static unmanaged function taking the block
    /// pointer first. Made as a global block, which the runtime never copies or frees, so it can be reused.
    /// </summary>
    public static IntPtr GlobalBlock(IntPtr invoke, string signature)
    {
        var isa = dlsym(new IntPtr(-2) /* RTLD_DEFAULT */, "_NSConcreteGlobalBlock");
        var descriptor = (long*)Marshal.AllocHGlobal(3 * sizeof(long));
        descriptor[0] = 0;
        descriptor[1] = 32; // size of the block below
        descriptor[2] = (long)Marshal.StringToHGlobalAnsi(signature);

        var block = (byte*)Marshal.AllocHGlobal(32);
        *(IntPtr*)block = isa;
        *(int*)(block + 8) = BlockIsGlobal | BlockHasSignature;
        *(int*)(block + 12) = 0;
        *(IntPtr*)(block + 16) = invoke;
        *(IntPtr*)(block + 24) = (IntPtr)descriptor;
        return (IntPtr)block;
    }

    /// <summary>Calls a block that takes one integer argument, e.g. a completion handler given to us.</summary>
    public static void CallBlock(IntPtr block, long argument)
    {
        if (block == IntPtr.Zero)
            return;
        var invoke = (delegate* unmanaged<IntPtr, long, void>)Marshal.ReadIntPtr(block, 16);
        invoke(block, argument);
    }
}
