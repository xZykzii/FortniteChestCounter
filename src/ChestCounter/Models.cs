using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter;

public sealed class ChestRecord
{
    public float TimeSeconds { get; set; }
    public string Kind { get; set; } = "";
    public string Blueprint { get; set; } = "";
    /// <summary>True when the chest was destroyed by the player instead of being opened.</summary>
    public bool Destroyed { get; set; }
    public List<string> Items { get; set; } = new();
    public List<string> TreasureItems { get; set; } = new();
}

public sealed class TreasureFind
{
    /// <summary>Name to display (re-resolved from treasures.json so renaming updates old matches).</summary>
    public string DisplayName => PrizeId != null ? TreasureNames.Friendly(PrizeId) : L.Name(Item);

    public string Item { get; set; } = "";
    public string RawItem { get; set; } = "";
    /// <summary>Prize id sent by the server with the golden chest, e.g. "DelilahPrize:dpid_item4".</summary>
    public string? PrizeId { get; set; }
    /// <summary>True when detected from the golden treasure chest itself.</summary>
    public bool FromGoldenChest { get; set; }
    /// <summary>True when detected only from the prize handler (exact time, item unknown unless sent at that moment).</summary>
    public bool FromPrizeHandler { get; set; }
    /// <summary>True when the time is exact (golden chest or prize handler), false when estimated.</summary>
    public bool TimeExact { get; set; }
    /// <summary>True when the source chest was destroyed rather than opened.</summary>
    public bool SourceDestroyed { get; set; }
    public string? Teammate { get; set; }
    public string SourceLabel => SourceDestroyed ? $"{L.Name(SourceKind)} ({L.T("destruido", "destroyed")})" : L.Name(SourceKind);
    /// <summary>When it was obtained (replay seconds). Exact for golden chests, estimated otherwise.</summary>
    public float? ObtainedAtSeconds { get; set; }
    /// <summary>Kind of container / interaction it came from (Chest, Loot cannon...).</summary>
    public string SourceKind { get; set; } = "";
    public string SourceBlueprint { get; set; } = "";
    public bool SourceIsChest { get; set; } = true;
    /// <summary>True when the source is known for sure (golden chest), false when it is the last interaction before use.</summary>
    public bool SourceCertain { get; set; }
    /// <summary>For estimated sources: other (non-chest) interactions between the source and the first use.</summary>
    public List<string> AlsoPossible { get; set; } = new();
}

/// <summary>
/// A teammate of the tracked player in one match. Only what happens near the recording player is in the replay
/// (the game does not send what happens far away), so <see cref="ChestsSeen"/> is a minimum, never a total.
/// </summary>
public sealed class TeammateRecord
{
    public string Name { get; set; } = "";
    public string? EpicId { get; set; }
    /// <summary>Chests this teammate was seen opening or destroying.</summary>
    public int ChestsSeen { get; set; }
    /// <summary>Everything they were seen searching, by kind (chests, ammo boxes, food boxes, loot cannons).</summary>
    public Dictionary<string, int> Kinds { get; set; } = new();
}

public sealed class MatchRecord
{
    public string SessionId { get; set; } = "";
    public string ReplayFile { get; set; } = "";
    public DateTime AnalyzedAtUtc { get; set; }
    public DateTime? MatchStartUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Playlist { get; set; }
    public string? GameVersion { get; set; }
    public string? PlayerName { get; set; }
    public string? EpicId { get; set; }
    public bool TrackedByUsername { get; set; }
    /// <summary>Set when none of the configured names played this match (name of the replay owner).</summary>
    public string? NotTrackedOwner { get; set; }
    public bool IsReplayOwner { get; set; }
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int? Eliminations { get; set; }
    public string? Map { get; set; }
    public bool IsCustomMatch { get; set; }
    public List<SeenPrize> SeenPrizes { get; set; } = new();
    public List<TeamChestCount> TeamChests { get; set; } = new();
    public int TeamChestsOpened { get; set; }
    public int VehicleSeconds { get; set; }
    public int VehicleMeters { get; set; }
    public int UnrecordedVehicleLoot { get; set; }
    private const int MISSING_VEHICLE_PILES = 3;
    [JsonIgnore] public bool HasVehicleChestsMissing => UnrecordedVehicleLoot >= MISSING_VEHICLE_PILES;
    public int ChestsOpened { get; set; }
    public int AmmoBoxesOpened { get; set; }
    public int OtherContainersOpened { get; set; }
    /// <summary>Non-chest interactions by kind (loot cannons, ammo boxes, food boxes...).</summary>
    public Dictionary<string, int> OtherInteractions { get; set; } = new();
    public int AnalyzerVersion { get; set; } = 1;
    public bool IsBattleRoyale { get; set; } = true;
    public List<ChestRecord> Chests { get; set; } = new();
    public List<TreasureFind> TreasureFinds { get; set; } = new();
    /// <summary>Teammates of the match (empty in solo). Null for matches analyzed before analyzer version 13.</summary>
    public List<TeammateRecord>? Teammates { get; set; }
    public List<SquadmateChestCount>? Squadmates { get; set; }
    public List<TreasureFind> TeamTreasureFinds { get; set; } = new();
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    /// <summary>How many non-chest interactions of a kind (ammo box, food box, loot cannon) this match has.</summary>
    public int Interactions(string kind) => OtherInteractions.Count > 0
        ? OtherInteractions.GetValueOrDefault(kind)
        : kind == ChestKinds.AmmoBox ? AmmoBoxesOpened : 0; // old matches only kept the ammo boxes

