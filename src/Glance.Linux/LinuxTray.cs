using Glance.DBus;
using Tmds.DBus;

namespace Glance;

/// <summary>One StatusNotifierItem (KDE, GNOME with AppIndicator, XFCE, Cinnamon, MATE, LXQt, Budgie).</summary>
internal sealed class SniItem : IStatusNotifierItem
{
    readonly StatusNotifierItemProperties _p = new();
    event Action? NewIcon, NewToolTip, NewTitle;
    event Action<string>? NewStatus;

    public SniItem(ObjectPath path, ObjectPath menu, string id)
    {
        ObjectPath = path;
        _p.Menu = menu;
        _p.Id = id;
    }

    public ObjectPath ObjectPath { get; }
    public Action? Activated { get; set; }

    public void Update(IReadOnlyList<Canvas> icons, string title, string tooltipTitle, string tooltipBody)
    {
        _p.IconPixmap = icons.Select(c => (c.W, c.H, c.ToArgbBigEndian())).ToArray();
        _p.Title = title;
        _p.ToolTip = ("", Array.Empty<(int, int, byte[])>(), tooltipTitle, tooltipBody);
        NewIcon?.Invoke();
        NewToolTip?.Invoke();
        NewTitle?.Invoke();
    }

    public Task ContextMenuAsync(int x, int y) => Task.CompletedTask; // hosts show the dbusmenu
    public Task ActivateAsync(int x, int y) { Dispatch.Post(() => Activated?.Invoke()); return Task.CompletedTask; }
    public Task SecondaryActivateAsync(int x, int y) => ActivateAsync(x, y);
    public Task ScrollAsync(int delta, string orientation) => Task.CompletedTask;

    public Task<IDisposable> WatchNewTitleAsync(Action handler, Action<Exception>? onError = null) => Sub(() => NewTitle += handler, () => NewTitle -= handler);
    public Task<IDisposable> WatchNewIconAsync(Action handler, Action<Exception>? onError = null) => Sub(() => NewIcon += handler, () => NewIcon -= handler);
    public Task<IDisposable> WatchNewAttentionIconAsync(Action handler, Action<Exception>? onError = null) => Sub(() => { }, () => { });
    public Task<IDisposable> WatchNewOverlayIconAsync(Action handler, Action<Exception>? onError = null) => Sub(() => { }, () => { });
    public Task<IDisposable> WatchNewToolTipAsync(Action handler, Action<Exception>? onError = null) => Sub(() => NewToolTip += handler, () => NewToolTip -= handler);
    public Task<IDisposable> WatchNewStatusAsync(Action<string> handler, Action<Exception>? onError = null) => Sub(() => NewStatus += handler, () => NewStatus -= handler);

    public Task<object> GetAsync(string prop) => Task.FromResult<object>(prop switch
    {
        "Category" => _p.Category,
        "Id" => _p.Id,
        "Title" => _p.Title,
        "Status" => _p.Status,
        "WindowId" => _p.WindowId,
        "IconThemePath" => _p.IconThemePath,
        "Menu" => _p.Menu,
        "ItemIsMenu" => _p.ItemIsMenu,
        "IconName" => _p.IconName,
        "IconPixmap" => _p.IconPixmap,
        "OverlayIconName" => _p.OverlayIconName,
        "OverlayIconPixmap" => _p.OverlayIconPixmap,
        "AttentionIconName" => _p.AttentionIconName,
        "AttentionIconPixmap" => _p.AttentionIconPixmap,
        "AttentionMovieName" => _p.AttentionMovieName,
        "ToolTip" => _p.ToolTip,
        _ => "",
    });

    public Task<StatusNotifierItemProperties> GetAllAsync() => Task.FromResult(_p);
    public Task SetAsync(string prop, object val) => Task.CompletedTask;
    public Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler) => Sub(() => { }, () => { });

    internal static Task<IDisposable> Sub(Action add, Action remove)
    {
        add();
        return Task.FromResult<IDisposable>(new Unsub(remove));
    }

    sealed class Unsub : IDisposable
    {
        Action? _a;
        public Unsub(Action a) => _a = a;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }
}

