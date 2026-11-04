using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AsterDock.UI.Services;

/// <summary>
/// Unpackaged Windows toast via combase, without a Windows TFM or CsWinRT projection.
/// The file compiles on Linux and only calls COM when <see cref="OperatingSystem.IsWindows"/> is true.
/// AUMID and the in-process activator CLSID are stable so a click can find this process.
/// </summary>
public sealed class WindowsToastBackend : ISystemNotificationBackend, IDisposable
{
    public const string ActivatorClsid = "A7C4E1B2-5D83-4F06-9E21-6B8D0C4F2A19";

    private readonly Func<string?>? _iconPath;
    private bool _disabled;

    public WindowsToastBackend(Func<string?>? iconPath = null) => _iconPath = iconPath;

    public bool IsAvailable => OperatingSystem.IsWindows() && !_disabled;

    public event EventHandler<SystemNotificationActivation>? Activated;

    public bool TryPublish(SystemNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows() || _disabled) return false;
        try
        {
            if (!WindowsToastNative.TryShow(request, _iconPath?.Invoke(), Raise)) return Fail();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Windows 通知未能发送: " + exception.Message);
            return Fail();
        }
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) WindowsToastNative.DisposeRegistration();
    }

    internal static string? ReadActivationToken(string? args)
    {
        if (string.IsNullOrEmpty(args)) return null;
        const string key = "token=";
        var index = args.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return null;
        var token = args[(index + key.Length)..];
        var end = token.IndexOf('&');
        if (end >= 0) token = token[..end];
        token = token.Trim();
        return token.Length == 0 ? null : token;
    }

    private bool Fail()
    {
        _disabled = true;
        return false;
    }

    private void Raise(string token) => Activated?.Invoke(this, new SystemNotificationActivation { Token = token });
}

[SupportedOSPlatform("windows")]
internal static class WindowsToastNative
{
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private static readonly object Gate = new();
    private static readonly Guid ActivatorClass = new(WindowsToastBackend.ActivatorClsid);
    private static Action<string>? _raise;
    private static ActivatorClassFactory? _factory;
    private static bool _comReady;
    private static bool _registered;
    private static int _cookie;

    public static bool TryShow(SystemNotificationRequest request, string? iconPath, Action<string> raise)
    {
        lock (Gate)
        {
            _raise = raise;
            if (!EnsureCom() || !EnsureRegistered(iconPath)) return false;
            return Show(request, iconPath);
        }
    }

