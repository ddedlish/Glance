namespace Glance;

/// <summary>Reads the bundled CHANGELOG.md into versions and bullet lines for the «Что нового» window.</summary>
public static class Changelog
{
    public sealed record Line(string Text, int Level);
    public sealed record Entry(string Version, IReadOnlyList<Line> Lines);

    public static IReadOnlyList<Entry> Parse(string markdown)
    {
        var list = new List<Entry>();
        string? ver = null;
        var lines = new List<Line>();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                if (ver is not null) list.Add(new Entry(ver, lines));
                ver = raw[3..].Trim();
                lines = new List<Line>();
                continue;
            }
            if (ver is null) continue;
            var t = raw.TrimEnd();
            if (t.Length == 0) continue;
            int indent = t.Length - t.TrimStart().Length;
            t = t.Trim();
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) t = t[2..];
            t = t.Replace("**", "").Replace("`", "");
            if (t.Length > 0) lines.Add(new Line(t, indent >= 2 ? 1 : 0));
        }
        if (ver is not null) list.Add(new Entry(ver, lines));
        return list;
    }

    /// <summary>The changelog embedded into the program (null if missing).</summary>
    public static string? Bundled()
    {
        try
        {
            foreach (var asm in new[] { System.Reflection.Assembly.GetEntryAssembly(), typeof(Changelog).Assembly })
            {
                using var st = asm?.GetManifestResourceStream("CHANGELOG.md");
                if (st is null) continue;
                using var r = new StreamReader(st);
                return r.ReadToEnd();
            }
        }
        catch { }
        return null;
    }
}
