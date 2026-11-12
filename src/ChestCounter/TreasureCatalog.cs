namespace ChestCounter;

/// <summary>A treasure the player can tick as obtained in the report (gizmos are left out: they can be found again).</summary>
/// <param name="Id">Key saved by the report page (browser storage).</param>
/// <param name="PrizeIds">Prize ids sent by the golden chest, when known: the item is then ticked by itself.</param>
public sealed record TreasurePrize(string Id, string Kind, string Name, string? Image, params string[] PrizeIds);

/// <summary>One week of the treasure hunt: its own outfit, pickaxe or back bling, car decal, jam track and loading screen.</summary>
/// <param name="From">Start, UTC (weeks change on Sundays at 14:00 UTC).</param>
public sealed record TreasureWeek(int Number, DateTime From, DateTime To, string Title, IReadOnlyList<TreasurePrize> Prizes);

/// <summary>
/// A "Treasure Hour": for two hours, some treasures are much more likely (multiplier per kind, e.g. gold outfit ×7.5).
/// Times in UTC. <paramref name="Confirmed"/> false = expected from the weekly pattern, not announced yet.
/// </summary>
public sealed record TreasureHour(DateTime StartUtc, DateTime EndUtc, IReadOnlyDictionary<string, double> Boosts, bool Confirmed);

/// <summary>Odds of one kind of treasure, per chest opened in a normal match.</summary>
public sealed record TreasureOdds(string Kind, int OneIn, bool Weekly);

/// <summary>
/// A message shown at the top of the window and of the report (from content.json), during its dates and to the versions
/// it concerns: a known problem, a fix to install, an exceptional Treasure Hour... "warning" stays until its end date,
/// "info" can be closed by the player.
/// </summary>
public sealed record Notice(string Id, DateTime FromUtc, DateTime ToUtc, string Level, IReadOnlyDictionary<string, string> Text,
    IReadOnlyDictionary<string, string> Title, string? Link, Version? MinVersion, Version? MaxVersion)
{
    public string TextIn(string lang) => Pick(Text, lang);

    public string TitleIn(string lang) => Pick(Title, lang);

    /// <summary>For this version of the tool (a problem fixed in 1.5.1 is not shown to 1.5.1).</summary>
    public bool Concerns(Version v)
    {
        var x = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        return (MinVersion == null || x >= MinVersion) && (MaxVersion == null || x <= MaxVersion);
    }

    private static string Pick(IReadOnlyDictionary<string, string> d, string lang) =>
        d.TryGetValue(lang, out var t) ? t : d.TryGetValue("en", out var en) ? en : d.Values.FirstOrDefault() ?? "";
}

/// <summary>
/// Chapter 7 season 3 treasure hunt ("Delilah"): odds published by Epic (relayed by FortniteStatus and fortnite.bot) and
/// the prizes of each week. Odds are per chest, the same all season, except during "Treasure Hours" (Saturday 19:00-21:00
/// and Sunday 02:00-04:00 UTC, schedule from fortnite.bot): a chest opened then counts with the boosted odds.
/// Apart from gizmos, the same treasure cannot be obtained twice.
/// Images: fortnite.bot (loaded by the browser when the report is opened).
/// </summary>
public static class TreasureCatalog
{
    public const string GoldOutfit = "outfit", PirateShop = "pirate-shop", VehicleDecal = "car", PickaxeOrBackBling = "pickaxe", JamTrack = "jam", LoadingScreen = "loading", Gizmo = "gizmo";

    /// <summary>Prize id of the Pirate Shop Access token (a bare GUID, no "DelilahPrize:" prefix).</summary>
    public const string PirateShopPrizeId = "9f73f94d-2e61-4523-97d7-d61235d3365e";

    // ---- Data: content/content.json ------------------------------------------------------------------
    // Built into the exe, then replaced by a newer copy downloaded from the repository (kept in the data folder): new
    // weeks, prize ids, Treasure Hours, odds and notices reach every counter without a new version of the tool.

    /// <summary>The data in use (swapped as a whole when a newer content.json is accepted).</summary>
    private sealed record Data(int Revision, IReadOnlyList<TreasureOdds> Odds, TreasurePrize PirateShopAccess, IReadOnlyList<TreasureWeek> Weeks,
        IReadOnlyList<TreasureHour> TreasureHours, IReadOnlyList<Notice> Notices);

    private static volatile Data? _loaded;

    // Read at first use (after the checks below are set up): the copy built into the exe, until a newer one replaces it.
    private static Data _data
    {
        get => _loaded ??= Parse(ReadEmbedded(), out var error) ?? throw new InvalidDataException("content.json built into the exe is not valid: " + error);
        set => _loaded = value;
    }

