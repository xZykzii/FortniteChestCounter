using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>Lo que muestra la ventana.</summary>
public sealed record MonitorState(OverlayView View, IReadOnlyList<Profile> Profiles, string? CurrentKey, UpdateInfo? Update, string? Notice, bool AutoCloseOffer, ChestStats Stats)
{
    /// <summary>Equipo de la partida en curso o de la última (null si «Compañeros» está desactivado).</summary>
    public TeamSummary? Team { get; init; }
}

/// <summary>Un compañero y los cofres que se le vieron abrir (un mínimo).</summary>
public sealed record TeamMember(string Name, int Chests);

/// <summary>
/// Equipo de una partida para la ventana principal.
/// <paramref name="Unknown"/>: partida analizada antes de guardar compañeros (sin datos de equipo).
/// </summary>
public sealed record TeamSummary(bool Live, int Mine, IReadOnlyList<TeamMember> Mates, bool Unknown)
{
    public int MatesTotal => Mates.Sum(m => m.Chests);
    public int Total => Mine + MatesTotal;
}

/// <summary>Estadísticas del perfil mostrado (la partida en curso cuenta ya en hoy y en la mejor partida).</summary>
/// <param name="Today">Cofres de hoy.</param>
/// <param name="BestDay">Día con más cofres, o null si no hay partidas.</param>
/// <param name="BestDayChests">Cofres de ese día.</param>
/// <param name="Average">Media de cofres por partida terminada.</param>
/// <param name="BestMatch">Cofres de la mejor partida.</param>
public sealed record ChestStats(int Matches, double Average, int BestMatch, int Treasures, int Today, DateTime? BestDay, int BestDayChests)
{
    public static readonly ChestStats Empty = new(0, 0, 0, 0, 0, null, 0);

    /// <summary>Cofres de los últimos 7 días, del más antiguo al de hoy (incluye la partida en curso).</summary>
    public IReadOnlyList<DayChests> LastDays { get; init; } = Array.Empty<DayChests>();

    /// <summary>Partidas guardadas en el histórico (todas, no solo las 100 últimas; sobreviven aunque Fortnite borre el replay).</summary>
    public int HistoryMatches { get; init; }

    /// <summary>Fecha local de la primera partida del histórico, o null si no hay ninguna.</summary>
    public DateTime? HistorySince { get; init; }

    /// <summary>Resumen de la última partida terminada del perfil mostrado, o null si no hay ninguna.</summary>
    public LastMatchSummary? LastMatch { get; init; }
}

/// <summary>Cifras de la última partida: cofres abiertos (incluidos los destruidos), tesoros, puesto y eliminaciones.</summary>
public sealed record LastMatchSummary(int Chests, int Destroyed, int Treasures, int? Placement, int? Players, int? Eliminations);

/// <summary>Cofres de un día concreto.</summary>
public sealed record DayChests(DateTime Day, int Chests);

