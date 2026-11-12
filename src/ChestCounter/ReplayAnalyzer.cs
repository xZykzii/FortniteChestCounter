using System.Text.RegularExpressions;
using FortniteReplayReader;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using FortniteReplayReader.Models.NetFieldExports.Items;
using FortniteReplayReader.Models.NetFieldExports.Vehicles;
using FortniteReplayReader.Models.NetFieldExports.Weapons;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ChestCounter;

/// <summary>
/// Reads one .replay file and extracts every container searched by the tracked player,
/// together with the items that were tossed out of it.
/// </summary>
public sealed class ReplayAnalyzer : FortniteReplayReader.ReplayReader
{
    /// <summary>Stale = state received with the channel itself (searched before it became relevant to the recorder).</summary>
    private sealed record SearchEvent(float Time, string ActorKey, string Label, uint? SearcherPawn, bool Destroyed = false, bool Stale = false);
    /// <summary>Owner = the container the item was thrown out of (the server makes it the owner of the pickup).</summary>
    private sealed record TossEvent(float Time, string Item, Pt? Loc, uint? Owner);

    /// <summary>A position at a given time (world units: centimeters).</summary>
    private readonly record struct Pt(float T, float X, float Y, float Z);

    /// <summary>A player sitting in a vehicle: the vehicle's movement is then the player's.</summary>
    private sealed class SeatSpan(uint pawn, uint vehicle, float from)
    {
        public uint Pawn { get; } = pawn;
        public uint Vehicle { get; } = vehicle;
        public float From { get; } = from;
        public float To { get; set; } = float.MaxValue;
    }

    /// <summary>A closed chest destroyed with no instigator given by the server (its support was broken...).</summary>
    private sealed record OrphanDestroy(float Time, string ActorKey, string Label, uint Actor);

    private static readonly Regex VehicleClass = new(@"_Vehicle(_Static)?_C$|MeatballVehicle", RegexOptions.Compiled);
    private sealed record EquipEvent(float Time, string Item, uint Pawn);
    private sealed record PrizeEvent(float Time, string PrizeId, uint? Pawn, string Container);