    public static int Revision => _data.Revision;
    public static IReadOnlyList<TreasureOdds> Odds => _data.Odds;
    public static TreasurePrize PirateShopAccess => _data.PirateShopAccess;
    public static IReadOnlyList<TreasureWeek> Weeks => _data.Weeks;
    public static IReadOnlyList<TreasureHour> TreasureHours => _data.TreasureHours;
    public static IReadOnlyList<Notice> Notices => _data.Notices;

    /// <summary>Notices to show now.</summary>
    public static IEnumerable<Notice> ActiveNotices(DateTime utc) => Notices.Where(n => utc >= n.FromUtc && utc < n.ToUtc && n.Concerns(AppInfo.Version));

    private const string ContentFile = "content.json";
    // CHESTCOUNTER_CONTENT_URL: another address, to try a new content.json before it is pushed to main.
    private static string RemoteUrl => Environment.GetEnvironmentVariable("CHESTCOUNTER_CONTENT_URL") is { Length: > 0 } test ? test
        : $"https://raw.githubusercontent.com/{AppInfo.Repo}/main/content/{ContentFile}";

    private static string ReadEmbedded()
    {
        using var stream = typeof(TreasureCatalog).Assembly.GetManifestResourceStream("content.json")
            ?? throw new InvalidDataException("content.json is not built into the exe");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Uses the copy kept in the data folder when it is newer than the one built into the exe.</summary>
    public static void LoadCached(string dataFolder)
    {
        try
        {
            var path = Path.Combine(dataFolder, ContentFile);
            if (File.Exists(path) && Parse(File.ReadAllText(path), out _) is { } cached && cached.Revision > _data.Revision)
            {
                _data = cached;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo leer el catálogo guardado; se usa el incluido", ex);
            // unreadable copy: the built-in data stays
        }
    }

    /// <summary>
    /// Downloads content.json from the repository; a valid copy with a higher revision is kept and used at once.
    /// Returns true when the data changed (the report must be written again).
    /// </summary>
    public static async Task<bool> RefreshAsync(string dataFolder)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounter/{AppInfo.VersionText} (+{AppInfo.RepoUrl})");
            var json = await http.GetStringAsync(RemoteUrl);
            if (json.Length > 512 * 1024 || Parse(json, out var error) is not { } fresh)
            {
                return false;
            }
            if (fresh.Revision <= _data.Revision)
            {
                return false;
            }
            Directory.CreateDirectory(dataFolder);
            var path = Path.Combine(dataFolder, ContentFile);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            _data = fresh;
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo actualizar el catálogo; se mantiene el actual", ex);
            return false; // offline, or GitHub unreachable: the current data stays
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SafeId = new(@"^[a-z0-9][a-z0-9-]{0,39}$");
    private static readonly System.Text.RegularExpressions.Regex SafePrizeId = new(@"^[A-Za-z0-9:_.\-]{1,80}$");
    private static readonly string[] Kinds = { GoldOutfit, PirateShop, VehicleDecal, PickaxeOrBackBling, JamTrack, LoadingScreen, Gizmo };

    /// <summary>
    /// Reads and checks content.json. Only data, never code or markup: every text is shown escaped, every number has to
    /// be in range, every kind known, every picture an https address. Anything wrong and the whole file is refused.
    /// </summary>
    private static Data? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1)
            {
                error = "schema";
                return null; // a newer format, for a newer version of the tool
            }
            var revision = root.GetProperty("revision").GetInt32();
            if (revision < 1)
            {
                throw new InvalidDataException("revision");
            }

            static string Text(System.Text.Json.JsonElement e, int max)
            {
                var t = e.GetString() ?? "";
                return t.Length <= max ? t : throw new InvalidDataException("text too long");
            }
            static string Kind(System.Text.Json.JsonElement e) => Array.IndexOf(Kinds, e.GetString()) >= 0 ? e.GetString()! : throw new InvalidDataException("kind");
            static DateTime When(System.Text.Json.JsonElement e) =>
                DateTime.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            TreasurePrize Prize(System.Text.Json.JsonElement e)
            {
                var id = e.GetProperty("id").GetString() ?? "";
                if (!SafeId.IsMatch(id)) throw new InvalidDataException("prize id");
                string? image = null;
                if (e.TryGetProperty("image", out var img) && img.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    image = Text(img, 300);
                    if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("image");
                }
                var ids = e.TryGetProperty("prizeIds", out var list) ? list.EnumerateArray().Select(x => x.GetString() ?? "").Take(6).ToArray() : Array.Empty<string>();
                if (ids.Any(x => !SafePrizeId.IsMatch(x))) throw new InvalidDataException("prizeIds");
                return new TreasurePrize(id, Kind(e.GetProperty("kind")), Text(e.GetProperty("name"), 80), image, ids);
            }

            var odds = root.GetProperty("odds").EnumerateArray().Take(20).Select(o =>
            {
                var oneIn = o.GetProperty("oneIn").GetInt32();
                if (oneIn is < 1 or > 100_000_000) throw new InvalidDataException("oneIn");
                return new TreasureOdds(Kind(o.GetProperty("kind")), oneIn, o.TryGetProperty("weekly", out var w) && w.GetBoolean());
            }).ToList();
            if (Kinds.Any(k => odds.All(o => o.Kind != k))) throw new InvalidDataException("odds missing");

            var weeks = root.GetProperty("weeks").EnumerateArray().Take(30).Select(w => new TreasureWeek(
                w.GetProperty("number").GetInt32(), When(w.GetProperty("start")), When(w.GetProperty("end")), Text(w.GetProperty("title"), 80),
                w.GetProperty("prizes").EnumerateArray().Take(20).Select(Prize).ToList())).ToList();
            if (weeks.Any(w => w.To <= w.From)) throw new InvalidDataException("week dates");

            var hours = root.TryGetProperty("treasureHours", out var th) ? th.EnumerateArray().Take(300).Select(h =>
            {
                var boosts = h.GetProperty("boosts").EnumerateObject().ToDictionary(b => Array.IndexOf(Kinds, b.Name) >= 0 ? b.Name : throw new InvalidDataException("boost kind"),
                    b => b.Value.GetDouble() is var x and >= 1 and <= 100 ? x : throw new InvalidDataException("boost"));
                var hour = new TreasureHour(When(h.GetProperty("start")), When(h.GetProperty("end")), boosts, h.TryGetProperty("confirmed", out var cf) && cf.GetBoolean());
                return hour.EndUtc > hour.StartUtc && hour.EndUtc - hour.StartUtc <= TimeSpan.FromHours(24) ? hour : throw new InvalidDataException("hour dates");
            }).ToList() : new List<TreasureHour>();

            static Dictionary<string, string> Langs(System.Text.Json.JsonElement e, int max) =>
                e.EnumerateObject().Where(t => t.Name is "fr" or "en" or "es").ToDictionary(t => t.Name, t => Text(t.Value, max));
            static Version? Ver(System.Text.Json.JsonElement n, string name) =>
                n.TryGetProperty(name, out var v) ? (Version.TryParse(v.GetString(), out var x) ? x : throw new InvalidDataException(name)) : null;
            var notices = root.TryGetProperty("notices", out var nt) ? nt.EnumerateArray().Take(10).Select(n => new Notice(
                n.GetProperty("id").GetString() is { } nid && SafeId.IsMatch(nid) ? nid : throw new InvalidDataException("notice id"),
                When(n.GetProperty("from")), When(n.GetProperty("to")),
                n.GetProperty("level").GetString() is "info" or "warning" ? n.GetProperty("level").GetString()! : throw new InvalidDataException("level"),
                Langs(n.GetProperty("text"), 600),
                n.TryGetProperty("title", out var tt) ? Langs(tt, 100) : new Dictionary<string, string>(),
                // a web page to open ("More info"): https only
                n.TryGetProperty("link", out var lk) ? (lk.GetString() is { Length: <= 300 } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : throw new InvalidDataException("notice link")) : null,
                Ver(n, "minVersion"), Ver(n, "maxVersion"))).ToList() : new List<Notice>();

            var pirate = Prize(root.GetProperty("pirateShop"));
            if (pirate.Kind != PirateShop) throw new InvalidDataException("pirateShop");
            return new Data(revision, odds, pirate, weeks, hours, notices);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>The prize a golden chest prize id stands for (case does not matter), or null when not known yet.</summary>
    public static TreasurePrize? Find(string prizeId) =>
        Weeks.SelectMany(w => w.Prizes).Prepend(PirateShopAccess)
            .FirstOrDefault(p => p.PrizeIds.Any(id => string.Equals(id, prizeId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The multiplier of a kind of treasure at a moment (1 outside the Treasure Hours).</summary>
    public static double Boost(string kind, DateTime utc) =>
        TreasureHours.FirstOrDefault(h => utc >= h.StartUtc && utc < h.EndUtc)?.Boosts.GetValueOrDefault(kind, 1) is { } b and > 0 ? b : 1;

    /// <summary>The week a moment falls in (null outside the hunt).</summary>
    public static TreasureWeek? WeekOf(DateTime utc) => Weeks.FirstOrDefault(w => utc >= w.From && utc < w.To);

    public static (DateTime[] Timed, long Normal) EligibleAttempts(TreasureOdds odds, TreasureWeek? week,
        IReadOnlyList<TreasureWeek> weeks, IEnumerable<DateTime> timesUtc,
        IEnumerable<(DateOnly? Day, long Chests)> normal, TimeZoneInfo zone)
    {
        if (weeks.Count == 0 || (odds.Weekly && week == null)) return ([], 0);
        var from = odds.Weekly ? week!.From : weeks.Min(w => w.From);
        var to = odds.Weekly ? week!.To : weeks.Max(w => w.To);
        var timed = timesUtc.Where(t => t >= from && t < to).ToArray();
        var plain = normal.Where(n => n.Day is { } day ? IsDayWithin(day, from, to, zone) : !odds.Weekly)
            .Sum(n => n.Chests);
        return (timed, plain);
    }

    private static bool IsDayWithin(DateOnly day, DateTime from, DateTime to, TimeZoneInfo zone)
    {
        if (day == DateOnly.MaxValue) return false;
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(start) || zone.IsInvalidTime(end) ||
            zone.IsAmbiguousTime(start) || zone.IsAmbiguousTime(end)) return false;
        return TimeZoneInfo.ConvertTimeToUtc(start, zone) >= from &&
            TimeZoneInfo.ConvertTimeToUtc(end, zone) <= to;
    }

    public static string KindName(string kind) => kind switch
    {
        GoldOutfit => L.T("Tenue en or", "Gold Outfit", "Traje de oro"),
        PirateShop => L.T("Accès à la boutique pirate", "Pirate Shop Access", "Acceso a la tienda pirata"),
        VehicleDecal => L.T("Véhicule et autocollant", "Vehicle & Decal", "Vehículo y adhesivo"),
        PickaxeOrBackBling => L.T("Pioche ou sac à dos", "Pickaxe or Back Bling", "Pico o mochila"),
        JamTrack => L.T("Piste musicale", "Jam Track", "Pista musical"),
        LoadingScreen => L.T("Écran de chargement", "Loading Screen", "Pantalla de carga"),
        _ => L.T("Gizmo (n'importe lequel)", "Gizmo (any)", "Artilugio (cualquiera)"),
    };

    /// <summary>
    /// Chance of having obtained it at least once in <paramref name="chests"/> chests: 1 − (1 − p)^n. Each chest keeps
    /// the same odds; what grows is the chance of at least one success over all of them.
    /// </summary>
    public static double AtLeastOnce(int oneIn, long chests)
    {
        if (oneIn < 1) throw new ArgumentOutOfRangeException(nameof(oneIn));
        if (chests <= 0) return 0;
        return oneIn == 1 ? 1 : -double.ExpM1(chests * double.LogP1(-1.0 / oneIn));
    }

    /// <summary>
    /// Same, chest by chest: 1 − Π(1 − p × boost), each chest with the boost of the moment it was opened (Treasure
    /// Hours), plus <paramref name="plainChests"/> chests at the normal odds (added by hand: their time is unknown).
    /// </summary>
    public static double AtLeastOnce(TreasureOdds odds, IEnumerable<DateTime> chestTimesUtc, long plainChests)
    {
        if (odds.OneIn < 1) throw new ArgumentOutOfRangeException(nameof(odds));
        var times = chestTimesUtc.ToList();
        if (odds.OneIn == 1) return plainChests > 0 || times.Count > 0 ? 1 : 0;
        var p = 1.0 / odds.OneIn;
        var sum = Math.Max(0, plainChests) * double.LogP1(-p);
        foreach (var t in times)
        {
            var boosted = p * Boost(odds.Kind, t);
            if (boosted >= 1) return 1;
            sum += double.LogP1(-boosted);
        }
        return -double.ExpM1(sum);
    }

    /// <summary>Chests needed to reach a given cumulative chance (0.5 = "one chance in two").</summary>
    public static long ChestsFor(int oneIn, double chance)
    {
        if (oneIn < 1 || !double.IsFinite(chance) || chance <= 0 || chance >= 1) throw new ArgumentOutOfRangeException(nameof(chance));
        return oneIn == 1 ? 1 : (long)Math.Ceiling(Math.Log(1 - chance) / double.LogP1(-1.0 / oneIn));
    }
}
