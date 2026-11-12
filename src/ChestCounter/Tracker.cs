using System.Text;

namespace ChestCounter;

/// <summary>
/// Vigila la carpeta de replays de Fortnite, analiza una vez cada replay terminado y acumula los resultados.
/// </summary>
public sealed class Tracker
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Replay terminado por Fortnite (ya no en directo, con la clave real escrita): bastan unos segundos sin cambios.</summary>
    private static readonly TimeSpan FinalizedQuietPeriod = TimeSpan.FromSeconds(4);

    /// <summary>Un replay terminado sin cambios desde hace este tiempo está completo seguro (replays de antes).</summary>
    private static readonly TimeSpan OldReplayAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Dictionary<string, long> _lastSeenSize = new();

    public Tracker(Store store, Config config, TrackerData data)
    {
        _store = store;
        _config = config;
        _data = data;

        // Antes de la versión 1.1, las partidas de otras cuentas se rechazaban (valor antiguo "autre joueur"): ahora tienen su propio perfil.
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value.StartsWith("autre joueur", StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }

        // Historial de versiones anteriores a la 2.0.9: nombres internos en francés -> nombres actuales.
        if (_data.UpgradeNames())
        {
            try { _store.SaveData(_data); } catch { /* se guardará con el próximo cambio */ }
        }
    }

    public TrackerData Data => _data;

    /// <summary>True while a replay is waiting to be finished / to become stable.</summary>
    public bool HasPending { get; private set; }

    /// <summary>True when a match has just ended and its replay is about to be read: check again within seconds.</summary>
    public bool IsFinishing { get; private set; }

    /// <summary>Devuelve a Windows la memoria usada por el análisis (la herramienta en reposo ocupa unos pocos MB).</summary>
    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    public static string FileKey(FileInfo f) => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    public IEnumerable<FileInfo> ReplayFiles()
    {
        var dir = new DirectoryInfo(_config.DemosFolder);
        if (!dir.Exists)
        {
            return Enumerable.Empty<FileInfo>();
        }
        return dir.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc);
    }

    public IEnumerable<FileInfo> UnprocessedFiles() => ReplayFiles().Where(f => !_data.ProcessedFiles.ContainsKey(FileKey(f)));

    /// <summary>Replays not analyzed yet and not being recorded (what the next scan will read).</summary>
    public int BacklogCount() => UnprocessedFiles().Count(f => ReplayHeaderProbe.IsLive(f.FullName) != true);

    /// <summary>
    /// Deletes the whole history (a copy of data.json is saved to <paramref name="backupPath"/> first) and marks
    /// the replays already in the folder as skipped, so that only the next matches are counted. The match being
    /// played, if any, still counts.
    /// </summary>
    public void Reset(string backupPath)
    {
        if (File.Exists(_store.DataPath))
        {
            File.Copy(_store.DataPath, backupPath, true);
        }
        _data.Matches.Clear();
        _data.ProcessedFiles.Clear();
        foreach (var f in ReplayFiles().Where(f => ReplayHeaderProbe.IsLive(f.FullName) != true))
        {
            _data.ProcessedFiles[FileKey(f)] = "skipped";
        }
        _store.SaveData(_data);
    }

    public void MarkSkipped(IEnumerable<FileInfo> files)
    {
        foreach (var f in files)
        {
            _data.ProcessedFiles[FileKey(f)] = "skipped";
        }
        _store.SaveData(_data);
    }

    /// <summary>Result of the analysis of one replay.</summary>
    private sealed record Outcome(MatchRecord? Match = null, bool Updated = false, string? Skipped = null, string? Error = null, List<string>? NewPrizeIds = null);

    /// <summary>
    /// One pass over the folder. Returns the number of new matches recorded. Several replays at once (replays left
    /// from earlier) are shown as a progress bar and a summary; a single one (end of a match) gets its own line.
    /// </summary>
    /// <param name="ignoreQuietPeriod">Process finished replays immediately (used by --once).</param>
    public int Scan(bool ignoreQuietPeriod = false)
    {
        HasPending = false;
        IsFinishing = false;
        var ready = new List<FileInfo>();
        foreach (var file in UnprocessedFiles().ToList())
        {
            file.Refresh();
            if (!file.Exists)
            {
                continue;
            }
            if (!IsReady(file, ignoreQuietPeriod))
            {
                HasPending = true;
                continue;
            }
            ready.Add(file);
        }
        if (ready.Count == 0)
        {
            return 0;
        }

        var batch = ready.Count >= 2;
        var profilesBefore = Profiles.List(_data).Count;
        var bar = batch ? new ProgressBar(L.T("Analizando replays", "Analyzing replays"), ready.Count) : null;
        var outcomes = new List<Outcome>();
        for (var i = 0; i < ready.Count; i++)
        {
            var file = ready[i];
            if (batch)
            {
                bar!.Update(i, file.Name);
                ReportWriter.Progress = new ScanProgress(i, ready.Count);
                ReportWriter.WriteAll(_store, _config, _data);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                var mb = (file.Length / 1024 / 1024.0).ToString("F1", L.Culture);
                var justEnded = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(2);
                Console.WriteLine(justEnded
                    ? L.F("[{0:HH:mm:ss}] Partida terminada: analizando el replay ({1} MB)…",
                          "[{0:HH:mm:ss}] Match over: analyzing the replay ({1} MB)…", DateTime.Now, mb)
                    : L.F("[{0:HH:mm:ss}] Analizando {1} ({2} MB)…",
                          "[{0:HH:mm:ss}] Analyzing {1} ({2} MB)…", DateTime.Now, file.Name, mb));
                Console.ResetColor();
            }

            var outcome = Process(file);
            outcomes.Add(outcome);
            if (!batch)
            {
                PrintOutcome(outcome);
            }
        }

        if (batch)
        {
            bar!.Update(ready.Count);
            bar.Clear();
            ReportWriter.Progress = null;
            PrintBatchSummary(outcomes);
            if (profilesBefore < 2 && Profiles.List(_data).Count >= 2)
            {
                NewProfilesFound = true;
            }
        }

        var added = outcomes.Count(o => o.Match != null);
        if (added > 0 || batch)
        {
            ReportWriter.WriteAll(_store, _config, _data);
        }
        return added;
    }

    /// <summary>Set when a scan found replays of a second account: the player should choose their profile.</summary>
    public bool NewProfilesFound { get; set; }

    private bool IsReady(FileInfo file, bool ignoreQuietPeriod)
    {
        var live = ReplayHeaderProbe.IsLive(file.FullName);
        if (live == true)
        {
            return false; // match still being recorded
        }

        var quietFor = DateTime.UtcNow - file.LastWriteTimeUtc;
        if (live == false && (ignoreQuietPeriod || quietFor > OldReplayAge))
        {
            return true;
        }

        // The file must not have changed for a while and its size must be stable between two scans.
        var stable = _lastSeenSize.TryGetValue(file.FullName, out var size) && size == file.Length;
        _lastSeenSize[file.FullName] = file.Length;

        if (live == null)
        {
            // Header could not be read: be extra careful.
            return stable && quietFor > TimeSpan.FromMinutes(2);
        }

        // Fortnite clears the "live" flag and writes the real encryption key when it stops recording: the replay
        // is complete. Read it as soon as its size has not changed for a few seconds (two checks in a row).
        if (!ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            IsFinishing = true;
            return stable && quietFor >= FinalizedQuietPeriod;
        }
        return stable && quietFor > QuietPeriod;
    }

    private Outcome Process(FileInfo file)
    {
        var key = FileKey(file);
        MatchRecord record;
        try
        {
            record = Analyze(file, ParseTimeout);
        }
        catch (Exception ex)
        {
            var message = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
            ErrorLog.Write($"Unreadable replay: {file.FullName} ({file.Length} bytes, {file.LastWriteTime:yyyy-MM-dd HH:mm:ss})", ex);
            _data.ProcessedFiles[key] = "error: " + message;
            _store.SaveData(_data);
            return new Outcome(Error: $"{file.Name} : {message}");
        }

        record.ReplayFile = file.Name;

        var existing = _data.Matches.FirstOrDefault(m => m.SessionId == record.SessionId);
        if (existing != null && existing.AnalyzerVersion >= record.AnalyzerVersion)
        {
            _data.ProcessedFiles[key] = record.SessionId;
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("partida ya contada (misma partida, archivo renombrado)",
                "match already counted (same match, renamed file)"));
        }


        if (!record.IsBattleRoyale || (_config.SeasonBattleRoyaleOnly && !GameModes.Counts(record)))
        {
            // Also drop a record left by an older version of the tool for this replay.
            // Historical records remain in the ledger.
            _data.ProcessedFiles[key] = "other mode";
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("no es una partida Battle Royale (creativo / otro modo)",
                "not a Battle Royale match (Creative / other mode)"));
        }

        if (!Profiles.IsSupportedVersion(record))
        {
            // Historical records remain in the ledger.
            _data.ProcessedFiles[key] = "older version";
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("versión anterior a v43 (Capítulo 7 Mini Temporada)",
                "version prior to v43 (Chapter 7 Mini Season)"));
        }

        if (existing != null)
        {
            // Re-analysis with a newer version of the tool: replace the old record.
            record.AnalyzedAtUtc = existing.AnalyzedAtUtc;
            _data.Matches.Remove(existing);
        }

        _data.Matches.Add(record);
        _data.ProcessedFiles[key] = record.SessionId;
        _store.SaveData(_data);
        if (_liveFile == file.FullName)
        {
            ClearLive(); // the final analysis replaces the live estimate
        }

        // First match ever: its account becomes the profile.
        if (string.IsNullOrEmpty(_config.Profile))
        {
            _config.Profile = Profiles.Key(record);
            if (_config.InitialChests.Remove("", out var before) && !_config.InitialChests.ContainsKey(_config.Profile))
            {
                _config.InitialChests[_config.Profile] = before;
            }
            try { _store.SaveConfig(_config); } catch { /* ignore */ }
        }

        var newIds = _store.RegisterPrizeIds(record.TreasureFinds.Where(f => f.PrizeId != null).Select(f => f.PrizeId!));
        return new Outcome(record, Updated: existing != null, NewPrizeIds: newIds);
    }

    private void PrintOutcome(Outcome o)
    {
        if (o.Error != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.F("   No se puede leer este replay: {0}", "   Cannot read this replay: {0}", o.Error));
            Console.ResetColor();
            PrintErrorLogHint();
            return;
        }
        if (o.Skipped != null)
        {
            Console.WriteLine($"   {L.T("Ignorado", "Skipped")}: {o.Skipped}.");
            return;
        }

        var m = o.Match!;
        PrintMatch(m, o.Updated);
        var current = Profiles.Current(_data, _config);
        if (current != null && Profiles.Key(m) != current.Key)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            var name = m.PlayerName ?? L.T("cuenta desconocida", "unknown account");
            Console.WriteLine(L.F("   Partida jugada en otra cuenta ({0}): cuenta para su propio perfil. Perfil mostrado: {1}.",
                "   Match played on another account ({0}): it counts for its own profile. Profile shown: {1}.", name, current.Name));
            Console.ResetColor();
        }
        PrintNewPrizeIds(o.NewPrizeIds);
    }

    /// <summary>Only real prize ids are worth asking about (unknown gizmos are listed in treasures.json anyway).</summary>
    private void PrintNewPrizeIds(List<string>? ids)
    {
        ids = ids?.Where(id => !id.StartsWith(TreasureNames.UnknownPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ids is { Count: > 0 })
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(L.F("   Tesoro(s) por nombrar en {0}: {1}",
                "   Treasure(s) to name in {0}: {1}", _store.PrizeNamesPath, string.Join(", ", ids)));
            Console.ResetColor();
        }
    }

    private void PrintErrorLogHint()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(L.F("   Detalles en {0}. Suele deberse a una actualización de Fortnite: notifícalo en {1}",
            "   Details in {0}. Usually caused by a Fortnite update: report it at {1}", _store.ErrorLogPath, AppInfo.IssuesUrl));
        Console.ResetColor();
    }

    private void PrintBatchSummary(List<Outcome> outcomes)
    {
        var matches = outcomes.Where(o => o.Match != null).Select(o => o.Match!).ToList();
        var skipped = outcomes.Count(o => o.Skipped != null);
        var errors = outcomes.Where(o => o.Error != null).Select(o => o.Error!).ToList();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  ✔ {L.Count(outcomes.Count, "replay analizado", "replays analizados", "replay analyzed", "replays analyzed")}");
        Console.ResetColor();
        Console.Write(": ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{L.Count(matches.Count, "partida", "partidas", "match", "matches")}, {L.Count(matches.Sum(m => m.ChestsOpened), "cofre", "cofres", "chest", "chests")}");
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        if (treasures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($", ★ {L.Count(treasures, "tesoro", "tesoros", "treasure", "treasures")}");
        }
        Console.ResetColor();
        Console.WriteLine();
        if (skipped > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(L.F("    {0} ignorado(s): versiones anteriores, modos creativos o partidas ya contadas.",
                "    {0} skipped: older versions, Creative modes or matches already counted.", skipped));
            Console.ResetColor();
        }
        if (errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.F("    {0} replay(s) ilegible(s):", "    {0} unreadable replay(s):", errors.Count));
            foreach (var e in errors.Take(5))
            {
                Console.WriteLine($"      {e}");
            }
            Console.ResetColor();
            PrintErrorLogHint();
        }
        PrintNewPrizeIds(outcomes.Where(o => o.NewPrizeIds != null).SelectMany(o => o.NewPrizeIds!).ToList());
    }

    /// <summary>Reads the file (even while Fortnite writes it) and parses it on a lowest-priority background thread.</summary>
    private MatchRecord Analyze(FileInfo file, TimeSpan timeout)
    {
        byte[] bytes;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        MatchRecord? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                // The replays saved on a PC are those of its player: the replay owner is tracked (one profile per account).
                result = new ReplayAnalyzer().Analyze(ms, file.Name, "");
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "ReplayAnalysis",
        };
        worker.Start();
        if (!worker.Join(timeout))
        {
            throw new TimeoutException(L.T("análisis demasiado largo", "analysis took too long"));
        }
        bytes = Array.Empty<byte>();
        ReleaseMemory();
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); // keep the original stack
        }
        return result!;
    }

    // ---- Live match ---------------------------------------------------------------------------
    private string? _liveFile;
    private long _liveSize = -1;
    private DateTime _liveParsedUtc = DateTime.MinValue;

    /// <summary>Estimate of the match being played (replay still recorded), or null. Not saved.</summary>
    public MatchRecord? LiveMatch { get; private set; }

    /// <summary>True while a replay is being recorded (a match is in progress).</summary>
    public bool IsLive => _liveFile != null;

    private void ClearLive()
    {
        _liveFile = null;
        _liveSize = -1;
        LiveMatch = null;
    }

    /// <summary>
    /// Re-reads the replay being recorded when Fortnite has appended data to it (about every 25 s), at most once
    /// every <see cref="Config.LiveIntervalSeconds"/>. Returns true when the live figures changed.
    /// </summary>
    public bool UpdateLive()
    {
        // The newest replay still flagged "live" and written recently (an old one left by a crash is ignored).
        var file = UnprocessedFiles()
            .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(3))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => ReplayHeaderProbe.IsLive(f.FullName) == true);

        if (file == null)
        {
            var had = _liveFile != null;
            // Match over: keep the estimate on screen until the final analysis replaces it, unless it went stale.
            if (had && (!File.Exists(_liveFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_liveFile!) > TimeSpan.FromMinutes(5)))
            {
                ClearLive();
                return true;
            }
            return false;
        }

        var started = false;
        if (file.FullName != _liveFile)
        {
            ClearLive();
            _liveFile = file.FullName;
            started = true;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(L.F("[{0:HH:mm:ss}] Partida en curso… (sus cofres se contarán al final)",
                "[{0:HH:mm:ss}] Match in progress… (its chests will be counted at the end)", DateTime.Now));
            Console.ResetColor();
        }

        // Fortnite writes the encryption key only at the end of the match: until then the data cannot be read,
        // so we only know that a match is in progress (no useless re-reading).
        if (!_config.LiveUpdate || ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            return started;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.LiveIntervalSeconds));
        if (file.Length == _liveSize || DateTime.UtcNow - _liveParsedUtc < interval)
        {
            return started;
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            _liveParsedUtc = DateTime.UtcNow; // try again at the next interval
            if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") is { Length: > 0 })
            {
                Console.Error.WriteLine($"DBG live parse failed: {ex}");
            }
            return started;
        }
        _liveSize = file.Length;
        _liveParsedUtc = DateTime.UtcNow;

        var before = LiveMatch;
        LiveMatch = record;
        var changed = before?.ChestsOpened != LiveMatch?.ChestsOpened || before?.TreasureFinds.Count != LiveMatch?.TreasureFinds.Count || (before == null) != (LiveMatch == null);
        if (changed && LiveMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {L.T("Partida en curso", "Match in progress")}: {L.Count(LiveMatch.ChestsOpened, "cofre", "cofres", "chest", "chests")}"
                + (LiveMatch.TreasureFinds.Count > 0 ? $", {L.Count(LiveMatch.TreasureFinds.Count, "tesoro", "tesoros", "treasure", "treasures")}" : ""));
            Console.ResetColor();
        }
        return changed || started;
    }

    /// <summary>
    /// When the analysis logic changed, re-analyze the replays still on disk (records are replaced, not duplicated).
    /// Must run before any scan. Returns true when existing matches will be re-analyzed (worth telling the player).
    /// </summary>
    public bool UpgradeIfNeeded()
    {
        if (_data.AnalyzerVersion >= ReplayAnalyzer.CurrentVersion)
        {
            return false;
        }

        var hadData = _data.Matches.Count > 0;
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value != "skipped" && kv.Value != "other mode" && kv.Value != "older version").Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }
        _data.AnalyzerVersion = ReplayAnalyzer.CurrentVersion;
        _store.SaveData(_data);
        return hadData;
    }

    private void PrintMatch(MatchRecord m, bool updated)
    {
        m = Profiles.Adjust(m, _config)!;
        var key = Profiles.Key(m);
        var total = _data.Matches.Where(x => Profiles.Key(x) == key && Profiles.Counted(x, _config))
            .Sum(x => Profiles.Adjust(x, _config)!.ChestsOpened);
        total += _data.ReportedByProfile.GetValueOrDefault(key).ForPeriod(_config.CountFrom).Chests;
        if (!_config.CountFrom.HasValue)
        {
            total += Profiles.InitialChests(_config, key);
        }
        var when = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"   {(updated ? "↻" : "✔")} {L.T("Partida del", "Match of")} {L.DateTimeShort(when)}");
        Console.ResetColor();
        Console.Write($" ({Pretty.Playlist(m.Playlist)}, {Pretty.Placement(m)}): ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        var destroyed = m.Chests.Count(c => c.Destroyed);
        Console.Write(L.Count(m.ChestsOpened, "cofre", "cofres", "chest", "chests")
            + (destroyed > 0 ? L.F(" ({0} destruido(s))", " ({0} destroyed)", destroyed) : ""));
        Console.ResetColor();
        Console.WriteLine($"  →  total: {L.N(total)}");

        foreach (var find in m.TreasureFinds)
        {
            Console.ForegroundColor = find.FromGoldenChest ? ConsoleColor.Yellow : ConsoleColor.Cyan;
            Console.WriteLine($"   {(find.FromGoldenChest ? "★★" : "★")} {L.T("Tesoro obtenido: ", "Treasure obtained: ")}{find.DisplayName} ({Pretty.Obtained(find)})");
            if (!find.SourceIsChest)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(L.F("   ⚑ Origen inusual: {0}{1} [{2}] → anotado en el informe",
                    "   ⚑ Unusual source: {0}{1} [{2}] → noted in the report",
                    find.SourceLabel, find.SourceCertain ? "" : L.T(" (probable)", " (likely)"), find.SourceBlueprint));
            }
            Console.ResetColor();
        }
    }

    /// <summary>Totals of the profile shown.</summary>
    public void PrintSummary()
    {
        var profile = Profiles.Current(_data, _config);
        var profileView = Profiles.View(_data, _config);
        var reported = HistoryCompatibility.Reported(profileView);
        var matches = profileView.Matches;
        var counted = matches.Sum(m => m.ChestsOpened);
        var chests = counted + Profiles.InitialChests(_data, _config);
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        var sb = new StringBuilder();
        if (profile != null)
        {
            sb.Append($"{profile.Name}   |   ");
        }
        sb.Append(L.F("Partidas: {0}   |   Cofres: {1}   |   Tesoros: {2}",
            "Matches: {0}   |   Chests: {1}   |   Treasures: {2}", L.N(matches.Count + reported.Matches), L.N(chests), L.N(treasures)));
        if (matches.Count > 0)
        {
            var avg = HistoryCompatibility.Average(profileView).ToString("F1", L.Culture);
            sb.Append(L.F("   |   Media: {0} / partida", "   |   Average: {0} / match", avg));
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  " + sb);
        Console.ResetColor();
    }
}

