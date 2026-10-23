using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Renders the icon family from the design artboards (2026-10-23 "OpenMount UI 与图标设计", Icon/Tray boards) into .ico files.
/// Geometry is kept in the design grids (64 for the app and drive icons, 16 for the tray) and scaled per size, with the
/// per-size simplifications the design prescribes (no connector / LED / activity bar below 32 px, 16 px uses its own grid).
/// </summary>
internal static class Program
{
    private static readonly Color Blue = Hex("#2457E6");
    private static readonly Color White = Colors.White;
    private static readonly Color Ink = Hex("#1B1F24");
    private static readonly Color Amber = Hex("#F59E0B");
    private static readonly Color SkyBlue = Hex("#60A5FA");
    private static readonly Color BodyFill = Hex("#DCE4FB");
    private static readonly Color OfflineFill = Hex("#F3F5F8");
    private static readonly Color Grey = Hex("#8A93A0");
    private static readonly Color TrayIdleDark = Hex("#9AA3AE");

    // 64-grid app icon
    private const string Cloud64 = "M20 30a8 8 0 0 1 15.2-3.2A6.5 6.5 0 0 1 46 31.5c0 .2 0 .3-.1.5H21.5A4.5 4.5 0 0 1 20 30z";
    private const string Cloud64Small = "M19 30a9 9 0 0 1 17-3.5A7 7 0 0 1 47 32H21a5 5 0 0 1-2-2z";
    // 16-grid glyph (tray and 16 px app icon)
    private const string Cloud16 = "M4.5 7.5a3 3 0 0 1 5.8-1A2.5 2.5 0 0 1 12.5 8H5a1.5 1.5 0 0 1-.5-.5z";
    private const string Arrow16 = "M12.5 13.6v-3.4M11 11.6l1.5-1.5 1.5 1.5";
    // badge glyphs for Explorer drive icons (64-grid, inside the r=10 badge at 18,16)
    private const string GlobeGlyph = "M8 16h20M18 6a14 14 0 0 1 0 20M18 6a14 14 0 0 0 0 20";
    private const string KeyGlyph = "M16.8 16.2 24 9M21 12l2 2";
    private const string CloudGlyph = "M12.5 19.5a3.5 3.5 0 0 1-.4-7A4.8 4.8 0 0 1 21.5 13a3.3 3.3 0 0 1 0 6.5z";

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(FindRepoRoot(), "src", "OpenMount.App", "Assets");
        Directory.CreateDirectory(outDir);

        WriteIco(Path.Combine(outDir, "OpenMount.ico"), new[] { 256, 48, 32, 24, 16 }, DrawAppIcon);

        foreach (var state in new[] { "idle", "connected", "attention", "uploading" })
        {
            WriteIco(Path.Combine(outDir, $"tray-{state}-dark.ico"), new[] { 16, 20, 24, 32 }, (dc, size) => DrawTray(dc, size, state, darkTaskbar: true));
            WriteIco(Path.Combine(outDir, $"tray-{state}-light.ico"), new[] { 16, 20, 24, 32 }, (dc, size) => DrawTray(dc, size, state, darkTaskbar: false));
        }

        foreach (var kind in new[] { "webdav", "sftp", "cloud", "nas", "offline" })
            WriteIco(Path.Combine(outDir, $"drive-{kind}.ico"), new[] { 256, 48, 32, 16 }, (dc, size) => DrawDriveIcon(dc, size, kind));