    /// <summary>Chests a teammate was seen opening, plus the non-chest kinds the player chose to count.</summary>
    public static int Seen(TeammateRecord t, IReadOnlyCollection<string> kinds) =>
        t.ChestsSeen + kinds.Sum(k => t.Kinds.GetValueOrDefault(k));

    /// <summary>Chests the teammates of this match were seen opening (a minimum: only what happened nearby).</summary>
    public int TeammatesSeen(IReadOnlyCollection<string> kinds) => Teammates?.Sum(t => Seen(t, kinds)) ?? 0;

    /// <summary>Teammates' chests added to <see cref="ChestsOpened"/> by the setting (only on adjusted copies).</summary>
    [JsonIgnore]
    public int TeammatesAdded { get; private set; }

    /// <summary>The player's own chests (with the chosen extra kinds), without the teammates' ones.</summary>
    [JsonIgnore]
    public int OwnChests => ChestsOpened - TeammatesAdded;

    /// <summary>
    /// Copy whose chest total also includes the given non-chest kinds (settings "count ammo boxes"...) and, when
    /// asked, the chests the teammates were seen opening. The saved history is never changed. Returns the same match
    /// when nothing is added.
    /// </summary>
    public MatchRecord WithExtras(IReadOnlyCollection<string> kinds, bool addTeammates = false)
    {
        var extra = kinds.Sum(Interactions);
        var mates = addTeammates ? TeammatesSeen(kinds) : 0;
        if (extra == 0 && mates == 0)
        {
            return this;
        }
        var copy = (MatchRecord)MemberwiseClone();
        copy.ChestsOpened = ChestsOpened + extra + mates;
        copy.TeammatesAdded = mates;
        return copy;
    }
}

public sealed class Config
{
    /// <summary>
    /// Language chosen in Settings ("es", "en"…); null = the Windows language. Saved under a new name: the old key
    /// held "es" for everyone and would force Spanish.
    /// </summary>
    [JsonPropertyName("UiLanguage")]
    public string? Language { get; set; }

