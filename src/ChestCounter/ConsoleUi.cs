namespace ChestCounter;

/// <summary>
/// Salida de consola para los modos de línea de comandos (--once, --help, --overlay-preview). La ventana
/// de la aplicación no usa la consola.
/// </summary>
public static class ConsoleUi
{
    /// <summary>True when the console can be redrawn in place (a real console, not a redirected output).</summary>
    public static bool Interactive { get; } = !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static void Text(string text, ConsoleColor? color = null)
    {
        if (color is { } c) Console.ForegroundColor = c;
        foreach (var line in text.Split('\n'))
        {
            Console.WriteLine("  " + line);
        }
        if (color != null) Console.ResetColor();
    }

    public static void Hint(string text) => Text(text, ConsoleColor.DarkGray);
}

/// <summary>One line progress bar, redrawn in place: "  Analizando  [██████░░░░]  12 / 31  archivo".</summary>
public sealed class ProgressBar
{
    private readonly string _label;
    private readonly int _total;
    private int _lastLength;

    public ProgressBar(string label, int total)
    {
        _label = label;
        _total = Math.Max(1, total);
    }

    public void Update(int done, string? detail = null)
    {
        const int barWidth = 28;
        var filled = (int)Math.Round(barWidth * Math.Clamp(done, 0, _total) / (double)_total);
        var bar = new string('█', filled) + new string('░', barWidth - filled);
        var count = $"{done} / {_total}";
        if (!ConsoleUi.Interactive)
        {
            return; // no redraw possible: the summary line at the end is enough
        }

        int width;
        try { width = Console.WindowWidth; } catch { width = 80; }
        var text = $"  {_label}  {bar}  {count}";
        if (!string.IsNullOrEmpty(detail))
        {
            var room = width - text.Length - 4;
            if (room > 8)
            {
                text += "  " + (detail.Length > room ? "…" + detail[^(room - 1)..] : detail);
            }
        }
        Console.Write("\r");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"  {_label}  ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(bar);
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(text[(4 + _label.Length + bar.Length)..]);
        Console.ResetColor();
        if (text.Length < _lastLength)
        {
            Console.Write(new string(' ', _lastLength - text.Length));
        }
        _lastLength = text.Length;
    }

    /// <summary>Erases the bar so the summary can be written on the same line.</summary>
    public void Clear()
    {
        if (!ConsoleUi.Interactive || _lastLength == 0)
        {
            return;
        }
        Console.Write("\r" + new string(' ', _lastLength) + "\r");
        _lastLength = 0;
    }
}