        Console.WriteLine("icons written to " + outDir);
        return 0;
    }

    // ---------------------------------------------------------------- app icon

    private static void DrawAppIcon(DrawingContext dc, int size)
    {
        if (size <= 16)
        {
            dc.PushTransform(new ScaleTransform(size / 16.0, size / 16.0));
            dc.DrawRoundedRectangle(Brush(Blue), null, new Rect(0.5, 0.5, 15, 15), 3.5, 3.5);
            dc.DrawGeometry(Brush(White), null, Geometry.Parse(Cloud16));
            dc.DrawRoundedRectangle(Brush(White), null, new Rect(3, 9.5, 10, 4), 1.3, 1.3);
            dc.Pop();
            return;
        }
        dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
        dc.DrawRoundedRectangle(Brush(Blue), null, new Rect(4, 4, 56, 56), 14, 14);
        if (size <= 24)
        {
            dc.DrawGeometry(Brush(White), null, Geometry.Parse(Cloud64Small));
            dc.DrawRoundedRectangle(Brush(White), null, new Rect(13, 36, 38, 15), 4, 4);
        }
        else
        {
            dc.DrawGeometry(Brush(White), null, Geometry.Parse(Cloud64));
            if (size >= 48) dc.DrawRectangle(Brush(White), null, new Rect(31, 32, 2, 4)); // connector
            dc.DrawRoundedRectangle(Brush(White), null, new Rect(14, 36, 36, 14), 4, 4);
            var led = size <= 32 ? 2.6 : 2.4;
            dc.DrawEllipse(Brush(Blue), null, new Point(43, 43), led, led);
            if (size >= 256) dc.DrawRoundedRectangle(Brush(Blue, 0.35), null, new Rect(18, 41.5, 14, 3), 1.5, 1.5); // activity bar
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- tray

    private static void DrawTray(DrawingContext dc, int size, string state, bool darkTaskbar)
    {
        var glyphColor = state == "idle" ? (darkTaskbar ? TrayIdleDark : Grey) : (darkTaskbar ? White : Ink);
        var scale = size / 16.0;
        dc.PushTransform(new ScaleTransform(scale, scale));
        Geometry glyph = new GeometryGroup
        {
            FillRule = FillRule.Nonzero,
            Children =
            {
                Geometry.Parse(Cloud16),
                new RectangleGeometry(new Rect(3, 9.5, 10, 4), 1.3, 1.3),
            },
        };
        var marked = state is "attention" or "uploading";
        if (marked)
        {
            // punch a hole so the marker separates from the glyph on any taskbar colour
            glyph = new CombinedGeometry(GeometryCombineMode.Exclude, glyph, new EllipseGeometry(new Point(12.5, 12), 3.2, 3.2));
        }
        dc.DrawGeometry(Brush(glyphColor), null, glyph);
        if (state == "attention")
        {
            dc.DrawEllipse(Brush(Amber), null, new Point(12.5, 12), 2.3, 2.3);
        }
        else if (state == "uploading")
        {
            var pen = new Pen(Brush(darkTaskbar ? SkyBlue : Blue), 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, Geometry.Parse(Arrow16));
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- Explorer drive icons

    private static void DrawDriveIcon(DrawingContext dc, int size, string kind)
    {
        var offline = kind == "offline";
        var accent = offline ? Grey : Blue;
        dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
        var bodyPen = new Pen(Brush(accent), 2.5);
        if (offline) bodyPen.DashStyle = new DashStyle(new double[] { 1.6, 1.2 }, 0);
        var body = size <= 16 ? new Rect(4, 20, 56, 26) : new Rect(6, 22, 52, 22);
        dc.DrawRoundedRectangle(Brush(offline ? OfflineFill : BodyFill), bodyPen, body, 6, 6);
        var ledR = size <= 16 ? 4.0 : 3.0;
        dc.DrawEllipse(Brush(accent), null, new Point(48, 33), ledR, ledR);
        if (size > 16) dc.DrawRoundedRectangle(Brush(accent, 0.45), null, new Rect(12, 31, 20, 4), 2, 2);
        if (!offline && size > 16)
        {
            dc.DrawEllipse(Brush(White), new Pen(Brush(Blue), 2.5), new Point(18, 16), 10, 10);
            var glyphPen = new Pen(Brush(Blue), size <= 32 ? 2.6 : 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            switch (kind)
            {
                case "webdav":
                    dc.DrawGeometry(null, glyphPen, Geometry.Parse(GlobeGlyph));
                    break;
                case "sftp":
                    dc.DrawEllipse(null, glyphPen, new Point(14.5, 18.5), 3, 3);
                    dc.DrawGeometry(null, glyphPen, Geometry.Parse(KeyGlyph));
                    break;
                case "cloud":
                    dc.DrawGeometry(null, glyphPen, Geometry.Parse(CloudGlyph));
                    break;
                case "nas":
                    dc.DrawRoundedRectangle(null, glyphPen, new Rect(11.5, 10.5, 13, 4.5), 1.5, 1.5);
                    dc.DrawRoundedRectangle(null, glyphPen, new Rect(11.5, 17, 13, 4.5), 1.5, 1.5);
                    break;
            }
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- rendering + ICO container

    private static BitmapSource Render(int size, Action<DrawingContext, int> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            draw(dc, size);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void WriteIco(string path, int[] sizes, Action<DrawingContext, int> draw)
    {
        var blobs = new List<byte[]>();
        foreach (var size in sizes)
        {
            var img = Render(size, draw);
            blobs.Add(size >= 256 ? EncodePng(img) : EncodeDib(img));
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(blobs[i].Length);
            w.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (var b in blobs) w.Write(b);
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
        Console.WriteLine($"{Path.GetFileName(path)}: {string.Join("/", sizes)} px, {ms.Length} bytes");
    }

    private static byte[] EncodePng(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>32-bit BGRA DIB with straight alpha plus the 1-bit AND mask, as classic ICO entries expect.</summary>
    private static byte[] EncodeDib(BitmapSource img)
    {
        var straight = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var w = straight.PixelWidth;
        var h = straight.PixelHeight;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        straight.CopyPixels(pixels, stride, 0);

        var maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);               // biSize
        bw.Write(w);                // biWidth
        bw.Write(h * 2);            // biHeight (XOR + AND)
        bw.Write((ushort)1);        // biPlanes
        bw.Write((ushort)32);       // biBitCount
        bw.Write(0);                // biCompression BI_RGB
        bw.Write(stride * h + maskStride * h); // biSizeImage
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (var y = h - 1; y >= 0; y--) bw.Write(pixels, y * stride, stride);
        var maskRow = new byte[maskStride];
        for (var y = h - 1; y >= 0; y--)
        {
            Array.Clear(maskRow);
            for (var x = 0; x < w; x++)
            {
                var alpha = pixels[y * stride + x * 4 + 3];
                if (alpha == 0) maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
            }
            bw.Write(maskRow);
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static SolidColorBrush Brush(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenMount.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("OpenMount.sln not found above " + AppContext.BaseDirectory);
    }
}
