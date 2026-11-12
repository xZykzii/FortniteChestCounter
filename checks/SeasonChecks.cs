using ChestCounter;
using System.Text.Json;

internal static class SeasonChecks
{
    public static void Run(Action<bool, string> check)
    {
        Catalog(check);
        Chances(check);
        Modes(check);
        Reports(check);
        Career(check);
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
        foreach (var playlist in new[] { "Playlist_DefaultSolo", "Playlist_DefaultDuo", "Playlist_NoBuildBR_Squad" })
            check(GameModes.Counts(new MatchRecord { Playlist = playlist, Map = GameModes.SeasonMaps[0] }), "Regular seasonal BR is accepted.");
        foreach (var playlist in new[] { "Playlist_Habanero_Solo", "Playlist_Figment_Solo", "Playlist_Reload_Squad", "Creative", "", "Playlist_DefaultSolo<script>" })
            check(!GameModes.Counts(new MatchRecord { Playlist = playlist }), "Non-event playlists are excluded from new tracking.");
        check(!GameModes.Counts(new MatchRecord { Playlist = "Playlist_DefaultSolo", IsCustomMatch = true }), "Custom matches are excluded.");
        var old = new MatchRecord { Playlist = "Playlist_Figment_Solo", ChestsOpened = 20, GameVersion = "v42.0", EpicId = "demo" };
        check(Profiles.View(new TrackerData { Matches = [old] }, new Config { Profile = "demo" }).Matches.Count == 1, "New-game mode rules do not hide stored historical matches.");
        check(!new MatchRecord { UnrecordedVehicleLoot = 2 }.HasVehicleChestsMissing, "Two isolated loot piles do not raise a vehicle warning.");
        check(new MatchRecord { UnrecordedVehicleLoot = 3 }.HasVehicleChestsMissing, "Three unexplained piles raise the documented vehicle warning.");
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
