using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TrafikRuler.Core;
using TrafikRuler.UI.Theme;
using TrafikRuler.Localization;

namespace TrafikRuler.UI;

/// <summary>Значок в трее с меню в стиле Windows 11.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly RuleEngine _engine;
    private readonly MainWindow _window;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openItem, _pauseItem, _exitItem;
    private bool _hintShown;

    public TrayController(RuleEngine engine, MainWindow window, Action exit)
    {
        _engine = engine;
        _window = window;

        _openItem = new ToolStripMenuItem(Loc.T("TrayOpen"), null, (_, _) => _window.ShowAndActivate());
        _pauseItem = new ToolStripMenuItem(Loc.T("TrayPauseAll"), null, (_, _) => TogglePause());
        _exitItem = new ToolStripMenuItem(Loc.T("TrayExit"), null, (_, _) => exit());
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = true,
            Padding = new Padding(4, 6, 4, 6),
            Font = new Font("Segoe UI", 9.5f),
        };
        _menu.Items.AddRange([_openItem, _pauseItem, new ToolStripSeparator(), _exitItem]);
        foreach (ToolStripItem item in _menu.Items)
            item.Padding = new Padding(4, 6, 12, 6);
        _menu.Opening += (_, _) => StyleMenu();
        _menu.Opened += (_, _) => RoundCorners(_menu.Handle);

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcons.Get(IconState.Idle),
            Text = "TrafikRuler",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _window.ShowAndActivate();
        };

        _window.HiddenToTray += () =>
        {
            if (_hintShown)
                return;
            _hintShown = true;
            _notifyIcon.ShowBalloonTip(3000, Loc.T("TrayHintTitle"), Loc.T("TrayHintText"), ToolTipIcon.Info);
        };
        _engine.Changed += OnEngineChanged;
    }

    private void OnEngineChanged() => _window.Dispatcher.BeginInvoke(UpdateTray);

    private void UpdateTray()
    {
        var state = _engine.HasError ? IconState.Error
            : _engine.Views.Any(v => v.State == RuleState.Active) ? IconState.Active
            : IconState.Idle;
        _notifyIcon.Icon = AppIcons.Get(state);
        string text = "TrafikRuler: " + _engine.Status;
        _notifyIcon.Text = text.Length > 63 ? text[..62] + "…" : text;
    }

    private void TogglePause()
    {
        _engine.Settings.Paused = !_engine.Settings.Paused;
        Logger.Write(Loc.T(_engine.Settings.Paused ? "LogAllPaused" : "LogResumed"));
        _engine.SaveAndApply();
    }

    private void StyleMenu()
    {
        var palette = new MenuPalette(ThemeManager.IsDark);
        _menu.Renderer = new FluentMenuRenderer(palette);
        _menu.BackColor = palette.Background;
        bool paused = _engine.Settings.Paused;
        _openItem.Text = Loc.T("TrayOpen");
        _exitItem.Text = Loc.T("TrayExit");
        _pauseItem.Text = Loc.T(paused ? "TrayResume" : "TrayPauseAll");
        SetGlyph(_openItem, "", palette);
        SetGlyph(_pauseItem, paused ? Glyphs.Play : Glyphs.Pause, palette);
        SetGlyph(_exitItem, "", palette);
        _openItem.Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold);
    }

    private static void SetGlyph(ToolStripMenuItem item, string glyph, MenuPalette palette)
    {
        var old = item.Image;
        item.Image = GlyphBitmap(glyph, palette.Text);
        old?.Dispose();
    }

    private static Bitmap GlyphBitmap(string glyph, Color color)
    {
        const int size = 16;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(IconFontName(), 11f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), fmt);
        return bmp;
    }

    private static string? _iconFont;

    private static string IconFontName()
    {
        if (_iconFont != null)
            return _iconFont;
        using var fonts = new InstalledFontCollection();
        _iconFont = fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        return _iconFont;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Скругление углов меню на Windows 11 (DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND).</summary>
    private static void RoundCorners(IntPtr hwnd)
    {
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    public void Dispose()
    {
        _engine.Changed -= OnEngineChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private sealed record MenuPalette(bool Dark)
    {
        public Color Background => Dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF9, 0xF9, 0xF9);
        public Color Border => Dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xD6, 0xD6, 0xD6);
        public Color Hover => Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEA, 0xEA, 0xEA);
        public Color Text => Dark ? Color.White : Color.FromArgb(0x1B, 0x1B, 0x1B);
        public Color Separator => Dark ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0xE0, 0xE0, 0xE0);
    }

    private sealed class FluentMenuRenderer(MenuPalette palette) : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(palette.Background);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(palette.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
                return;
            var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using var path = Rounded(r, 4);
            using var brush = new SolidBrush(palette.Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = palette.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(palette.Separator);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
