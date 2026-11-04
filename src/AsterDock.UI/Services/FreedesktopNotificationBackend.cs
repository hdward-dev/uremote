using System.Collections.Concurrent;
using Tmds.DBus.Protocol;

namespace AsterDock.UI.Services;

/// <summary>
/// org.freedesktop.Notifications on the session bus. Missing bus or service becomes unavailable
/// instead of throwing, including on KDE Plasma 6 / XWayland where the daemon may be absent.
/// </summary>
public sealed class FreedesktopNotificationBackend : ISystemNotificationBackend, IDisposable
{
    private readonly Func<string?> _address;
    private readonly Func<string?>? _iconPath;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<uint, string> _ids = new();
    private DBusConnection? _connection;
    private IDisposable? _actions;
    private IDisposable? _closed;
    private bool _transportFailed;
    private bool _serviceMissing;

    public FreedesktopNotificationBackend(Func<string?>? address = null, Func<string?>? iconPath = null)
    {
        _address = address ?? SessionAddress;
        _iconPath = iconPath;
    }

    public bool IsAvailable
    {
        get
        {
            if (_serviceMissing || _transportFailed) return false;
            lock (_gate) return Connect() is not null;
        }
    }

    public event EventHandler<SystemNotificationActivation>? Activated;

    public bool TryPublish(SystemNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DBusConnection? connection;
        lock (_gate)
        {
            if (_serviceMissing || _transportFailed) return false;
            connection = Connect();
        }

        if (connection is null) return false;
        try
        {
            return WithoutUiContext(() =>
            {
                var call = FreedesktopNotifyMessage.Create(request, _iconPath?.Invoke());
                var sent = connection.CallMethodAsync(CreateNotifyMessage(connection, call), ReadId, null);
                if (!sent.Wait(TimeSpan.FromSeconds(2)) || sent.IsCanceled) return false;
                if (sent.IsFaulted)
                {
                    var error = sent.Exception?.GetBaseException();
                    if (error is DBusErrorReplyException dbus && dbus.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
                        _serviceMissing = true;
                    return false;
                }

                _ids[sent.GetAwaiter().GetResult()] = request.Token;
                return true;
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var error = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
            if (error is DBusErrorReplyException dbus && dbus.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
                _serviceMissing = true;
            return false;
        }
    }

    public void Dispose()
    {
        _actions?.Dispose();
        _closed?.Dispose();
        _connection?.Dispose();
        _connection = null;
    }

    internal static MessageBuffer CreateNotifyMessage(DBusConnection connection, FreedesktopNotifyMessage message)
    {
        // MessageWriter is a ref struct. A copy would leave the caller's buffer empty, so write through one local.
        var writer = connection.GetMessageWriter();
        try
        {
            WriteNotify(ref writer, message);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteNotify(ref MessageWriter writer, FreedesktopNotifyMessage message)
    {
        writer.WriteMethodCallHeader(
            FreedesktopNotifyMessage.Service,
            FreedesktopNotifyMessage.Path,
            FreedesktopNotifyMessage.Interface,
            "Notify",
            FreedesktopNotifyMessage.Signature,
            MessageFlags.None);
        writer.WriteString(message.AppName);
        writer.WriteUInt32(message.ReplacesId);
        writer.WriteString(message.AppIcon);
        writer.WriteString(message.Summary);
        writer.WriteString(message.Body);
        writer.WriteArray(message.Actions.ToArray());
        var hints = new Dictionary<string, VariantValue>
        {
            ["urgency"] = VariantValue.Byte(message.Urgency),
            ["desktop-entry"] = message.DesktopEntry
        };
        if (!string.IsNullOrWhiteSpace(message.ImagePath)) hints["image-path"] = message.ImagePath;
        writer.WriteDictionary(hints);
        writer.WriteInt32(message.ExpireTimeout);
    }

    private DBusConnection? Connect()
    {
        if (_connection is not null) return _connection;
        return WithoutUiContext(ConnectCore);
    }

    private DBusConnection? ConnectCore()
    {
        string? address;
        try { address = _address(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { address = null; }
        if (string.IsNullOrWhiteSpace(address))
        {
            _transportFailed = true;
            return null;
        }

        var connection = new DBusConnection(address);
        try
        {
            var connected = connection.ConnectAsync().AsTask();
            if (!connected.Wait(TimeSpan.FromSeconds(2)))
            {
                connection.Dispose();
                _transportFailed = true;
                return null;
            }

            if (connected.IsFaulted) throw connected.Exception!;
            if (!NotificationServiceReady(connection))
            {
                connection.Dispose();
                return null;
            }

            _actions = connection.WatchSignalAsync(null, FreedesktopNotifyMessage.Path, FreedesktopNotifyMessage.Interface, "ActionInvoked", ReadAction, OnAction, default, false, null).AsTask().GetAwaiter().GetResult();
            _closed = connection.WatchSignalAsync(null, FreedesktopNotifyMessage.Path, FreedesktopNotifyMessage.Interface, "NotificationClosed", ReadClosed, OnClosed, default, false, null).AsTask().GetAwaiter().GetResult();
            _connection = connection;
            return connection;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            connection.Dispose();
            _transportFailed = true;
            return null;
        }
    }

    private static uint ReadId(Message message, object? state) => message.GetBodyReader().ReadUInt32();

    private static (uint Id, string Action) ReadAction(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadUInt32(), reader.ReadString());
    }

    private static (uint Id, uint Reason) ReadClosed(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadUInt32(), reader.ReadUInt32());
    }

    private void OnAction(Notification<(uint Id, string Action)> notification)
    {
        try
        {
            if (!notification.HasValue || !_ids.TryRemove(notification.Value.Id, out var token)) return;
            Activated?.Invoke(this, new SystemNotificationActivation { Token = token });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("系统通知激活未能送达: " + exception.Message);
        }
    }

    private void OnClosed(Notification<(uint Id, uint Reason)> notification)
    {
        if (!notification.HasValue) return;
        // Reason 2 is a user dismiss. Keep the id so a following ActionInvoked can still route.
        if (notification.Value.Reason is 1 or 3 or 4) _ids.TryRemove(notification.Value.Id, out _);
    }

    private bool NotificationServiceReady(DBusConnection connection)
    {
        try
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                "org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                "org.freedesktop.DBus",
                "GetNameOwner",
                "s",
                MessageFlags.None);
            writer.WriteString(FreedesktopNotifyMessage.Service);
            var call = connection.CallMethodAsync(writer.CreateMessage(), ReadOwner, null);
            if (!call.Wait(TimeSpan.FromSeconds(2)))
            {
                _transportFailed = true;
                return false;
            }

            if (!call.IsFaulted && !call.IsCanceled && !string.IsNullOrEmpty(call.GetAwaiter().GetResult()))
                return true;
            var error = call.Exception?.GetBaseException();
            if (error is DBusErrorReplyException dbus && dbus.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
                _serviceMissing = true;
            else
                _transportFailed = true;
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _transportFailed = true;
            return false;
        }
    }

    private static string ReadOwner(Message message, object? state) => message.GetBodyReader().ReadString();

    private static T WithoutUiContext<T>(Func<T> action)
    {
        var previous = SynchronizationContext.Current;
        if (previous is null) return action();
        SynchronizationContext.SetSynchronizationContext(null);
        try { return action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static string? SessionAddress()
    {
        try { return DBusAddress.Session; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }
}
