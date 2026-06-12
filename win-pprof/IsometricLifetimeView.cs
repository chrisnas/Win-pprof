#nullable enable
using System;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace win_pprof;

/// <summary>
/// Isometric 3D "chess board" of object lifetimes: one receding axis is the allocation type,
/// the other is the age bucket (1s, 10s, 100s, …). Each tile grows a cuboid whose height
/// encodes the count (or bytes) for that type/age slot — tall stacks at old ages are leak candidates.
///
/// Drawn directly with SkiaSharp (LiveCharts has no 3D primitive) using a classic 2:1 isometric
/// projection and the painter's algorithm (back-to-front) so nearer bars correctly occlude far ones.
/// </summary>
public sealed class IsometricLifetimeView : SKElement
{
    // 2:1 isometric: a unit step on a ground axis maps to (±halfW, +halfH) on screen.
    private const float TileHalfHRatio = 0.5f;     // halfH = halfW * this
    private const float MaxBarHeightPx = 220f;     // tallest bar (the max value) in screen px
    private const float MinBarHeightPx = 2f;       // floor so non-zero slots stay visible

    // Invert the age axis so the oldest buckets (where leaks pile up = tallest bars) sit at the
    // back and don't occlude the shorter, younger bars in front.
    private const bool InvertAgeAxis = true;

    // Upper bound on type-name length before the shared cut algorithm kicks in (namespace…Type<…>).
    private const int TypeLabelMaxChars = 40;

    private double[,]? _grid;
    private string[] _typeNames = Array.Empty<string>();
    private string[] _bucketLabels = Array.Empty<string>();
    private double _maxValue;
    private bool _weightByBytes = true;

    public IsometricLifetimeView()
    {
        // Honour the WPF DPI so text and lines stay crisp on high-DPI displays.
        IgnorePixelScaling = false;
    }

