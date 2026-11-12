using System.Net;
using System.Text;
using System.Text.Json;

namespace ChestCounter;

public sealed class TeamChestCount
{
    public string Name { get; set; } = "";
    public string? EpicId { get; set; }
    public bool IsBot { get; set; }
    public int Chests { get; set; }
    public int Destroyed { get; set; }
}

public sealed class SeenPrize
{
    public string Player { get; set; } = "";
    public string PrizeId { get; set; } = "";
    public float TimeSeconds { get; set; }
}

/// <summary>Season content is separate from the immutable match ledger.</summary>
public static class SeasonFeatures
{
    private const int REFRESH_HOURS = 2;
    private const int DISPLAY_HOURS = 8;
    private const int SECONDS_PER_MINUTE = 60;
    private const int MAX_MATCH_SECONDS = 86_400;
    private static int _isRefreshing;
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static void Initialize(Store store)
    {
        TreasureCatalog.LoadCached(store.Folder);
        TreasureImages.Folder = Path.Combine(store.Folder, "images");
    }

    public static System.Threading.Timer Start(Monitor monitor, Store store, Config config) =>
        new(_ => _ = Refresh(monitor, store, config), null, TimeSpan.Zero, TimeSpan.FromHours(REFRESH_HOURS));

    private static async Task Refresh(Monitor monitor, Store store, Config config)
    {
        if (!config.ContentUpdates || Interlocked.Exchange(ref _isRefreshing, 1) != 0) return;
        try
        {
            var hasChanges = await TreasureCatalog.RefreshAsync(store.Folder);
            hasChanges |= await TreasureImages.DownloadMissingAsync() > 0;
            if (hasChanges) monitor.RefreshReport();
        }
        catch (Exception ex) { ErrorLog.Write("No se pudo actualizar el catálogo", ex); }
        finally { Interlocked.Exchange(ref _isRefreshing, 0); }
    }

    public static string Panels(TrackerData data, Config config, int initialChests)
    {
        var sb = new StringBuilder();
        sb.Append("<style>" + Styles + "</style>");
        Chances(sb, data, initialChests, config);
        Collection(sb, data.Matches, config.Profile ?? Profiles.Current(data, config)?.Key ?? "");
        Events(sb, data.Matches);
        return sb.ToString();
    }

    private static void OpenPanel(StringBuilder sb, string id, string title, string description)
    {
        sb.Append($"<section class=\"panel season\" id=\"t-{id}\" role=\"tabpanel\" aria-labelledby=\"tab-{id}\"><h2>{H(title)}</h2><p class=\"muted\">{H(description)}</p>");
    }

    public static List<DateTime> ChestTimes(IEnumerable<MatchRecord> matches) => matches
        .Where(m => m.MatchStartUtc.HasValue)
        .SelectMany(m => m.Chests.Where(c => float.IsFinite(c.TimeSeconds) && c.TimeSeconds >= 0 && c.TimeSeconds <= MAX_MATCH_SECONDS)
            .Select(c => m.MatchStartUtc!.Value.AddSeconds(c.TimeSeconds))).ToList();

    private static void Chances(StringBuilder sb, TrackerData data, int initialChests, Config config)
    {
        OpenPanel(sb, "probabilidades", "Probabilidades", "Probabilidad estimada de haber obtenido cada premio con tus cofres elegibles. Excluye cajas, cañones y cofres de compañeros.");
        var times = ChestTimes(data.Matches);
        var normal = NormalAttempts(data, config, initialChests, times.Count);
        var weeks = TreasureCatalog.Weeks;
        var now = DateTime.UtcNow;
        var week = weeks.Where(w => w.From <= now).MaxBy(w => w.From);
        if (week != null)
            sb.Append($"<p>Premios de la semana {week.Number} · {week.From.ToLocalTime():g} a {week.To.ToLocalTime():g}</p>");
        foreach (var reported in data.ReportedByProfile.Values)
        {
            if (reported.Day is { } day)
                sb.Append($"<p class=\"muted\">Otro PC: {reported.Chests:N0} cofres del {day:d}, sin hora de apertura. Tasa normal para los premios disponibles ese día.</p>");
        }
        sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Premio</th><th>Cofres elegibles</th><th>Por cofre</th><th>Al menos una vez</th><th>50 % a tasa normal</th></tr></thead><tbody>");
        foreach (var odds in TreasureCatalog.Odds)
        {
            var (timed, plain) = TreasureCatalog.EligibleAttempts(odds, week, weeks, times, normal, TimeZoneInfo.Local);
            var chance = TreasureCatalog.AtLeastOnce(odds, timed, plain);
            var prize = odds.Weekly ? week?.Prizes.FirstOrDefault(p => p.Kind == odds.Kind)?.Name : "Toda la temporada";
            var label = H(TreasureCatalog.KindName(odds.Kind));
            if (prize != null) label += $"<br><small class=\"muted\">{H(prize)}</small>";
            sb.Append($"<tr><td>{label}</td><td>{timed.LongLength + plain:N0}</td><td>1 / {odds.OneIn:N0}</td><td>{chance:P2}</td><td>{TreasureCatalog.ChestsFor(odds.OneIn, .5):N0}</td></tr>");
        }
        sb.Append("</tbody></table></div><p class=\"muted\">Una fecha sin hora cuenta a tasa normal si todo ese día cae dentro del período del premio. Los cofres sin fecha solo se estiman para premios de toda la temporada.</p></section>");
    }

