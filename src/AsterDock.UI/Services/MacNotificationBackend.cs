using System.Runtime.InteropServices;
using AsterDock.Contracts;

namespace AsterDock.UI.Services;

/// <summary>
/// UNUserNotificationCenter via libobjc. Compiles everywhere and runs only on macOS.
/// Unpackaged <c>dotnet</c> processes often cannot present notifications; a
/// <c>dev.hdward.asterdock</c> bundle is what the system associates them with.
/// Authorization is requested once. Until it is granted, publish fails closed so the
/// in-app queue can keep the message.
/// </summary>
public sealed class MacNotificationBackend : ISystemNotificationBackend
{
    private const int Authorized = 2;
    private const int Provisional = 3;
    private const int Ephemeral = 4;
    private const int Denied = 1;
    private int _framework;

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return false;
            if (_framework == 0) _framework = MacNotifications.FrameworkLoaded() ? 1 : -1;
            return _framework > 0;
        }
    }

    public event EventHandler<SystemNotificationActivation>? Activated;

    public bool TryPublish(SystemNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable) return false;
        try
        {
            return MacNotifications.TryPost(request, token => Activated?.Invoke(this, new SystemNotificationActivation { Token = token }));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("macOS 通知未能发送: " + exception.Message);
            return false;
        }
    }
}

internal static class MacNotifications
{
    private const string ObjC = "libobjc";
    private const int BlockHasSignature = 1 << 30;
    private const int Authorized = 2;
    private const int Provisional = 3;
    private const int Ephemeral = 4;
    private const int Denied = 1;
    private static readonly List<Delegate> KeepAlive = [];
    private static readonly object Gate = new();
    private static IntPtr _center;
    private static Action<string>? _activated;
    private static ManualResetEventSlim? _authWait;
    private static int _authStatus = int.MinValue;
    private static ManualResetEventSlim? _postWait;
    private static bool _postOk;

    public static bool FrameworkLoaded()
    {
        if (dlopen("/System/Library/Frameworks/UserNotifications.framework/UserNotifications", 1) == IntPtr.Zero)
            return false;
        return Class("UNUserNotificationCenter") != IntPtr.Zero
            && Class("UNMutableNotificationContent") != IntPtr.Zero
            && Class("UNNotificationRequest") != IntPtr.Zero;
    }

    public static bool TryPost(SystemNotificationRequest request, Action<string> activated)
    {
        lock (Gate)
        {
            _activated = activated;
            if (!EnsureCenter()) return false;
            var status = QueryAuthorization(TimeSpan.FromMilliseconds(800));
            if (status is not (Authorized or Provisional or Ephemeral))
            {
                if (status == Denied || status < 0) return false;
                if (!RequestAuthorization(TimeSpan.FromMilliseconds(1500))) return false;
            }

            return Post(request, TimeSpan.FromSeconds(2));
        }
    }

