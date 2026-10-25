using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Рисует иконку AppEgress: скруглённая плашка с градиентом, на ней развилка маршрута.
/// Вход слева, узел-переключатель, активный путь со стрелкой вверх-вправо и приглушённый путь вниз.
/// Все размеры заданы в сетке 256×256 и масштабируются; для мелких размеров линии толще, детали проще.
/// </summary>
internal static class Program
{
    private sealed record Palette(Color From, Color To, Color Accent);

    private static readonly Palette Active = new(Rgb(0x38, 0xB6, 0xFF), Rgb(0x4F, 0x46, 0xE5), Rgb(0x2E, 0x6B, 0xF0));
    private static readonly Palette Idle = new(Rgb(0x9A, 0xA3, 0xAE), Rgb(0x5A, 0x62, 0x6E), Rgb(0x6B, 0x73, 0x80));
    private static readonly Palette Error = new(Rgb(0xFF, 0x7A, 0x6B), Rgb(0xD1, 0x2A, 0x3A), Rgb(0xC4, 0x2B, 0x1C));

    // Промежуточные 20/40/128 px Windows получает масштабированием соседних кадров, их не храним.
    private static readonly int[] AppSizes = [16, 24, 32, 48, 64, 256];
    // Трей: 16 px при 100 %, 24 при 150 %, 32 при 200 %, 48 при 300 %.
    private static readonly int[] TraySizes = [16, 20, 24, 32, 48];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--brand")
        {
            WriteBrand(args[1], args[2]);
            Console.WriteLine("Готово: " + Path.GetFullPath(args[1]));
            return 0;
        }
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование:");
            Console.Error.WriteLine("  IconGen <папка для .ico> [папка для превью .png]");
            Console.Error.WriteLine("  IconGen --brand <папка> <скриншот.png>   logo.png и social-preview.png для README/GitHub");
            return 1;
        }
        string outDir = args[0];
        string? previewDir = args.Length > 1 ? args[1] : null;
        Directory.CreateDirectory(outDir);

        WriteIco(Path.Combine(outDir, "app.ico"), AppSizes, Active);
        WriteIco(Path.Combine(outDir, "tray-active.ico"), TraySizes, Active);
        WriteIco(Path.Combine(outDir, "tray-idle.ico"), TraySizes, Idle);
        WriteIco(Path.Combine(outDir, "tray-error.ico"), TraySizes, Error);

        if (previewDir != null)
            WritePreview(previewDir);

        Console.WriteLine("Готово: " + Path.GetFullPath(outDir));
        return 0;
    }

    // ---------------------------------------------------------------- рисование

    private static BitmapSource Render(int size, Palette p)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size / 256.0;
            dc.PushTransform(new ScaleTransform(s, s));
            Draw(dc, size, p);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Draw(DrawingContext dc, int size, Palette p)
    {
        bool tiny = size <= 24;
        bool small = size <= 48;

        // Плашка. На мелких размерах занимает почти всё поле, чтобы не терять пиксели.
        double inset = tiny ? 6 : 12;
        double radius = tiny ? 48 : 56;
        var plate = new RectangleGeometry(new Rect(inset, inset, 256 - 2 * inset, 256 - 2 * inset), radius, radius);
        var bg = new LinearGradientBrush(p.From, p.To, new Point(0.1, 0), new Point(0.9, 1));
        dc.DrawGeometry(bg, null, plate);

        if (!tiny)
        {
            // Мягкий блик сверху и тонкая окантовка: объём, как у иконок Windows 11.
            var shine = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0x48, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.55)],
                new Point(0, 0), new Point(0, 1));
            dc.DrawGeometry(shine, null, plate);
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), size >= 64 ? 2 : 3);
            dc.DrawGeometry(null, edge, new RectangleGeometry(
                new Rect(inset + 1, inset + 1, 256 - 2 * inset - 2, 256 - 2 * inset - 2), radius - 1, radius - 1));
        }

        double stroke = size switch
        {
            <= 20 => 32,
            <= 24 => 28,
            <= 32 => 24,
            <= 48 => 22,
            <= 64 => 20,
            _ => 18,
        };

        // Ключевые точки маршрута
        var start = new Point(tiny ? 40 : 48, 128);
        var split = new Point(tiny ? 100 : 106, 128);
        var upEnd = new Point(tiny ? 186 : 182, tiny ? 76 : 82);
        var downEnd = new Point(tiny ? 192 : 190, tiny ? 182 : 176);

        var white = Brushes.White;
        var dim = new SolidColorBrush(Color.FromArgb(small ? (byte)0x99 : (byte)0x80, 255, 255, 255));

        // Приглушённый путь вниз: «обычный маршрут»
        var downPath = Curve(split, downEnd);
        dc.DrawGeometry(null, RoundPen(dim, stroke), downPath);
        double dotR = stroke * 0.62;
        dc.DrawEllipse(dim, null, downEnd, dotR, dotR);

        // Свечение под активным путём на больших размерах
        var upPath = Curve(split, upEnd);
        if (!small)
            dc.DrawGeometry(null, RoundPen(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)), stroke * 1.9), upPath);

        // Вход и активный путь
        dc.DrawLine(RoundPen(white, stroke), start, split);
        dc.DrawGeometry(null, RoundPen(white, stroke), upPath);

        // Стрелка на конце активного пути, повёрнута по касательной кривой
        DrawArrowHead(dc, upEnd, stroke, tiny);

        // Узел-переключатель в точке развилки
        double nodeR = stroke * (tiny ? 0.95 : 1.05);
        dc.DrawEllipse(white, null, split, nodeR, nodeR);
        if (!tiny)
        {
            double innerR = nodeR * 0.48;
            dc.DrawEllipse(new SolidColorBrush(p.Accent), null, split, innerR, innerR);
        }
    }

    /// <summary>Плавная S-кривая от развилки к концу: выходит и приходит горизонтально.</summary>
    private static Geometry Curve(Point from, Point to)
    {
        double dx = to.X - from.X;
        var fig = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new BezierSegment(
            new Point(from.X + dx * 0.45, from.Y),
            new Point(from.X + dx * 0.45, to.Y),
            to, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    private static void DrawArrowHead(DrawingContext dc, Point tip, double stroke, bool tiny)
    {
        // Кривая приходит к концу горизонтально, поэтому стрелка смотрит вправо.
        double len = stroke * (tiny ? 1.55 : 1.75);
        double half = stroke * (tiny ? 1.25 : 1.35);
        var baseX = tip.X - len * 0.15;
        var fig = new PathFigure { StartPoint = new Point(baseX, tip.Y - half), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(baseX + len, tip.Y), true));
        fig.Segments.Add(new LineSegment(new Point(baseX, tip.Y + half), true));
        var g = new PathGeometry([fig]);
        // Скругляем углы треугольника обводкой того же цвета
        dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, stroke * 0.35) { LineJoin = PenLineJoin.Round }, g);
    }

    private static Pen RoundPen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    // ---------------------------------------------------------------- .ico

    /// <summary>
    /// Пишет .ico: размеры до 48 как 32-битный BMP (DIB) с альфой — так их читают все API, включая
    /// System.Drawing и трей; от 64 — как PNG (поддерживается с Windows Vista), чтобы файл был компактным.
    /// </summary>
    private static void WriteIco(string path, int[] sizes, Palette palette)
    {
        var images = sizes.Select(sz => (Size: sz, Data: sz >= 64 ? Png(Render(sz, palette)) : Dib(Render(sz, palette)))).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (sz, data) in images)
        {
            w.Write((byte)(sz >= 256 ? 0 : sz));
            w.Write((byte)(sz >= 256 ? 0 : sz));
            w.Write((byte)0);          // colors
            w.Write((byte)0);          // reserved
            w.Write((ushort)1);        // planes
            w.Write((ushort)32);       // bpp
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images)
            w.Write(data);
    }

    private static byte[] Png(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Dib(BitmapSource src)
    {
        // Перевод из premultiplied в обычный BGRA
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        int maskStride = ((w + 31) / 32) * 4;
        var mask = new byte[maskStride * h];

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // BITMAPINFOHEADER; высота удвоена (XOR + AND маски)
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);                         // BI_RGB
        bw.Write(pixels.Length + mask.Length);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        // Строки снизу вверх
        for (int y = h - 1; y >= 0; y--)
            bw.Write(pixels, y * stride, stride);

        // AND-маска: 1 там, где пиксель полностью прозрачен (для старых API без альфы)
        for (int y = h - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < w; x++)
                if (pixels[y * stride + x * 4 + 3] == 0)
                    row[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(row);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Увеличение без сглаживания: каждый пиксель становится квадратом k×k.</summary>
    private static BitmapSource Magnify(BitmapSource src, int k)
    {
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        int bw = w * k, bstride = bw * 4;
        var big = new byte[bstride * h * k];
        for (int y = 0; y < h * k; y++)
            for (int x = 0; x < bw; x++)
                Buffer.BlockCopy(px, (y / k) * stride + (x / k) * 4, big, y * bstride + x * 4, 4);
        return BitmapSource.Create(bw, h * k, 96, 96, PixelFormats.Pbgra32, null, big, bstride);
    }

    // ---------------------------------------------------------------- брендинг

    /// <summary>logo.png (512 px) и social-preview.png (1280×640, превью ссылки на GitHub).</summary>
    private static void WriteBrand(string dir, string screenshotPath)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "logo.png"), Png(Render(512, Active)));

        const int W = 1280, H = 640;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Фон: глубокий тёмно-синий с акцентным свечением слева
            dc.DrawRectangle(new LinearGradientBrush(Rgb(0x0A, 0x0F, 0x24), Rgb(0x1A, 0x1B, 0x4B), new Point(0, 0), new Point(1, 1)),
                null, new Rect(0, 0, W, H));
            var glow = new RadialGradientBrush(Color.FromArgb(0x55, 0x38, 0xB6, 0xFF), Color.FromArgb(0, 0x38, 0xB6, 0xFF))
            {
                Center = new Point(0.2, 0.35), GradientOrigin = new Point(0.2, 0.35), RadiusX = 0.55, RadiusY = 0.9,
            };
            dc.DrawRectangle(glow, null, new Rect(0, 0, W, H));

            // Скриншот справа, уходит за край: тень, скругление, тонкая рамка
            var shot = new BitmapImage();
            shot.BeginInit();
            shot.CacheOption = BitmapCacheOption.OnLoad;
            shot.UriSource = new Uri(Path.GetFullPath(screenshotPath));
            shot.EndInit();
            double sw = 820, sh = sw * shot.PixelHeight / shot.PixelWidth;
            var shotRect = new Rect(600, 120, sw, sh);
            var shadow = new RectangleGeometry(new Rect(shotRect.X + 10, shotRect.Y + 22, sw, sh), 18, 18);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), null, shadow);
            var clip = new RectangleGeometry(shotRect, 16, 16);
            dc.PushClip(clip);
            dc.DrawImage(shot, shotRect);
            dc.Pop();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), 1.5), clip);

            // Логотип и тексты слева
            dc.DrawImage(Render(256, Active), new Rect(88, 112, 120, 120));
            double dip = 1.0;
            var display = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var text = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var title = new FormattedText("AppEgress", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, display, 70, Brushes.White, dip);
            dc.DrawText(title, new Point(84, 262));
            var tagline = new FormattedText("Route any Windows app through\nthe network adapter you choose.",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, text, 28,
                new SolidColorBrush(Rgb(0xC8, 0xCF, 0xE8)), dip) { LineHeight = 38 };
            dc.DrawText(tagline, new Point(88, 360));

            // Плашки-теги
            double x = 88;
            foreach (var tag in new[] { "Ethernet", "Wi-Fi", "VPN", "LTE" })
            {
                var ft = new FormattedText(tag, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    text, 20, Brushes.White, dip);
                var r = new Rect(x, 470, ft.Width + 32, 40);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)),
                    new Pen(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), 1), r, 20, 20);
                dc.DrawText(ft, new Point(x + 16, 470 + (40 - ft.Height) / 2));
                x += r.Width + 12;
            }
            var foot = new FormattedText("Windows 10 / 11  ·  free & open source  ·  GPL-3.0",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, text, 18,
                new SolidColorBrush(Rgb(0x8E, 0x97, 0xB8)), dip);
            dc.DrawText(foot, new Point(88, 548));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        File.WriteAllBytes(Path.Combine(dir, "social-preview.png"), Png(rtb));
    }

    // ---------------------------------------------------------------- превью

    /// <summary>Лист превью: все размеры на светлом и тёмном фоне, плюс варианты для трея.</summary>
    private static void WritePreview(string dir)
    {
        Directory.CreateDirectory(dir);
        int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];
        const int pad = 24;
        int trayWidth = pad + 3 * (new[] { 16, 24, 32 }.Sum(sz => sz * 3 + 28) + 20);
        int width = Math.Max(sizes.Sum() + pad * (sizes.Length + 1), trayWidth);
        int rowH = 256 + pad * 2;
        int trayRowH = 48 * 2 + pad * 2;
        int height = rowH * 2 + trayRowH * 2;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var light = new SolidColorBrush(Rgb(0xF3, 0xF3, 0xF3));
            var dark = new SolidColorBrush(Rgb(0x20, 0x20, 0x20));
            dc.DrawRectangle(light, null, new Rect(0, 0, width, rowH));
            dc.DrawRectangle(dark, null, new Rect(0, rowH, width, rowH));
            dc.DrawRectangle(light, null, new Rect(0, rowH * 2, width, trayRowH));
            dc.DrawRectangle(dark, null, new Rect(0, rowH * 2 + trayRowH, width, trayRowH));

            for (int row = 0; row < 2; row++)
            {
                double x = pad;
                foreach (int sz in sizes)
                {
                    var img = Render(sz, Active);
                    // Пиксель в пиксель, без сглаживания при выводе
                    dc.DrawImage(img, new Rect(x, row * rowH + rowH - pad - sz, sz, sz));
                    x += sz + pad;
                }
            }

            // Трей: три состояния в 16/24/32, и каждое ещё увеличено ×2 для разглядывания
            for (int row = 0; row < 2; row++)
            {
                double x = pad;
                double y0 = rowH * 2 + row * trayRowH + pad;
                foreach (var pal in new[] { Active, Idle, Error })
                {
                    foreach (int sz in new[] { 16, 24, 32 })
                    {
                        var img = Render(sz, pal);
                        dc.DrawImage(img, new Rect(x, y0 + 48 - sz, sz, sz));
                        x += sz + 10;
                        var big = Magnify(img, 2);
                        dc.DrawImage(big, new Rect(x, y0 + 96 - sz * 2, sz * 2, sz * 2));
                        x += sz * 2 + 18;
                    }
                    x += 20;
                }
            }
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        File.WriteAllBytes(Path.Combine(dir, "icon-sheet.png"), Png(rtb));
    }
}
