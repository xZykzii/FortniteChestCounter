using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChestCounter;

public sealed record TokenStatus(string Text, bool HasConfirmedToken)
{
    public static readonly TokenStatus Empty = new("TOKEN DE TIENDA PIRATA · Sin detecciones", false);
}

/// <summary>Reward identity and recipient evidence shared by the window and report.</summary>
public static class TreasureStatus
{
    private const int MAX_TOKEN_RECIPIENTS = 3;
    private const int RECEIPT_MATCH_SECONDS = 5;
    private static readonly Regex TokenName = new(
        @"Pirate[ _-]*Shop|DelilahShop|(?:token|acceso)(?: de| a| la| tienda|[ _-])*pirata",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record Observation(string Scope, string Recipient, string Prize, float? Time, bool IsToken);

    public static bool IsTokenId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var bare = id[(id.LastIndexOf(':') + 1)..];
        return string.Equals(bare, TreasureCatalog.PirateShopPrizeId, StringComparison.OrdinalIgnoreCase)
            || TreasureCatalog.Find(bare)?.Kind == TreasureCatalog.PirateShop;
    }

    public static bool IsTokenName(string? name) => name != null && TokenName.IsMatch(name);

    public static bool IsToken(TreasureFind find)
    {
        var hasReceipt = find.FromGoldenChest || find.Evidence == "recipient-handler";
        return hasReceipt && (IsTokenId(find.PrizeId) || IsTokenId(find.RawItem) || IsTokenName(find.RawItem));
    }

    public static void AddObservedToken(List<SeenPrize> seen, string handler, string recipient, float time)
    {
        if (!IsTokenName(handler) || !float.IsFinite(time) || time < 0) return;
        if (seen.Any(f => f.Player == recipient && IsToken(f) && Math.Abs(f.TimeSeconds - time) <= RECEIPT_MATCH_SECONDS)) return;
        seen.Add(new SeenPrize
        {
            Player = recipient,
            PrizeId = TreasureCatalog.PirateShopPrizeId,
            TimeSeconds = time,
            Evidence = "recipient-handler",
        });
    }

    private static bool IsToken(SeenPrize find) =>
        (find.Evidence is "golden-prize" or "recipient-handler") && IsTokenId(find.PrizeId);

    private static IEnumerable<Observation> Observations(MatchRecord match)
    {
        foreach (var find in match.OwnTreasureFinds)
            yield return ObservationOf(find, "Propio", match.PlayerName ?? "Tú");
        foreach (var find in match.TeamTreasureFinds)
            yield return ObservationOf(find, "Equipo", string.IsNullOrEmpty(find.Teammate) ? "Compañero sin identificar" : find.Teammate);
        foreach (var find in match.SeenPrizes)
            yield return new Observation("Otro observado", find.Player, PrizeName(find.PrizeId), find.TimeSeconds,
                IsToken(find));
    }

    private static Observation ObservationOf(TreasureFind find, string scope, string recipient) =>
        new(scope, recipient, find.DisplayName, find.ObtainedAtSeconds, find.IsPirateToken);

    private static string PrizeName(string id) => TreasureCatalog.Find(id)?.Name ?? TreasureNames.Friendly(id);

