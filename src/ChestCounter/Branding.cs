using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Product assets and the inventory-inspired visual language. No remote assets at runtime.</summary>
internal static class Branding
{
    private const int ICON_SIZE = 192;
    private const int ALPHA_THRESHOLD = 8;
    private static readonly Lazy<Bitmap> ChestImage = new(LoadChest);
    private static readonly Lazy<byte[]> ChestPng = new(EncodeChest);
    public static Bitmap Chest => ChestImage.Value;
    public static string ChestUri => "data:image/png;base64," + Convert.ToBase64String(ChestPng.Value);
    public static string ChestHtml => $"<img class=\"chest-art\" src=\"{ChestUri}\" alt=\"Cofre de Fortnite\">";

    private static Bitmap LoadChest()
    {
        using var source = typeof(Branding).Assembly.GetManifestResourceStream("ChestCounter.Assets.chest.png")
            ?? throw new InvalidOperationException("No se encontró el cofre de la aplicación.");
        using var original = new Bitmap(source);
        var bounds = AlphaBounds(original);
        var result = new Bitmap(ICON_SIZE, ICON_SIZE, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        var scale = Math.Min((float)ICON_SIZE / bounds.Width, (float)ICON_SIZE / bounds.Height);
        var width = bounds.Width * scale;
        var height = bounds.Height * scale;
        graphics.DrawImage(original, new RectangleF((ICON_SIZE - width) / 2, (ICON_SIZE - height) / 2, width, height), bounds, GraphicsUnit.Pixel);
        return result;
    }

    private static Rectangle AlphaBounds(Bitmap image)
    {
        var left = image.Width;
        var top = image.Height;
        var right = 0;
        var bottom = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).A <= ALPHA_THRESHOLD) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        if (left >= right || top >= bottom) throw new InvalidDataException("El cofre no contiene una imagen visible.");
        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static byte[] EncodeChest()
    {
        using var stream = new MemoryStream();
        Chest.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public static void DrawChest(IntPtr hdc, Rectangle bounds)
    {
        using var graphics = Graphics.FromHdc(hdc);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(Chest, bounds);
    }

    public static Icon WindowIcon(Size? size = null)
    {
        using var source = typeof(Branding).Assembly.GetManifestResourceStream("ChestCounter.Assets.app.ico")
            ?? throw new InvalidOperationException("No se encontró el icono de la aplicación.");
        return size is { } requested ? new Icon(source, requested) : new Icon(source);
    }

    public static Label Label(Control parent, string text, float size = 10, Color? color = null)
    {
        var label = new Label
        {
            Text = text, Font = Theme.Text(size), ForeColor = color ?? Theme.Ink,
            BackColor = Color.Transparent, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
        };
        parent.Controls.Add(label);
        return label;
    }

    public static PillButton Button(Control parent, string text, Action action, bool isPrimary = false)
    {
        var button = new PillButton
        {
            Text = text, Font = Theme.Text(10), Radius = 8, Height = 40,
            BackColor = isPrimary ? Theme.Ink : Theme.Panel,
            ForeColor = isPrimary ? Theme.Bg : Theme.Ink,
            HoverColor = isPrimary ? Color.White : Theme.PanelHover,
            Margin = new Padding(0, 0, 8, 0),
        };
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
        return button;
    }

    public static string RepositoryLabel => L.T("Repositorio en GitHub", "GitHub repository");

    public static LinkLabel RepositoryLink(Control parent)
    {
        var link = new LinkLabel
        {
            Text = RepositoryLabel + " ↗", LinkColor = Theme.Muted, ActiveLinkColor = Theme.Gold,
            VisitedLinkColor = Theme.Muted, LinkBehavior = LinkBehavior.HoverUnderline,
            Font = Theme.Text(10), TextAlign = ContentAlignment.MiddleRight,
            BackColor = Color.Transparent, AutoSize = false, TabStop = true,
        };
        link.LinkClicked += (_, _) => Open(AppInfo.RepoUrl);
        parent.Controls.Add(link);
        return link;
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo abrir el enlace o archivo", ex);
            MessageBox.Show("No se pudo abrir. Inténtalo de nuevo desde el navegador o la carpeta de datos.",
                "Contador de cofres", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    public static Card Section(Control parent, Padding? padding = null)
    {
        var section = new Card { Dock = DockStyle.Fill, Radius = 12, Padding = padding ?? new Padding(20), Margin = new Padding(0, 0, 0, 16) };
        parent.Controls.Add(section);
        return section;
    }

    public static ComboBox Select(Control parent, Point location, int width)
    {
        var select = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel, ForeColor = Theme.Ink, Font = Theme.Text(10),
            Location = location, Width = width, IntegralHeight = true,
        };
        parent.Controls.Add(select);
        return select;
    }

    public static string ReportCss => """
        :root{color-scheme:dark;--bg:#141415;--card:#19191b;--card2:#232325;--ink:#ededed;--muted:#a1a1a6;
          --faint:#85858b;--line:#303033;--cyan:#e3b461;--cyan-soft:rgba(227,180,97,.06);--amber:#e3b461;
          --mint:#97b7a1;--rose:#a1a1a6;--blue:#55555b;--line2:#303033;--shadow:none}
        html,body{background:#141415!important;color:#ededed!important;font-family:'Segoe UI Variable','Segoe UI',sans-serif}
        main{max-width:1280px;padding:40px 32px;margin:auto}*{box-shadow:none!important;animation:none!important}
        h1,h2,h3,b,strong,.v{font-weight:500!important}h1{font-size:28px;letter-spacing:0}
        .masthead{padding-bottom:24px;border-bottom:1px solid var(--line);margin-bottom:24px;gap:24px}
        .brand{gap:20px}.logo{width:88px;height:88px;border:0;background:none;box-shadow:none;border-radius:0}
        .chest-art{display:block;width:100%;height:100%;object-fit:contain}.eyebrow{font-size:13px}
        .eyebrow,.kpi .l,.head,.cap,.sh,.badge,.tabs{text-transform:none!important;letter-spacing:0!important}
        .upd{background:none;border:0;padding:0;font-size:12px}.dot{width:6px;height:6px;background:#97b7a1}
        .tabs{border-bottom:1px solid var(--line);padding:0;gap:24px;background:none;border-radius:0;overflow-x:auto;margin:0 0 24px;max-width:100%;width:100%}
        .tab{border-radius:0!important;border:0!important;background:none!important;color:var(--muted);padding:12px 0!important;font-size:14px;font-weight:400}
        .tab[aria-selected=true],.tab.active{color:var(--ink)!important;border-bottom:2px solid var(--amber)!important}
        .tab .c{background:none!important;color:var(--muted);padding:0 0 0 8px;font-size:12px}
        .kpis{gap:16px;margin-top:24px}.kpi,.card{background:var(--card)!important;border:1px solid var(--line);border-radius:12px}
        .kpi{padding:24px}.kpi.hero{background:var(--card)!important}.kpi .v{font-size:32px;letter-spacing:0}
        .kpi.hero .v{font-size:64px;color:var(--amber)}.kpi .l{font-size:13px}.kpi .s{font-size:13px;line-height:1.6}
        .kpi .ros,.kpi .mnt,.kpi .acc{color:var(--ink)}.split{gap:32px;margin-top:24px;padding-top:20px}
        section{margin-top:32px}.sh{margin-bottom:16px}h2{font-size:18px}.pad{padding:24px}
        .bar,.bar i,.bar .fill{background:#55555b!important}.col.today .bar{background:var(--amber)!important}
        .chart{grid-template-columns:40px minmax(0,1fr);overflow:hidden}.plot,.xaxis,.col,.card,.panel,section{min-width:0}.col{min-width:0}.tw{max-width:100%;min-width:0}
        table{font-size:14px}th{font-size:12px;font-weight:400!important;text-transform:none!important;letter-spacing:0!important;color:var(--muted);background:#202022}
        td,th{padding:16px!important;border-bottom:1px solid var(--line)}tr:hover{background:#232325}
        .badge,.pill{border-radius:4px!important;background:#232325!important;color:var(--muted)!important;border:0!important}
        .cy,.acc{color:var(--amber)}.mini,.mini i{border-radius:2px!important}.profiles{margin:0 0 24px}
        .btn,button,a{transition:color .16s,background .16s}button:focus-visible,a:focus-visible{outline:2px solid var(--amber);outline-offset:4px}
        footer{margin-top:40px;padding-top:24px;display:block;border-top:1px solid var(--line)}footer a{color:var(--amber)}
        @media(prefers-color-scheme:light){:root{--bg:#141415;--card:#19191b;--ink:#ededed;--muted:#a1a1a6;--line:#303033}}
        @media(max-width:700px){main{padding:24px 16px}.logo{width:60px;height:60px}.tabs{gap:20px}.kpi{padding:16px}.kpi.hero .v{font-size:48px}.split{gap:16px}td,th{padding:12px!important}}
        """;
}