/// <summary>Minimal com.canonical.dbusmenu exporter: flat menu with labels, separators, checks and radios.</summary>
internal sealed class DbusMenu : IDBusMenu
{
    public sealed record Item(string Label, Action? OnClick = null, bool Enabled = true, bool Separator = false, string? Toggle = null, bool Checked = false);

    readonly DBusMenuProperties _props = new();
    List<Item> _items = new();
    uint _revision = 1;
    event Action<(uint revision, int parent)>? LayoutUpdated;

    public DbusMenu(ObjectPath path) => ObjectPath = path;
    public ObjectPath ObjectPath { get; }

    public void SetItems(List<Item> items)
    {
        _items = items;
        _revision++;
        LayoutUpdated?.Invoke((_revision, 0));
    }

    IDictionary<string, object> Props(int id) => Props(_items, id);

    static IDictionary<string, object> Props(List<Item> items, int id)
    {
        if (id <= 0 || id > items.Count) return new Dictionary<string, object> { ["children-display"] = "submenu" };
        var it = items[id - 1];
        var d = new Dictionary<string, object>();
        if (it.Separator) { d["type"] = "separator"; return d; }
        d["label"] = it.Label.Replace("_", "__"); // '_' is a mnemonic marker
        d["enabled"] = it.Enabled;
        d["visible"] = true;
        if (it.Toggle is not null)
        {
            d["toggle-type"] = it.Toggle;
            d["toggle-state"] = it.Checked ? 1 : 0;
        }
        return d;
    }

    public Task<(uint revision, (int, IDictionary<string, object>, object[]) layout)> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames)
    {
        var items = _items;
        object[] children = parentId == 0 && recursionDepth != 0
            ? Enumerable.Range(1, items.Count).Select(i => (object)(i, Props(items, i), Array.Empty<object>())).ToArray()
            : Array.Empty<object>();
        int id = parentId >= 0 && parentId <= items.Count ? parentId : 0;
        return Task.FromResult((_revision, (id, Props(items, id), children)));
    }

    public Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames)
    {
        var items = _items;
        return Task.FromResult(ids.Where(i => i >= 0 && i <= items.Count).Select(i => (i, Props(items, i))).ToArray());
    }

    public Task<object> GetPropertyAsync(int id, string name) =>
        Task.FromResult(id >= 0 && id <= _items.Count && Props(id).TryGetValue(name, out var v) ? v : (object)"");

    public Task EventAsync(int id, string eventId, object data, uint timestamp)
    {
        var items = _items;
        if (eventId == "clicked" && id >= 1 && id <= items.Count)
        {
            var it = items[id - 1];
            if (it.Enabled && it.OnClick is Action a) Dispatch.Post(a);
        }
        return Task.CompletedTask;
    }

    public async Task<int[]> EventGroupAsync((int, string, object, uint)[] events)
    {
        foreach (var (id, ev, data, ts) in events) await EventAsync(id, ev, data, ts);
        return Array.Empty<int>();
    }

    public Task<bool> AboutToShowAsync(int id) => Task.FromResult(false);
    public Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] ids) => Task.FromResult((Array.Empty<int>(), Array.Empty<int>()));

    public Task<IDisposable> WatchItemsPropertiesUpdatedAsync(Action<((int, IDictionary<string, object>)[] updatedProps, (int, string[])[] removedProps)> handler, Action<Exception>? onError = null)
        => SniItem.Sub(() => { }, () => { });
    public Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint revision, int parent)> handler, Action<Exception>? onError = null)
        => SniItem.Sub(() => LayoutUpdated += handler, () => LayoutUpdated -= handler);
    public Task<IDisposable> WatchItemActivationRequestedAsync(Action<(int id, uint timestamp)> handler, Action<Exception>? onError = null)
        => SniItem.Sub(() => { }, () => { });

    public Task<object> GetAsync(string prop) => Task.FromResult<object>(prop switch
    {
        "Version" => _props.Version,
        "TextDirection" => _props.TextDirection,
        "Status" => _props.Status,
        "IconThemePath" => _props.IconThemePath,
        _ => "",
    });
    public Task<DBusMenuProperties> GetAllAsync() => Task.FromResult(_props);
    public Task SetAsync(string prop, object val) => Task.CompletedTask;
    public Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler) => SniItem.Sub(() => { }, () => { });
}

