using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FindFont.App.Services;

/// <summary>
/// 离屏出图与 Typeface 解析。拆出此文件只为守住单文件 300 行的规约，
/// 缓存状态（字典、字节预算、去重表）仍留在 FontRenderCache 主文件里。
/// </summary>
public sealed partial class FontRenderCache
{
    private BitmapSource RenderCore(RenderRequest request)
    {
        var typeface = ResolveTypeface(request.FontFilePath);

        var dpi = request.Dpi <= 0 ? 96 : request.Dpi;
        var scale = dpi / 96.0;

        var formatted = new FormattedText(
            string.IsNullOrEmpty(request.Text) ? " " : request.Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            request.FontSize,
            Brushes.Black,
            null,
            TextFormattingMode.Ideal,
            scale)
        {
            MaxTextWidth = Math.Max(8, request.PixelWidth - 8),
            MaxTextHeight = Math.Max(8, request.PixelHeight),
            Trimming = TextTrimming.CharacterEllipsis,
            LineHeight = Math.Max(request.FontSize * 1.15, request.FontSize),
        };

        // 垂直居中：展开态有 220px 高，贴着顶画会让字形浮在上半边。
        var top = Math.Max(0, (request.PixelHeight - formatted.Height) / 2);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, request.PixelWidth, request.PixelHeight));
            dc.DrawText(formatted, new Point(2, top));
        }

        var rtb = new RenderTargetBitmap(
            Math.Max(1, request.PixelWidth),
            Math.Max(1, request.PixelHeight),
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    /// <summary>
    /// 取 Typeface。首次为某文件构造时优先用「文件 URI + #家族名」，
    /// 避开按家族名二次查找系统字体族的开销，也避免同类名撞车。
    /// </summary>
    private Typeface ResolveTypeface(string? path)
    {
        var fallback = new Typeface("Segoe UI");
        if (string.IsNullOrWhiteSpace(path)) return fallback;

        lock (_sync)
        {
            if (_typefaces.TryGetValue(path, out var cached)) return cached ?? fallback;
        }

        var glyph = ResolveGlyphTypeface(path);
        Typeface? built = null;

        if (glyph is not null && glyph.FamilyNames.Count > 0)
        {
            var family = glyph.FamilyNames.Values.First();
            try
            {
                built = new Typeface(
                    new FontFamily(new Uri(Path.GetFullPath(path)), "./#" + family),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal);
                if (!built.TryGetGlyphTypeface(out _)) built = null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
            {
                built = null;
            }

            built ??= new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }

        lock (_sync)
        {
            _typefaces[path] = built;
            return built ?? fallback;
        }
    }

    private GlyphTypeface? ResolveGlyphTypeface(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (_sync)
        {
            if (_glyphs.TryGetValue(path, out var cached)) return cached;
            if (_invalidGlyphs.ContainsKey(path)) return null;
        }

        GlyphTypeface? glyph = null;
        try
        {
            if (File.Exists(path)) glyph = new GlyphTypeface(new Uri(Path.GetFullPath(path)));
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or ArgumentException or IOException)
        {
            glyph = null;
        }

        lock (_sync)
        {
            if (glyph is null)
            {
                _invalidGlyphs[path] = true;
                return null;
            }

            _glyphs[path] = glyph;
            return glyph;
        }
    }

    private static CacheKey ToKey(RenderRequest r) =>
        new(r.FontKey, r.Text, r.FontSize, r.PixelWidth, r.PixelHeight, r.Dpi);

    private void Store(CacheKey key, BitmapSource bitmap)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
                _bytes -= existing.Value.Key.Bytes;
            }

            var node = _lru.AddFirst((key, bitmap));
            _map[key] = node;
            _bytes += key.Bytes;

            // 双约束：条数上限 + 字节预算。永远保留刚插入的那一张。
            while ((_map.Count > _capacity || _bytes > _byteBudget) && _lru.Last is { } last && _map.Count > 1)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Key.Bytes;
            }

            if (_bytes < 0) _bytes = 0;
        }
    }

    private BitmapSource Placeholder(RenderRequest request)
    {
        var width = Math.Max(1, request.PixelWidth);
        var height = Math.Max(1, request.PixelHeight);
        var size = Math.Min(18, request.FontSize);

        lock (_sync)
        {
            if (_placeholders.TryGetValue((width, height), out var cached) && Math.Abs(size - 18) < 0.01)
                return cached;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            var text = new FormattedText(
                "Unknown Font",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                size,
                Brushes.Gray,
                null,
                TextFormattingMode.Ideal,
                1.0);
            dc.DrawText(text, new Point(2, Math.Max(0, height / 2.0 - text.Height / 2)));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        if (Math.Abs(size - 18) < 0.01)
        {
            lock (_sync) _placeholders[(width, height)] = rtb;
        }

        return rtb;
    }
}
