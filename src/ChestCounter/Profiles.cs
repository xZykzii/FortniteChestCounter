using System.Globalization;
using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>An Epic account that recorded replays on this PC. Stats are kept per profile.</summary>
public sealed record Profile(string Key, string Name, int Matches, int Chests, DateTime LastPlayed);

/// <summary>
/// Replays saved on a PC are normally those of its player, so the replay owner (the recording player) is tracked.
/// When several accounts recorded replays here, each one gets its own profile and the player picks the account
/// they play on: the overlay and the report only show that profile.
/// </summary>
public static class Profiles
{
    /// <summary>Minimum Fortnite major version counted for Chapter 7 Mini Season (v43.X).</summary>
    public const int MinFortniteMajorVersion = 43;

    /// <summary>
    /// Extracts the Fortnite major version from the engine branch string (e.g. "++Fortnite+Release-43.00" -> 43).
    /// </summary>
    public static int? ParseFortniteMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = Regex.Match(version, @"(?:Release-|v|\b)(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return major;
        }

        return null;
    }

    /// <summary>
    /// Retain every saved match, including earlier seasons. The date filter only changes the displayed view.
    /// </summary>
    public static bool IsSupportedVersion(MatchRecord? m)
    {
        if (m == null)
        {
            return false;
        }

        // Preserve the complete history; date filtering is optional and never deletes matches.
        return true;
    }

    /// <summary>Stable key of a match's account: Epic id (survives a name change), else the display name.</summary>
    public static string Key(MatchRecord m) => !string.IsNullOrEmpty(m.EpicId) ? m.EpicId : m.PlayerName ?? "";

    public static List<Profile> List(TrackerData d, Config? c = null) => d.Matches
        .Where(IsSupportedVersion)
        .GroupBy(Key)
        .Select(g =>
        {
            var history = g.OrderByDescending(When).ToList();
            var validMatches = c == null ? history : history.Where(m => Counted(m, c)).ToList();
            var latest = history[0];
            var name = history.Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            var chests = validMatches.Sum(m => (c != null ? Adjust(m, c)! : m).ChestsOpened);
            var reported = d.ReportedByProfile.GetValueOrDefault(g.Key).ForPeriod(c?.CountFrom);
            var estimated = c is { CountFrom: null } ? InitialChests(c, g.Key) : 0;
            chests += reported.Chests + estimated;
            return new Profile(g.Key, name ?? L.T("Cuenta desconocida", "Unknown account"), validMatches.Count + reported.Matches, chests, When(latest));
        })
        .OrderByDescending(p => p.LastPlayed)
        .ToList();

    private static DateTime When(MatchRecord m) => m.MatchStartUtc ?? m.AnalyzedAtUtc;

    /// <summary>The selected profile when it exists, else the most recently played one (null = no match yet).</summary>
    public static Profile? Current(TrackerData d, Config c)
    {
        var all = List(d, c);
        return all.FirstOrDefault(p => p.Key == c.Profile) ?? all.FirstOrDefault();
    }

    /// <summary>
    /// The data of one profile only, as seen by the window, the overlay and the report. With the "count ammo
    /// boxes / food boxes / loot cannons" settings, the matches are copies whose total includes them.
    /// </summary>
    public static TrackerData View(TrackerData d, Config c)
    {
        var current = Current(d, c);
        var key = current?.Key ?? c.Profile ?? "";
        var reported = d.ReportedByProfile.GetValueOrDefault(key).ForPeriod(c.CountFrom);
        return new TrackerData
        {
            Version = d.Version,
            AnalyzerVersion = d.AnalyzerVersion,
            CreatedUtc = d.CreatedUtc,
            Matches = d.Matches.Where(m => (current == null || Key(m) == current.Key) && Counted(m, c)).Select(m => Adjust(m, c)!).ToList(),
            ProcessedFiles = d.ProcessedFiles,
            ReportedByProfile = reported.Matches > 0 ? new() { [key] = reported } : new(),
        };
    }

    /// <summary>Local day a match was played on.</summary>
    public static DateTime Day(MatchRecord m) => When(m).ToLocalTime().Date;

    /// <summary>True when the match counts for statistics (version v43.X+ and respects the "count from" date setting).</summary>
    public static bool Counted(MatchRecord m, Config c) =>
        IsSupportedVersion(m) && (c.CountFrom is not { } from || Day(m) >= from.Date);

    /// <summary>A match as shown: its total also includes the non-chest kinds and, if chosen, the teammates' chests.</summary>
    public static MatchRecord? Adjust(MatchRecord? m, Config c) => m?.WithExtras(c.ExtraKinds(), c.CountTeammateChests, c.CountTeammateTreasures);

    /// <summary>Estimated chests opened before the tool for a profile key ("" = before the first match).</summary>
    public static int InitialChests(Config c, string? key) =>
        c.InitialChests != null && c.InitialChests.TryGetValue(key ?? "", out var n) ? Math.Max(0, n) : 0;

    /// <summary>Chests without timestamps for the selected profile and period.</summary>
    public static int InitialChests(TrackerData d, Config c)
    {
        var key = Current(d, c)?.Key;
        var estimated = c.CountFrom.HasValue ? 0 : InitialChests(c, key);
        return estimated + d.ReportedByProfile.GetValueOrDefault(key ?? c.Profile ?? "").ForPeriod(c.CountFrom).Chests;
    }

    public static string Describe(Profile p) =>
        $"{p.Name}  ({L.Count(p.Matches, "partida", "partidas", "match", "matches")}, {L.Count(p.Chests, "cofre", "cofres", "chest", "chests")})";
}
