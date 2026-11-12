using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Gráfico de barras verticales con los cofres de cada día. Las barras se reparten en el ancho disponible.</summary>
[SupportedOSPlatform("windows")]
public sealed class DailyChart : Control
{
    private IReadOnlyList<DayChests> _days = Array.Empty<DayChests>();

    public DailyChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    public void SetDays(IReadOnlyList<DayChests> days)
    {
        _days = days;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (_days.Count == 0)
        {
            return;
        }

        // La fuente del control se escala con la ventana; la negrita es solo para las cifras.
        using var fontValue = new Font(Font, FontStyle.Regular);
        using var fontDay = new Font(Font, FontStyle.Regular);

        // Las franjas de texto y las barras salen del alto de la fuente: con medidas fijas, al maximizar las cifras se cortaban.
        // A 9 pt y 96 ppp equivalen a las de siempre (cifra 18, día 16, barra 28).
        var lineH = fontDay.GetHeight(g);
        var valueH = fontValue.GetHeight(g) + 2f;
        var dayH = lineH;
        var max = _days.Max(d => d.Chests);
        var slot = (float)Width / _days.Count;
        var barWidth = Math.Min(slot - 6f, lineH * 1.75f);
        var radius = barWidth / 7f;
        var baseline = Height - dayH - 4f;          // línea base de las barras
        var maxBar = Math.Max(0f, baseline - valueH - 6f); // altura máxima de una barra
        using var brushPast = new SolidBrush(Theme.Blue);
        using var brushToday = new SolidBrush(Theme.Cyan);
        using var brushEmpty = new SolidBrush(Theme.Line);
        using var brushValue = new SolidBrush(Theme.Ink);
        using var brushMuted = new SolidBrush(Theme.Muted);
        using var centerFar = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        using var centerNear = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

        for (var i = 0; i < _days.Count; i++)
        {
            var d = _days[i];
            var isToday = i == _days.Count - 1;
            var x = i * slot;
            var cx = x + slot / 2f;
            var h = max > 0 ? d.Chests / (float)max * maxBar : 0f;

            if (d.Chests > 0)
            {
                h = Math.Max(h, 4f);
                var bar = new RectangleF(cx - barWidth / 2f, baseline - h, barWidth, h);
                using var path = Shape.Rounded(bar, Math.Min(radius, h / 2f));
                g.FillPath(isToday ? brushToday : brushPast, path);
            }
            else
            {
                g.FillRectangle(brushEmpty, cx - barWidth / 2f, baseline - 2f, barWidth, 2f);
            }

            // Cifra encima de cada barra.
            g.DrawString(L.N(d.Chests), fontValue, brushValue,
                new RectangleF(x, baseline - h - valueH - 2f, slot, valueH), centerFar);

            // Día abreviado debajo (lun, mar…).
            var name = d.Day.ToString("ddd", L.Culture);
            if (name.Length > 3)
            {
                name = name[..3];
            }
            g.DrawString(name, fontDay, isToday ? brushValue : brushMuted,
                new RectangleF(x, baseline + 4f, slot, dayH), centerNear);
        }
    }
}
