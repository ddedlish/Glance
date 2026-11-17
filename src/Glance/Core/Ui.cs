namespace Glance;

/// <summary>Marshals work onto the WinForms UI thread. Always asynchronous – never re-enters the caller.</summary>
internal static class Ui
{
    static SynchronizationContext? _ctx;
    static int _uiThread;

    public static bool IsUiThread => Environment.CurrentManagedThreadId == _uiThread;

    public static void Init()
    {
        _ctx = SynchronizationContext.Current ?? throw new InvalidOperationException("No UI context");
        _uiThread = Environment.CurrentManagedThreadId;
        Dispatch.Post = Post;
    }

    public static void Post(Action action)
    {
        _ctx?.Post(static s =>
        {
            try { ((Action)s!)(); }
            catch (Exception e) { Log.Error("UI post", e); }
        }, action);
    }

    /// <summary>Runs <paramref name="action"/> once after <paramref name="ms"/> on the UI thread.</summary>
    public static void Delay(int ms, Action action)
    {
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            try { action(); }
            catch (Exception e) { Log.Error("UI delay", e); }
        };
        t.Start();
    }
}
