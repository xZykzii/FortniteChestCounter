using ChestCounter;
using System.Text.Json;

internal static class SeasonChecks
{
    public static void Run(Action<bool, string> check)
    {
        Catalog(check);
        Chances(check);
        WeeklyEligibility(check);
        Modes(check);
        RankedRecovery(check);
        Reports(check);
        Career(check);
    }

    private static void WeeklyEligibility(Action<bool, string> check)
    {
        var weeks = TreasureCatalog.Weeks;
        var current = weeks.Single(w => w.Number == 2);
        var previous = weeks.Single(w => w.Number == 1);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Chile in November", TimeSpan.FromHours(-3), "Chile", "Chile");
        var saturday = new DateOnly(2026, 11, 7);
        var sunday = saturday.AddDays(1);
        (DateOnly? Day, long Chests)[] normal = [(saturday, 1050), (sunday, 30), (sunday.AddDays(1), 3), (null, 7), (saturday.AddMonths(-1), 9)];
        DateTime[] times = [previous.From.AddTicks(-1), current.From.AddTicks(-1), current.From, current.To.AddTicks(-1), current.To, weeks.Max(w => w.To)];
        var car = TreasureCatalog.Odds.Single(o => o.Kind == TreasureCatalog.VehicleDecal);
        var newer = TreasureCatalog.EligibleAttempts(car, current, weeks, times, normal, zone);
        check(newer.Timed.SequenceEqual(new[] { current.From, current.To.AddTicks(-1) }), "The current vehicle uses the inclusive start and exclusive end of its rotation.");
        check(newer.Normal == 3, "Saturday's 1,050 and the rotation day without time are excluded from the current vehicle.");
        var older = TreasureCatalog.EligibleAttempts(car, previous, weeks, times, normal, zone);
        check(older.Normal == 1050 && older.Timed.Length == 1, "Saturday belongs to the previous vehicle's rotation.");
        var inactive = TreasureCatalog.EligibleAttempts(car, null, weeks, times, normal, zone);
        check(inactive.Timed.Length == 0 && inactive.Normal == 0, "A weekly reward has no eligible attempts without a rotation.");
        foreach (var kind in new[] { TreasureCatalog.PirateShop, TreasureCatalog.Gizmo })
        {
            var odds = TreasureCatalog.Odds.Single(o => o.Kind == kind);
            var season = TreasureCatalog.EligibleAttempts(odds, current, weeks, times, normal, zone);
            check(season.Timed.Length == 4 && season.Normal == 1090, "Permanent prizes include both days and exclude dates outside the season: " + kind);
        }
    }

    private static void Catalog(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ChestSeasonChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        SeasonFeatures.Initialize(new Store(folder));
        check(TreasureCatalog.Weeks.Count > 0, "The validated season catalog is embedded.");
        check(TreasureCatalog.Find(TreasureCatalog.PirateShopPrizeId)?.Kind == TreasureCatalog.PirateShop, "Bare GUID resolves the pirate-shop token.");
        var revision = TreasureCatalog.Revision;
        File.WriteAllText(Path.Combine(folder, "content.json"), "{\"schema\":99}");
        TreasureCatalog.LoadCached(folder);
        check(TreasureCatalog.Revision == revision, "Invalid cached catalog cannot replace working content.");
        var images = TreasureCatalog.Weeks.SelectMany(w => w.Prizes).Prepend(TreasureCatalog.PirateShopAccess).Where(p => p.Image != null);
        foreach (var prize in images)
            check(TreasureImages.Src(prize.Image)?.StartsWith("data:image/webp;base64,") == true, "Every bundled prize has an offline picture: " + prize.Id);
        check(!TreasureImages.Missing(), "Bundled images do not trigger network downloads.");
    }

