using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChestCounter;
using CounterMonitor = ChestCounter.Monitor;

internal static class CombinedChecks
{
    private const int EXPECTED_ANALYZER = 17;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" }))
        {
            var root = Path.Combine(Path.GetTempPath(), "ChestCounterChecks-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "fixture");
            CreateFixture(source);
            args = [source, Path.Combine(root, "checks")];
        }
        if (args.Length < 2) throw new ArgumentException("Expected snapshot and scratch folders.");
        var snapshot = Path.GetFullPath(args[0]);
        var scratch = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(scratch);
        L.Use("es");
        var (store, config, data) = Migration(snapshot, Path.Combine(scratch, "migration"));
        Totals(store, config, data);
        Filters(config, data);
        InvalidFiles(Path.Combine(scratch, "invalid"));
        Roundtrip(store, config, data);
        if (args.Length > 2) Replay(args[2], data);
        Console.WriteLine($"PASS: {_checks} checks; {data.Matches.Count} original matches preserved.");
        return 0;
    }

    private static void Check(bool hasPassed, string message)
    {
        if (!hasPassed) throw new InvalidOperationException(message);
        _checks++;
    }

    private static (Store Store, Config Config, TrackerData Data) Migration(string source, string destination)
    {
        if (Directory.Exists(destination)) throw new InvalidOperationException("Use a new scratch folder.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        var store = new Store(destination);
        store.LoadPrizeNames();
        var before = JsonNode.Parse(File.ReadAllText(store.DataPath))!;
        var originalConfig = JsonNode.Parse(File.ReadAllText(store.ConfigPath))!;
        var originalReported = File.ReadAllBytes(Path.Combine(destination, "reported-totals.json"));
        HistoryCompatibility.Backup(store);
        var config = store.LoadConfig()!;
        var data = store.LoadData();
        var after = JsonNode.Parse(File.ReadAllText(store.DataPath))!;
        CompareOriginal(before, after);
        Check(config.Language == originalConfig["Language"]!.GetValue<string>(), "Preserve Spanish language.");
        Check(config.UpdateMode == "off" && config.CombinedFormat == HistoryCompatibility.FORMAT_VERSION, "Protect combined build.");
        Check(config.OverlayVisible == (originalConfig["OverlayVisible"] ?? originalConfig["ShowOverlay"])!.GetValue<bool>(), "Preserve overlay visibility.");
        Check(config.CountTeammateChests == (originalConfig["CountTeammateChests"] ?? originalConfig["IsSquadOverlayEnabled"])!.GetValue<bool>(), "Preserve personal/team mode.");
        Check(config.ShowTeammates == (originalConfig["CombinedFormat"] != null ? originalConfig["ShowTeammates"]!.GetValue<bool>() : true), "Preserve companions preference.");
        Check(originalReported.SequenceEqual(File.ReadAllBytes(Path.Combine(destination, "reported-totals.json"))), "External totals file unchanged.");
        Check(JsonNode.DeepEquals(before, JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "backup-before-combined/data.json")))), "Backup preserves original history.");
        Check(data.Matches.All(m => m.AnalyzerVersion == EXPECTED_ANALYZER), "Keep existing analysis versions.");
        foreach (var m in data.Matches.Where(m => m.Squadmates != null))
            Check(m.Teammates!.Select(t => t.ChestsSeen).SequenceEqual(m.Squadmates!.Select(t => t.ChestsOpened)), "Map all squad counts.");
        return (store, config, data);
    }

    private static void CompareOriginal(JsonNode before, JsonNode after)
    {
        var oldMatches = before["Matches"]!.AsArray();
        var newMatches = after["Matches"]!.AsArray();
        Check(oldMatches.Count == newMatches.Count, "No lost or added matches.");
        foreach (var old in oldMatches)
        {
            var current = newMatches.Single(m => m!["SessionId"]!.GetValue<string>() == old!["SessionId"]!.GetValue<string>())!;
            foreach (var pair in old!.AsObject())
                Check(JsonNode.DeepEquals(Factual(pair.Value), Factual(current[pair.Key])), $"Preserve original field {pair.Key}.");
        }
        foreach (var pair in before.AsObject().Where(p => p.Key != "Matches"))
            Check(JsonNode.DeepEquals(pair.Value, after[pair.Key]), $"Preserve history field {pair.Key}.");
    }

    private static JsonNode? Factual(JsonNode? value)
    {
        var copy = value?.DeepClone();
        if (copy is JsonArray list)
        {
            for (var i = 0; i < list.Count; i++) list[i] = Factual(list[i]);
        }
        if (copy is not JsonObject obj) return copy;
        obj.Remove("DisplayName");
        obj.Remove("SourceLabel");
        foreach (var key in obj.Select(p => p.Key).ToList()) obj[key] = Factual(obj[key]);
        return copy;
    }

    private static void Totals(Store store, Config config, TrackerData data)
    {
        var tracker = new Tracker(store, config, data);
        var view = Profiles.View(data, config);
        var reported = HistoryCompatibility.Reported(view);
        var expectedReported = JsonSerializer.Deserialize<Dictionary<string, ReportedTotals>>(File.ReadAllText(Path.Combine(store.Folder, "reported-totals.json")))!;
        Check(reported == expectedReported[config.Profile!], "Keep the reported other-PC record.");
        var actualChests = view.Matches.Sum(m => m.ChestsOpened);
        var expectedTotal = actualChests + reported.Chests;
        var expectedMatches = view.Matches.Count + reported.Matches;
        var overlay = OverlayModel.Build(view, null, false, Profiles.InitialChests(data, config));
        Check(overlay.Total == L.N(expectedTotal), "Overlay includes the external total exactly once.");
        var hasShownTeammates = config.ShowTeammates;
        config.ShowTeammates = true;
        var report = ReportWriter.Report(config, view, Profiles.List(data, config), Profiles.InitialChests(data, config));
        config.ShowTeammates = hasShownTeammates;
        Check(report.Contains("Otro PC:") && report.Contains($"{reported.Matches} partidas"), "Report explains external record.");
        Check(report.Contains(expectedTotal.ToString("#,0", L.Culture)), "Report shows preserved total.");
        Check(report.Contains($"{view.Matches.Count} locales + {reported.Matches} otro PC"), "Report explains match counts.");
        Check(report.Contains("tab-companeros") && report.Contains("Top"), "Keep teammate rankings.");
        var expectedAverage = (double)expectedTotal / expectedMatches;
        Check(Math.Abs(HistoryCompatibility.Average(view) - expectedAverage) < double.Epsilon, "Correct lifetime average.");
        var monitor = new CounterMonitor(store, config, data, tracker, store.Folder);
        var stats = (ChestStats)typeof(CounterMonitor).GetMethod("BuildStats", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(monitor, null)!;
        Check(stats.HistoryMatches == expectedMatches && stats.Matches == expectedMatches, "Window includes reported matches.");
        Check(stats.Average == expectedAverage, "Window and report agree on average.");
        var sourceChests = data.Matches.Sum(m => m.ChestsOpened);
        config.CountTeammateChests = true;
        var teamView = Profiles.View(data, config);
        var teamOverlay = OverlayModel.Build(teamView, null, false, Profiles.InitialChests(data, config));
        var expectedTeam = expectedTotal + view.Matches.Sum(m => m.TeammatesSeen(config.ExtraKinds()));
        Check(teamOverlay.Total == L.N(expectedTeam), "Team mode counts personal + observed squad + external once.");
        Check(data.Matches.Sum(m => m.ChestsOpened) == sourceChests, "Switching modes never alters personal history.");
        config.CountTeammateChests = false;
        var csv = ReportWriter.Csv(data);
        Check(csv.Contains($";{reported.Chests};;;reported-totals.json;{reported.Matches}"), "CSV preserves reported amount and match count.");
        var other = data.Matches.Select(Profiles.Key).First(k => k != config.Profile);
        var selection = config.Profile;
        config.Profile = other;
        Check(HistoryCompatibility.Reported(Profiles.View(data, config)) == default, "No leakage into another account.");
        config.Profile = selection;
        Console.WriteLine($"Totals: personal {expectedTotal}, matches {expectedMatches}, team minimum {expectedTeam}, average {expectedAverage:0.00}.");
    }

    private static void Filters(Config config, TrackerData data)
    {
        var oldest = new MatchRecord { SessionId = "old", GameVersion = "++Fortnite+Release-42.00", ChestsOpened = 12,
            MatchStartUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
        Check(Profiles.IsSupportedVersion(oldest), "Preserve earlier-season history.");
        config.CountFrom = null;
        Check(Profiles.Counted(oldest, config), "No default date exclusion.");
        config.CountFrom = new DateTime(2026, 11, 1);
        Check(!Profiles.Counted(oldest, config), "Optional date filter still works.");
        config.CountFrom = null;
        var malicious = new MatchRecord { SessionId = "escaped", PlayerName = "<script>bad</script>",
            TeamTreasureFinds = [new TreasureFind { Item = "<script>bad</script>", Teammate = "<img src=x>" }] };
        var html = HistoryCompatibility.TeamTreasures([malicious]);
        Check(!html.Contains("<script>") && !html.Contains("<img src=x>"), "Escape all imported team names and treasures.");
        Check(html.Contains("&lt;script&gt;"), "Team treasure details remain readable.");
        Check(ReplayAnalyzer.CurrentVersion == EXPECTED_ANALYZER, "Do not force reanalysis of preserved records.");
        Check(AppInfo.IsCombinedBuild, "Combined builds disable upstream replacement.");
    }

    private static void InvalidFiles(string root)
    {
        Directory.CreateDirectory(root);
        var broken = new Store(Path.Combine(root, "broken"));
        File.WriteAllText(broken.DataPath, "{invalid");
        var didReject = false;
        try { broken.LoadData(); } catch (InvalidDataException) { didReject = true; }
        Check(didReject && File.ReadAllText(broken.DataPath) == "{invalid", "Malformed data must not become an empty history.");
        var duplicate = new Store(Path.Combine(root, "duplicate"));
        var repeated = new MatchRecord { SessionId = "repeated", ChestsOpened = 7 };
        duplicate.SaveData(new TrackerData { Matches = [repeated, repeated] });
        didReject = false;
        try { duplicate.LoadData(); } catch (InvalidDataException) { didReject = true; }
        Check(didReject, "Reject duplicate identities before modifying history.");
        var external = new Store(Path.Combine(root, "external"));
        File.WriteAllText(Path.Combine(external.Folder, "reported-totals.json"), "{\"account\":{\"Chests\":-5,\"Matches\":25}}");
        didReject = false;
        try { external.LoadData(); } catch (InvalidDataException) { didReject = true; }
        Check(didReject, "Reject invalid reported totals.");
    }

    private static void Roundtrip(Store store, Config config, TrackerData data)
    {
        store.SaveConfig(config);
        store.SaveData(data);
        var first = File.ReadAllBytes(store.DataPath);
        var restored = store.LoadData();
        store.SaveData(restored);
        Check(first.SequenceEqual(File.ReadAllBytes(store.DataPath)), "Second load/save is byte stable.");
        HistoryCompatibility.Backup(store);
        var reloaded = store.LoadConfig()!;
        Check(reloaded.CombinedFormat == config.CombinedFormat, "Migration applies only once.");
        var before = restored.Matches.Sum(m => m.ChestsOpened);
        var tracker = new Tracker(store, reloaded, restored);
        Check(!tracker.UpgradeIfNeeded(), "Existing history is not recalculated by a version mismatch.");
        Check(restored.Matches.Sum(m => m.ChestsOpened) == before, "No changed chest totals on startup.");
        ReportWriter.WriteAll(store, reloaded, restored);
        Check(File.Exists(store.ReportPath) && File.Exists(store.CsvPath), "Produce usable report and CSV.");
    }

    private static void Replay(string file, TrackerData history)
    {
        using var stream = File.OpenRead(file);
        var actual = new ReplayAnalyzer().Analyze(stream, file, null);
        var expected = history.Matches.Single(m => m.SessionId == actual.SessionId);
        Check(actual.ChestsOpened == expected.ChestsOpened, "Reference replay chest count matches installed analyzer.");
        Check(actual.Squadmates!.Sum(t => t.ChestsOpened) == expected.Squadmates!.Sum(t => t.ChestsOpened), "Reference squad count unchanged.");
        Check(actual.Teammates!.Sum(t => t.ChestsSeen) == actual.Squadmates.Sum(t => t.ChestsOpened), "New teammate model agrees with preserved squad algorithm.");
        Check(actual.TreasureFinds.Count == expected.TreasureFinds.Count, "Preserve reference personal treasures.");
        Check(actual.TeamTreasureFinds.Count == expected.TeamTreasureFinds.Count, "Preserve reference team treasures.");
        Console.WriteLine($"Reference replay: {actual.ChestsOpened} personal chests; squad and treasures agree.");
    }

    private static void CreateFixture(string folder)
    {
        Directory.CreateDirectory(folder);
        var store = new Store(folder);
        var config = new { Language = "es", Profile = "demo", ShowOverlay = true, IsSquadOverlayEnabled = false };
        File.WriteAllText(store.ConfigPath, JsonSerializer.Serialize(config));
        var data = new TrackerData { AnalyzerVersion = EXPECTED_ANALYZER };
        for (var i = 0; i < 3; i++)
        {
            var isOther = i == 2;
            data.Matches.Add(new MatchRecord
            {
                SessionId = "fixture-" + i, ReplayFile = "demo-" + i + ".replay",
                AnalyzedAtUtc = DateTime.UtcNow, MatchStartUtc = DateTime.UtcNow.AddDays(-i),
                GameVersion = "++Fortnite+Release-43.00", EpicId = isOther ? "other" : "demo",
                PlayerName = isOther ? "Otra cuenta" : "Jugador", ChestsOpened = 12 + i,
                AnalyzerVersion = EXPECTED_ANALYZER, Placement = 5, Squadmates = [new("Compañero", 7)],
            });
        }
        store.SaveData(data);
        File.WriteAllText(Path.Combine(folder, "reported-totals.json"), JsonSerializer.Serialize(new Dictionary<string, ReportedTotals> { ["demo"] = new(2, 50) }));
    }
}