    private static List<(DateOnly? Day, long Chests)> NormalAttempts(TrackerData data, Config config, int initialChests, int timedCount)
    {
        var extras = config.ExtraKinds();
        var rawCount = data.Matches.Sum(m => m.Chests.Count > 0 ? (long)m.Chests.Count : Math.Max(0, m.OwnChests - extras.Sum(m.Interactions)));
        var normal = data.ReportedByProfile.Values.Select(t => (t.Day, Chests: (long)t.Chests)).ToList();
        var estimated = Math.Max(0, initialChests - normal.Sum(t => t.Chests));
        normal.Add((null, estimated + Math.Max(0, rawCount - timedCount)));
        return normal;
    }

    private static void Collection(StringBuilder sb, List<MatchRecord> matches, string profile)
    {
        OpenPanel(sb, "coleccion", "Tu colección", "Los premios detectados en tus cofres se marcan automáticamente. Puedes anotar otros; esas marcas se guardan por perfil en este navegador.");
        var found = matches.SelectMany(m => m.TreasureFinds).Select(t => t.PrizeId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        sb.Append($"<div class=\"collection\" data-profile=\"{H(profile)}\">");
        PrizeCard(sb, TreasureCatalog.PirateShopAccess, found);
        foreach (var week in TreasureCatalog.Weeks)
        {
            sb.Append($"<h3>Semana {week.Number} · {H(week.Title)}</h3>");
            foreach (var prize in week.Prizes) PrizeCard(sb, prize, found);
        }
        sb.Append("</div><p class=\"muted\">Una marca manual no cambia tus estadísticas ni identifica premios de un replay.</p></section><script>" + CollectionScript + "</script>");
    }

    private static void PrizeCard(StringBuilder sb, TreasurePrize prize, HashSet<string> found)
    {
        var isFound = prize.PrizeIds.Any(found.Contains);
        var image = TreasureImages.Src(prize.Image);
        sb.Append($"<label class=\"prize{(isFound ? " obtained" : "")}\">");
        if (image != null) sb.Append($"<img loading=\"lazy\" src=\"{H(image)}\" alt=\"\" width=\"128\" height=\"128\">");
        sb.Append($"<span>{H(prize.Name)}</span><small>{H(TreasureCatalog.KindName(prize.Kind))}</small><span><input type=\"checkbox\" data-prize=\"{H(prize.Id)}\"{(isFound ? " checked disabled" : "")}> {(isFound ? "Detectado en tus cofres" : "Lo tengo")}</span></label>");
    }

    private static void Events(StringBuilder sb, List<MatchRecord> matches)
    {
        OpenPanel(sb, "eventos", "Horas del tesoro", "Horarios en tu zona local. Las franjas previstas siguen el patrón del catálogo y pueden cambiar.");
        var hours = TreasureCatalog.TreasureHours.Where(h => h.EndUtc > DateTime.UtcNow).Take(DISPLAY_HOURS).ToList();
        sb.Append("<p id=\"treasure-clock\" aria-live=\"polite\"></p><div class=\"table-wrap\"><table><thead><tr><th>Inicio</th><th>Fin</th><th>Estado</th><th>Multiplicadores</th></tr></thead><tbody>");
        foreach (var hour in hours)
        {
            var boosts = string.Join(" · ", hour.Boosts.Select(b => TreasureCatalog.KindName(b.Key) + " ×" + b.Value.ToString("0.##")));
            sb.Append($"<tr><td><time data-utc=\"{hour.StartUtc:O}\">{hour.StartUtc:u}</time></td><td><time data-utc=\"{hour.EndUtc:O}\">{hour.EndUtc:u}</time></td><td>{(hour.Confirmed ? "Confirmada en catálogo" : "Prevista")}</td><td>{H(boosts)}</td></tr>");
        }
        sb.Append("</tbody></table></div>");
        if (hours.Count == 0) sb.Append("<p>No hay próximas franjas en el catálogo actual.</p>");
        foreach (var notice in TreasureCatalog.ActiveNotices(DateTime.UtcNow))
            sb.Append($"<aside class=\"season-note\"><b>{H(notice.TitleIn(L.Code))}</b><p>{H(notice.TextIn(L.Code))}</p></aside>");
        VehicleWarnings(sb, matches);
        SeenTreasures(sb, matches);
        var schedule = JsonSerializer.Serialize(hours.Select(h => new { start = h.StartUtc, end = h.EndUtc, confirmed = h.Confirmed }));
        sb.Append("</section><script>const treasureHours=" + schedule + ";" + HoursScript + "</script>");
    }

    private static void VehicleWarnings(StringBuilder sb, List<MatchRecord> matches)
    {
        sb.Append("<h2>Vehículos y límites del replay</h2><p>Al romper cofres con vehículos, Fortnite puede omitir la apertura en el replay. Un aviso indica posible subconteo; no inventa cofres ni modifica el total.</p>");
        var warnings = matches.Where(m => m.HasVehicleChestsMissing).OrderByDescending(m => m.MatchStartUtc).ToList();
        if (warnings.Count == 0) sb.Append("<p class=\"muted\">No hay avisos registrados. Las partidas antiguas no se vuelven a analizar automáticamente.</p>");
        foreach (var match in warnings)
            sb.Append($"<p class=\"season-note\">{match.MatchStartUtc?.ToLocalTime():g} · {match.VehicleSeconds / SECONDS_PER_MINUTE} min en vehículo · {match.VehicleMeters:N0} m · {match.UnrecordedVehicleLoot} grupos de botín sin apertura asociada.</p>");
    }

    private static void SeenTreasures(StringBuilder sb, List<MatchRecord> matches)
    {
        var seen = matches.SelectMany(m => m.SeenPrizes).Where(p => TreasureCatalog.Find(p.PrizeId)?.Kind is TreasureCatalog.GoldOutfit or TreasureCatalog.PirateShop).ToList();
        sb.Append("<details><summary>Premios vistos en otros jugadores · experimental</summary><p>Solo se conoce lo que el replay transmitió cerca de ti. Estos premios no se suman a tu colección ni a tu conteo.</p>");
        if (seen.Count == 0) sb.Append("<p>No hay hallazgos registrados.</p>");
        foreach (var prize in seen) sb.Append($"<p>{H(prize.Player)} · {H(TreasureCatalog.Find(prize.PrizeId)?.Name)}</p>");
        sb.Append("</details>");
    }

    private const string Styles = """
        .season h2{margin-top:26px}.table-wrap{overflow:auto}.season table{width:100%;border-collapse:collapse}.season td,.season th{text-align:left;padding:14px;border-bottom:1px solid var(--line)}
        .collection{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:16px}.collection h3{grid-column:1/-1;margin-bottom:0}.prize{display:flex;flex-direction:column;gap:12px;background:var(--card);border:1px solid var(--line);padding:18px;border-radius:12px;min-width:0}.prize img{align-self:center;object-fit:contain}.prize small{color:var(--muted)}.prize.obtained{border-color:var(--amber)}.prize input{accent-color:var(--amber)}.season-note{padding:16px;border-left:3px solid var(--amber);background:var(--card)}.season details{margin-top:24px}.season summary{cursor:pointer}
        """;

    private const string CollectionScript = """
        (()=>{const collection=document.querySelector('.collection');if(!collection)return;
        const prefix='cc-collection-'+collection.dataset.profile+'-';
        collection.querySelectorAll('input[data-prize]:not(:disabled)').forEach(input=>{
          const key=prefix+input.dataset.prize;try{input.checked=localStorage.getItem(key)==='1'}catch(e){console.info('Colección: almacenamiento no disponible')}
          input.addEventListener('change',()=>{try{localStorage.setItem(key,input.checked?'1':'0')}catch(e){console.info('Colección: no se pudo guardar la marca')}})
        })})();
        """;

    private const string HoursScript = """
        (()=>{const REFRESH_MS=30000,MS_PER_MINUTE=60000;
        document.querySelectorAll('time[data-utc]').forEach(t=>t.textContent=new Date(t.dataset.utc).toLocaleString());
        function update(){const now=Date.now(),h=treasureHours.find(h=>Date.parse(h.end)>now),label=document.getElementById('treasure-clock');
        if(!h){label.textContent='Sin próximos horarios publicados.';return}
        const isActive=Date.parse(h.start)<=now,minutes=Math.ceil(((isActive?Date.parse(h.end):Date.parse(h.start))-now)/MS_PER_MINUTE);
        label.textContent=(h.confirmed?'Franja confirmada':'Franja prevista')+(isActive?' · termina en ':' · comienza en ')+minutes+' min';}
        update();setInterval(update,REFRESH_MS)})();
        """;
}