    private static void Chances(Action<bool, string> check)
    {
        const double EPSILON = 0.000000001;
        check(TreasureCatalog.AtLeastOnce(100, 0) == 0, "Zero attempts have zero cumulative chance.");
        check(Math.Abs(TreasureCatalog.AtLeastOnce(100, 1) - .01) < EPSILON, "A single opening uses the per-chest rate.");
        check(TreasureCatalog.AtLeastOnce(1, 1) == 1, "Certain events have probability one.");
        var certain = new TreasureOdds(TreasureCatalog.GoldOutfit, 1, false);
        check(TreasureCatalog.AtLeastOnce(certain, Array.Empty<DateTime>(), 0) == 0, "Empty certain-event sample is zero, not NaN.");
        check(TreasureCatalog.AtLeastOnce(certain, [DateTime.UtcNow], 0) == 1, "One certain dated event is one.");
        var hour = TreasureCatalog.TreasureHours.First(h => h.Boosts.Count > 0);
        var kind = hour.Boosts.Keys.First();
        var odds = TreasureCatalog.Odds.First(o => o.Kind == kind);
        check(TreasureCatalog.Boost(kind, hour.StartUtc) == hour.Boosts[kind], "Boost begins at the inclusive start.");
        check(TreasureCatalog.Boost(kind, hour.EndUtc) == 1, "Boost ends at the exclusive boundary.");
        var expected = 1 - (1 - 1.0 / odds.OneIn) * (1 - hour.Boosts[kind] / odds.OneIn);
        check(Math.Abs(TreasureCatalog.AtLeastOnce(odds, [hour.StartUtc], 1) - expected) < EPSILON, "Dated boosts combine with undated normal-rate chests.");
        check(TreasureCatalog.ChestsFor(100, .5) == 69, "The 50% threshold rounds upward.");
        check(TreasureCatalog.ChestsFor(1, .5) == 1, "Certain-event threshold requires one chest.");
        check(TreasureCatalog.WeekOf(TreasureCatalog.Weeks[0].From) == TreasureCatalog.Weeks[0], "Week starts are inclusive.");
    }

    private static void Modes(Action<bool, string> check)
    {
        foreach (var playlist in new[] { "Playlist_DefaultSolo", "Playlist_DefaultDuo", "Playlist_NoBuildBR_Squad",
            "Playlist_HabaneroSolo", "Playlist_HabaneroDuo", "Playlist_HabaneroSquad",
            "Playlist_Habanero_Solo", "Playlist_HabaneroBR_Duo", "Playlist_Habanero_NoBuildBR_Solo",
            "Playlist_Ranked_Squad", "Playlist_RankedBR_Trio", "Playlist_RankedNoBuildBR_Duo", "Playlist_NoBuildBR_Ranked_Solo",
            "/Game/Playlists/Playlist_HabaneroBR_Solo.Playlist_HabaneroBR_Solo" })
            check(GameModes.Counts(new MatchRecord { Playlist = playlist, Map = GameModes.SeasonMaps[0] }), "Regular seasonal BR is accepted.");
        foreach (var playlist in new[] { "Playlist_Figment_Solo", "Playlist_Reload_Squad", "Creative", "", "Playlist_DefaultSolo<script>",
            "Playlist_HabaneroDuo_LTM", "Playlist_HabaneroBR_Solo_LTM", "Playlist_Ranked_Creative", "Playlist_RankedNoBuildBR_Duo_LTM" })
            check(!GameModes.Counts(new MatchRecord { Playlist = playlist }), "Non-event playlists are excluded from new tracking.");
        check(!GameModes.Counts(new MatchRecord { Playlist = "Playlist_DefaultSolo", IsCustomMatch = true }), "Custom matches are excluded.");
        var old = new MatchRecord { Playlist = "Playlist_Figment_Solo", ChestsOpened = 20, GameVersion = "v42.0", EpicId = "demo" };
        check(Profiles.View(new TrackerData { Matches = [old] }, new Config { Profile = "demo" }).Matches.Count == 1, "New-game mode rules do not hide stored historical matches.");
        check(!new MatchRecord { UnrecordedVehicleLoot = 2 }.HasVehicleChestsMissing, "Two isolated loot piles do not raise a vehicle warning.");
        check(new MatchRecord { UnrecordedVehicleLoot = 3 }.HasVehicleChestsMissing, "Three unexplained piles raise the documented vehicle warning.");
    }