    /// <summary>Selected profile (Epic id or name of the account the player plays on); null = most recent one.</summary>
    public string? Profile { get; set; }
    public int CombinedFormat { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool SeasonBattleRoyaleOnly { get; set; } = true;
    public bool ContentUpdates { get; set; } = true;
    public string OverlayStyle { get; set; } = "hud";
    public int OverlaySize { get; set; } = 100;
    public int OverlayOpacity { get; set; } = 100;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    public string DemosFolder { get; set; } = "";

    /// <summary>Open the tool automatically when Fortnite starts (invisible standby process started with Windows).</summary>
    public bool StartWithFortnite { get; set; }

    /// <summary>
    /// "auto" (install new versions between matches, default), "prompt" (offer them) or "off" (never check GitHub).
    /// Saved under a new name since 2.0.21: everyone moved to automatic updates once; later choices are kept.
    /// </summary>
    [JsonPropertyName("Updates")]
    public string UpdateMode { get; set; } = "off";
    /// <summary>
    /// Open the report when the tool starts (off by default). Saved under a new name: the old key defaulted to on
    /// while doing nothing, so old configs must not suddenly start opening the browser.
    /// </summary>
    [JsonPropertyName("OpenReportAtStartup")]
    public bool OpenReportOnStart { get; set; }
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    /// <summary>Locked overlay = click-through, cannot be moved (Ctrl+Shift+L to toggle).</summary>
    public bool OverlayLocked { get; set; }

    /// <summary>Overlay shown on screen. Off by default: the player turns it on with the Overlay button or Ctrl+Shift+O.</summary>
    public bool OverlayVisible { get; set; }

    /// <summary>Count the chests of the match in progress (re-reads the replay being recorded).</summary>
    public bool LiveUpdate { get; set; } = true;

    /// <summary>Minimum delay between two readings of the replay in progress (seconds, 10 minimum).</summary>
    public int LiveIntervalSeconds { get; set; } = 30;

    /// <summary>Port of the local OBS page (http://localhost:port/), served while the tool is open.</summary>
    public int ObsPort { get; set; } = ObsServer.DefaultPort;

    /// <summary>
    /// Estimate of the chests opened before using the tool, per profile key ("" = no profile yet).
    /// Added to the total only (not to averages, records or per-day figures).
    /// </summary>
    public Dictionary<string, int> InitialChests { get; set; } = new();

    /// <summary>Add the ammo boxes, food boxes and loot cannons to the chest total (off by default).</summary>
    public bool CountAmmoBoxes { get; set; }
    public bool CountFoodBoxes { get; set; }
    public bool CountLootCannons { get; set; }

    /// <summary>
    /// Show the teammates section in the report (off by default): their chests come from the player's own replay,
    /// which only holds what happens nearby, so they are a minimum.
    /// </summary>
    public bool ShowTeammates { get; set; }

    /// <summary>
    /// Count only the matches played on or after this local date (null = all the replays, the default). The older
    /// matches stay saved in data.json: they are only hidden from the figures, and come back when this is cleared.
    /// </summary>
    public DateTime? CountFrom { get; set; }

    /// <summary>
    /// Add the chests the teammates were seen opening to the player's own total (off by default). Like the extra
    /// kinds, only the figures shown change: the saved history keeps the player's own chests.
    /// </summary>
    public bool CountTeammateChests { get; set; }

    /// <summary>Non-chest kinds the player chose to add to the chest total.</summary>
    public IReadOnlyCollection<string> ExtraKinds()
    {
        var kinds = new List<string>(3);
        if (CountAmmoBoxes) kinds.Add(ChestKinds.AmmoBox);
        if (CountFoodBoxes) kinds.Add(ChestKinds.FoodBox);
        if (CountLootCannons) kinds.Add(ChestKinds.Cannon);
        return kinds;
    }
}

public sealed class TrackerData
{
    /// <summary>
    /// Converts the internal names written by versions before 2.0.9 (in French) to the current ones.
    /// Returns true when something changed (the history must be saved).
    /// </summary>
    public bool UpgradeNames()
    {
        var changed = false;
        string Up(string value)
        {
            var current = L.Upgrade(value);
            changed |= !ReferenceEquals(current, value) && current != value;
            return current;
        }
        void UpList(List<string> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                list[i] = Up(list[i]);
            }
        }

        foreach (var m in Matches)
        {
            if (m.NotTrackedOwner != null)
            {
                m.NotTrackedOwner = Up(m.NotTrackedOwner);
            }
            foreach (var c in m.Chests)
            {
                c.Kind = Up(c.Kind);
                UpList(c.Items);
                UpList(c.TreasureItems);
            }
            foreach (var f in m.TreasureFinds)
            {
                f.Item = Up(f.Item);
                f.SourceKind = Up(f.SourceKind);
                UpList(f.AlsoPossible);
            }
            if (m.OtherInteractions.Keys.Any(k => L.Upgrade(k) != k))
            {
                var merged = new Dictionary<string, int>();
                foreach (var kv in m.OtherInteractions)
                {
                    var key = Up(kv.Key);
                    merged[key] = merged.GetValueOrDefault(key) + kv.Value;
                }
                m.OtherInteractions = merged;
            }
        }
        foreach (var key in ProcessedFiles.Where(kv => kv.Value == "autre mode").Select(kv => kv.Key).ToList())
        {
            ProcessedFiles[key] = "other mode";
            changed = true;
        }
        return changed;
    }

    [JsonIgnore]
    public Dictionary<string, ReportedTotals> ReportedByProfile { get; set; } = new();
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    public int Version { get; set; } = 1;
    public int AnalyzerVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<MatchRecord> Matches { get; set; } = new();

    /// <summary>Key = file name|size|last write ticks. Value = session id, "skipped" or "error: ...".</summary>
    public Dictionary<string, string> ProcessedFiles { get; set; } = new();
}

