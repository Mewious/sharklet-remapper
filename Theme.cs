using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TailwindRemapper;

// Sharklet look, taken from the in-game tablet and main menu (Figma file ij2HgE2paaxrejSbIktSws).
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(13, 16, 21);
    public static readonly Color Card = Color.FromArgb(17, 20, 24);
    public static readonly Color CardBorder = Color.FromArgb(38, 41, 46);
    public static readonly Color Field = Color.FromArgb(27, 30, 35);
    public static readonly Color FieldHover = Color.FromArgb(37, 40, 46);
    public static readonly Color Text = Color.White;
    public static readonly Color MutedText = Color.FromArgb(150, 153, 160);
    public static readonly Color DisabledText = Color.FromArgb(70, 72, 75);
    public static readonly Color Accent = Color.FromArgb(255, 0, 117);
    public static readonly Color AccentEnd = Color.FromArgb(255, 3, 145);
    public static readonly Color Selection = Color.FromArgb(74, 14, 46);

    private static readonly PrivateFontCollection fonts = LoadFonts();
    public static readonly FontFamily Display = fonts.Families.Length > 0 ? fonts.Families[0] : new FontFamily("Segoe UI");
    public static readonly Font Body = new("Segoe UI", 10);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 10);

    public static Font DisplayFont(float size) => new(Display, size, FontStyle.Bold);

    // Montserrat Bold ships inside the exe (SIL Open Font License). GDI also needs it registered
    // for labels, which draw text through GDI rather than GDI+.
    private static PrivateFontCollection LoadFonts()
    {
        var collection = new PrivateFontCollection();
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("TailwindRemapper.Montserrat-Bold.ttf");
        if (stream == null) return collection;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var memory = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        collection.AddMemoryFont(memory, bytes.Length);
        uint added = 0;
        AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, ref added);
        return collection;
    }

    public static void ApplyWindow(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = Body;
        form.HandleCreated += (_, _) =>
        {
            var dark = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
            var caption = ColorTranslator.ToWin32(Background);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
        };
    }

    // Gives the standard controls inside a window the dark look; custom controls draw themselves.
    public static void Apply(Control root)
    {
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Field;
                    combo.ForeColor = Text;
                    DarkScrollbars(combo);
                    break;
                case TextBox text when text.BackColor != Color.Black:
                    text.BorderStyle = BorderStyle.FixedSingle;
                    text.BackColor = Field;
                    text.ForeColor = Text;
                    break;
                case NumericUpDown number:
                    number.BorderStyle = BorderStyle.FixedSingle;
                    number.BackColor = Field;
                    number.ForeColor = Text;
                    break;
                case CheckBox check:
                    check.FlatStyle = FlatStyle.Flat;
                    check.FlatAppearance.BorderColor = CardBorder;
                    check.FlatAppearance.CheckedBackColor = Accent;
                    check.ForeColor = Text;
                    break;
                case DataGridView grid:
                    DarkScrollbars(grid);
                    break;
            }
            Apply(control);
        }
    }

    public static void DarkScrollbars(Control control)
    {
        void Set(Control target)
        {
            if (target.IsHandleCreated) SetWindowTheme(target.Handle, "DarkMode_Explorer", null);
            else target.HandleCreated += (_, _) => SetWindowTheme(target.Handle, "DarkMode_Explorer", null);
        }
        Set(control);
        foreach (Control child in control.Controls) if (child is ScrollBar) Set(child);
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr font, uint length, IntPtr reserved, ref uint fonts);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subApp, string? idList);
}

// Main menu GlowButton in WinForms: pink gradient for primary actions, dark field otherwise.
internal sealed class SharkletButton : Button
{
    private bool hovered;
    private bool pressed;

    public bool Primary { get; set; }

    public SharkletButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.DisplayFont(9.5f);
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        AutoSize = true;
        Padding = new Padding(12, 7, 12, 7);
        Margin = new Padding(0, 0, 8, 8);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text.ToUpperInvariant(), Font);
        return new Size(text.Width + Padding.Horizontal, text.Height + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Parent?.BackColor ?? Theme.Background);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(bounds, 6);
        var fade = !Enabled ? 0.45 : pressed ? 0.8 : 1.0;
        Color Shade(Color color) => Color.FromArgb((int)(color.R * fade), (int)(color.G * fade), (int)(color.B * fade));
        if (Primary)
        {
            using var fill = new LinearGradientBrush(bounds, Shade(Theme.Accent), Shade(Theme.AccentEnd), LinearGradientMode.Vertical);
            graphics.FillPath(fill, path);
            if (hovered && Enabled)
            {
                using var ring = new Pen(Color.FromArgb(200, Color.White), 1.5f);
                graphics.DrawPath(ring, path);
            }
        }
        else
        {
            using var fill = new SolidBrush(Shade(hovered && Enabled ? Theme.FieldHover : Theme.Field));
            graphics.FillPath(fill, path);
            using var border = new Pen(hovered && Enabled ? Theme.Accent : Theme.CardBorder);
            graphics.DrawPath(border, path);
        }
        TextRenderer.DrawText(graphics, Text.ToUpperInvariant(), Font, Rectangle.Round(bounds), Enabled ? ForeColor : Theme.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(120, Color.White)) { DashStyle = DashStyle.Dot };
            using var inner = Theme.Rounded(RectangleF.Inflate(bounds, -3, -3), 4);
            graphics.DrawPath(focus, inner);
        }
    }
}

// Tablet card: rounded, near-black, faint white border.
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Parent?.BackColor ?? Theme.Background);
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 16);
        using var fill = new SolidBrush(Theme.Card);
        graphics.FillPath(fill, path);
        using var border = new Pen(Theme.CardBorder);
        graphics.DrawPath(border, path);
    }
}