    private static void RankedRecovery(Action<bool, string> check)
    {
        var store = new Store(Path.Combine(Path.GetTempPath(), "ChestRankedChecks-" + Guid.NewGuid().ToString("N")));
        var match = new MatchRecord { SessionId = "stored", ChestsOpened = 17, AnalyzerVersion = ReplayAnalyzer.CurrentVersion };
        var data = new TrackerData
        {
            AnalyzerVersion = ReplayAnalyzer.CurrentVersion,
            Matches = [match],
            ProcessedFiles = new() { ["ranked"] = "other mode", ["saved"] = "stored", ["intentional"] = "skipped", ["old"] = "older version" },
        };
        var tracker = new Tracker(store, new Config(), data);
        check(!data.ProcessedFiles.ContainsKey("ranked"), "Previously excluded replays are eligible for the normal scan.");
        check(data.ProcessedFiles["saved"] == "stored" && data.ProcessedFiles["intentional"] == "skipped" && data.ProcessedFiles["old"] == "older version", "Recovery preserves counted, intentionally skipped and unsupported-version entries.");
        check(data.Matches.Single() == match && match.ChestsOpened == 17 && !tracker.UpgradeIfNeeded(), "Recovery preserves existing match counts and analysis.");
        data.ProcessedFiles["creative"] = "other mode";
        store.SaveData(data);
        var loaded = store.LoadData();
        _ = new Tracker(store, new Config(), loaded);
        check(loaded.PlaylistFilterVersion > 0 && loaded.ProcessedFiles.ContainsKey("creative"), "The excluded-mode migration runs only once after persistence.");
        check(!GameModes.Counts(new MatchRecord { Playlist = "Playlist_HabaneroBR_Solo", Map = "OtherMap" }), "Ranked still respects the season map.");
        check(!GameModes.Counts(new MatchRecord { Playlist = "Playlist_HabaneroBR_Solo", IsCustomMatch = true }), "Custom ranked playlists remain excluded.");
    }

    private static void Reports(Action<bool, string> check)
    {
        var match = new MatchRecord { EpicId = "demo", PlayerName = "<script>bad()</script>", ChestsOpened = 1,
            MatchStartUtc = DateTime.UtcNow, SeenPrizes = [new SeenPrize { Player = "<script>bad()</script>", PrizeId = TreasureCatalog.PirateShopPrizeId }],
            Chests = [new ChestRecord { TimeSeconds = 10, Kind = "Chest" }] };
        var data = new TrackerData { Matches = [match] };
        var before = JsonSerializer.Serialize(data);
        var html = SeasonFeatures.Panels(data, new Config { Profile = "demo" }, 10);
        check(!html.Contains("<script>bad()</script>"), "Other-player names are HTML encoded.");
        check(html.Contains("&lt;script&gt;bad()&lt;/script&gt;"), "Escaped evidence remains visible.");
        check(new[] { "t-probabilidades", "t-coleccion", "t-eventos" }.All(html.Contains), "All season panels render.");
        check(JsonSerializer.Serialize(data) == before, "Season rendering does not mutate the match ledger.");
        check(SeasonFeatures.ChestTimes([match])[0] == match.MatchStartUtc.Value.AddSeconds(10), "Chest timestamps use match start plus replay time.");
        match.Chests[0].TimeSeconds = float.NaN;
        check(SeasonFeatures.ChestTimes([match]).Count == 0, "Invalid replay times are excluded from chance boosts.");
        var view = OverlayModel.Build(data, null, false, 0);
        ReportWriter.VisualStyle = "hud";
        check(ReportWriter.OverlayBox(view).Contains("box hud"), "OBS uses the selected HUD style.");
        ReportWriter.VisualStyle = "classic";
        check(!ReportWriter.OverlayBox(view).Contains("box hud"), "Classic remains available in OBS.");
    }

    private static void Career(Action<bool, string> check)
    {
        check(AppExperience.Estimate(120, 80, 2400, new ReportedTotals(25, 1050)) == 450, "Career estimate excludes matches already imported from another PC.");
        check(AppExperience.Estimate(100, 80, 2400, new ReportedTotals(25, 1050)) == 0, "Career estimate never subtracts stored chests.");
        var hasRejected = false;
        try { AppExperience.Estimate(120, 2, 20, default); }
        catch (ArgumentOutOfRangeException) { hasRejected = true; }
        check(hasRejected, "A small sample cannot produce a career estimate.");
    }
}