    // "DelilahPrize:dpid_item4", or a bare GUID (the Pirate Shop Access token has no prefix).
    private static readonly Regex PrizeIdPattern = new(@"[A-Za-z][A-Za-z0-9_]*:[A-Za-z0-9_.\-]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    // Treasure hunt items do not drop on the ground: they go straight into the inventory.
    // They only show up in the replay when the player equips them (a weapon/gizmo actor is spawned).
    private static readonly Regex TreasureActor = new(
        @"FlameCeleb|RelicExtractor|RelicFinder|LuckyLocator|SmugglerExtractionKey|ExtractionAccelerat",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UaidSuffix = new(@"_UAID_.*$", RegexOptions.Compiled);

    // Items that are part of the treasure hunt "Standard" prize pool, or anything that looks like a treasure.
    private static readonly Regex TreasureItem = new(
        @"RelicExtractor|RelicFinder|LuckyLocator|FlameCeleb|SmugglerExtractionKey|ExtractionAccelerat|Treasure|PirateShop|DelilahShop",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Limited-time event loot ("happy hours"): a portable extractor that simply drops from chests. It looks like the
    // treasure hunt gizmo (WID_RelicExtractor) but is not a treasure: no prize handler is granted for it.
    private static readonly Regex EventItem = new(@"RelicExtractor_Consumable", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static ReplayAnalyzer()
    {
        // Map every chest / container blueprint we do not know yet onto the generic container group.
        NetFieldParser.AliasResolver = path =>
        {
            if (path.Contains(':') || path.EndsWith("_ClassNetCache", StringComparison.Ordinal))
            {
                return null;
            }

            var last = path[(path.LastIndexOf('/') + 1)..];

            // Variants of the golden treasure chest animation (e.g. BGA_DelilahTreasureHuntAnimatedChest_Rare) carry the
            // prize id too: map them onto TreasureHuntChest, not GenericContainer (their name contains "Chest").
            if (last.StartsWith("BGA_DelilahTreasureHuntAnimatedChest", StringComparison.Ordinal))
            {
                return "/DelilahTreasureHuntGameplay/Gameplay/Actors/BGA_DelilahTreasureHuntAnimatedChest.BGA_DelilahTreasureHuntAnimatedChest_C";
            }

            if (last.StartsWith("B_", StringComparison.Ordinal) && TreasureActor.IsMatch(last))
            {
                return GenericWeapon.GroupPath;
            }

            // Cars, SUVs, carriages, boats: their movement is the movement of the players sitting in them.
            if (VehicleClass.IsMatch(last))
            {
                return GenericVehicle.GroupPath;
            }

            return path.Contains("/Containers/", StringComparison.OrdinalIgnoreCase)
                   || last.Contains("Chest", StringComparison.OrdinalIgnoreCase)
                ? GenericContainer.GroupPath
                : null;
        };

        // Loot cannons: every cannon blueprint exposes the FireLootCannon RPC through its own ClassNetCache.
        NetFieldParser.ClassNetCacheAliasResolver = path =>
            path.Contains("LootCannon", StringComparison.OrdinalIgnoreCase) && path.EndsWith("_ClassNetCache", StringComparison.Ordinal)
                ? LootCannonCache.Path
                : null;
    }

    private readonly Dictionary<uint, string> _channelKey = new();
    private readonly Dictionary<uint, string> _channelLabel = new();
    private readonly Dictionary<uint, uint> _lastSearcher = new();
    private readonly HashSet<string> _searched = new();
    private readonly List<SearchEvent> _searches = new();
    /// <summary>When each channel was opened (a container state received right then is "stale").</summary>
    private readonly Dictionary<uint, float> _channelOpenAt = new();
    /// <summary>Searched state of each open container channel (false again when it is refilled).</summary>
    private readonly Dictionary<uint, bool> _channelSearched = new();
    /// <summary>Who made the last recorded search of each container key.</summary>
    private readonly Dictionary<string, uint?> _lastOpener = new();

    // Where everybody is: pawns on foot, vehicles, and who sits in which vehicle.
    private readonly Dictionary<uint, List<Pt>> _pawnTrack = new();
    private readonly Dictionary<uint, List<Pt>> _vehicleTrack = new();
    private readonly List<SeatSpan> _seatSpans = new();
    private readonly Dictionary<(uint Vehicle, int Seat), SeatSpan> _seated = new();
    private readonly List<OrphanDestroy> _orphans = new();
    private readonly HashSet<uint> _orphanChannels = new();
    /// <summary>Containers the replay shows opened or destroyed (actor ids): the loot they dropped is explained.</summary>
    private readonly HashSet<uint> _openedContainers = new();

    private static void Track(Dictionary<uint, List<Pt>> tracks, uint id, FVector? location, float minInterval)
    {
        if (location == null)
        {
            return;
        }
        if (!tracks.TryGetValue(id, out var track))
        {
            tracks[id] = track = new List<Pt>();
        }
        if (track.Count == 0 || Now - track[^1].T >= minInterval)
        {
            track.Add(new Pt(Now, (float)location.X, (float)location.Y, (float)location.Z));
        }
    }

    /// <summary>Position on a track at a given time (interpolated), or null when the track has nothing close in time.</summary>
    private static Pt? At(List<Pt>? track, float t, float maxGap = 3f)
    {
        if (track == null || track.Count == 0)
        {
            return null;
        }
        int lo = 0, hi = track.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (track[mid].T < t) lo = mid + 1; else hi = mid;
        }
        var after = track[lo];
        if (lo == 0 || after.T <= t)
        {
            return Math.Abs(after.T - t) <= maxGap ? after : null;
        }
        var before = track[lo - 1];
        if (after.T - before.T <= maxGap)
        {
            var f = (t - before.T) / (after.T - before.T);
            return new Pt(t, before.X + (after.X - before.X) * f, before.Y + (after.Y - before.Y) * f, before.Z + (after.Z - before.Z) * f);
        }
        var nearest = t - before.T <= after.T - t ? before : after;
        return Math.Abs(nearest.T - t) <= maxGap ? nearest : null;
    }

    /// <summary>Where a player's pawn is: in its vehicle while seated, on its own track otherwise.</summary>
    private Pt? PositionOf(uint pawn, float t)
    {
        foreach (var span in _seatSpans)
        {
            if (span.Pawn == pawn && t >= span.From - 0.5f && t <= span.To + 0.5f && At(_vehicleTrack.GetValueOrDefault(span.Vehicle), t) is { } inVehicle)
            {
                return inVehicle;
            }
        }
        return At(_pawnTrack.GetValueOrDefault(pawn), t);
    }

    private static float Distance(Pt a, Pt b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    private static readonly string? Debug = Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG");

    /// <summary>Level path or spawn location: the same container keeps the same key all match long.</summary>
    private static bool StableKey(string key) => !key.StartsWith("guid:", StringComparison.Ordinal) && !key.StartsWith("ch:", StringComparison.Ordinal);

    /// <summary>A chest spawned during the match (not the sand pile left by a buried chest, nor any other prop).</summary>
    private static bool IsDynamicChest(string label) =>
        !label.StartsWith("Prop_", StringComparison.OrdinalIgnoreCase) && ChestKinds.IsChest(ChestKinds.Classify(label));
    private readonly List<TossEvent> _tosses = new();
    private readonly List<EquipEvent> _equips = new();
    private readonly List<PrizeEvent> _prizes = new();
    private readonly Dictionary<string, float> _firstExport = new();
    private static readonly Regex TrackedExport = new(@"TreasureHandler|WID_(FlameCeleb|RelicExtractor|RelicFinder|LuckyLocator|SmugglerExtractionKey)", RegexOptions.Compiled);
    private readonly HashSet<uint> _prizeChannelsSeen = new();
    private readonly HashSet<uint> _equipChannelsSeen = new();
    private readonly Dictionary<uint, uint> _pawnToPlayerState = new();
    private readonly Dictionary<uint, int> _playerStateToPlayerId = new();

    // Treasure prize handler abilities ("GA_TreasureHandler_*"): class netguid -> name.
    private readonly Dictionary<uint, string> _treasureHandlerGuids = new();

    /// <summary>One treasure prize handler granted to a player (one per treasure obtained).</summary>
    private sealed record GrantEvent(float Time, uint PlayerStateActor, int ElementId, string Handler);
    private readonly List<GrantEvent> _grants = new();

    /// <summary>Bump when the analysis changes so that existing replays are re-analyzed.</summary>
    public const int CurrentVersion = 17;
    private const int MAX_SQUAD_SIZE = 4;

    private static readonly bool ProbeDebug = Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "4";

    public ReplayAnalyzer() : base(Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "2" ? new StderrLogger() : null,
        Enum.TryParse<ParseMode>(Environment.GetEnvironmentVariable("CHESTCOUNTER_MODE"), out var mode) ? mode : ParseMode.Normal)
    {
        // The ability system component of the player state is not decoded by the library (its fields have no
        // exported names): its raw FastArray payloads are read here to find every treasure handler granted.
        Regex? debugFilter = null;
        if (ProbeDebug)
        {
            debugFilter = new Regex(Environment.GetEnvironmentVariable("CHESTCOUNTER_PROBE") is { Length: > 0 } p
                ? p : "Treasure|Prize|Magpie|Inventory|Quest|Reward|Hunt|PlayerController|PlayerState|Notif", RegexOptions.IgnoreCase);
            NetGuidCache.KeepOnCleanup = true;
        }
        RawProbe.Filter = path => path.Contains("FortAbilitySystemComponent", StringComparison.Ordinal) || debugFilter?.IsMatch(path) == true;
        RawProbe.Sink = (channel, group, field, data, bits) =>
        {
            // FortAbilitySystemComponent_ClassNetCache (unnamed fields) or FortAbilitySystemComponentAthena_ClassNetCache
            // (named "ActivatableAbilities", handed over raw by the reader instead of its generic NetDelta decoding).
            if (group.Contains("AbilitySystemComponent", StringComparison.Ordinal) && group.EndsWith("_ClassNetCache", StringComparison.Ordinal)
                && field is "?" or "ActivatableAbilities" && _treasureHandlerGuids.Count > 0)
            {
                ReadAbilityGrants(channel, data, bits);
            }
            if (debugFilter?.IsMatch(group + "." + field) == true)
            {
                ProbeSink(channel, group, field, data, bits);
            }
        };
    }

    /// <summary>
    /// Decodes a FastArray delta (ActivatableAbilities) without knowing the field names: header, then for each
    /// changed element its id and its properties (handle, bit count, payload). An element whose object reference
    /// is a treasure handler class means a treasure was given to the owner of this ability system.
    /// </summary>
    private void ReadAbilityGrants(uint channel, byte[] data, int bits)
    {
        var pos = 1; // bSupportsFastArrayDeltaStructSerialization
        int Bit() => pos < bits ? (data[pos >> 3] >> (pos & 7)) & 1 : throw new IndexOutOfRangeException();
        uint Read(int n) { uint v = 0; for (var k = 0; k < n; k++) { v |= (uint)Bit() << k; pos++; } return v; }
        uint ReadPacked()
        {
            uint v = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = Read(8);
                v |= (b >> 1) << shift;
                if ((b & 1) == 0) return v;
            }
            throw new InvalidDataException();
        }

        try
        {
            _ = Read(32); _ = Read(32); // replication keys
            var deletes = Read(32);
            var changed = Read(32);
            if (deletes > 512 || changed > 512 || bits < 129)
            {
                return;
            }
            pos += (int)deletes * 32;
            for (var i = 0; i < changed; i++)
            {
                var elementId = (int)Read(32);
                while (true)
                {
                    var handle = ReadPacked();
                    if (handle == 0) break;
                    var numBits = (int)ReadPacked();
                    if (numBits < 0 || pos + numBits > bits) return;
                    if (numBits is 8 or 16 or 24 or 32 or 40)
                    {
                        var start = pos;
                        uint guid = 0;
                        for (var shift = 0; pos < start + numBits; shift += 7)
                        {
                            var b = Read(8);
                            guid |= (b >> 1) << shift;
                            if ((b & 1) == 0) break;
                        }
                        pos = start + numBits;
                        if (_treasureHandlerGuids.TryGetValue(guid, out var handler))
                        {
                            var actor = Channels[channel]?.ActorId ?? 0;
                            if (!_grants.Any(g => g.PlayerStateActor == actor && g.ElementId == elementId))
                            {
                                _grants.Add(new GrantEvent(Now, actor, elementId, handler));
                            }
                        }
                    }
                    else
                    {
                        pos += numBits;
                    }
                }
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or InvalidDataException)
        {
            // Not a FastArray delta (or another layout): ignore.
            if (ProbeDebug) Console.Error.WriteLine($"GRANTFAIL {Now,7:F1}s ch{channel} bits={bits} pos={pos} {e.GetType().Name}");
        }
    }

    /// <summary>Debug 4: prints undecoded payloads with any readable text / object reference they contain.</summary>
    private void ProbeSink(uint channel, string group, string field, byte[] data, int bits)
    {
        var strings = new HashSet<string>();
        for (var shift = 0; shift < 8; shift++)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i + 1 <= (bits - shift) / 8; i++)
            {
                var bitPos = i * 8 + shift;
                var b = (data[bitPos / 8] >> (bitPos % 8)) | (bitPos / 8 + 1 < data.Length ? data[bitPos / 8 + 1] << (8 - bitPos % 8) : 0);
                var c = (char)(b & 0xFF);
                if (char.IsAsciiLetterOrDigit(c) || c is '_' or ':' or '.' or '/' or '-' or ' ')
                {
                    sb.Append(c);
                    continue;
                }
                if (sb.Length >= 5) strings.Add(sb.ToString().Trim());
                sb.Clear();
            }
            if (sb.Length >= 5) strings.Add(sb.ToString().Trim());
        }

        // Single object reference (packed int netguid)?
        var guidText = "";
        if (bits <= 48)
        {
            uint value = 0;
            var shiftBits = 0;
            foreach (var by in data)
            {
                value |= (uint)(by >> 1) << shiftBits;
                shiftBits += 7;
                if ((by & 1) == 0 || shiftBits > 28) break;
            }
            var path = PathOf(value);
            if (!string.IsNullOrEmpty(path)) guidText = $" guid={value}:{CleanName(path)}";
        }

        // Brute force: every bit offset decoded as a packed netguid that resolves to a known object path.
        if (bits > 48 && Environment.GetEnvironmentVariable("CHESTCOUNTER_PROBE_GUIDS") == "1")
        {
            var refs = new List<string>();
            int Bit(int pos) => pos < bits ? (data[pos >> 3] >> (pos & 7)) & 1 : 0;
            for (var start = 0; start + 8 <= bits; start++)
            {
                uint value = 0;
                var pos = start;
                for (var n = 0; n < 5 && pos + 8 <= bits; n++)
                {
                    var by = 0;
                    for (var k = 0; k < 8; k++) by |= Bit(pos++) << k;
                    value |= (uint)(by >> 1) << (7 * n);
                    if ((by & 1) == 0) break;
                }
                if (value > 64)
                {
                    var p = PathOf(value);
                    if (!string.IsNullOrEmpty(p)) refs.Add($"@{start}:{value}:{CleanName(p)}");
                }
            }
            if (refs.Count > 0) guidText += " refs=[" + string.Join(" ", refs) + "]";
        }

        var hex = bits <= (Environment.GetEnvironmentVariable("CHESTCOUNTER_PROBE_HEX") == "1" ? 100000 : 256) ? " hex=" + Convert.ToHexString(data) : "";
        var owner = _channelLabel.GetValueOrDefault(channel, "?");
        Console.Error.WriteLine($"PRB {Now,7:F1}s ch{channel} [{owner}] {CleanName(group)}.{field} bits={bits}{guidText}{hex}{(strings.Count > 0 ? " txt=[" + string.Join(" | ", strings.Where(s => s.Length >= 5)) + "]" : "")}");
    }

    private sealed class StderrLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Console.Error.WriteLine($"LOG {logLevel} {ReplayAnalyzer.Now,7:F1}s {formatter(state, exception)}");
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update)
    {
        base.OnNetDeltaRead(channelIndex, update);
        if (update.Export is GrantedAbility g && Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") is "1" or "2")
        {
            Console.Error.WriteLine($"DBG ability {Now,7:F1}s ch{channelIndex} idx={update.ElementIndex} del={update.Deleted} ability={CleanName(PathOf(g.Ability))} src={CleanName(PathOf(g.SourceObject))}");
        }
    }

    private static string Safe(Func<string> f) { try { return f(); } catch { return "!"; } }

    internal static float Now => Unreal.Core.ReplayReader<FortniteReplay>.CurrentTime;

    private string PathOf(uint? guid)
    {
        if (guid is null || guid == 0)
        {
            return "";
        }
        return _netGuidCache.TryGetPathName(guid.Value, out var path) ? path : "";
    }

    private static string CleanName(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }
        return UaidSuffix.Replace(name, "");
    }

    /// <summary>
    /// The prize handler ability class is sent the first time the recording player receives a treasure:
    /// that gives the exact moment of the first treasure of the match (even one never used).
    /// </summary>
    protected override void OnNetGuidExported(uint netGuid, string pathName)
    {
        if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "3")
        {
            Console.Error.WriteLine($"EXP {Now,7:F1}s {pathName}");
        }
        if (TrackedExport.IsMatch(pathName))
        {
            var name = CleanName(pathName);
            _firstExport.TryAdd(name, Now);
            // The ability spec references the class default object ("Default__GA_TreasureHandler_..._C").
            if (name.Contains("TreasureHandler", StringComparison.Ordinal))
            {
                _treasureHandlerGuids[netGuid] = name.Replace("Default__", "");
            }
        }
    }