    public static void DisposeRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (_cookie == 0) return;
            try { CoRevokeClassObject(_cookie); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            _cookie = 0;
            _factory = null;
        }
    }

    internal static void HandleActivation(string? args)
    {
        var token = WindowsToastBackend.ReadActivationToken(args);
        if (token is null) return;
        Action<string>? raise;
        lock (Gate) raise = _raise;
        raise?.Invoke(token);
    }

    private static bool EnsureCom()
    {
        if (_comReady) return true;
        var hr = CoInitializeEx(IntPtr.Zero, 2);
        if (hr < 0 && hr != RpcEChangedMode) return false;
        var ro = RoInitialize(hr == RpcEChangedMode ? 1 : 0);
        if (ro < 0 && ro != RpcEChangedMode) return false;
        _comReady = true;
        return true;
    }

    private static bool EnsureRegistered(string? iconPath)
    {
        if (_registered) return true;
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(SystemNotificationIdentity.WindowsAppUserModelId);
            using var appKey = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Classes\AppUserModelId\{SystemNotificationIdentity.WindowsAppUserModelId}");
            appKey?.SetValue("DisplayName", SystemNotificationIdentity.AppName);
            appKey?.SetValue("CustomActivator", "{" + ActivatorBraces() + "}");
            if (!string.IsNullOrWhiteSpace(iconPath)) appKey?.SetValue("IconUri", iconPath);
            var process = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(process))
            {
                using var server = Registry.CurrentUser.CreateSubKey($@"SOFTWARE\Classes\CLSID\{{{ActivatorBraces()}}}\LocalServer32");
                server?.SetValue("", process);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Windows 通知标识未能写入: " + exception.Message);
        }

        try
        {
            _factory = new ActivatorClassFactory();
            var clsid = ActivatorClass;
            var hr = CoRegisterClassObject(ref clsid, _factory, 4, 1, out _cookie);
            if (hr < 0) _cookie = 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Windows 通知激活未能注册: " + exception.Message);
            return false;
        }

        _registered = _cookie != 0;
        return _registered;
    }

    private static string ActivatorBraces() => WindowsToastBackend.ActivatorClsid;

    private static bool Show(SystemNotificationRequest request, string? iconPath)
    {
        IntPtr managerName = default, managerPtr = default, aumid = default, notifierPtr = default;
        IntPtr documentName = default, documentPtr = default, documentIo = default, xml = default;
        IntPtr factoryName = default, factoryPtr = default, toastPtr = default;
        try
        {
            managerName = HString("Windows.UI.Notifications.ToastNotificationManager");
            var managerId = typeof(IToastNotificationManagerStatics).GUID;
            if (RoGetActivationFactory(managerName, ref managerId, out managerPtr) < 0) return false;
            var manager = (IToastNotificationManagerStatics)Marshal.GetObjectForIUnknown(managerPtr);
            aumid = HString(SystemNotificationIdentity.WindowsAppUserModelId);
            if (manager.CreateToastNotifierWithId(aumid, out notifierPtr) < 0) return false;
            var notifier = (IToastNotifier)Marshal.GetObjectForIUnknown(notifierPtr);

            documentName = HString("Windows.Data.Xml.Dom.XmlDocument");
            if (RoActivateInstance(documentName, out documentPtr) < 0) return false;
            var ioId = typeof(IXmlDocumentIO).GUID;
            if (Marshal.QueryInterface(documentPtr, in ioId, out documentIo) < 0) return false;
            var document = (IXmlDocumentIO)Marshal.GetObjectForIUnknown(documentIo);
            xml = HString(WindowsToastContent.Create(request, iconPath));
            if (document.LoadXml(xml) < 0) return false;

            factoryName = HString("Windows.UI.Notifications.ToastNotification");
            var factoryId = typeof(IToastNotificationFactory).GUID;
            if (RoGetActivationFactory(factoryName, ref factoryId, out factoryPtr) < 0) return false;
            var factory = (IToastNotificationFactory)Marshal.GetObjectForIUnknown(factoryPtr);
            if (factory.CreateToastNotification(documentPtr, out toastPtr) < 0) return false;
            return notifier.Show(toastPtr) >= 0;
        }
        finally
        {
            Delete(managerName);
            Delete(aumid);
            Delete(documentName);
            Delete(xml);
            Delete(factoryName);
            Release(toastPtr);
            Release(factoryPtr);
            Release(documentIo);
            Release(documentPtr);
            Release(notifierPtr);
            Release(managerPtr);
        }
    }

    private static IntPtr HString(string value)
    {
        var hr = WindowsCreateString(value, (uint)value.Length, out var handle);
        if (hr < 0) throw new InvalidOperationException("WindowsCreateString " + hr);
        return handle;
    }

    private static void Delete(IntPtr handle)
    {
        if (handle != IntPtr.Zero) WindowsDeleteString(handle);
    }

    private static void Release(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero) Marshal.Release(pointer);
    }

    [DllImport("combase")] private static extern int RoInitialize(int initType);
    [DllImport("combase")] private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase")] private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);
    [DllImport("combase")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, uint length, out IntPtr hstring);
    [DllImport("combase")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("ole32")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32")] private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.Interface)] ActivatorClassFactory factory, uint context, uint flags, out int cookie);
    [DllImport("ole32")] private static extern int CoRevokeClassObject(int cookie);
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [ComVisible(true)]
    [SupportedOSPlatform("windows")]
    private sealed class ActivatorClassFactory : WindowsToastClassFactory
    {
        public void CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero) Marshal.ThrowExceptionForHR(unchecked((int)0x80040110));
            var unknown = Marshal.GetIUnknownForObject(new WindowsNotificationActivator());
            try
            {
                var hr = Marshal.QueryInterface(unknown, in riid, out instance);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public void LockServer(bool fLock) { }
    }

    [ComImport]
    [Guid("50ac103f-d235-4598-bbef-98fe4d1a3ad4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationManagerStatics
    {
        void GetIids(out int iidCount, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        [PreserveSig] int CreateToastNotifier(out IntPtr notifier);
        [PreserveSig] int CreateToastNotifierWithId(IntPtr applicationId, out IntPtr notifier);
        [PreserveSig] int GetTemplateContent(int type, out IntPtr content);
    }

    [ComImport]
    [Guid("04124b20-82c6-4229-b109-fd9ed4662b53")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationFactory
    {
        void GetIids(out int iidCount, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        [PreserveSig] int CreateToastNotification(IntPtr content, out IntPtr notification);
    }

    [ComImport]
    [Guid("75927b93-03f3-41ec-91d3-6e5bac1b38e7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotifier
    {
        void GetIids(out int iidCount, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        [PreserveSig] int Show(IntPtr notification);
        [PreserveSig] int Hide(IntPtr notification);
        [PreserveSig] int get_Setting(out int value);
    }

    [ComImport]
    [Guid("6cd0e74e-ee65-4489-9ebf-ca43e87ba637")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocumentIO
    {
        void GetIids(out int iidCount, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        [PreserveSig] int LoadXml(IntPtr xml);
    }
}

[ComVisible(true)]
[Guid("A7C4E1B2-5D83-4F06-9E21-6B8D0C4F2A19")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class WindowsNotificationActivator : WindowsToastNativeCallback
{
    public void Activate(string appUserModelId, string invokedArgs, IntPtr data, uint count)
    {
        if (OperatingSystem.IsWindows()) WindowsToastNative.HandleActivation(invokedArgs);
    }
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface WindowsToastClassFactory
{
    void CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance);
    void LockServer(bool fLock);
}

[ComImport]
[Guid("53e31837-6600-4a81-9395-75cffe746f94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface WindowsToastNativeCallback
{
    void Activate(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        IntPtr data,
        uint count);
}
