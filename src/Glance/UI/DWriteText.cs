using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Glance;

/// <summary>
/// Text on glass the way modern Windows (WinUI) and iOS draw text on translucent surfaces: DirectWrite with
/// grayscale antialiasing and natural symmetric rendering, written by Direct2D straight into the premultiplied
/// buffer. Exact colours, correct gamma, crisp stems, no halos and no assumption about what is behind the
/// window. Any failure switches back to the GDI path for the rest of the session.
/// </summary>
internal static class DWriteText
{
    static ID2D1Factory? _factory;
    static IDWriteFactory? _dw;
    static ID2D1DCRenderTarget? _rt;
    static readonly Dictionary<(string, float, System.Drawing.FontStyle, int), IDWriteTextFormat> Formats = new();
    public static bool Failed;

    public static bool Draw(IntPtr hdc, Size buffer, string s, Font f, Color c, RectangleF r, RectangleF clip, TextFormatFlags flags)
    {
        if (Failed || hdc == IntPtr.Zero) return false;
        try
        {
            _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            _dw ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
            if (_rt is null)
            {
                var props = new RenderTargetProperties(
                    RenderTargetType.Default,
                    new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96, 96, RenderTargetUsage.None, FeatureLevel.Default);
                _rt = _factory.CreateDCRenderTarget(props);
                _rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
                _rt.AntialiasMode = AntialiasMode.PerPrimitive;
            }
            _rt.BindDC(hdc, new Vortice.RawRect(0, 0, buffer.Width, buffer.Height));
            var fmt = Format(f, flags);
            _rt.BeginDraw();
            _rt.PushAxisAlignedClip(new Vortice.Mathematics.Rect(clip.X, clip.Y, clip.Width, clip.Height), AntialiasMode.Aliased);
            using (var brush = _rt.CreateSolidColorBrush(new Vortice.Mathematics.Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f)))
                _rt.DrawText(s, fmt, new Vortice.Mathematics.Rect(r.X, r.Y, r.Width, r.Height), brush, DrawTextOptions.Clip);
            _rt.PopAxisAlignedClip();
            _rt.EndDraw();
            return true;
        }
        catch (Exception e)
        {
            Failed = true;
            Log.Error("DirectWrite text", e);
            try { _rt?.Dispose(); } catch { }
            _rt = null;
            return false;
        }
    }

    static IDWriteTextFormat Format(Font f, TextFormatFlags flags)
    {
        int align = (int)(flags & (TextFormatFlags.HorizontalCenter | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.Bottom | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis));
        var key = (f.Name, f.Size, f.Style, align);
        if (Formats.TryGetValue(key, out var fmt)) return fmt;

        // GDI family names → DirectWrite family + weight («… Semib», «… Semibold» are weights, not families)
        string family = f.Name;
        var weight = f.Bold ? FontWeight.Bold : FontWeight.Normal;
        foreach (var suffix in new[] { " Semibold", " Semib" })
            if (family.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { family = family[..^suffix.Length]; weight = FontWeight.SemiBold; }
        float px = f.Unit == GraphicsUnit.Pixel ? f.Size : f.SizeInPoints * 96f / 72f;
        fmt = _dw!.CreateTextFormat(family, null!, weight, f.Italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, px, "ru-ru");
        fmt.TextAlignment = (flags & TextFormatFlags.HorizontalCenter) != 0 ? TextAlignment.Center : (flags & TextFormatFlags.Right) != 0 ? TextAlignment.Trailing : TextAlignment.Leading;
        fmt.ParagraphAlignment = (flags & TextFormatFlags.VerticalCenter) != 0 ? ParagraphAlignment.Center : (flags & TextFormatFlags.Bottom) != 0 ? ParagraphAlignment.Far : ParagraphAlignment.Near;
        fmt.WordWrapping = (flags & TextFormatFlags.WordBreak) != 0 ? WordWrapping.Wrap : WordWrapping.NoWrap;
        if ((flags & TextFormatFlags.EndEllipsis) != 0)
        {
            using var sign = _dw.CreateEllipsisTrimmingSign(fmt);
            fmt.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        }
        Formats[key] = fmt;
        return fmt;
    }
}