    /// <summary>Feed the [type, bucket] grid and trigger a redraw.</summary>
    public void SetData(double[,] grid, string[] typeNames, string[] bucketLabels, double maxValue, bool weightByBytes)
    {
        _grid = grid;
        _typeNames = typeNames;
        _bucketLabels = bucketLabels;
        _maxValue = maxValue;
        _weightByBytes = weightByBytes;
        InvalidateVisual();
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x20, 0x20, 0x28));

        var grid = _grid;
        if (grid == null)
            return;

        int nTypes = grid.GetLength(0);     // rows (one receding axis)
        int nBuckets = grid.GetLength(1);   // cols (the other receding axis)
        if (nTypes == 0 || nBuckets == 0)
            return;

        float width = e.Info.Width;
        float height = e.Info.Height;

        // Pre-cut the type names with the shared algorithm and the larger (2×) font, then size the
        // right margin to the longest one so we show as many characters as the data needs without
        // clipping or wasting space. Both legends sit on the right (type names on the front-right
        // edge, age labels on the back-right edge), so the horizontal slack goes there.
        using var typeFont = new SKFont { Size = 18 };
        var typeLabels = new string[nTypes];
        float maxTypeLabelW = 0;
        for (int r = 0; r < nTypes; r++)
        {
            typeLabels[r] = LifetimeControl.TruncateLabel(_typeNames[r], TypeLabelMaxChars);
            float w = typeFont.MeasureText(typeLabels[r]);
            if (w > maxTypeLabelW)
                maxTypeLabelW = w;
        }

        const float marginLeft = 12f;
        const float marginTop = 24f;
        const float marginBottom = 12f;
        float labelGap = 2 * typeFont.MeasureText("M"); // matches the push-off in DrawAxisLabels
        float marginRight = Math.Clamp(maxTypeLabelW + labelGap + 8f, 80f, width * 0.5f);

        float availW = width - marginLeft - marginRight;
        float availH = height - marginTop - marginBottom - MaxBarHeightPx;
        if (availW <= 0 || availH <= 0)
            return;

        // The diamond footprint spans (nBuckets + nTypes) half-tiles in each screen direction.
        float halfWFromWidth = availW / (nBuckets + nTypes);
        float halfWFromHeight = availH / ((nBuckets + nTypes) * TileHalfHRatio);
        float halfW = Math.Max(6f, Math.Min(halfWFromWidth, halfWFromHeight));
        float halfH = halfW * TileHalfHRatio;

        // Origin = projected position of grid corner (col=0, row=0).
        // x: cols push right (+), rows push left (−); shift so the left-most point clears the margin.
        // y: both axes push down; leave MaxBarHeightPx of headroom up top for the tallest bar.
        float footprintW = (nBuckets + nTypes) * halfW;
        float originX = marginLeft + nTypes * halfW + (availW - footprintW) / 2f;
        float originY = marginTop + MaxBarHeightPx;

        // Project ground coords (fx along buckets/cols, fy along types/rows) to screen, lifted by z px.
        SKPoint Project(float fx, float fy, float z) => new(
            originX + (fx - fy) * halfW,
            originY + (fx + fy) * halfH - z);

        DrawGroundTiles(canvas, grid, nTypes, nBuckets, Project);
        DrawBars(canvas, grid, nTypes, nBuckets, Project);
        DrawAxisLabels(canvas, nTypes, nBuckets, typeLabels, typeFont, Project);
        DrawTitle(canvas, width);
    }

    private void DrawGroundTiles(SKCanvas canvas, double[,] grid, int nTypes, int nBuckets, Func<float, float, float, SKPoint> project)
    {
        using var fillA = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0x2E, 0x2E, 0x3A), IsAntialias = true };
        using var fillB = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0x26, 0x26, 0x30), IsAntialias = true };
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x40, 0x40, 0x50), StrokeWidth = 1, IsAntialias = true };

        for (int r = 0; r < nTypes; r++)
        {
            for (int c = 0; c < nBuckets; c++)
            {
                using var path = new SKPath();
                path.MoveTo(project(c, r, 0));
                path.LineTo(project(c + 1, r, 0));
                path.LineTo(project(c + 1, r + 1, 0));
                path.LineTo(project(c, r + 1, 0));
                path.Close();
                canvas.DrawPath(path, ((r + c) & 1) == 0 ? fillA : fillB);
                canvas.DrawPath(path, edge);
            }
        }
    }

    private void DrawBars(SKCanvas canvas, double[,] grid, int nTypes, int nBuckets, Func<float, float, float, SKPoint> project)
    {
        double max = _maxValue > 0 ? _maxValue : 1;

        using var topPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var leftPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var rightPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var outline = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 90), StrokeWidth = 1, IsAntialias = true };

        // Painter's algorithm: draw far tiles (small r+c) first so near bars overwrite them.
        for (int diag = 0; diag <= (nTypes - 1) + (nBuckets - 1); diag++)
        {
            for (int r = 0; r < nTypes; r++)
            {
                int c = diag - r;
                if (c < 0 || c >= nBuckets)
                    continue;

                // c is the geometric column (0 = back). Map it to the data bucket honouring inversion.
                int dataCol = InvertAgeAxis ? nBuckets - 1 - c : c;
                double v = grid[r, dataCol];
                if (v <= 0)
                    continue;

                float ratio = (float)(v / max);
                float z = MinBarHeightPx + ratio * (MaxBarHeightPx - MinBarHeightPx);

                var top = HeightColor(ratio);
                topPaint.Color = top;
                rightPaint.Color = Shade(top, 0.78f);
                leftPaint.Color = Shade(top, 0.60f);

                // Ground corners of this tile.
                var g00 = project(c, r, 0);
                var g10 = project(c + 1, r, 0);
                var g11 = project(c + 1, r + 1, 0);
                var g01 = project(c, r + 1, 0);

                // Top-face corners (lifted by z).
                var t00 = project(c, r, z);
                var t10 = project(c + 1, r, z);
                var t11 = project(c + 1, r + 1, z);
                var t01 = project(c, r + 1, z);

                // The two faces meeting the front corner (c+1, r+1) are the visible sides.
                DrawQuad(canvas, g10, g11, t11, t10, rightPaint, outline); // right face
                DrawQuad(canvas, g01, g11, t11, t01, leftPaint, outline);  // left face
                DrawQuad(canvas, t00, t10, t11, t01, topPaint, outline);   // top face
            }
        }
    }

    private static void DrawQuad(SKCanvas canvas, SKPoint a, SKPoint b, SKPoint c, SKPoint d, SKPaint fill, SKPaint outline)
    {
        using var path = new SKPath();
        path.MoveTo(a);
        path.LineTo(b);
        path.LineTo(c);
        path.LineTo(d);
        path.Close();
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, outline);
    }

    private void DrawAxisLabels(SKCanvas canvas, int nTypes, int nBuckets, string[] typeLabels, SKFont typeFont, Func<float, float, float, SKPoint> project)
    {
        using var font = new SKFont { Size = 12 };
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var highlight = new SKPaint { Color = new SKColor(0xFF, 0xCC, 0x80), IsAntialias = true }; // light orange

        // Type names (already cut with the shared algorithm) along the FRONT (col = nBuckets) receding
        // edge. That edge faces lower-right, so labels go to its right (left-aligned). The bars sit to
        // the LEFT of this edge, so push the labels ~2 characters further right to keep a clear gap.
        // BuggyBits types (the deliberate-leak demo) are highlighted in light orange.
        float gap = 2 * typeFont.MeasureText("M");
        for (int r = 0; r < nTypes; r++)
        {
            var p = project(nBuckets, r + 0.5f, 0);
            var paint = _typeNames[r].StartsWith("Buggybits", StringComparison.OrdinalIgnoreCase) ? highlight : text;
            canvas.DrawText(typeLabels[r], p.X + gap, p.Y + 6, typeFont, paint);
        }

        // Age bucket labels along the back (row = 0) receding edge — left-aligned, just outside it.
        // Map the geometric column to the data bucket so the labels track the inverted axis.
        for (int c = 0; c < nBuckets; c++)
        {
            int dataCol = InvertAgeAxis ? nBuckets - 1 - c : c;
            var p = project(c + 0.5f, 0, 0);
            canvas.DrawText(_bucketLabels[dataCol], p.X + 8, p.Y - 4, font, text);
        }
    }

    private void DrawTitle(SKCanvas canvas, float width)
    {
        using var font = new SKFont { Size = 13 };
        using var text = new SKPaint { Color = new SKColor(0xC8, 0xC8, 0xD2), IsAntialias = true };
        var title = $"Types × age — bar height = {(_weightByBytes ? "bytes" : "count")} per slot";
        canvas.DrawText(title, 12, 16, font, text);
    }

    /// <summary>Blue → cyan → yellow → red ramp, matching the heatmap's cool-to-hot reading.</summary>
    private static SKColor HeightColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.5f)
            return Lerp(new SKColor(0x0D, 0x47, 0xA1), new SKColor(0xFF, 0xEB, 0x3B), t / 0.5f);
        return Lerp(new SKColor(0xFF, 0xEB, 0x3B), new SKColor(0xE5, 0x39, 0x35), (t - 0.5f) / 0.5f);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t));

    private static SKColor Shade(SKColor c, float f) => new(
        (byte)(c.Red * f), (byte)(c.Green * f), (byte)(c.Blue * f));
}
