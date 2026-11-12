using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Figuras redondeadas para los controles dibujados a mano.</summary>
internal static class Shape
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Tarjeta con esquinas redondeadas y borde fino. Sus textos hijos usan el color de la tarjeta.</summary>
[SupportedOSPlatform("windows")]
public sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    /// <summary>Radio de las esquinas, en píxeles.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 16;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Las esquinas recortadas se ven del color del fondo de la ventana.
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Shape.Rounded(rect, Radius);
        using var fill = new SolidBrush(BackColor);
        using var edge = new Pen(Theme.CardEdge, 1f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(edge, path);
    }
}

/// <summary>Botón con esquinas redondeadas que se ilumina al pasar el ratón.</summary>
[SupportedOSPlatform("windows")]
public sealed class PillButton : Button
{
    private bool _hover;

    public PillButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        Font = Theme.Text(10f);
        Cursor = Cursors.Hand;
    }

    /// <summary>Color de fondo cuando el ratón está encima.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverColor { get; set; } = Theme.PanelHover;

    /// <summary>Radio de las esquinas, en píxeles.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 12;

    private Color? _indicator;

    /// <summary>Punto de estado delante del texto y borde del mismo color (null = sin indicador).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Indicator
    {
        get => _indicator;
        set
        {
            if (_indicator != value)
            {
                _indicator = value;
                Invalidate();
            }
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Nada: OnPaint pinta el fondo redondeado completo.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Shape.Rounded(rect, Radius);
        using var fill = new SolidBrush(_hover ? HoverColor : BackColor);
        g.FillPath(fill, path);
        if (_indicator is not { } dot)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }

        // Borde fino del color del estado y un punto delante del texto (todo centrado).
        using (var edge = new Pen(Color.FromArgb(150, dot), 1.2f))
        using (var inner = Shape.Rounded(new RectangleF(1f, 1f, Width - 2, Height - 2), Radius))
        {
            g.DrawPath(edge, inner);
        }
        var flags = TextFormatFlags.SingleLine;
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size, flags);
        var d = Math.Max(6f, Font.Height * 0.45f);
        var gap = d * 0.8f;
        var left = (Width - (d + gap + textSize.Width)) / 2f;
        using (var brush = new SolidBrush(dot))
        {
            g.FillEllipse(brush, left, (Height - d) / 2f, d, d);
        }
        var textRect = new Rectangle((int)(left + d + gap), 0, textSize.Width + 2, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, ForeColor, flags | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Interruptor redondeado (sustituye a la casilla blanca de Windows).</summary>
[SupportedOSPlatform("windows")]
public sealed class ToggleSwitch : Control
{
    private bool _checked;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(40, 22);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var track = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var path = Shape.Rounded(track, track.Height / 2))
        using (var fill = new SolidBrush(_checked ? Theme.Accent : Theme.Line))
        {
            g.FillPath(fill, path);
            if (Focused)
            {
                using var ring = new Pen(Theme.AccentSoft, 1f);
                g.DrawPath(ring, path);
            }
        }
        var d = track.Height - 6;
        var x = _checked ? track.Right - d - 3 : track.Left + 3;
        using var knob = new SolidBrush(_checked ? Theme.AccentInk : Theme.Muted);
        g.FillEllipse(knob, x, track.Top + 3, d, d);
    }
}

/// <summary>
/// Etiqueta de una sola línea que nunca se corta: si el texto no cabe en su caja (ancho o alto),
/// se dibuja con la letra justa para que quepa. Su fuente es el tamaño máximo.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FitLabel : Label
{
    private Font? _fit;       // fuente reducida propia (null: se usa la del control)
    private Font? _fitBase;   // fuente del control con la que se calculó
    private string? _fitKey;

    public FitLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var text = Text ?? "";
        if (text.Length == 0)
        {
            return;
        }
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | Align(TextAlign);
        var font = FitFont(e.Graphics, text);
        TextRenderer.DrawText(e.Graphics, text, font, ClientRectangle, Enabled ? ForeColor : SystemColors.GrayText, flags);
    }

    private Font FitFont(IDeviceContext dc, string text)
    {
        var key = $"{text}|{ClientSize}";
        if (key == _fitKey && ReferenceEquals(_fitBase, Font))
        {
            return _fit ?? Font;
        }
        var size = Font.Size;
        var box = ClientSize;
        const TextFormatFlags measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var fit = Font;
        for (var i = 0; i < 12; i++)
        {
            var m = TextRenderer.MeasureText(dc, text, fit, new Size(int.MaxValue, int.MaxValue), measure);
            if (m.Width <= box.Width && m.Height <= box.Height || size <= 5f)
            {
                break;
            }
            // Reduce lo que falte (en ancho o alto) y prueba otra vez: el redondeo de GDI puede pedir un paso más.
            var ratio = Math.Min(box.Width / (float)Math.Max(1, m.Width), box.Height / (float)Math.Max(1, m.Height));
            size = Math.Max(5f, size * Math.Min(ratio, 0.97f));
            if (!ReferenceEquals(fit, Font)) fit.Dispose();
            fit = new Font(Font.FontFamily, size, Font.Style, Font.Unit);
        }
        _fit?.Dispose();
        _fit = ReferenceEquals(fit, Font) ? null : fit;
        _fitBase = Font;
        _fitKey = key;
        return fit;
    }

    private static TextFormatFlags Align(ContentAlignment a) => a switch
    {
        ContentAlignment.TopLeft => TextFormatFlags.Top | TextFormatFlags.Left,
        ContentAlignment.TopCenter => TextFormatFlags.Top | TextFormatFlags.HorizontalCenter,
        ContentAlignment.TopRight => TextFormatFlags.Top | TextFormatFlags.Right,
        ContentAlignment.MiddleLeft => TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
        ContentAlignment.MiddleCenter => TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter,
        ContentAlignment.MiddleRight => TextFormatFlags.VerticalCenter | TextFormatFlags.Right,
        ContentAlignment.BottomLeft => TextFormatFlags.Bottom | TextFormatFlags.Left,
        ContentAlignment.BottomCenter => TextFormatFlags.Bottom | TextFormatFlags.HorizontalCenter,
        _ => TextFormatFlags.Bottom | TextFormatFlags.Right,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fit?.Dispose();
            _fit = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>Botón cuadrado redondeado con un logo centrado (redes del pie de Ajustes); se ilumina al pasar el ratón.</summary>
[SupportedOSPlatform("windows")]
public sealed class LogoButton : Control
{
    private bool _hover;

    public LogoButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Logo { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 9;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Card);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Shape.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Radius))
        using (var fill = new SolidBrush(_hover ? Theme.PanelHover : BackColor))
        {
            g.FillPath(fill, path);
        }
        if (Logo == null)
        {
            return;
        }
        // El logo ocupa algo más de la mitad del botón; se reduce con filtro bicúbico para que no se vea pixelado.
        var side = (int)Math.Round(Math.Min(Width, Height) * 0.55);
        var dest = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // sin halo en los bordes al reducir
        attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = _hover ? 1f : 0.78f });
        g.DrawImage(Logo, dest, 0, 0, Logo.Width, Logo.Height, GraphicsUnit.Pixel, attributes);
    }
}

/// <summary>Menús desplegables con los colores de la aplicación (fondo oscuro, resaltado suave, texto claro).</summary>
[SupportedOSPlatform("windows")]
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new Colors())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.ForeColor;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected)
        {
            return;
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shape.Rounded(new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 6);
        using var fill = new SolidBrush(Theme.PanelHover);
        e.Graphics.FillPath(fill, path);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.PanelHover;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Panel;
    }
}