/// <summary>Reads just enough of the replay file header to know whether the match is still being recorded.</summary>
public static class ReplayHeaderProbe
{
    private const uint FileMagic = 0x1CA2E27F;

    /// <summary>
    /// True when the replay is encrypted and its key is not written yet. Fortnite writes the real key only when
    /// the match ends (all zeros before): the data of a match in progress cannot be read.
    /// </summary>
    public static bool KeyMissing(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic) return true;
            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64) return true;
                fs.Seek(count * 20L, SeekOrigin.Current);
            }
            fs.Seek(12, SeekOrigin.Current);
            var len = br.ReadInt32();
            fs.Seek(len < 0 ? -2L * len : len, SeekOrigin.Current);
            br.ReadUInt32(); // bIsLive
            br.ReadInt64(); // timestamp
            br.ReadUInt32(); // compressed
            if (br.ReadUInt32() == 0) return false; // not encrypted
            var keyLen = br.ReadInt32();
            if (keyLen <= 0 || keyLen > 256) return true;
            return br.ReadBytes(keyLen).All(b => b == 0);
        }
        catch
        {
            return true;
        }
    }

    public static bool? IsLive(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic)
            {
                return null;
            }

            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64)
                {
                    return null;
                }
                fs.Seek(count * 20L, SeekOrigin.Current);
            }

            br.ReadUInt32(); // LengthInMS
            br.ReadUInt32(); // NetworkVersion
            br.ReadUInt32(); // Changelist

            var len = br.ReadInt32(); // FriendlyName (FString)
            if (len < 0)
            {
                fs.Seek(-2L * len, SeekOrigin.Current);
            }
            else
            {
                fs.Seek(len, SeekOrigin.Current);
            }

            return br.ReadUInt32() != 0;
        }
        catch
        {
            return null;
        }
    }
}
