using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace ArtSport.ArtVpn.Ui;

internal static class Palette
{
    public static readonly Color Canvas = Color.FromArgb(244, 247, 251);
    public static readonly Color Ink = Color.FromArgb(25, 43, 64);
    public static readonly Color Muted = Color.FromArgb(94, 108, 126);
    public static readonly Color Blue = Color.FromArgb(50, 98, 167);
    public static readonly Color BlueSoft = Color.FromArgb(235, 241, 249);
    public static readonly Color Green = Color.FromArgb(19, 137, 91);
    public static readonly Color GreenSoft = Color.FromArgb(237, 246, 241);
    public static readonly Color Amber = Color.FromArgb(181, 115, 17);
    public static readonly Color AmberSoft = Color.FromArgb(255, 245, 222);
    public static readonly Color Red = Color.FromArgb(190, 58, 66);
    public static readonly Color RedSoft = Color.FromArgb(255, 236, 238);
    public static readonly Color Border = Color.FromArgb(220, 227, 237);
}

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Radius { get; set; } = 12;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color BorderColor { get; set; } = Palette.Border;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int BorderWidth { get; set; } = 1;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Padding = new Padding(22);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var rectangle = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(rectangle, Radius);
        using var brush = new LinearGradientBrush(rectangle, BackColor, ControlPaint.Light(BackColor, 0.06f), 35f);
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 2 || Height < 2) return;
        using var path = Rounded(new Rectangle(0, 0, Width, Height), Radius);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }

    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedButton : Button
{
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 2 || Height < 2) return;
        using var path = RoundedPanel.Rounded(new Rectangle(0, 0, Width, Height), Math.Max(4, 8 * DeviceDpi / 96));
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }
}

internal static class UiFactory
{
    public static Label Label(string text, float size = 10, bool semibold = false, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI" + (semibold ? " Semibold" : ""), size),
        ForeColor = color ?? Palette.Ink,
        BackColor = Color.Transparent,
        UseCompatibleTextRendering = false
    };

    public static Button Button(string name, string text, bool primary = false, int width = 160) => new RoundedButton
    {
        Name = name,
        Text = text,
        Width = width,
        Height = 44,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Palette.Blue : Color.White,
        ForeColor = primary ? Color.White : Palette.Ink,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Cursor = Cursors.Hand,
        Margin = new Padding(6, 0, 0, 0),
        UseVisualStyleBackColor = false,
        TabStop = true
    }.WithBorder(primary ? Palette.Blue : Palette.Border);

    private static Button WithBorder(this Button button, Color color)
    {
        button.FlatAppearance.BorderColor = color;
        button.FlatAppearance.BorderSize = 1;
        button.MouseEnter += (_, _) =>
        {
            // Selected route buttons change colour after status refresh.
            var selected = button.BackColor == Palette.Blue;
            button.FlatAppearance.MouseOverBackColor = selected ? Color.FromArgb(40, 80, 137) : Palette.BlueSoft;
            button.FlatAppearance.MouseDownBackColor = selected ? Color.FromArgb(34, 69, 119) : Color.FromArgb(220, 230, 243);
        };
        return button;
    }
}
