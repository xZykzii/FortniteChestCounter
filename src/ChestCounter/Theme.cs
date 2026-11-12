using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Paleta y estilo de la interfaz: grafito con un acento dorado.</summary>
[SupportedOSPlatform("windows")]
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(20, 20, 21);        // fondo
    public static readonly Color Card = Color.FromArgb(25, 25, 27);      // tarjetas
    public static readonly Color CardEdge = Color.FromArgb(46, 46, 49);  // borde de tarjeta
    public static readonly Color Panel = Color.FromArgb(35, 35, 37);     // botones y campos
    public static readonly Color PanelHover = Color.FromArgb(46, 46, 49);
    public static readonly Color Line = Color.FromArgb(48, 48, 51);
    public static readonly Color Ink = Color.FromArgb(237, 237, 237);
    public static readonly Color Muted = Color.FromArgb(161, 161, 166);
    public static readonly Color Cyan = Color.FromArgb(227, 180, 97);    // cofres en total
    public static readonly Color Amber = Color.FromArgb(227, 180, 97);   // partida actual, cofres
    public static readonly Color Mint = Color.FromArgb(151, 183, 161);    // en directo
    public static readonly Color Accent = Color.FromArgb(227, 180, 97);  // acciones principales (cian)
    public static readonly Color AccentHover = Color.FromArgb(239, 200, 136);
    public static readonly Color AccentInk = Color.FromArgb(20, 20, 21);   // texto sobre el cian
    public static readonly Color AccentSoft = Color.FromArgb(227, 180, 97); // avisos
    public static readonly Color Blue = Color.FromArgb(85, 85, 91);    // barras del gráfico
    public static readonly Color Rose = Color.FromArgb(161, 161, 166);   // tesoros
    public static readonly Color Coral = Color.FromArgb(221, 121, 119);    // desactivado

    // Nombres usados por otras ventanas (mismo significado que antes).
    public static Color Gold => Amber;
    public static Color Green => Mint;
    public static Color Red => Coral;

    public static Font Text(float size = 9f, FontStyle style = FontStyle.Regular) => new("Segoe UI Variable Text", size, style);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Barra de título oscura y esquinas redondeadas de Windows 11 (en versiones antiguas se ignora).
    /// </summary>
    public static void Chrome(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // nombre anterior de la misma opción
            int round = 2;                                        // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch
        {
            // sin soporte: la ventana queda con el estilo normal
        }
    }

    /// <summary>Botón plano con el estilo de la aplicación (usado en ajustes).</summary>
    public static void Style(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Panel;
        b.ForeColor = Ink;
        b.Font = Text(9f);
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = PanelHover;
        b.FlatAppearance.MouseDownBackColor = Line;
    }
}