    protected override void OnChannelOpened(uint channelIndex, NetworkGUID? actor)
    {
        base.OnChannelOpened(channelIndex, actor);

        var channel = Channels[channelIndex];
        var archetype = PathOf(channel?.ArchetypeId);
        var actorPath = PathOf(channel?.ActorId);

        var label = CleanName(!string.IsNullOrEmpty(archetype) ? archetype : actorPath);
        if (!string.IsNullOrEmpty(actorPath))
        {
            // Static (level placed) actors have no archetype; their actor path is unique per chest.
            _channelKey[channelIndex] = actorPath;
        }
        else if (channel?.Actor?.Location is { } loc && IsDynamicChest(label))
        {
            // Spawned during the match (buried chest, relic chest): a new GUID each time it becomes relevant again,
            // but always the same spawn location. The location is its identity for the whole match.
            _channelKey[channelIndex] = $"{label}@{loc.X:F0},{loc.Y:F0}";
        }
        else
        {
            _channelKey[channelIndex] = $"guid:{channel?.ActorId}";
        }
        _channelLabel[channelIndex] = label;
        _channelOpenAt[channelIndex] = Now;
        _channelSearched.Remove(channelIndex);
        _lastSearcher.Remove(channelIndex);
        _equipChannelsSeen.Remove(channelIndex);
        _prizeChannelsSeen.Remove(channelIndex);
        _orphanChannels.Remove(channelIndex);
    }