    private static bool EnsureCenter()
    {
        if (_center != IntPtr.Zero) return true;
        var center = Msg(Class("UNUserNotificationCenter"), Sel("currentNotificationCenter"));
        if (center == IntPtr.Zero) return false;
        var pair = objc_allocateClassPair(Class("NSObject"), "AsterDockNotificationDelegate", IntPtr.Zero);
        if (pair == IntPtr.Zero) return false;
        var imp = new ResponseImp(OnResponse);
        KeepAlive.Add(imp);
        if (class_addMethod(pair, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"), Marshal.GetFunctionPointerForDelegate(imp), "v@:@@@?") == 0)
            return false;
        objc_registerClassPair(pair);
        var del = Msg(Msg(pair, Sel("alloc")), Sel("init"));
        Msg(center, Sel("setDelegate:"), del);
        _center = center;
        return true;
    }

    private static int QueryAuthorization(TimeSpan timeout)
    {
        _authStatus = int.MinValue;
        _authWait = new ManualResetEventSlim(false);
        var block = Block(new SettingsImp(OnSettings), "v@?@");
        Msg(_center, Sel("getNotificationSettingsWithCompletionHandler:"), block);
        if (!_authWait.Wait(timeout)) return -1;
        return _authStatus;
    }

    private static bool RequestAuthorization(TimeSpan timeout)
    {
        _authStatus = int.MinValue;
        _authWait = new ManualResetEventSlim(false);
        var block = Block(new AuthImp(OnAuth), "v@?c@");
        Msg(_center, Sel("requestAuthorizationWithOptions:completionHandler:"), (IntPtr)6, block);
        if (!_authWait.Wait(timeout)) return false;
        return _authStatus is Authorized or Provisional or Ephemeral;
    }

    private static bool Post(SystemNotificationRequest request, TimeSpan timeout)
    {
        var pool = Msg(Msg(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try
        {
            var content = Msg(Msg(Class("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
            var title = string.IsNullOrWhiteSpace(request.Title) ? request.Body : request.Title;
            var body = string.IsNullOrWhiteSpace(request.Title) ? "" : request.Body;
            Msg(content, Sel("setTitle:"), Ns(title));
            Msg(content, Sel("setBody:"), Ns(body));
            if (request.Kind != FeedbackKind.Info)
                Msg(content, Sel("setSound:"), Msg(Class("UNNotificationSound"), Sel("defaultSound")));
            var posted = Msg(Class("UNNotificationRequest"), Sel("requestWithIdentifier:content:trigger:"), Ns(request.Token), content, IntPtr.Zero);
            _postOk = false;
            _postWait = new ManualResetEventSlim(false);
            var block = Block(new ErrorImp(OnPosted), "v@?@");
            Msg(_center, Sel("addNotificationRequest:withCompletionHandler:"), posted, block);
            return _postWait.Wait(timeout) && _postOk;
        }
        finally
        {
            if (pool != IntPtr.Zero) Msg(pool, Sel("drain"));
        }
    }

    private static void OnSettings(IntPtr block, IntPtr settings)
    {
        _authStatus = settings == IntPtr.Zero ? Denied : (int)MsgNInt(settings, Sel("authorizationStatus"));
        _authWait?.Set();
    }

    private static void OnAuth(IntPtr block, byte granted, IntPtr error)
    {
        _authStatus = granted != 0 && error == IntPtr.Zero ? Authorized : Denied;
        _authWait?.Set();
    }

    private static void OnPosted(IntPtr block, IntPtr error)
    {
        _postOk = error == IntPtr.Zero;
        _postWait?.Set();
    }

    private static void OnResponse(IntPtr self, IntPtr cmd, IntPtr center, IntPtr response, IntPtr completion)
    {
        try
        {
            var notification = Msg(response, Sel("notification"));
            var posted = Msg(notification, Sel("request"));
            var token = ReadNs(Msg(posted, Sel("identifier")));
            if (!string.IsNullOrEmpty(token)) _activated?.Invoke(token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("macOS 通知激活未能送达: " + exception.Message);
        }

        InvokeBlock(completion);
    }

    private static void InvokeBlock(IntPtr block)
    {
        if (block == IntPtr.Zero) return;
        var invoke = Marshal.ReadIntPtr(block, IntPtr.Size * 2);
        if (invoke == IntPtr.Zero) return;
        Marshal.GetDelegateForFunctionPointer<VoidImp>(invoke)(block);
    }

    private static IntPtr Block(Delegate invoke, string signature)
    {
        KeepAlive.Add(invoke);
        var descriptor = Marshal.AllocHGlobal(IntPtr.Size * 3);
        Marshal.WriteIntPtr(descriptor, 0, IntPtr.Zero);
        Marshal.WriteIntPtr(descriptor, IntPtr.Size, (IntPtr)(IntPtr.Size * 4));
        Marshal.WriteIntPtr(descriptor, IntPtr.Size * 2, Marshal.StringToHGlobalAnsi(signature));
        var block = Marshal.AllocHGlobal(IntPtr.Size * 4);
        Marshal.WriteIntPtr(block, 0, dlsym(new IntPtr(-2), "_NSConcreteGlobalBlock"));
        Marshal.WriteInt32(block, IntPtr.Size, BlockHasSignature);
        Marshal.WriteInt32(block, IntPtr.Size + 4, 0);
        Marshal.WriteIntPtr(block, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(invoke));
        Marshal.WriteIntPtr(block, IntPtr.Size * 3, descriptor);
        return block;
    }

    private static IntPtr Ns(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        var native = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, native, bytes.Length);
        var result = Msg(Class("NSString"), Sel("stringWithUTF8String:"), native);
        Marshal.FreeHGlobal(native);
        return result;
    }

    private static string? ReadNs(IntPtr value)
    {
        if (value == IntPtr.Zero) return null;
        var utf8 = Msg(value, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResponseImp(IntPtr self, IntPtr cmd, IntPtr center, IntPtr response, IntPtr completion);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SettingsImp(IntPtr block, IntPtr settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AuthImp(IntPtr block, byte granted, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ErrorImp(IntPtr block, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidImp(IntPtr block);

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint MsgNInt(IntPtr receiver, IntPtr selector);
    [DllImport("libSystem")] private static extern IntPtr dlopen(string path, int mode);
    [DllImport("libSystem")] private static extern IntPtr dlsym(IntPtr handle, string symbol);
}
