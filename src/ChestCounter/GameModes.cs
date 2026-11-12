using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>
/// Which matches count: regular Battle Royale (build or zero build) on the map of the current season, where the chest
/// treasure hunt runs. Not counted: ranked, OG, Reload, Blitz and other limited-time modes, Creative / UEFN islands,
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
    /// (zero build). Anything else is left out, even on the season map: ranked ("Habanero", Epic's code name), OG
    /// ("Figment"), Reload, Blitz, LTMs...
    /// </summary>
    private static readonly Regex BattleRoyalePlaylist = new(
        @"^Playlist_(Default(Solo|Duo|Duos|Trio|Trios|Squad|Squads)|Trios|NoBuildBR_(Solo|Duo|Duos|Trio|Trios|Squad|Squads))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsSeasonMap(string? map) => map != null && SeasonMaps.Any(m => map.Equals(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsBattleRoyalePlaylist(string? playlist) => playlist != null && BattleRoyalePlaylist.IsMatch(playlist);

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
        return L.T($"pas le Battle Royale classique ({mode})", $"not the regular Battle Royale ({mode})", $"no es el Battle Royale normal ({mode})");
    }
}