    /// <summary>Extracts "DelilahPrize:dpid_item4" from the raw property bits (FString inside a struct).</summary>
    private static string? ReadPrizeId(DebuggingObject? raw)
    {
        if (raw == null || raw.TotalBits == 0)
        {
            return null;
        }
        var ascii = new string(raw.Bytes.Select(b => b is >= 32 and < 127 ? (char)b : ' ').ToArray());
        var match = PrizeIdPattern.Match(ascii);
        return match.Success ? match.Value : null;
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
        base.OnExportRead(channelIndex, exportGroup);

        switch (exportGroup)
        {
            case BaseContainer container:
                if (container.SearchingPawn is uint searcher && searcher != 0)
                {
                    _lastSearcher[channelIndex] = searcher;
                }

                var containerKey = _channelKey.GetValueOrDefault(channelIndex, $"ch:{channelIndex}");
                var containerLabel = _channelLabel.GetValueOrDefault(channelIndex, "?");
                // State received together with the channel: the container was searched before it became relevant
                // (coming back near it), or it is a "dormant" box whose channel only opens when it is searched.
                var stale = Now - _channelOpenAt.GetValueOrDefault(channelIndex, float.MinValue) < 0.5f;

                if (container.bAlreadySearched == false)
                {
                    // Refilled during the match (waves every ~3 min): it can be searched, and counted, again.
                    _channelSearched[channelIndex] = false;
                    if (Debug == "1")
                    {
                        Console.Error.WriteLine($"DBG refill {Now,7:F1}s {containerLabel} {containerKey}");
                    }
                }
                else if (container.bAlreadySearched == true && !_channelSearched.GetValueOrDefault(channelIndex))
                {
                    _channelSearched[channelIndex] = true;
                    var openedBy = _lastSearcher.TryGetValue(channelIndex, out var s) ? s : (uint?)null;
                    // Stale + same opener as the last recorded search of that container = that old search seen again,
                    // not a new one. A container without a stable identity (new GUID each time) is recorded once.
                    // Anything else is recorded, even a container already searched: it was refilled and opened again.
                    var seenAgain = stale && (StableKey(containerKey)
                        ? _lastOpener.TryGetValue(containerKey, out var previous) && previous == openedBy
                        : _searched.Contains(containerKey));
                    if (!seenAgain)
                    {
                        _searched.Add(containerKey);
                        _lastOpener[containerKey] = openedBy;
                        _searches.Add(new SearchEvent(Now, containerKey, containerLabel, openedBy, Stale: stale));
                    }
                    if (Channels[channelIndex]?.ActorId is uint openedActor)
                    {
                        _openedContainers.Add(openedActor);
                    }
                    else if (Debug == "1")
                    {
                        Console.Error.WriteLine($"DBG seen-again {Now,7:F1}s {containerLabel} {containerKey}");
                    }
                }
                else if (container.bDestroyed && container.SearchingPawn is uint destroyer && destroyer != 0
                    && CanCountDestroyedChest(channelIndex, containerKey, stale, container.SearchAnimationCount))
                {
                    // Broken instead of opened (support destroyed, explosion...): the loot spills out and the
                    // server sets SearchingPawn to whoever destroyed it. Counted like an opened chest: once, or once
                    // more if it was refilled since its last opening.
                    _channelSearched[channelIndex] = true;
                    _lastOpener[containerKey] = destroyer;
                    _searches.Add(new SearchEvent(Now, containerKey, containerLabel, destroyer, Destroyed: true, Stale: stale));
                }
                if (container.bDestroyed && Channels[channelIndex]?.ActorId is uint brokenActor)
                {
                    _openedContainers.Add(brokenActor);
                }
                if (container.bDestroyed && container.SearchingPawn is null or 0 && !stale && StableKey(containerKey)
                    && (!_searched.Contains(containerKey) || (_channelSearched.TryGetValue(channelIndex, out var opened) && !opened))
                    && Channels[channelIndex]?.ActorId is uint orphanActor && _orphanChannels.Add(channelIndex))
                {
                    // A closed chest destroyed without an instigator (its support was broken, it fell...). Who did it is
                    // worked out at the end, from the loot that spilled and who stood right there (ResolveOrphans).
                    _orphans.Add(new OrphanDestroy(Now, containerKey, containerLabel, orphanActor));
                }
                break;

            case GenericVehicle vehicle when vehicle.ReplicatedMovement is { } vehicleMove && Channels[channelIndex]?.ActorId is uint movingVehicle:
                Track(_vehicleTrack, movingVehicle, vehicleMove.Location, 0.25f);
                break;

            case SeatComponent seats when seats.PlayerSlots != null && Channels[channelIndex]?.ActorId is uint seatVehicle:
                // Only the seats that changed carry a value: a pawn (someone sat down) or 0 (the seat was left).
                for (var seat = 0; seat < seats.PlayerSlots.Length; seat++)
                {
                    if (seats.PlayerSlots[seat]?.Player is not uint occupant)
                    {
                        continue;
                    }
                    if (_seated.Remove((seatVehicle, seat), out var leaving))
                    {
                        leaving.To = Now;
                    }
                    if (occupant != 0)
                    {
                        var span = new SeatSpan(occupant, seatVehicle, Now);
                        _seated[(seatVehicle, seat)] = span;
                        _seatSpans.Add(span);
                    }
                }
                break;

            case GrantedAbility ga when Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") is "1" or "2":
                Console.Error.WriteLine($"DBG abilityexp {Now,7:F1}s ch{channelIndex} owner={ga.OwnerActor} ability={CleanName(PathOf(ga.Ability))} src={CleanName(PathOf(ga.SourceObject))}");
                break;

            case LootCannonFired cannon when cannon.InteractingPawn is uint gunner && gunner != 0:
                var cannonKey = _channelKey.GetValueOrDefault(channelIndex, $"ch:{channelIndex}") + $"#fire{Now:F0}";
                if (_searched.Add(cannonKey))
                {
                    _searches.Add(new SearchEvent(Now, cannonKey, _channelLabel.GetValueOrDefault(channelIndex, "LootCannon"), gunner));
                }
                break;

            case TreasureHuntChest prize:
                var prizeId = ReadPrizeId(prize.PrizeTemplateId);
                if (prizeId != null && prize.bSuccess != false && _prizeChannelsSeen.Add(channelIndex))
                {
                    _prizes.Add(new PrizeEvent(Now, prizeId, prize.OpeningPawn ?? prize.Instigator, PathOf(prize.SourceContainer)));
                }
                break;

            case GenericWeapon weapon:
                var holder = weapon.Instigator ?? weapon.Owner?.Value;
                if (holder is uint h && h != 0 && _equipChannelsSeen.Add(channelIndex))
                {
                    var itemName = weapon.WeaponData?.Name;
                    if (string.IsNullOrEmpty(itemName))
                    {
                        itemName = _channelLabel.GetValueOrDefault(channelIndex, "?");
                    }
                    _equips.Add(new EquipEvent(Now, itemName, h));
                }
                break;

            case FortPickup pickup when pickup.bTossedFromContainer == true && pickup.ItemDefinition != null:
                var item = pickup.ItemDefinition.Name;
                if (string.IsNullOrEmpty(item))
                {
                    item = CleanName(PathOf(pickup.ItemDefinition.Value));
                }
                if (!string.IsNullOrEmpty(item))
                {
                    // The pickup is spawned where the container stands, then flies to where it lands.
                    var spawn = Channels[channelIndex]?.Actor?.Location;
                    _tosses.Add(new TossEvent(Now, item, spawn == null ? null : new Pt(Now, (float)spawn.X, (float)spawn.Y, (float)spawn.Z), pickup.Owner));
                }
                break;

            case PlayerPawn pawn:
                var pawnId = Channels[channelIndex]?.ActorId;
                if (pawnId != null)
                {
                    if (pawn.PlayerState is uint ps && ps != 0)
                    {
                        _pawnToPlayerState[pawnId.Value] = ps;
                    }
                    if (pawn.ReplicatedMovement is { } pawnMove)
                    {
                        Track(_pawnTrack, pawnId.Value, pawnMove.Location, 0.5f);
                    }
                }
                break;

            case FortPlayerState state:
                var psId = Channels[channelIndex]?.ActorId;
                var playerId = state.PlayerId is null ? state.PlayerID : (int?)state.PlayerId;
                if (psId != null && playerId != null)
                {
                    _playerStateToPlayerId[psId.Value] = playerId.Value;
                }
                break;
        }
    }