/// <summary>Owns the tray items: one per device (or one summary), re-registers when the panel restarts.</summary>
internal sealed class LinuxTray
{
    readonly Connection _session;
    readonly DbusMenu _menu;
    readonly Dictionary<string, SniItem> _items = new(StringComparer.Ordinal);
    int _next;
    bool _watcherUp;

    public Action? Activated { get; set; }

    public LinuxTray(Connection session)
    {
        _session = session;
        _menu = new DbusMenu(new ObjectPath("/org/glance/Menu"));
    }

    public async Task StartAsync()
    {
        await _session.RegisterObjectAsync(_menu);
        await _session.ResolveServiceOwnerAsync("org.kde.StatusNotifierWatcher", e =>
        {
            bool up = !string.IsNullOrEmpty(e.NewOwner);
            Dispatch.Post(() => OnWatcher(up, force: true));
        }, ex => Log.Info($"SNI watcher: {ex.Message}"));
        try
        {
            bool active = await _session.IsServiceActiveAsync("org.kde.StatusNotifierWatcher");
            if (active) Dispatch.Post(() => OnWatcher(true, force: false));
            if (!active) Log.Info("No StatusNotifierWatcher: tray icons need KDE/XFCE/Cinnamon or the GNOME AppIndicator extension");
        }
        catch { }
    }

    void OnWatcher(bool up, bool force)
    {
        bool was = _watcherUp;
        _watcherUp = up;
        // (Re)register all items when the watcher appears or restarts (panel restart).
        if (up && (!was || force)) foreach (var it in _items.Values) _ = RegisterAsync(it);
    }

    public void SetMenu(List<DbusMenu.Item> items) => _menu.SetItems(items);

    /// <summary>Makes the visible set of icons equal to <paramref name="wanted"/> (key → render data).</summary>
    public async Task SyncAsync(IReadOnlyList<(string Key, IReadOnlyList<Canvas> Icons, string Title, string TipTitle, string TipBody)> wanted)
    {
        foreach (var key in _items.Keys.Where(k => wanted.All(w => w.Key != k)).ToList())
        {
            var it = _items[key];
            _items.Remove(key);
            try { _session.UnregisterObject(it.ObjectPath); } catch { }
        }
        foreach (var w in wanted)
        {
            if (!_items.TryGetValue(w.Key, out var it))
            {
                it = new SniItem(new ObjectPath($"/org/glance/Item{_next++}"), _menu.ObjectPath, "glance-" + _next) { Activated = () => Activated?.Invoke() };
                it.Update(w.Icons, w.Title, w.TipTitle, w.TipBody);
                _items[w.Key] = it;
                try
                {
                    await _session.RegisterObjectAsync(it);
                    if (_watcherUp) await RegisterAsync(it);
                }
                catch (Exception e) { Log.Error("SNI register", e); }
            }
            else it.Update(w.Icons, w.Title, w.TipTitle, w.TipBody);
        }
    }

    async Task RegisterAsync(SniItem it)
    {
        try
        {
            var watcher = _session.CreateProxy<IStatusNotifierWatcher>("org.kde.StatusNotifierWatcher", new ObjectPath("/StatusNotifierWatcher"));
            await watcher.RegisterStatusNotifierItemAsync(it.ObjectPath.ToString());
        }
        catch (Exception e) { Log.Info($"SNI watcher register: {e.Message}"); }
    }
}
