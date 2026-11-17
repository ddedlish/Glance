using Tmds.DBus;

// D-Bus contracts used by the Linux build. Method names map to D-Bus by dropping "Async";
// WatchXxxAsync maps to signal Xxx.

namespace Glance.DBus;

// ───────────── UPower (system bus) ─────────────

[DBusInterface("org.freedesktop.UPower")]
public interface IUPower : IDBusObject
{
    Task<ObjectPath[]> EnumerateDevicesAsync();
    Task<IDisposable> WatchDeviceAddedAsync(Action<ObjectPath> handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchDeviceRemovedAsync(Action<ObjectPath> handler, Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.UPower.Device")]
public interface IUPowerDevice : IDBusObject
{
    Task<IDictionary<string, object>> GetAllAsync();
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

// ───────────── Desktop notifications (session bus) ─────────────

[DBusInterface("org.freedesktop.Notifications")]
public interface INotifications : IDBusObject
{
    Task<uint> NotifyAsync(string appName, uint replacesId, string appIcon, string summary, string body,
        string[] actions, IDictionary<string, object> hints, int expireTimeout);
    Task CloseNotificationAsync(uint id);
    Task<IDisposable> WatchActionInvokedAsync(Action<(uint id, string actionKey)> handler, Action<Exception>? onError = null);
}

// ───────────── StatusNotifierItem (tray, session bus) ─────────────

[DBusInterface("org.kde.StatusNotifierWatcher")]
public interface IStatusNotifierWatcher : IDBusObject
{
    Task RegisterStatusNotifierItemAsync(string service);
}

[DBusInterface("org.kde.StatusNotifierItem")]
public interface IStatusNotifierItem : IDBusObject
{
    Task ContextMenuAsync(int x, int y);
    Task ActivateAsync(int x, int y);
    Task SecondaryActivateAsync(int x, int y);
    Task ScrollAsync(int delta, string orientation);
    Task<IDisposable> WatchNewTitleAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewIconAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewAttentionIconAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewOverlayIconAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewToolTipAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewStatusAsync(Action<string> handler, Action<Exception>? onError = null);
    Task<object> GetAsync(string prop);
    Task<StatusNotifierItemProperties> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

[Dictionary]
public class StatusNotifierItemProperties
{
    public string Category = "Hardware";
    public string Id = "glance";
    public string Title = "Glance";
    public string Status = "Active";
    public int WindowId;
    public string IconThemePath = "";
    public ObjectPath Menu = new("/MenuBar");
    public bool ItemIsMenu;
    public string IconName = "";
    public (int, int, byte[])[] IconPixmap = Array.Empty<(int, int, byte[])>();
    public string OverlayIconName = "";
    public (int, int, byte[])[] OverlayIconPixmap = Array.Empty<(int, int, byte[])>();
    public string AttentionIconName = "";
    public (int, int, byte[])[] AttentionIconPixmap = Array.Empty<(int, int, byte[])>();
    public string AttentionMovieName = "";
    public (string, (int, int, byte[])[], string, string) ToolTip = ("", Array.Empty<(int, int, byte[])>(), "", "");
}

// ───────────── com.canonical.dbusmenu (tray menu) ─────────────

[DBusInterface("com.canonical.dbusmenu")]
public interface IDBusMenu : IDBusObject
{
    Task<(uint revision, (int, IDictionary<string, object>, object[]) layout)> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames);
    Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames);
    Task<object> GetPropertyAsync(int id, string name);
    Task EventAsync(int id, string eventId, object data, uint timestamp);
    Task<int[]> EventGroupAsync((int, string, object, uint)[] events);
    Task<bool> AboutToShowAsync(int id);
    Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] ids);
    Task<IDisposable> WatchItemsPropertiesUpdatedAsync(Action<((int, IDictionary<string, object>)[] updatedProps, (int, string[])[] removedProps)> handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint revision, int parent)> handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchItemActivationRequestedAsync(Action<(int id, uint timestamp)> handler, Action<Exception>? onError = null);
    Task<object> GetAsync(string prop);
    Task<DBusMenuProperties> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

[Dictionary]
public class DBusMenuProperties
{
    public uint Version = 3;
    public string TextDirection = "ltr";
    public string Status = "normal";
    public string[] IconThemePath = Array.Empty<string>();
}