    /// <summary>
    /// Parses <paramref name="file"/> and returns what the tracked player did with chests.
    /// </summary>
    /// <param name="username">Epic display name to track. If null/empty or not found, the replay owner is used.</param>
    private List<SquadmateChestCount>? CountSquadmates(IReadOnlyList<PlayerData> players, PlayerData? target, HashSet<uint> targetPawns)
    {
        if (target?.Id is not > 0 || target.TeamIndex is not > 0)
        {
            return null;
        }

        // TeamIndex is assigned within the match. Reject large teams and exclude bots and the tracked player.
        var team = players
            .Where(p => p.TeamIndex == target.TeamIndex && !p.IsBot && p.Id is > 0)
            .GroupBy(p => p.Id!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        if (team.Count > MAX_SQUAD_SIZE || !team.Remove(target.Id.Value) || team.Count == 0)
        {
            return null;
        }

        var counts = new Dictionary<int, int>();
        foreach (var search in _searches)
        {
            if ((!search.Stale || StableKey(search.ActorKey)) && search.SearcherPawn is uint pawn && !targetPawns.Contains(pawn)
                && _pawnToPlayerState.TryGetValue(pawn, out var state)
                && _playerStateToPlayerId.TryGetValue(state, out var playerId)
                && team.ContainsKey(playerId) && ChestKinds.IsChest(ChestKinds.Classify(search.Label)))
            {
                counts[playerId] = counts.GetValueOrDefault(playerId) + 1;
            }
        }

        return team.Select(p => new SquadmateChestCount(p.Value.PlayerName ?? p.Value.PlayerNameCustomOverride, counts.GetValueOrDefault(p.Key)))
            .OrderByDescending(p => p.ChestsOpened)
            .ThenBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<TeammateRecord>? CountTeammates(IReadOnlyList<PlayerData> players, PlayerData? target, HashSet<uint> targetPawns)
    {
        if (target?.Id is not > 0 || target.TeamIndex is not > 0) return null;
        var team = players.Where(p => p.TeamIndex == target.TeamIndex && !p.IsBot && p.Id is > 0)
            .GroupBy(p => p.Id!.Value).ToDictionary(g => g.Key, g => g.First());
        if (team.Count > MAX_SQUAD_SIZE || !team.Remove(target.Id.Value) || team.Count == 0) return null;
        var counts = team.Keys.ToDictionary(id => id, _ => new Dictionary<string, int>());
        foreach (var search in _searches)
        {
            if ((search.Stale && !StableKey(search.ActorKey)) || search.SearcherPawn is not uint pawn
                || targetPawns.Contains(pawn) || !_pawnToPlayerState.TryGetValue(pawn, out var state)
                || !_playerStateToPlayerId.TryGetValue(state, out var id) || !counts.TryGetValue(id, out var kinds)) continue;
            var kind = ChestKinds.Classify(search.Label);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
        return team.Select(p => new TeammateRecord
        {
            Name = p.Value.PlayerName ?? p.Value.PlayerNameCustomOverride ?? L.T("Compañero", "Teammate"),
            EpicId = p.Value.EpicId,
            ChestsSeen = counts[p.Key].Where(k => ChestKinds.IsChest(k.Key)).Sum(k => k.Value),
            Kinds = counts[p.Key],
        }).OrderByDescending(t => t.ChestsSeen).ToList();
    }

    private bool CanCountDestroyedChest(uint channelIndex, string key, bool isStale, uint? searchAnimationCount)
    {
        if (_searched.Add(key))
        {
            return true;
        }

        if (_channelSearched.TryGetValue(channelIndex, out var hasBeenSearched))
        {
            return !hasBeenSearched;
        }

        // A refilled chest can reopen its channel with the default unsearched state omitted.
        // A new loot animation and explicit destruction after channel open confirm the new interaction.
        return !isStale && searchAnimationCount is > 0;
    }


    public MatchRecord Analyze(Stream stream, string file, string? username)
    {
        var replay = ReadReplay(stream);

        if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "4")
        {
            var groupFilter = new Regex(Environment.GetEnvironmentVariable("CHESTCOUNTER_PROBE_GROUPS") ?? "AbilitySpec", RegexOptions.IgnoreCase);
            foreach (var (path, group) in _netGuidCache.NetFieldExportGroupMap.Where(kv => groupFilter.IsMatch(kv.Key)))
            {
                Console.Error.WriteLine($"GRP {path} ({group.NetFieldExportsLength})");
                for (var i = 0; i < group.NetFieldExports.Length; i++)
                {
                    if (group.NetFieldExports[i] is { } e) Console.Error.WriteLine($"GRP   [{i}] {e.Name}");
                }
            }
        }

        // --- Who are we tracking? -------------------------------------------------------------
        var players = replay.PlayerData?.ToList() ?? new List<PlayerData>();
        PlayerData? target = null;
        var matchedByName = false;

        // One or several Epic names (several accounts), separated by commas. Strict: if names are configured,
        // only those players are counted; a match played by another account is not counted at all.
        var names = (username ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length > 0)
        {
            target = players.FirstOrDefault(p => names.Any(n => string.Equals(p.PlayerName?.Trim(), n, StringComparison.OrdinalIgnoreCase)));
            matchedByName = target != null;
        }

        uint? recorderPs = replay.GameData?.RecorderId;
        PlayerData? recorderPlayer = null;
        if (recorderPs != null && _playerStateToPlayerId.TryGetValue(recorderPs.Value, out var recorderPlayerId))
        {
            recorderPlayer = players.FirstOrDefault(p => p.Id == recorderPlayerId);
        }
        var notTracked = names.Length > 0 && target == null;
        if (target == null && !notTracked)
        {
            target = recorderPlayer;
        }

        // PlayerState actor(s) of the target -> pawn(s) of the target
        var targetPlayerStates = new HashSet<uint>();
        if (target?.Id != null)
        {
            foreach (var kv in _playerStateToPlayerId.Where(kv => kv.Value == target.Id))
            {
                targetPlayerStates.Add(kv.Key);
            }
        }
        if (targetPlayerStates.Count == 0 && recorderPs != null && !matchedByName && !notTracked)
        {
            targetPlayerStates.Add(recorderPs.Value);
        }

        var isRecorderTarget = recorderPs != null && targetPlayerStates.Contains(recorderPs.Value);
        var targetPawns = _pawnToPlayerState.Where(kv => targetPlayerStates.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet();

        // --- Everything the target searched / interacted with ------------------------------------
        // A stale search of a stable container is real (old searches seen again were dropped, and the target was right
        // there: dormant ammo box, chest broken...). A stale search of a container without identity is not counted.
        ResolveOrphans();
        var interactions = _searches
            .Where(s => s.SearcherPawn is uint p && targetPawns.Contains(p) && (!s.Stale || StableKey(s.ActorKey)))
            .OrderBy(s => s.Time)
            .Select(s => (s.Time, Kind: ChestKinds.Classify(s.Label), s.Label, s.ActorKey, s.Destroyed))
            .ToList();

        var chests = new List<ChestRecord>();
        var others = new Dictionary<string, int>();
        foreach (var search in interactions)
        {
            if (!ChestKinds.IsChest(search.Kind))
            {
                others[search.Kind] = others.GetValueOrDefault(search.Kind) + 1;
                continue;
            }

            var items = _tosses
                .Where(t => t.Time >= search.Time - 0.25f && t.Time <= search.Time + 0.6f)
                .Select(t => t.Item)
                .ToList();

            chests.Add(new ChestRecord
            {
                TimeSeconds = MathF.Round(search.Time, 1),
                Kind = search.Kind,
                Blueprint = search.Label,
                Destroyed = search.Destroyed,
                Items = items,
                TreasureItems = items.Where(i => TreasureItem.IsMatch(i) && !EventItem.IsMatch(i)).ToList(),
            });
        }

        if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "1")
        {
            string NameOfPawn(uint pawn) =>
                _pawnToPlayerState.TryGetValue(pawn, out var ps) && _playerStateToPlayerId.TryGetValue(ps, out var pid)
                    ? players.FirstOrDefault(p => p.Id == pid)?.PlayerName ?? $"pid{pid}" : $"pawn{pawn}";
            foreach (var e in _equips)
            {
                Console.Error.WriteLine($"DBG equip {e.Time,7:F1}s {e.Item} by {NameOfPawn(e.Pawn)}{(targetPawns.Contains(e.Pawn) ? " (TARGET)" : "")}");
            }
            foreach (var ex in _firstExport)
            {
                Console.Error.WriteLine($"DBG export {ex.Value,7:F1}s {ex.Key}");
            }
            foreach (var hg in _treasureHandlerGuids)
            {
                Console.Error.WriteLine($"DBG handlerguid {hg.Key} {hg.Value}");
            }
            foreach (var gr in _grants)
            {
                var psName = _playerStateToPlayerId.TryGetValue(gr.PlayerStateActor, out var gpid) ? players.FirstOrDefault(p => p.Id == gpid)?.PlayerName ?? $"pid{gpid}" : $"ps{gr.PlayerStateActor}";
                Console.Error.WriteLine($"DBG grant {gr.Time,7:F1}s {gr.Handler} elem={gr.ElementId} to {psName}{(targetPlayerStates.Contains(gr.PlayerStateActor) ? " (TARGET)" : "")}");
                foreach (var se in _searches.Where(x => x.Time >= gr.Time - 3 && x.Time <= gr.Time + 2).OrderBy(x => Math.Abs(x.Time - gr.Time)).Take(2))
                {
                    Console.Error.WriteLine($"DBG   near {se.Time,7:F1}s {CleanName(se.Label)} by {(se.SearcherPawn is uint sp2 ? NameOfPawn(sp2) : "?")}");
                }
            }
            foreach (var ex in _firstExport.Where(kv => kv.Key.Contains("TreasureHandler")).Take(1))
            {
                foreach (var se in _searches.Where(x => x.Time >= ex.Value - 6 && x.Time <= ex.Value + 3).OrderBy(x => x.Time))
                {
                    Console.Error.WriteLine($"DBG nearhandler {se.Time,7:F1}s (Δ{se.Time - ex.Value:+0.0;-0.0}) {CleanName(se.Label)}{(se.Destroyed ? " DÉTRUIT" : "")} by {(se.SearcherPawn is uint sp ? NameOfPawn(sp) : "?")}{(se.SearcherPawn is uint tp2 && targetPawns.Contains(tp2) ? " (MOI)" : "")}");
                }
            }
            foreach (var pz in _prizes)
            {
                Console.Error.WriteLine($"DBG prize {pz.Time,7:F1}s {pz.PrizeId} by {(pz.Pawn is uint pp ? NameOfPawn(pp) : "?")}{(pz.Pawn is uint tp && targetPawns.Contains(tp) ? " (TARGET)" : "")} chest={CleanName(pz.Container)}");
            }
            foreach (var s in _searches.Where(s => s.Label.Contains("Cannon", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"DBG cannon {s.Time,7:F1}s {s.Label} by {(s.SearcherPawn is uint cp ? NameOfPawn(cp) : "?")}");
            }
            foreach (var i in interactions)
            {
                Console.Error.WriteLine($"DBG interact {i.Time,7:F1}s {i.Kind} {i.Label}{(i.Destroyed ? " (DÉTRUIT)" : "")}");
            }
        }

        // --- Treasures obtained by the target ----------------------------------------------------
        var finds = new List<TreasureFind>();

        // 1) Golden treasure chest: the server spawns an animated chest carrying the prize id and its source container. Certain.
        foreach (var pz in _prizes.Where(p => p.Pawn is uint pawn && targetPawns.Contains(pawn)))
        {
            var label = CleanName(pz.Container);
            var kind = string.IsNullOrEmpty(label) ? "inconnu" : ChestKinds.Classify(label);
            finds.Add(new TreasureFind
            {
                Item = TreasureNames.Friendly(pz.PrizeId),
                RawItem = pz.PrizeId,
                PrizeId = pz.PrizeId,
                FromGoldenChest = true,
                TimeExact = true,
                SourceDestroyed = interactions.Any(i => i.Destroyed && i.ActorKey == pz.Container),
                ObtainedAtSeconds = MathF.Round(pz.Time, 1),
                SourceKind = kind,
                SourceBlueprint = label,
                SourceIsChest = ChestKinds.IsChest(kind),
                SourceCertain = true,
            });
        }

        // Teammates (same team as the target): treasures are shared with the whole team, so the recording player is
        // granted each treasure a teammate finds too. Those are listed apart, never in the player's own count.
        var teammatePawns = new Dictionary<uint, string>();
        if (target?.TeamIndex is int teamIndex)
        {
            foreach (var mate in players.Where(p => p.TeamIndex == teamIndex && p.Id != target.Id))
            {
                var mateStates = _playerStateToPlayerId.Where(kv => kv.Value == mate.Id).Select(kv => kv.Key).ToHashSet();
                foreach (var kv in _pawnToPlayerState.Where(kv => mateStates.Contains(kv.Value)))
                {
                    teammatePawns[kv.Key] = mate.PlayerName ?? "";
                }
            }
        }
        var hasTeam = target?.TeamIndex is int ti && players.Any(p => p.TeamIndex == ti && p.Id != target.Id);

        // Chests of each teammate, counted exactly like the player's own (same events, same rules). The only limit
        // is what the replay holds: a chest is recorded when it is within about 150 m of the recording player, or
        // when that player comes by later while it still shows who searched it.
        var teamChests = new List<TeamChestCount>();
        if (target?.TeamIndex is int squad)
        {
            foreach (var mate in players.Where(p => p.TeamIndex == squad && p.Id != target.Id))
            {
                var matePawns = teammatePawns.Keys.Where(k => _pawnToPlayerState.TryGetValue(k, out var mps) && _playerStateToPlayerId.GetValueOrDefault(mps, -1) == mate.Id).ToHashSet();
                var mateChests = _searches
                    .Where(s => s.SearcherPawn is uint p && matePawns.Contains(p) && (!s.Stale || StableKey(s.ActorKey)) && ChestKinds.IsChest(ChestKinds.Classify(s.Label)))
                    .ToList();
                teamChests.Add(new TeamChestCount
                {
                    Name = mate.PlayerName ?? "",
                    EpicId = mate.EpicId,
                    IsBot = mate.IsBot,
                    Chests = mateChests.Count,
                    Destroyed = mateChests.Count(s => s.Destroyed),
                });
            }
        }
        var (vehicleSeconds, vehicleMeters, unrecordedLoot) = VehicleRuns(targetPawns);
        var teamFinds = new List<TreasureFind>();
        foreach (var pz in _prizes.Where(p => p.Pawn is uint pawn && teammatePawns.ContainsKey(pawn)))
        {
            var label = CleanName(pz.Container);
            var kind = string.IsNullOrEmpty(label) ? "inconnu" : ChestKinds.Classify(label);
            teamFinds.Add(new TreasureFind
            {
                Item = TreasureNames.Friendly(pz.PrizeId),
                RawItem = pz.PrizeId,
                PrizeId = pz.PrizeId,
                FromGoldenChest = true,
                TimeExact = true,
                ObtainedAtSeconds = MathF.Round(pz.Time, 1),
                SourceKind = kind,
                SourceBlueprint = label,
                SourceIsChest = ChestKinds.IsChest(kind),
                SourceCertain = true,
                Teammate = teammatePawns[pz.Pawn!.Value],
            });
        }

        // 2) Items seen in the target's hands are NOT counted on their own (they can come from a teammate,
        //    since treasures are shared by team, or from elsewhere). They are only used to name a treasure below.
        //    Event loot that only looks like a treasure is left out.
        var equippedByMe = _equips.Where(e => targetPawns.Contains(e.Pawn) && !EventItem.IsMatch(e.Item)).OrderBy(e => e.Time).ToList();

        // 3) An item thrown out of a container as a pickup is never a treasure: treasure hunt items go straight into
        //    the inventory and grant a prize handler (4). The only such pickups seen are "happy hour" event extractors
        //    (WID_RelicExtractor_Consumable), plain loot that must not be counted.

        // 4) Prize handlers: every treasure received grants a "GA_TreasureHandler_*" ability on the player state of
        //    the recording player (exact time, one grant per treasure). Counted only if the chest opened/destroyed at
        //    that moment is the target's own (not a teammate's: treasures are shared with the team).
        //    Fallback when no grant could be decoded: first export of the handler class (first treasure only).
        var handlerTimes = _grants
            .Where(g => targetPlayerStates.Contains(g.PlayerStateActor))
            .Select(g => g.Time)
            .OrderBy(t => t)
            .ToList();
        if (handlerTimes.Count == 0 && isRecorderTarget
            && _firstExport.Where(kv => kv.Key.Contains("TreasureHandler")).Select(kv => (float?)kv.Value).FirstOrDefault() is float firstHandler)
        {
            handlerTimes.Add(firstHandler);
        }

        for (var hi = 0; hi < handlerTimes.Count; hi++)
        {
            var h = handlerTimes[hi];
            var nextHandler = hi + 1 < handlerTimes.Count ? handlerTimes[hi + 1] : float.MaxValue;
            // Buried chests grant the prize when the dig completes, ~2.5s before the chest reports "searched": allow
            // up to +4s for them only.
            var closest = _searches
                .Where(x => x.Time >= h - 3 && (x.Time <= h + 2 || (x.Time <= h + 4 && ChestKinds.Classify(x.Label) == ChestKinds.Buried)))
                .OrderBy(x => Math.Abs(x.Time - h))
                .FirstOrDefault();
            var mine = closest != null && closest.SearcherPawn is uint cp && targetPawns.Contains(cp);
            var golden = finds.Concat(teamFinds).FirstOrDefault(f => f.FromGoldenChest && f.ObtainedAtSeconds is float g && Math.Abs(g - h) <= 5);
            if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "1")
            {
                Console.Error.WriteLine($"DBG decide {h,7:F1}s {(golden != null ? "GOLDEN" : mine ? "MINE" : closest != null ? "OTHER" : "NO-SEARCH")} closest={(closest == null ? "-" : $"{closest.Time:F1}s {closest.Label}")} by={(closest?.SearcherPawn is not uint who ? "?" : targetPawns.Contains(who) ? "me" : teammatePawns.ContainsKey(who) ? "mate" : "enemy")}");
            }

            if (mine && golden == null)
            {
                var kind = ChestKinds.Classify(closest!.Label);

                // Item identity: an item first sent at that exact moment, otherwise the only treasure item the
                // target later had in hand (if there is exactly one kind).
                var item = _firstExport.Where(kv => kv.Key.StartsWith("WID_") && !EventItem.IsMatch(kv.Key) && Math.Abs(kv.Value - h) <= 1).Select(kv => kv.Key).FirstOrDefault();
                if (item == null)
                {
                    var later = equippedByMe.Where(e => e.Time >= h && e.Time < nextHandler).Select(e => TreasureNames.Friendly(e.Item)).Where(n => !n.StartsWith("B_")).Distinct().ToList();
                    if (later.Count == 1)
                    {
                        item = equippedByMe.First(e => e.Time >= h && e.Time < nextHandler && TreasureNames.Friendly(e.Item) == later[0]).Item;
                    }
                }
                var itemName = item != null ? TreasureNames.Friendly(item) : null;

                // No id in the replay: a unique key per treasure (shown as a treasure gizmo).
                var matchStart = replay.GameData?.UtcTimeStartedMatch ?? replay.Info?.Timestamp ?? DateTime.UtcNow;
                var localStart = DateTime.SpecifyKind(matchStart, DateTimeKind.Utc).ToLocalTime();
                var hr = MathF.Round(h, 1);
                var unknownKey = item == null ? $"{TreasureNames.UnknownPrefix}{localStart:yyyy-MM-dd_HH.mm}@{(int)hr / 60}m{(int)hr % 60:00}s" : null;

                finds.Add(new TreasureFind
                {
                    Item = itemName ?? TreasureNames.UnknownGizmo,

                    RawItem = item ?? "GA_TreasureHandler",
                    PrizeId = unknownKey,
                    ObtainedAtSeconds = hr,
                    TimeExact = true,
                    SourceKind = kind,
                    SourceBlueprint = closest.Label,
                    SourceIsChest = ChestKinds.IsChest(kind),
                    SourceCertain = true,
                    SourceDestroyed = closest.Destroyed,
                    FromPrizeHandler = true,
                });
            }
            else if (!mine && golden == null && hasTeam && isRecorderTarget)
            {
                // A teammate's treasure: the chest opened at that moment by a teammate, or one out of the recording
                // player's sight (no chest seen at all).
                var byMate = closest?.SearcherPawn is uint sp && teammatePawns.TryGetValue(sp, out var mateName) ? mateName : null;
                var kind = byMate != null ? ChestKinds.Classify(closest!.Label) : "inconnu";
                teamFinds.Add(new TreasureFind
                {
                    Item = TreasureNames.UnknownGizmo,
                    RawItem = "GA_TreasureHandler",
                    ObtainedAtSeconds = MathF.Round(h, 1),
                    TimeExact = true,
                    SourceKind = kind,
                    SourceBlueprint = byMate != null ? closest!.Label : "",
                    SourceIsChest = byMate == null || ChestKinds.IsChest(kind),
                    SourceCertain = byMate != null,
                    SourceDestroyed = byMate != null && closest!.Destroyed,
                    FromPrizeHandler = true,
                    Teammate = byMate ?? "",
                });
            }
        }

        if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "1")
        {
            var enemy = _searches.Count(x => x.SearcherPawn is uint w && !targetPawns.Contains(w) && !teammatePawns.ContainsKey(w) && ChestKinds.IsChest(ChestKinds.Classify(x.Label)));
            var mates = _searches.Count(x => x.SearcherPawn is uint w && teammatePawns.ContainsKey(w) && ChestKinds.IsChest(ChestKinds.Classify(x.Label)));
            var goldenEnemy = _prizes.Count(x => x.Pawn is uint w && !targetPawns.Contains(w) && !teammatePawns.ContainsKey(w));
            Console.Error.WriteLine($"DBG searches enemy={enemy} mates={mates} duration={replay.Info?.LengthInMs / 1000} goldenEnemy={goldenEnemy} team={teammatePawns.Count}");
        }
        teamFinds.Sort((a, b) => (a.ObtainedAtSeconds ?? float.MaxValue).CompareTo(b.ObtainedAtSeconds ?? float.MaxValue));

        // Golden chests of everybody else in reach: the server sends the animated chest, with its prize and the pawn that
        // opened it, to every player around (same as for the player's own).
        var seen = new List<SeenPrize>();
        foreach (var pz in _prizes.Where(p => p.Pawn is uint pawn && !targetPawns.Contains(pawn) && !teammatePawns.ContainsKey(pawn)))
        {
            var who = _pawnToPlayerState.TryGetValue(pz.Pawn!.Value, out var ops) && _playerStateToPlayerId.TryGetValue(ops, out var opid)
                ? players.FirstOrDefault(p => p.Id == opid)
                : null;
            seen.Add(new SeenPrize
            {
                Player = who?.PlayerName ?? (who?.IsBot == true ? "Bot" : L.T("joueur inconnu", "unknown player", "jugador desconocido")),
                PrizeId = pz.PrizeId,
                TimeSeconds = MathF.Round(pz.Time, 1),
            });
        }
        finds.Sort((a, b) => (a.ObtainedAtSeconds ?? float.MaxValue).CompareTo(b.ObtainedAtSeconds ?? float.MaxValue));

        var info = replay.Info;
        var header = replay.Header;
        var isRecorder = recorderPs != null && targetPlayerStates.Contains(recorderPs.Value);
        var placement = target?.Placement ?? (isRecorder ? (int?)replay.TeamStats?.Position : null);

        return new MatchRecord
        {
            SessionId = replay.GameData?.GameSessionId ?? $"{info?.Timestamp:O}-{Path.GetFileName(file)}",
            ReplayFile = Path.GetFileName(file),
            AnalyzedAtUtc = DateTime.UtcNow,
            MatchStartUtc = replay.GameData?.UtcTimeStartedMatch ?? info?.Timestamp,
            DurationSeconds = info != null ? (int)(info.LengthInMs / 1000) : null,
            Playlist = replay.GameData?.CurrentPlaylist,
            GameVersion = header?.Branch,
            PlayerName = target?.PlayerName,
            EpicId = target?.EpicId,
            TrackedByUsername = matchedByName,
            NotTrackedOwner = notTracked ? (recorderPlayer?.PlayerName ?? "inconnu") : null,
            Placement = placement,
            TotalPlayers = (int?)replay.TeamStats?.TotalPlayers,
            IsReplayOwner = isRecorder,
            Eliminations = isRecorder ? (int?)replay.Stats?.Eliminations : null,
            ChestsOpened = chests.Count,
            Squadmates = CountSquadmates(players, target, targetPawns),
            Teammates = CountTeammates(players, target, targetPawns),
            AmmoBoxesOpened = others.GetValueOrDefault(ChestKinds.AmmoBox),
            OtherContainersOpened = others.Where(kv => kv.Key != ChestKinds.AmmoBox).Sum(kv => kv.Value),
            OtherInteractions = others,
            AnalyzerVersion = CurrentVersion,
            Map = header?.LevelNamesAndTimes?.Select(l => l.Item1).FirstOrDefault(l => !string.IsNullOrEmpty(l)),
            IsCustomMatch = replay.GameData?.IsCustomMatch == true,
            Chests = chests,
            TreasureFinds = finds,
            TeamTreasureFinds = teamFinds,
            SeenPrizes = seen,
            TeamChests = teamChests,
            TeamChestsOpened = teamChests.Sum(t => t.Chests),
            VehicleSeconds = vehicleSeconds,
            VehicleMeters = vehicleMeters,
            UnrecordedVehicleLoot = unrecordedLoot,
        };
    }

    /// <summary>
    /// Gives each closed chest destroyed without an instigator to the player who stood right at it when its loot
    /// spilled (within 4.5 m, nobody closer). The loot is the chest's own for sure: the server makes the container
    /// the owner of the pickups it throws out, which also tells where a level placed chest stands. Measured on 99
    /// replays: a player's own chests spill their loot at 1 to 4 m (2 m median); in every such case looked at, the
    /// player had just broken a prop next to the chest.
    /// </summary>
    private void ResolveOrphans()
    {
        foreach (var orphan in _orphans)
        {
            var loot = _tosses.Where(x => x.Loc != null && x.Owner == orphan.Actor && Math.Abs(x.Time - orphan.Time) <= 2f).Select(x => x.Loc!.Value).ToList();
            if (loot.Count == 0)
            {
                continue;
            }

            uint nearest = 0;
            var nearestDistance = 450f;
            foreach (var pawn in _pawnToPlayerState.Keys)
            {
                if (PositionOf(pawn, orphan.Time) is not { } at)
                {
                    continue;
                }
                var d = loot.Min(l => Distance(at, l));
                if (d < nearestDistance)
                {
                    nearestDistance = d;
                    nearest = pawn;
                }
            }
            if (nearest == 0)
            {
                continue;
            }

            if (Debug == "1")
            {
                Console.Error.WriteLine($"DBG orphan {orphan.Time,7:F1}s {orphan.Label} nearest pawn {nearest} at {nearestDistance / 100:F1} m");
            }
            if (_searched.Add(orphan.ActorKey + "#orphan" + orphan.Time.ToString("F0")))
            {
                _searches.Add(new SearchEvent(orphan.Time, orphan.ActorKey, orphan.Label, nearest, Destroyed: true));
            }
        }
    }

    /// <summary>
    /// Time and distance the player spent in vehicles, and the loot piles found along those drives that no recorded
    /// container explains. Fortnite sometimes does not record what a fast vehicle destroys (the chests then show up
    /// intact in its own replay viewer): the chests are not in the replay and cannot be counted, but the loot left
    /// behind shows that something is missing.
    /// </summary>
    private (int Seconds, int Meters, int UnrecordedLoot) VehicleRuns(HashSet<uint> pawns)
    {
        var path = new List<Pt>();
        var seconds = 0f;
        var centimeters = 0f;
        foreach (var span in _seatSpans.Where(s => pawns.Contains(s.Pawn)))
        {
            var points = (_vehicleTrack.GetValueOrDefault(span.Vehicle) ?? new List<Pt>()).Where(p => p.T >= span.From && p.T <= span.To).ToList();
            if (points.Count < 2)
            {
                continue;
            }
            seconds += points[^1].T - points[0].T;
            for (var i = 1; i < points.Count; i++)
            {
                var step = Distance(points[i - 1], points[i]);
                centimeters += step;
                // One point per meter, so that a pile next to the road is never between two points.
                var parts = Math.Max(1, (int)(step / 100));
                for (var k = 0; k < parts; k++)
                {
                    var f = (float)k / parts;
                    path.Add(new Pt(points[i - 1].T + (points[i].T - points[i - 1].T) * f,
                        points[i - 1].X + (points[i].X - points[i - 1].X) * f,
                        points[i - 1].Y + (points[i].Y - points[i - 1].Y) * f,
                        points[i - 1].Z + (points[i].Z - points[i - 1].Z) * f));
                }
            }
        }
        if (path.Count == 0)
        {
            return (0, 0, 0);
        }

        // Loot that appeared within 9 m of the drive, not before the vehicle got there (PassedAt = when it did).
        // A pickup lying on the ground is sent again each time the player comes back near it, still marked as tossed
        // from a container: only its first appearance counts. Otherwise the loot of a chest opened long before looks,
        // on the next drive past it, like a chest the vehicle broke.
        var seen = new Dictionary<string, List<Pt>>();
        var near = new List<(Pt Loc, string Item, float PassedAt, uint? Owner)>();
        foreach (var toss in _tosses.Where(t => t.Loc != null))
        {
            var loc = toss.Loc!.Value;
            if (!seen.TryGetValue(toss.Item, out var spots))
            {
                seen[toss.Item] = spots = new();
            }
            var sentAgain = spots.Any(s => Distance(s, loc) < 150);
            spots.Add(loc);
            if (sentAgain)
            {
                continue;
            }
            var best = float.MaxValue;
            var passedAt = 0f;
            foreach (var p in path)
            {
                var d = Distance(p, loc);
                if (d < best)
                {
                    best = d;
                    passedAt = p.T;
                }
            }
            if (best < 900 && toss.Time >= passedAt - 1)
            {
                near.Add((loc, toss.Item, passedAt, toss.Owner));
            }
        }

        // Piles (items within 5 m of each other) holding more than ammo, with no container searched or destroyed
        // by anyone when the vehicle went by, and not dropped by a container the replay shows opened (a chest opened
        // by someone just after the drive).
        var piles = new List<List<(Pt Loc, string Item, float PassedAt, uint? Owner)>>();
        foreach (var item in near)
        {
            var pile = piles.FirstOrDefault(p => p.Any(o => Distance(o.Loc, item.Loc) < 500));
            if (pile == null)
            {
                piles.Add(pile = new());
            }
            pile.Add(item);
        }
        var unexplained = piles.Count(p => p.Any(i => !i.Item.StartsWith("AthenaAmmoData", StringComparison.OrdinalIgnoreCase))
            && !p.Any(i => i.Owner is uint o && (_openedContainers.Contains(o) || _orphans.Any(x => x.Actor == o)))
            && !_searches.Any(s => Math.Abs(s.Time - p[0].PassedAt) <= 4));
        if (Debug == "1")
        {
            Console.Error.WriteLine($"DBG vehicle {seconds:F0}s {centimeters / 100:F0}m piles={piles.Count} unexplained={unexplained}");
        }
        return ((int)seconds, (int)(centimeters / 100), unexplained);
    }
}

public static class TreasureNames
{
    /// <summary>Prize id -> display name, loaded from treasures.json (user editable).</summary>
    public static Dictionary<string, string> PrizeNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key prefix of a treasure whose prize id is not in the replay (another prefix before version 1.1).</summary>
    public const string UnknownPrefix = "Unknown:";
    public const string UnknownGizmo = "Treasure gizmo";
    private const string OldUnknownPrefix = "Inconnu:";

