using System.Drawing;
using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>Icono de la ventana: el mismo icono de cofre que lleva el exe (ApplicationIcon).</summary>
[SupportedOSPlatform("windows")]
internal static class AppIcon
{
    public static Icon? Load()
    {
        try
        {
            return Branding.WindowIcon();
        }
        catch
        {
            return null; // sin icono: Windows usa el genérico
        }
    }
}
