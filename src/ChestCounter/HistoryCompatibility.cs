using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using FortniteReplayReader.Models;

namespace ChestCounter;

public readonly record struct ReportedTotals(int Matches, int Chests);
public sealed record SquadmateChestCount(string? PlayerName, int ChestsOpened);

/// <summary>Compatibility with the customized 1.4 history; never synthesizes replay matches.</summary>
public static class HistoryCompatibility
{
    public const int FORMAT_VERSION = 1;
    private const string REPORTED_FILE = "reported-totals.json";
    private const string BACKUP_FOLDER = "backup-before-combined";
    private static readonly string[] HISTORY_FILES = ["data.json", "config.json", REPORTED_FILE, "treasures.json"];

    public static void Backup(Store store)
    {
        var folder = Path.Combine(store.Folder, BACKUP_FOLDER);
        if (File.Exists(Path.Combine(folder, "complete.json"))) return;
        Directory.CreateDirectory(folder);
        foreach (var name in HISTORY_FILES)
        {
            var source = Path.Combine(store.Folder, name);
            var target = Path.Combine(folder, name);
            if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
        }
        File.WriteAllText(Path.Combine(folder, "complete.json"),
            JsonSerializer.Serialize(new { Format = FORMAT_VERSION, SavedUtc = DateTime.UtcNow }));
    }

    public static Config? LoadConfig(Store store, Config? config)
    {
        if (config == null || config.CombinedFormat >= FORMAT_VERSION) return config;
        Backup(store);
        using var doc = JsonDocument.Parse(File.ReadAllText(store.ConfigPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("UiLanguage", out _) && root.TryGetProperty("Language", out var language)
            && language.ValueKind == JsonValueKind.String) config.Language = language.GetString();
        if (!root.TryGetProperty("OverlayVisible", out _) && root.TryGetProperty("ShowOverlay", out var overlay)
            && overlay.ValueKind is JsonValueKind.True or JsonValueKind.False) config.OverlayVisible = overlay.GetBoolean();
        var isLegacy = root.TryGetProperty("IsSquadOverlayEnabled", out var team);
        if (isLegacy && team.ValueKind is JsonValueKind.True or JsonValueKind.False)
            config.CountTeammateChests = team.GetBoolean();
        if (isLegacy) config.ShowTeammates = true;
        config.UpdateMode = "off";
        config.CombinedFormat = FORMAT_VERSION;
        store.SaveConfig(config);
        return config;
    }

    public static TrackerData LoadData(Store store, TrackerData? data)
    {
        data ??= new TrackerData { AnalyzerVersion = ReplayAnalyzer.CurrentVersion };
        Validate(data);
        var hasChanged = false;
        foreach (var match in data.Matches)
        {
            if (match.Teammates != null || match.Squadmates == null) continue;
            match.Teammates = match.Squadmates.Select(m => new TeammateRecord
            {
                Name = m.PlayerName ?? L.T("Compañero", "Teammate"),
                ChestsSeen = m.ChestsOpened,
            }).ToList();
            hasChanged = true;
        }
        data.ReportedByProfile = LoadReported(store);
        if (!hasChanged) return data;
        Backup(store);
        store.SaveData(data);
        return data;
    }

    private static void Validate(TrackerData data)
    {
        if (data.Matches == null || data.ProcessedFiles == null)
            throw new InvalidDataException("El historial no tiene un formato válido. Se conserva el original.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in data.Matches)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.SessionId) || !ids.Add(m.SessionId)
                || m.ChestsOpened < 0 || m.Chests == null || m.TreasureFinds == null
                || m.OtherInteractions == null || m.TeamTreasureFinds == null
                || m.Teammates?.Any(t => t == null || t.ChestsSeen < 0 || t.Kinds == null) == true
                || m.Squadmates?.Any(t => t == null || t.ChestsOpened < 0) == true)
                throw new InvalidDataException("El historial no pasó la validación. Se conserva el original.");
        }
    }

    private static Dictionary<string, ReportedTotals> LoadReported(Store store)
    {
        var path = Path.Combine(store.Folder, REPORTED_FILE);
        if (!File.Exists(path)) return new();
        try
        {
            var totals = JsonSerializer.Deserialize<Dictionary<string, ReportedTotals>>(File.ReadAllText(path));
            if (totals == null || totals.Any(t => string.IsNullOrWhiteSpace(t.Key)
                || t.Value.Matches <= 0 || t.Value.Chests < 0))
                throw new InvalidDataException("Registro de otro PC inválido.");
            return totals;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo validar el registro del otro PC", ex);
            throw new InvalidDataException("No se pudo leer el registro del otro PC. Se conserva el original.", ex);
        }
    }