    /// <summary>Display name of a prize id (from treasures.json), or of a treasure item blueprint.</summary>
    public static string Friendly(string item)
    {
        if (item.Contains(':'))
        {
            var unknown = item.StartsWith(UnknownPrefix, StringComparison.OrdinalIgnoreCase) || item.StartsWith(OldUnknownPrefix, StringComparison.OrdinalIgnoreCase);
            if (PrizeNames.TryGetValue(item, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
            if (TreasureCatalog.Find(item) is { } prize) return prize.Name;
            if (unknown)
            {
                // Same treasure under the other prefix (named by the player with an older version, or re-analyzed).
                var rest = item[(item.IndexOf(':') + 1)..];
                foreach (var prefix in new[] { UnknownPrefix, OldUnknownPrefix })
                {
                    if (PrizeNames.TryGetValue(prefix + rest, out var other) && !string.IsNullOrWhiteSpace(other))
                    {
                        return other;
                    }
                }
                // Not a golden chest prize (those carry their id): one of the treasure hunt gizmos (golden
                // firework, portable extractor, locators, extraction accelerator), exact one not in the replay.
                return L.Name(UnknownGizmo);
            }
            return L.F("Tesoro desconocido ({0})", "Unknown treasure ({0})", item[(item.IndexOf(':') + 1)..]);
        }


        var i = item.ToLowerInvariant();
        if (i.Contains("flameceleb")) return "Golden firework";
        if (i.Contains("relicextractor")) return "Portable extractor";
        if (i.Contains("relicfinder")) return "Spirit locator";
        if (i.Contains("luckylocator")) return "Lucky locator";
        if (i.Contains("smugglerextractionkey") || i.Contains("extractionaccelerat")) return "Extraction accelerator";
        return item;
    }
}

public static class ChestKinds
{
    // Chests (counted in the total)
    public const string Normal = "Chest";
    public const string Rare = "Rare chest";
    public const string Buried = "Buried chest";
    public const string Extraction = "Extraction / relic chest";
    public const string PosterChest = "Spirit poster chest";
    public const string Other = "Other chest";

    // Other interactions (tracked separately, not in the chest total)
    public const string Cannon = "Loot cannon";
    public const string AmmoBox = "Ammo box";
    public const string FoodBox = "Food box";
    public const string OtherContainer = "Other container";

    private static readonly HashSet<string> ChestSet = new() { Normal, Rare, Buried, Extraction, PosterChest, Other };

    public static bool IsChest(string kind) => ChestSet.Contains(kind);

    public static string Classify(string blueprint)
    {
        var b = blueprint.ToLowerInvariant();
        if (b.Contains("lootcannon") || b.Contains("cannon")) return Cannon;
        if (b.Contains("ammo")) return AmmoBox;
        if (b.Contains("foodbox") || b.Contains("food_box")) return FoodBox;
        if (b.Contains("rarechest") || b.Contains("legendary") || b.Contains("rare_chest")) return Rare;
        if (b.Contains("buried")) return Buried;
        if (b.Contains("spriteposter") || b.Contains("booty")) return PosterChest;
        if (b.Contains("relic") || b.Contains("extraction")) return Extraction;
        if (b.Contains("tiered_chest") || b.Contains("normalchest") || b.Contains("standardchest")) return Normal;
        // Coolers, barrels, etc. are not chests.
        return b.Contains("chest") ? Other : OtherContainer;
    }
}
