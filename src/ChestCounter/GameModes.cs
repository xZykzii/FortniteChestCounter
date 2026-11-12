using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>
/// Which matches count: Battle Royale (build or zero build, public or ranked) on the map of the current season.
/// Not counted: OG, Reload, Blitz and other limited-time modes, Creative / UEFN islands,
/// Lego, Festival, private (custom) matches.
/// Two facts from the replay: the map, written in the replay header (readable at once, even while the match is being
/// recorded), and the playlist (mode) sent by the server.
/// </summary>
public static class GameModes
{
    /// <summary>Maps of the season. Chapter 7 season 3 ("Delilah", the pirate season): /Delilah/Maps/Delilah_Terrain.</summary>
    public static readonly string[] SeasonMaps = { "/Delilah/Maps/Delilah_Terrain" };

    /// <summary>
    /// Battle Royale playlists: Playlist_DefaultSolo / Duo / Trio / Squad (build), Playlist_Trios, Playlist_NoBuildBR_*
    /// (zero build), and their ranked variants (Habanero / Ranked). Other modes are excluded.
    /// </summary>
    private const string TEAM_NAMES = "Solo|Duo|Duos|Trio|Trios|Squad|Squads";
    private static readonly Regex BattleRoyalePlaylist = new(
        $@"^Playlist_(Default({TEAM_NAMES})|Trios|NoBuildBR_({TEAM_NAMES})|(Habanero(BR|_NoBuildBR)?|Ranked(BR|NoBuildBR)?|NoBuildBR_Ranked)_({TEAM_NAMES}))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSeasonMap(string? map) => map != null && SeasonMaps.Any(m => map.Equals(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsBattleRoyalePlaylist(string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist)) return false;
        var name = playlist[(playlist.LastIndexOf('/') + 1)..];
        name = name[(name.LastIndexOf('.') + 1)..];
        return BattleRoyalePlaylist.IsMatch(name);
    }

    /// <summary>
    /// True when the match counts. Matches saved by versions before 1.5 have no map: their playlist decides (they were
    /// all played this season, the tool did not exist before).
    /// </summary>
    public static bool Counts(MatchRecord m) => !m.IsCustomMatch && IsBattleRoyalePlaylist(m.Playlist) && (m.Map == null || IsSeasonMap(m.Map));

    /// <summary>Why a match does not count, for the console ("Fortnite OG (Playlist_Figment...)"), or null when it counts.</summary>
    public static string? WhyNot(MatchRecord m)
    {
        if (Counts(m))
        {
            return null;
        }
        var mode = string.IsNullOrEmpty(m.Playlist) ? L.T("mode inconnu", "unknown mode", "modo desconocido") : m.Playlist;
        if (m.IsCustomMatch)
        {
            return L.T($"partie personnalisée ({mode})", $"custom match ({mode})", $"partida personalizada ({mode})");
        }
        if (m.Map != null && !IsSeasonMap(m.Map))
        {
            return L.T($"pas sur la carte de la saison ({mode}, carte {m.Map})", $"not on the season's map ({mode}, map {m.Map})", $"no es el mapa de la temporada ({mode}, mapa {m.Map})");
        }
        return L.T($"pas le Battle Royale de la saison ({mode})", $"not seasonal Battle Royale ({mode})", $"no es Battle Royale de la temporada ({mode})");
    }
}