/// <summary>Reads/writes JSON files atomically in the data folder.</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; }

    /// <summary>
    /// %APPDATA%\ChestCounter. The tool was called ChestTracker before version 1.1: its folder is moved
    /// (history, settings and treasure names are kept).
    /// </summary>
    public static string DefaultFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "ChestCounter");
        var old = Path.Combine(appData, "ChestTracker");
        try
        {
            if (!Directory.Exists(folder) && File.Exists(Path.Combine(old, "data.json")))
            {
                Directory.Move(old, folder);
            }
        }
        catch
        {
            // the old folder is in use (old version still running): start fresh, nothing is lost
        }
        return folder;
    }

    public string ConfigPath => Path.Combine(Folder, "config.json");

    public string DataPath => Path.Combine(Folder, "data.json");
    public string ReportPath => Path.Combine(Folder, "report.html");
    public string OverlayPath => Path.Combine(Folder, "overlay.html");
    public string CsvPath => Path.Combine(Folder, "matches.csv");
    public string PrizeNamesPath => Path.Combine(Folder, "treasures.json");
    public string ErrorLogPath => Path.Combine(Folder, "errors.log");

    /// <summary>Premios conocidos de la caza del tesoro (semana 1). Los ids nuevos se añaden solos con un nombre vacío.</summary>
    public static Dictionary<string, string> DefaultPrizeNames => L.Code != "es"
        ? new()
        {
            ["DelilahPrize:dpid_item1"] = "A$AP Rocky solid gold gauntlets (pickaxe) ?",
            ["DelilahPrize:dpid_item2"] = "Plunder in Paradise (loading screen)",
            ["DelilahPrize:dpid_item3"] = "Mercedes-AMG G 63 (vehicle) ?",
            ["DelilahPrize:dpid_item4"] = "No Trespassing (music track)",
        }
        : new()
        {
            ["DelilahPrize:dpid_item1"] = "Guanteletes de A$AP Rocky de oro macizo (pico) ?",
            ["DelilahPrize:dpid_item2"] = "Plunder in Paradise (pantalla de carga)",
            ["DelilahPrize:dpid_item3"] = "Mercedes-AMG G 63 (vehículo) ?",
            ["DelilahPrize:dpid_item4"] = "No Trespassing (pista musical)",
        };

    public void LoadPrizeNames()
    {
        var names = Load<Dictionary<string, string>>(PrizeNamesPath);
        if (names == null)
        {
            names = DefaultPrizeNames;
            Save(PrizeNamesPath, names);
        }

        TreasureNames.PrizeNames.Clear();
        foreach (var kv in names)
        {
            TreasureNames.PrizeNames[kv.Key] = kv.Value;
        }
    }

    /// <summary>Adds unknown prize ids to treasures.json so the user can name them. Returns the new ids.</summary>
    public List<string> RegisterPrizeIds(IEnumerable<string> ids)
    {
        var added = ids.Where(id => !TreasureNames.PrizeNames.ContainsKey(id)).Distinct().ToList();
        if (added.Count == 0)
        {
            return added;
        }

        var names = Load<Dictionary<string, string>>(PrizeNamesPath) ?? DefaultPrizeNames;
        foreach (var id in added)
        {
            names[id] = "";
            TreasureNames.PrizeNames[id] = "";
        }
        Save(PrizeNamesPath, names);
        return added;
    }

    public Store(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        MigrateFileNames();
    }

    /// <summary>Versions before 1.1 used other file names: keep the user's treasure names, drop the old generated pages.</summary>
    private void MigrateFileNames()
    {
        try
        {
            var oldPrizes = Path.Combine(Folder, "tresors.json");
            if (File.Exists(oldPrizes) && !File.Exists(PrizeNamesPath))
            {
                File.Move(oldPrizes, PrizeNamesPath);
            }
            foreach (var generated in new[] { "rapport.html", "parties.csv" })
            {
                var path = Path.Combine(Folder, generated);
                if (File.Exists(path))
                {
                    File.Delete(path); // regenerated as report.html / matches.csv
                }
            }
        }
        catch
        {
            // not critical: the old files simply stay there
        }
    }

    public Config? LoadConfig() => HistoryCompatibility.LoadConfig(this, Load<Config>(ConfigPath));
    public void SaveConfig(Config config) => Save(ConfigPath, config);
    /// <summary>A new history starts at the current analysis version (nothing to re-analyze).</summary>
    public TrackerData LoadData() => HistoryCompatibility.LoadData(this, Load<TrackerData>(DataPath));
    public void SaveData(TrackerData data) => Save(DataPath, data);

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            // Keep a copy of the unreadable file rather than silently losing the history.
            var backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
            ErrorLog.Write($"Archivo ilegible: {path}", ex);

            throw new InvalidDataException("No se pudo leer un archivo de datos. Se conserva el original.", ex);
        }
    }

    private static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