/// <summary>
/// Vigila los replays en segundo plano: escaneo, partida en curso, overlay, actualizaciones y cierre automático.
/// Solo el hilo de vigilancia toca los datos (el escaneo puede tardar minutos); la ventana pide cambios y
/// recibe el estado publicado, así nunca se congela mientras se analizan replays.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Monitor
{
    /// <summary>Protege solo la configuración (escrituras cortas desde la ventana, el overlay y el hilo de vigilancia).</summary>
    private readonly object _cfgGate = new();
    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Tracker _tracker;
    private readonly string _dataDir;
    private readonly AutoResetEvent _wake = new(false);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _contentTimer;
    private volatile bool _stop;
    private volatile bool _publishRequested;
    private volatile bool _reportRequested;
    private volatile string? _shownKey;
    private volatile string? _shownName;
    private DateTime _nextScan = DateTime.UtcNow;
    private volatile UpdateInfo? _update;
    private bool _updateAnnounced;
    private volatile bool _installing;
    private volatile string? _notice;
    private volatile bool _keepOpenRequested;
    private DateTime? _fortniteClosedSince;
    private DateTime? _autoCloseAt;
    private bool _closeRequested;

    public Monitor(Store store, Config config, TrackerData data, Tracker tracker, string dataDir)
    {
        _store = store;
        _config = config;
        _data = data;
        _tracker = tracker;
        _dataDir = dataDir;
    }

    /// <summary>Ajustes actuales (leer; los cambios se hacen con <see cref="Change"/>).</summary>
    public Config Config => _config;

    /// <summary>Abierto por el proceso en espera de Fortnite: se cierra solo cuando Fortnite se cierra.</summary>
    public bool AutoMode { get; set; }

    /// <summary>Se pone a true cuando hay que reiniciar la nueva versión.</summary>
    public bool RestartAfterUpdate { get; private set; }

    /// <summary>Reinicio pedido desde Ajustes (cambio de idioma): la ventana se cierra y el exe vuelve a arrancar.</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>Cierra el contador para volver a abrirlo (los textos se leen al arrancar).</summary>
    public void Restart()
    {
        RestartRequested = true;
        CloseRequested?.Invoke();
    }

    /// <summary>Estado nuevo. Se llama desde el hilo de vigilancia: la ventana debe usar BeginInvoke.</summary>
    public event Action<MonitorState>? Changed;

    /// <summary>Pide cerrar la ventana (Fortnite cerrado, o nueva versión instalada). Desde otro hilo.</summary>
    public event Action? CloseRequested;

    public void Start()
    {
        if (OperatingSystem.IsWindows())
        {
            StartOverlay();
        }
        _contentTimer = SeasonFeatures.Start(this, _store, _config);
        RestartWatcher();
        ObsServer.Start(_config.ObsPort > 0 ? _config.ObsPort : ObsServer.DefaultPort);

        new Thread(Loop) { IsBackground = true, Name = "Vigilancia" }.Start();

        // Nueva versión en GitHub: única conexión de red de la herramienta (se puede desactivar en ajustes).
        if (_config.UpdateMode != "off")
        {
            _ = Task.Run(async () =>
            {
                var found = await Updater.CheckAsync();
                if (found != null)
                {
                    _update = found;
                    RequestPublish();
                }
            });
        }
    }

    public void Stop()
    {
        _contentTimer?.Dispose();
        ObsServer.Stop();
        _stop = true;
        _wake.Set();
        _watcher?.Dispose();
    }

    /// <summary>Mensaje de estado de la ventana (por ejemplo, el reanálisis tras una actualización).</summary>
    public void SetNotice(string? text)
    {
        _notice = text;
        RequestPublish();
    }

    /// <summary>Cambia un ajuste y lo guarda. La ventana se actualiza enseguida.</summary>
    public void Change(Action<Config> change)
    {
        lock (_cfgGate)
        {
            change(_config);
            SaveQuietly();
        }
        RequestPublish();
    }

    public void SetProfile(string key) => Change(c => c.Profile = key);

    /// <summary>Sumar o no al total las cajas de munición, de comida o los cañones de botín. Rehace el informe enseguida.</summary>
    public void SetCountExtra(Action<Config> change)
    {
        _reportRequested = true;
        Change(change);
    }

    /// <summary>Sumar o no a tu total los cofres vistos de tus compañeros. Rehace el informe enseguida.</summary>
    public void SetCountTeammates(bool count)
    {
        _reportRequested = true;
        Change(c => c.CountTeammateChests = count);
    }

    /// <summary>Contar solo desde una fecha (null = todas las partidas). Rehace el informe enseguida.</summary>
    public void SetOverlayStyle(string style)
    {
        if (!NativeOverlay.Styles.Contains(style)) return;
        ReportWriter.VisualStyle = style;
        Change(c => c.OverlayStyle = style);
        NativeOverlay.SetStyle(style);
    }

    public void SetCountFrom(DateTime? from)
    {
        _reportRequested = true;
        Change(c => c.CountFrom = from?.Date);
    }

    /// <summary>
    /// Días con partidas del perfil mostrado (todas, sin el filtro de fecha), del más reciente al más antiguo, con
    /// cuántas partidas hubo: es lo que ofrece el selector «Contar desde».
    /// </summary>
    public IReadOnlyList<(DateTime Day, int Matches)> MatchDays()
    {
        try
        {
            var key = Profiles.Current(_data, _config)?.Key;
            return _data.Matches.ToList()
                .Where(m => (key == null || Profiles.Key(m) == key) && Profiles.IsSupportedVersion(m))
                .GroupBy(Profiles.Day)
                .OrderByDescending(g => g.Key)
                .Select(g => (g.Key, g.Count()))
                .ToList();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<(DateTime, int)>(); // la lista cambió mientras se leía: sin días esta vez
        }
    }

    /// <summary>Mostrar u ocultar la sección «Compañeros» del informe. Rehace el informe enseguida.</summary>
    public void SetShowTeammates(bool show)
    {
        _reportRequested = true;
        Change(c => c.ShowTeammates = show);
    }

    private volatile string? _resetBackup;

    /// <summary>
    /// Restablece las estadísticas: borra todo el historial (y los cofres de antes del contador) y empieza de cero.
    /// Los replays que ya están en la carpeta no se vuelven a contar. Antes se guarda una copia de data.json.
    /// Devuelve la ruta de esa copia. Se hace en el hilo de vigilancia, que es el dueño de los datos.
    /// </summary>
    public string ResetStats()
    {
        var backup = Path.Combine(_store.Folder, $"data-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        _resetBackup = backup;
        _wake.Set();
        return backup;
    }

    /// <summary>Nombre del perfil mostrado, o null si aún no hay partidas.</summary>
    public string? ShownProfileName => _shownName;
    public ReportedTotals ReportedTotals => HistoryCompatibility.Reported(_data, _config);

    /// <summary>Cofres estimados de antes de usar el contador, del perfil mostrado.</summary>
    public int InitialChests
    {
        get
        {
            lock (_cfgGate)
            {
                return Profiles.InitialChests(_config, _shownKey);
            }
        }
    }

    /// <summary>Guarda el estimado de cofres de antes para el perfil mostrado y rehace el informe.</summary>
    public void SetInitialChests(int count)
    {
        var key = _shownKey ?? "";
        Change(c =>
        {
            if (count > 0)
            {
                c.InitialChests[key] = count;
            }
            else
            {
                c.InitialChests.Remove(key);
            }
        });
        _reportRequested = true;
        _wake.Set();
    }

    /// <summary>
    /// Al arrancar, antes de leer replays: rehace el informe (si no, tras actualizar seguiría el de la versión anterior
    /// hasta la próxima partida) y lo abre si está activado «Abrir el informe al iniciar». No se abre cuando el contador
    /// arranca solo con Fortnite, para no tapar el juego.
    /// </summary>
    private void WriteReportAtStart()
    {
        ReportWriter.WriteAll(_store, _config, _data);
        if (_config.OpenReportOnStart && !AutoMode && File.Exists(_store.ReportPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_store.ReportPath) { UseShellExecute = true })?.Dispose();
            }
            catch
            {
                // sin navegador: el botón «Informe» de la ventana sigue funcionando
            }
        }
    }

    public void ChangeDemosFolder(string folder)
    {
        Change(c => c.DemosFolder = folder);
        RestartWatcher();
    }

    /// <summary>Abrir con Fortnite: crea o quita la entrada de inicio. Devuelve false si no se puede (no es ChestCounter.exe).</summary>
    public bool SetStartWithFortnite(bool on)
    {
        lock (_cfgGate)
        {
            if (!OperatingSystem.IsWindows() || !FortniteWatch.Apply(on, _dataDir))
            {
                return false;
            }
            _config.StartWithFortnite = on;
            SaveQuietly();
        }
        RequestPublish();
        return true;
    }

    /// <summary>El jugador decide mantener el contador abierto tras cerrar Fortnite.</summary>
    public void KeepOpen()
    {
        AutoMode = false;
        _keepOpenRequested = true;
        RequestPublish();
    }

    /// <summary>El overlay siempre está disponible: el botón y Ctrl+Mayús+O lo muestran y ocultan.</summary>
    public bool OverlayEnabled => OperatingSystem.IsWindows();

    /// <summary>True cuando el overlay está en pantalla (desactivado por defecto).</summary>
    public bool OverlayVisible => OperatingSystem.IsWindows() && NativeOverlay.IsVisible;

    public void ToggleOverlay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (NativeOverlay.IsRunning)
        {
            NativeOverlay.Toggle();
            return;
        }
        lock (_cfgGate)
        {
            _config.OverlayVisible = true;
            SaveQuietly();
        }
        StartOverlay();
        RequestPublish();
    }

    /// <summary>Descarga e instala la nueva versión en segundo plano; luego pide reiniciar.</summary>
    public void InstallUpdate()
    {
        var u = _update;
        if (u == null || _installing)
        {
            return;
        }
        _installing = true;
        RequestPublish();
        _ = Task.Run(() =>
        {
            var error = Updater.Install(u);
            _installing = false;
            if (error == null)
            {
                RestartAfterUpdate = true;
                CloseRequested?.Invoke();
                return;
            }
            _notice = L.F("No se pudo actualizar: {0}", "Update failed: {0}", error);
            RequestPublish();
        });
    }

    /// <summary>Pide al hilo de vigilancia que publique el estado (se hace en cuanto puede).</summary>
    public void RefreshReport()
    {
        _reportRequested = true;
        RequestPublish();
    }

    public void RequestPublish()
    {
        _publishRequested = true;
        _wake.Set();
    }

    // ---- Hilo de vigilancia ----------------------------------------------------------------------

    private void Loop()
    {
        WriteReportAtStart();
        var first = true;
        while (!_stop)
        {
            var changed = first || _publishRequested;
            first = false;
            _publishRequested = false;

            if (_resetBackup is { } backup)
            {
                _resetBackup = null;
                try
                {
                    _tracker.Reset(backup);
                    lock (_cfgGate)
                    {
                        _config.InitialChests.Clear();
                        _config.Profile = null;
                        SaveQuietly();
                    }
                    _reportRequested = true;
                    changed = true;
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Restablecer estadísticas", ex);
                    _notice = L.F("No se pudo restablecer: {0}", "Could not reset: {0}", ex.Message);
                }
            }

            try
            {
                if (DateTime.UtcNow >= _nextScan)
                {
                    var added = _tracker.Scan();
                    var liveChanged = _tracker.UpdateLive();
                    changed |= added > 0 || liveChanged;

                    // Partida en curso: se relee cada 15 s. Replay terminado: enseguida. Si no, un escaneo de respaldo.
                    _nextScan = DateTime.UtcNow.AddSeconds(_tracker.IsFinishing ? 2 : _tracker.HasPending ? 15 : (_watcher != null && !AutoMode ? 60 : 10));
                }
                if (AutoMode)
                {
                    changed |= CheckAutoClose();
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Vigilancia", ex);
            }

            changed |= CheckUpdate();
            if (_reportRequested)
            {
                _reportRequested = false;
                ReportWriter.WriteAll(_store, _config, _data);
            }
            if (changed || _publishRequested)
            {
                _publishRequested = false;
                Publish();
            }

            // Espera a que pase algo (cambio de archivo, ajuste) o un segundo.
            _wake.WaitOne(TimeSpan.FromSeconds(1));
        }
    }

    private void Publish()
    {
        var shown = Profiles.Current(_data, _config);
        _shownKey = shown?.Key;
        _shownName = shown?.Name;
        var liveAdjusted = (_tracker.LiveMatch != null && Profiles.Counted(_tracker.LiveMatch, _config)) ? Profiles.Adjust(_tracker.LiveMatch, _config) : null;
        var view = OverlayModel.Build(Profiles.View(_data, _config), liveAdjusted, _tracker.IsLive && liveAdjusted != null, Profiles.InitialChests(_data, _config))
            with { TotalCaption = OverlayModel.TotalCaption(_config.CountTeammateChests) };
        ReportWriter.WriteOverlay(_store, view);
        ObsServer.Update(view);
        if (OperatingSystem.IsWindows() && NativeOverlay.IsRunning)
        {
            NativeOverlay.Update(view);
        }

        var notice = _installing
            ? L.T("Descargando la actualización…", "Downloading the update…")
            : _notice ?? (_autoCloseAt != null ? L.T("Fortnite está cerrado: el contador se cierra en 30 s.", "Fortnite is closed: the counter closes in 30 s.") : null);
        var state = new MonitorState(view, Profiles.List(_data, _config), Profiles.Current(_data, _config)?.Key, _update, notice, _autoCloseAt != null, BuildStats())
        {
            Team = BuildTeam(),
        };
        Changed?.Invoke(state);
    }

    /// <summary>
    /// Estadísticas del perfil mostrado sobre todo su historial guardado (no solo los replays que siguen en disco),
    /// con la partida en curso incluida. Son las mismas cifras que el informe.
    /// </summary>
    private ChestStats BuildStats()
    {
        var profileView = Profiles.View(_data, _config);
        var reported = HistoryCompatibility.Reported(profileView);
        var matches = profileView.Matches;
        var live = _tracker.IsLive && _tracker.LiveMatch != null && Profiles.Counted(_tracker.LiveMatch, _config) ? Profiles.Adjust(_tracker.LiveMatch, _config) : null;
        var today = DateTime.Now.Date;

        var perDay = new Dictionary<DateTime, int>();
        foreach (var m in matches)
        {
            var day = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime().Date;
            perDay[day] = perDay.GetValueOrDefault(day) + m.ChestsOpened;
        }
        if (live != null)
        {
            perDay[today] = perDay.GetValueOrDefault(today) + live.ChestsOpened;
        }

        var all = live != null ? matches.Append(live).ToList() : matches;
        var average = HistoryCompatibility.Average(profileView);
        var best = perDay.Count > 0 ? perDay.MaxBy(kv => kv.Value) : (KeyValuePair<DateTime, int>?)null;
        return new ChestStats(
            Matches: matches.Count + reported.Matches,
            Average: average,
            BestMatch: all.Count > 0 ? all.Max(m => m.ChestsOpened) : 0,
            Treasures: all.Sum(m => m.TreasureFinds.Count),
            Today: perDay.GetValueOrDefault(today),
            BestDay: best?.Key,
            BestDayChests: best?.Value ?? 0)
        {
            LastDays = BuildLastDays(today, live),
            HistoryMatches = matches.Count + reported.Matches,
            LastMatch = BuildLastMatch(),
            HistorySince = Profiles.View(_data, _config).Matches
                .Select(m => (DateTime?)(m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime().Date)
                .Min(),
        };
    }

    /// <summary>
    /// Equipo de la partida en curso (si se puede leer) o de la última: tus cofres y los vistos de cada compañero.
    /// Null si «Compañeros» está desactivado o aún no hay partidas.
    /// </summary>
    private TeamSummary? BuildTeam()
    {
        if (!_config.ShowTeammates)
        {
            return null;
        }
        var kinds = _config.ExtraKinds();
        var live = _tracker.IsLive && _tracker.LiveMatch != null && Profiles.Counted(_tracker.LiveMatch, _config) ? Profiles.Adjust(_tracker.LiveMatch, _config) : null;
        var m = live ?? Profiles.View(_data, _config).Matches.OrderByDescending(x => x.MatchStartUtc ?? x.AnalyzedAtUtc).FirstOrDefault();
        if (m == null)
        {
            return null;
        }
        var mates = (m.Teammates ?? new List<TeammateRecord>())
            .Select(t => new TeamMember(t.Name, MatchRecord.Seen(t, kinds)))
            .OrderByDescending(t => t.Chests)
            .ToList();
        return new TeamSummary(live != null, m.OwnChests, mates, m.Teammates == null);
    }

    /// <summary>Última partida terminada del perfil mostrado.</summary>
    private LastMatchSummary? BuildLastMatch()
    {
        var m = Profiles.View(_data, _config).Matches
            .OrderByDescending(x => x.MatchStartUtc ?? x.AnalyzedAtUtc)
            .FirstOrDefault();
        if (m == null)
        {
            return null;
        }
        return new LastMatchSummary(m.ChestsOpened, m.Chests.Count(c => c.Destroyed), m.TreasureFinds.Count,
            m.Placement, m.TotalPlayers, m.Eliminations);
    }

    /// <summary>Cofres de los últimos 7 días del perfil mostrado (todas sus partidas, no solo las 100 últimas).</summary>
    private List<DayChests> BuildLastDays(DateTime today, MatchRecord? live)
    {
        const int days = 7;
        var first = today.AddDays(-(days - 1));
        var sums = new Dictionary<DateTime, int>();
        var all = Profiles.View(_data, _config).Matches.Append(live).OfType<MatchRecord>();
        foreach (var m in all)
        {
            var day = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime().Date;
            if (day >= first && day <= today)
            {
                sums[day] = sums.GetValueOrDefault(day) + m.ChestsOpened;
            }
        }
        return Enumerable.Range(0, days)
            .Select(i => first.AddDays(i))
            .Select(d => new DayChests(d, sums.GetValueOrDefault(d)))
            .ToList();
    }

    /// <summary>Anuncia una versión nueva; en modo automático la instala cuando no hay partidas en curso. Devuelve true si hay que publicar.</summary>
    private bool CheckUpdate()
    {
        var u = _update;
        if (u == null || _updateAnnounced || _installing)
        {
            return false;
        }

        if (_config.UpdateMode == "auto")
        {
            var idle = !_tracker.IsLive && !_tracker.HasPending && !_tracker.IsFinishing;
            if (!idle || AppInfo.ExePath == null)
            {
                return false;
            }
            _updateAnnounced = true;
            InstallUpdate();
            return true;
        }

        _updateAnnounced = true;
        return true;
    }

    /// <summary>Opened with Fortnite: closes once Fortnite is closed and the last match is counted (5 min at most).</summary>
    private bool CheckAutoClose()
    {
        if (_keepOpenRequested)
        {
            _keepOpenRequested = false;
            _autoCloseAt = null;
            _fortniteClosedSince = null;
            _closeRequested = false;
            return true;
        }

        var now = DateTime.UtcNow;
        if (FortniteWatch.IsFortniteRunning())
        {
            var wasOffering = _autoCloseAt != null;
            _fortniteClosedSince = null;
            _autoCloseAt = null;
            return wasOffering;
        }

        _fortniteClosedSince ??= now;
        var lastMatchDone = !_tracker.IsLive && !_tracker.HasPending && !_tracker.IsFinishing;
        if (!lastMatchDone && now - _fortniteClosedSince.Value < TimeSpan.FromMinutes(5))
        {
            return false;
        }

        if (_autoCloseAt == null)
        {
            _autoCloseAt = now.AddSeconds(30);
            return true;
        }
        if (now >= _autoCloseAt && !_closeRequested)
        {
            _closeRequested = true;
            CloseRequested?.Invoke();
        }
        return false;
    }

    // ---- Piezas internas -------------------------------------------------------------------------

    private void StartOverlay()
    {
        NativeOverlay.SetStyle(_config.OverlayStyle);
        NativeOverlay.SetAppearance(_config.OverlaySize, _config.OverlayOpacity);
        NativeOverlay.Start(_config.OverlayX, _config.OverlayY, _config.OverlayLocked,
            (x, y) =>
            {
                lock (_cfgGate)
                {
                    _config.OverlayX = x;
                    _config.OverlayY = y;
                    SaveQuietly();
                }
            },
            locked =>
            {
                lock (_cfgGate)
                {
                    _config.OverlayLocked = locked;
                    SaveQuietly();
                }
            },
            _config.OverlayVisible,
            visible =>
            {
                lock (_cfgGate)
                {
                    _config.OverlayVisible = visible;
                    SaveQuietly();
                }
                RequestPublish(); // el botón de la ventana cambia de color
            }, () => SetCountTeammates(!_config.CountTeammateChests));
    }

    /// <summary>Windows avisa cuando cambia un replay: no hace falta releer la carpeta todo el tiempo.</summary>
    private void RestartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            if (Directory.Exists(_config.DemosFolder))
            {
                _watcher = new FileSystemWatcher(_config.DemosFolder, "*.replay")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += (_, _) => Wake();
                _watcher.Changed += (_, _) => Wake();
                _watcher.Renamed += (_, _) => Wake();
            }
        }
        catch
        {
            _watcher = null; // escaneo periódico de respaldo
        }
    }

    private void Wake()
    {
        var soon = DateTime.UtcNow.AddSeconds(2);
        if (soon < _nextScan)
        {
            _nextScan = soon;
        }
        _wake.Set();
    }

    private void SaveQuietly()
    {
        try { _store.SaveConfig(_config); } catch { /* ignore */ }
    }
}