    public static ReportedTotals Reported(TrackerData view) => new(
        view.ReportedByProfile.Values.Sum(t => t.Matches), view.ReportedByProfile.Values.Sum(t => t.Chests));

    public static ReportedTotals Reported(TrackerData data, Config config)
    {
        var key = Profiles.Current(data, config)?.Key ?? config.Profile ?? "";
        return data.ReportedByProfile.GetValueOrDefault(key);
    }

    public static double Average(TrackerData view)
    {
        var external = Reported(view);
        var count = view.Matches.Count + external.Matches;
        return count > 0 ? (double)(view.Matches.Sum(m => m.ChestsOpened) + external.Chests) / count : 0;
    }

    public static string ReportedNote(TrackerData view)
    {
        var totals = Reported(view);
        return totals.Matches == 0 ? "" : L.F(
            "Otro PC: {0} cofres en {1} partidas. Incluidos en el total y la media; sin inventar fechas ni tesoros.",
            "Other PC: {0} chests in {1} matches. Included in totals and averages, without invented dates or treasures.",
            L.N(totals.Chests), L.N(totals.Matches));
    }

    public static string TeamTreasures(IReadOnlyList<MatchRecord> matches)
    {
        var finds = matches.SelectMany(m => m.TeamTreasureFinds.Select(f => (m, f))).ToList();
        if (finds.Count == 0) return "";
        string H(string? text) => WebUtility.HtmlEncode(text ?? "");
        var sb = new StringBuilder("<details class=\"card pad\"><summary>");
        sb.Append(H(L.T("Tesoros del equipo (parcial)", "Team treasures (partial)")));
        sb.Append("</summary><table><thead><tr><th>Fecha</th><th>Compañero</th><th>Tesoro</th><th>Momento</th></tr></thead><tbody>");
        foreach (var (m, f) in finds)
            sb.Append($"<tr><td>{H(L.DateTimeShort((m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime()))}</td>"
                + $"<td>{H(f.Teammate)}</td><td>{H(f.DisplayName)}</td><td>{H(Pretty.Obtained(f))}</td></tr>");
        return sb.Append("</tbody></table></details>").ToString();
    }

    public static string ReportedCsv(TrackerData data)
    {
        var sb = new StringBuilder();
        foreach (var (key, totals) in data.ReportedByProfile)
        {
            var name = data.Matches.Where(m => Profiles.Key(m) == key).Select(m => m.PlayerName).FirstOrDefault() ?? "";
            name = name.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
            if (name.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') name = "'" + name;
            sb.AppendLine($";Otro PC;{name};;;{totals.Chests};;;reported-totals.json;{totals.Matches}");
        }
        return sb.ToString();
    }

    public static void ConfigureWindow(Form form, Monitor monitor, Control total, Control totalCaption, Control overlay)
    {
        form.KeyPreview = true;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.E || e.Modifiers != Keys.None || form.ActiveControl is TextBoxBase) return;
            monitor.SetCountTeammates(!monitor.Config.CountTeammateChests);
            e.Handled = true;
        };
        total.Cursor = Cursors.Hand;
        total.Click += (_, _) => monitor.SetCountTeammates(!monitor.Config.CountTeammateChests);
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Alternar personal / equipo (E)", "Toggle personal / team (E)"), null,
            (_, _) => monitor.SetCountTeammates(!monitor.Config.CountTeammateChests));
        menu.Items.Add(new ToolStripSeparator());
        foreach (var style in NativeOverlay.Styles)
        {
            var selected = style;
            menu.Items.Add(style == "hud" ? "HUD transparente" : "Clásico", null,
                (_, _) => monitor.SetOverlayStyle(selected));
        }
        overlay.ContextMenuStrip = menu;
        var tip = new ToolTip();
        tip.SetToolTip(total, L.T("Clic o E: personal / equipo", "Click or E: personal / team"));
        tip.SetToolTip(overlay, L.T("Clic derecho: estilo y conteo del overlay", "Right-click: overlay style and count"));
        monitor.Changed += state =>
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            try { form.BeginInvoke(new Action(() => totalCaption.Text = OverlayModel.TotalCaption(monitor.Config.CountTeammateChests))); }
            catch (InvalidOperationException ex) { ErrorLog.Write("Ventana cerrándose", ex); }
        };
        form.Disposed += (_, _) => { menu.Dispose(); tip.Dispose(); };
    }
}