    public static TokenStatus Current(MatchRecord? live, IEnumerable<MatchRecord> matches)
    {
        if (live != null)
        {
            var current = TokenIn(live, "esta partida");
            if (current.HasConfirmedToken) return current;
        }
        var last = matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).FirstOrDefault();
        return last == null ? TokenStatus.Empty : TokenIn(last, "la última partida");
    }

    private static TokenStatus TokenIn(MatchRecord match, string period)
    {
        var tokens = Observations(match).Where(o => o.IsToken).ToList();
        if (tokens.Count == 0) return TokenStatus.Empty;
        var recipients = tokens.Select(o => o.Scope + " · " + o.Recipient).Distinct().Take(MAX_TOKEN_RECIPIENTS);
        return new TokenStatus("★ TOKEN PIRATA · Detectado en " + period + "\n" + string.Join(" | ", recipients), true);
    }

    public static void AppendReport(StringBuilder sb, IReadOnlyList<MatchRecord> matches, bool isShared)
    {
        var rows = matches.SelectMany(m => Observations(m).Select(o => (Match: m, Find: o))).ToList();
        var tokens = rows.Where(r => r.Find.IsToken).ToList();
        sb.Append("<div class=\"callout\"><span class=\"ic\">★</span><div><b>Token de Tienda Pirata · ");
        sb.Append(tokens.Count > 0 ? L.N(tokens.Count) + " detecciones" : "Sin detecciones");
        sb.Append("</b><p>Se revisan los premios propios, del equipo y de otros jugadores registrados en tu replay. Los eventos lejanos y los premios desconocidos limitan la detección.</p></div></div>");
        sb.Append("<p class=\"sm\">Tu total de tesoros incluye ");
        sb.Append(isShared ? "premios propios y compartidos del equipo." : "tus premios propios.");
        sb.Append(" La atribución de un contenedor por coincidencia temporal se muestra como probable.</p>");
        if (tokens.Count > 0) AppendTokens(sb, tokens);
        AppendRewards(sb, rows.Select(r => r.Find).ToList());
        AppendObserved(sb, rows.Where(r => r.Find.Scope == "Otro observado").ToList());
    }

    private static void AppendTokens(StringBuilder sb, List<(MatchRecord Match, Observation Find)> rows)
    {
        sb.Append("<section><div class=\"sh\"><h2>Tokens detectados</h2></div><div class=\"card tw\"><table><thead><tr><th>Partida</th><th>Destinatario</th><th>Ámbito</th><th>Momento</th></tr></thead><tbody>");
        foreach (var (match, find) in rows)
        {
            var when = (match.MatchStartUtc ?? match.AnalyzedAtUtc).ToLocalTime();
            var time = find.Time is { } seconds ? Pretty.Clock(seconds) : "—";
            sb.Append($"<tr><td>{H(L.DateTimeShort(when))}</td><td>{H(find.Recipient)}</td><td>{H(find.Scope)}</td><td>{H(time)}</td></tr>");
        }
        sb.Append("</tbody></table></div></section>");
    }

    private static void AppendRewards(StringBuilder sb, List<Observation> rows)
    {
        if (rows.Count == 0) return;
        sb.Append("<section><div class=\"sh\"><h2>Premios registrados por destinatario</h2></div><div class=\"card tw\"><table><thead><tr><th>Premio</th><th>Propios</th><th>Equipo</th><th>Otros observados</th></tr></thead><tbody>");
        foreach (var reward in rows.GroupBy(r => r.Prize).OrderByDescending(g => g.Count()))
        {
            sb.Append($"<tr><td>{H(reward.Key)}</td><td>{L.N(reward.Count(r => r.Scope == "Propio"))}</td><td>{L.N(reward.Count(r => r.Scope == "Equipo"))}</td><td>{L.N(reward.Count(r => r.Scope == "Otro observado"))}</td></tr>");
        }
        sb.Append("</tbody></table></div></section>");
    }

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>Observed opponents' rewards keep their recipient and replay timestamp.</summary>
    private static void AppendObserved(StringBuilder sb, List<(MatchRecord Match, Observation Find)> rows)
    {
        if (rows.Count == 0) return;
        sb.Append("<details class=\"card pad\"><summary>Premios de otros jugadores observados</summary>");
        sb.Append("<div class=\"tw\"><table><thead><tr>");
        sb.Append("<th>Partida</th><th>Jugador</th><th>Premio</th><th>Momento</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var (match, find) in rows)
        {
            var when = (match.MatchStartUtc ?? match.AnalyzedAtUtc).ToLocalTime();
            var time = find.Time is { } seconds ? Pretty.Clock(seconds) : "—";
            sb.Append("<tr>");
            sb.Append($"<td>{H(L.DateTimeShort(when))}</td>");
            sb.Append($"<td>{H(find.Recipient)}</td>");
            sb.Append($"<td>{H(find.Prize)}</td>");
            sb.Append($"<td>{H(time)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        sb.Append("<p class=\"sm\">Cobertura limitada a lo que Fortnite envió al replay.</p>");
        sb.Append("</details>");
    }
}

/// <summary>A prize actor assembled from the incremental exports of its replay channel.</summary>
public sealed record TreasurePrizeEvent(float Time, string? PrizeId, uint? Pawn, string Container, bool? Success)
{
    public bool HasPrize => PrizeId != null && Success != false;
}

/// <summary>
/// A channel's prize id, recipient, source and success can arrive in different exports.
/// The original detection time is retained while later exports complete the same event.
/// Entries without a prize id or with an explicit failure are excluded by HasPrize.
/// Channel reuse starts a new entry and preserves the completed event for the old actor.
/// </summary>
public sealed class TreasurePrizeLog
{
    // The index tracks only the current actor on a channel.
    // Completed events remain in the list after ResetChannel.
    // Partial exports preserve fields that are absent from later messages.
    // A failed event remains available for inspection while HasPrize excludes it.
    private readonly List<TreasurePrizeEvent> _events = new();
    private readonly Dictionary<uint, int> _byChannel = new();

    public IReadOnlyList<TreasurePrizeEvent> Events => _events;

    public void ResetChannel(uint channel) => _byChannel.Remove(channel);

    public void Record(uint channel, float time, string? id, uint? pawn, string container, bool? success)
    {
        if (!float.IsFinite(time) || time < 0) return;
        id = string.IsNullOrWhiteSpace(id) ? null : id;
        if (pawn == 0) pawn = null;
        if (_byChannel.TryGetValue(channel, out var index))
        {
            var previous = _events[index];
            _events[index] = previous with
            {
                PrizeId = id ?? previous.PrizeId,
                Pawn = pawn ?? previous.Pawn,
                Container = string.IsNullOrEmpty(container) ? previous.Container : container,
                Success = success ?? previous.Success,
            };
            return;
        }
        _byChannel[channel] = _events.Count;
        _events.Add(new TreasurePrizeEvent(time, id, pawn, container, success));
    }
}
