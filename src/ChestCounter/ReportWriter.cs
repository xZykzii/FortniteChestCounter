using System.Globalization;
using System.Net;
using System.Text;

namespace ChestCounter;

public static class Pretty
{
    public static string Playlist(string? playlist)
    {
        if (string.IsNullOrEmpty(playlist))
        {
            return L.T("modo desconocido", "unknown mode");
        }

        var p = playlist.ToLowerInvariant();
        var parts = new List<string>();
        if (p.Contains("habanero") || p.Contains("ranked")) parts.Add(L.T("Clasificatoria", "Ranked"));
        if (p.Contains("nobuild")) parts.Add(L.T("Cero construcción", "Zero Build"));
        if (p.Contains("solo")) parts.Add("Solo");
        else if (p.Contains("duo")) parts.Add(L.T("Dúo", "Duos"));
        else if (p.Contains("trio")) parts.Add(L.T("Trío", "Trios"));
        else if (p.Contains("squad")) parts.Add(L.T("Escuadrón", "Squads"));
        return parts.Count > 0 ? string.Join(" · ", parts) : playlist.Replace("Playlist_", "");
    }

    public static string Placement(MatchRecord m)
    {
        if (m.Placement is not int place || place <= 0)
        {
            return L.T("puesto desconocido", "unknown placement");
        }
        if (place == 1)
        {
            return L.T("¡Victoria Real!", "Victory Royale!");
        }
        var ordinal = L.Ordinal(place);
        return m.TotalPlayers is int total && total > 0
            ? L.F("{0} de {1}", "{0} of {1}", ordinal, total)
            : ordinal;
    }

    public static string Clock(float seconds)
    {
        var s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"a las 7:16 · Cofre raro" (seguro) o "hacia las 5:04 · Cofre raro (probable)".</summary>
    public static string Obtained(TreasureFind f)
    {
        if (f.ObtainedAtSeconds is not float t)
        {
            return L.T("momento y origen desconocidos", "time and source unknown");
        }
        var when = f.TimeExact ? $"{L.T("a las", "at")} {Clock(t)}" : $"{L.T("hacia las", "around")} {Clock(t)}";
        return f.SourceCertain ? $"{when} · {f.SourceLabel}" : $"{when} · {f.SourceLabel} ({Likely})";
    }

    public static string Likely => L.T("probable", "likely");

    public static string ItemName(string item)
    {
        var name = item;
        foreach (var prefix in new[] { "WID_", "Athena", "AmmoData" })
        {
            name = name.Replace(prefix, "");
        }
        return name.Replace("_", " ").Trim();
    }
}

/// <summary>Replays analyzed so far / to analyze, shown as a loading card in the report.</summary>
public sealed record ScanProgress(int Done, int Total);

public static class ReportWriter
{
    public static readonly DateTime SessionStartUtc = DateTime.UtcNow;

    /// <summary>Set while several replays are being analyzed: the report shows a loading card and refreshes faster.</summary>
    public static volatile ScanProgress? Progress;

    /// <summary>A match belongs to this session if it ended after the tool was started (backlog replays do not count).</summary>
    public static bool IsThisSession(MatchRecord m)
    {
        var start = m.MatchStartUtc ?? m.AnalyzedAtUtc;
        if (start.Kind == DateTimeKind.Unspecified)
        {
            start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        }
        var end = start.ToUniversalTime().AddSeconds(m.DurationSeconds ?? 0);
        return end >= SessionStartUtc.AddMinutes(-2);
    }

    private static CultureInfo Loc => L.Culture;

    public static void WriteAll(Store store, Config config, TrackerData data)
    {
        try
        {
            var view = Profiles.View(data, config);
            WriteAtomic(store.ReportPath, Report(config, view, Profiles.List(data, config),Profiles.InitialChests(data, config)));
            WriteAtomic(store.OverlayPath, Overlay(OverlayModel.Build(view, null, false, Profiles.InitialChests(data, config), config.CountTeammateChests)));
            WriteAtomic(store.CsvPath, Csv(data));
        }
        catch (Exception ex)
        {
            Console.WriteLine(L.F("No se pudo escribir el informe: {0}", "Cannot write the report: {0}", ex.Message));
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static DateTime When(MatchRecord m) => (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

    // Misma paleta que la ventana (azul noche, cian y ámbar). Oscuro por defecto; claro si el sistema lo pide.
    private const string Style = """
        :root{--bg:#0b0e14;--bg2:#0f131b;--card:#151a24;--card2:#1a2030;--ink:#e6eaf2;--muted:#7a8496;--faint:#4a5468;--line:#232b3a;--line2:#2e3850;
          --cyan:#38d6f0;--cyan-soft:rgba(56,214,240,.12);--amber:#ffb84d;--amber-soft:rgba(255,184,77,.13);--mint:#3ddc97;--mint-soft:rgba(61,220,151,.13);
          --rose:#ff80aa;--rose-soft:rgba(255,128,170,.12);--blue:#3878dc;--shadow:0 1px 2px rgba(0,0,0,.3),0 8px 24px rgba(0,0,0,.18)}
        @media (prefers-color-scheme:light){:root:not([data-theme="dark"]){--bg:#f4f6fa;--bg2:#eef1f6;--card:#fff;--card2:#f7f9fc;--ink:#151a24;--muted:#5d6678;--faint:#9aa3b5;
          --line:#e3e7ef;--line2:#d4dae6;--cyan:#0b8fb0;--cyan-soft:rgba(11,143,176,.09);--amber:#b8730d;--amber-soft:rgba(184,115,13,.1);--mint:#13895a;--mint-soft:rgba(19,137,90,.1);
          --rose:#c2346a;--rose-soft:rgba(194,52,106,.08);--blue:#2f6fd6;--shadow:0 1px 2px rgba(20,30,50,.06),0 6px 20px rgba(20,30,50,.06)}}
        :root[data-theme="light"]{--bg:#f4f6fa;--bg2:#eef1f6;--card:#fff;--card2:#f7f9fc;--ink:#151a24;--muted:#5d6678;--faint:#9aa3b5;
          --line:#e3e7ef;--line2:#d4dae6;--cyan:#0b8fb0;--cyan-soft:rgba(11,143,176,.09);--amber:#b8730d;--amber-soft:rgba(184,115,13,.1);--mint:#13895a;--mint-soft:rgba(19,137,90,.1);
          --rose:#c2346a;--rose-soft:rgba(194,52,106,.08);--blue:#2f6fd6;--shadow:0 1px 2px rgba(20,30,50,.06),0 6px 20px rgba(20,30,50,.06)}
        *{box-sizing:border-box}
        html{background:var(--bg)}
        body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,sans-serif;-webkit-font-smoothing:antialiased}
        main{max-width:1120px;margin:0 auto;padding:28px 16px 56px}
        .num{font-variant-numeric:tabular-nums}
        .muted{color:var(--muted)}

        /* Cabecera */
        .masthead{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:14px 24px;padding-bottom:20px;margin-bottom:24px;border-bottom:1px solid var(--line)}
        .brand{display:flex;align-items:center;gap:14px;min-width:0}
        .logo{width:44px;height:44px;flex:none;border-radius:12px;display:grid;place-items:center;background:var(--cyan-soft);color:var(--cyan)}
        .eyebrow{font-size:11px;font-weight:700;letter-spacing:.14em;text-transform:none;color:var(--cyan)}
        h1{margin:0;font-size:24px;line-height:1.2;font-weight:700;letter-spacing:-.015em;overflow-wrap:anywhere}
        .upd{display:inline-flex;align-items:center;gap:8px;padding:6px 12px;border:1px solid var(--line);border-radius:99px;background:var(--card);color:var(--muted);font-size:12.5px}
        .dot{width:8px;height:8px;border-radius:50%;background:var(--mint);box-shadow:0 0 0 0 var(--mint);animation:pulse 2s ease-out infinite}
        @keyframes pulse{0%{box-shadow:0 0 0 0 rgba(61,220,151,.5)}70%{box-shadow:0 0 0 7px rgba(61,220,151,0)}100%{box-shadow:0 0 0 0 rgba(61,220,151,0)}}
        .profiles{margin:-12px 0 22px;font-size:13px;color:var(--muted)}

        /* Cifras clave */
        .kpis{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}
        .kpi{position:relative;background:var(--card);border:1px solid var(--line);border-radius:16px;padding:16px 18px;box-shadow:var(--shadow);min-width:0}
        .kpi .l{font-size:12px;font-weight:600;letter-spacing:.06em;text-transform:none;color:var(--muted)}
        .kpi .v{margin-top:6px;font-size:30px;font-weight:700;line-height:1.1;letter-spacing:-.02em;font-variant-numeric:tabular-nums;overflow-wrap:anywhere}
        .kpi .s{margin-top:4px;font-size:12.5px;color:var(--muted)}
        .kpi.hero{grid-column:span 2;grid-row:span 2;display:flex;flex-direction:column;justify-content:space-between;overflow:hidden;
          background:linear-gradient(135deg,var(--cyan-soft),transparent 65%),var(--card)}
        .kpi.hero .v{font-size:64px;color:var(--cyan);margin-top:10px}
        .kpi.hero .s{font-size:13.5px}
        .kpi .acc{color:var(--amber)} .kpi .ros{color:var(--rose)} .kpi .mnt{color:var(--mint)}
        .split{display:flex;gap:28px;margin-top:18px;padding-top:14px;border-top:1px solid var(--line)}
        .split b{display:block;font-size:20px;font-variant-numeric:tabular-nums}
        .split span{font-size:12px;color:var(--muted)}

        /* Secciones */
        section{margin-top:28px}
        .sh{display:flex;flex-wrap:wrap;align-items:baseline;justify-content:space-between;gap:4px 16px;margin:0 0 12px}
        h2{margin:0;font-size:16px;font-weight:700;letter-spacing:-.005em}
        .sh .meta{font-size:12.5px;color:var(--muted)}
        .card{background:var(--card);border:1px solid var(--line);border-radius:16px;box-shadow:var(--shadow)}
        .pad{padding:18px 20px}
        .grid2{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px 16px}
        .grid2 section{margin-top:0}

        /* Gráfico de 30 días */
        .chart{position:relative;display:grid;grid-template-columns:auto 1fr;gap:0 10px}
        .yaxis{display:flex;flex-direction:column;justify-content:space-between;height:200px;font-size:11px;color:var(--faint);text-align:right;font-variant-numeric:tabular-nums}
        .yaxis span{transform:translateY(-50%)} .yaxis span:last-child{transform:translateY(50%)}
        .plot{position:relative;height:200px;display:flex;align-items:flex-end;gap:3px;
          background:linear-gradient(var(--line) 1px,transparent 1px) 0 0/100% 50%;border-bottom:1px solid var(--line2)}
        .avg{position:absolute;left:0;right:0;z-index:2;border-top:1px dashed var(--amber);pointer-events:none}
        .avg span{position:absolute;left:0;top:-19px;font-size:11px;font-weight:600;color:var(--amber);background:var(--card);padding:0 4px;border-radius:4px}
        .col{flex:1;min-width:4px;height:100%;display:flex;align-items:flex-end;position:relative}
        .bar{width:100%;background:var(--blue);border-radius:4px 4px 1px 1px;transition:filter .12s,background .12s}
        .col.today .bar{background:var(--cyan)}
        .bar.zero{background:var(--line2);height:2px!important;border-radius:1px}
        .col:hover .bar{filter:brightness(1.25)}
        .tip{position:absolute;left:50%;transform:translateX(-50%);white-space:nowrap;background:var(--ink);color:var(--bg);
          padding:4px 9px;border-radius:8px;font-size:12px;font-weight:600;opacity:0;pointer-events:none;transition:opacity .12s;z-index:3}
        .col:nth-child(-n+4) .tip{left:0;transform:none}
        .col:nth-last-child(-n+4) .tip{left:auto;right:0;transform:none}
        .col:hover .tip{opacity:1}
        .xaxis{grid-column:2;display:flex;gap:3px;margin-top:8px;font-size:11px;color:var(--faint)}
        .xaxis span{flex:1;min-width:4px;text-align:center;white-space:nowrap;overflow:visible}
        .xaxis span:empty{visibility:hidden}
        .xaxis .t{color:var(--cyan);font-weight:600}

        /* Listas con barra de proporción */
        .rows{display:flex;flex-direction:column}
        .row{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px 12px;padding:10px 0;border-bottom:1px solid var(--line)}
        .row:last-child{border-bottom:none}
        .row .n{font-weight:700;font-variant-numeric:tabular-nums;text-align:right}
        .row .n small{font-weight:500;color:var(--muted);margin-left:6px}
        .track{grid-column:1/-1;height:6px;border-radius:3px;background:var(--bg2);overflow:hidden}
        .track i{display:block;height:100%;border-radius:3px;background:var(--cyan)}
        .row.sub{color:var(--muted)} .row.sub .n{color:var(--muted);font-weight:600}
        .row.warn .track i{background:var(--amber)}
        .note{margin:12px 0 0;font-size:12.5px;color:var(--muted)}

        /* Tablas */
        .tw{overflow-x:auto}
        table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
        th,td{text-align:left;padding:11px 16px;border-bottom:1px solid var(--line);vertical-align:middle}
        th{position:sticky;top:0;background:var(--card2);font-size:11.5px;font-weight:700;letter-spacing:.07em;text-transform:none;color:var(--muted);white-space:nowrap}
        thead tr th:first-child{border-top-left-radius:15px} thead tr th:last-child{border-top-right-radius:15px}
        tbody tr:last-child td{border-bottom:none}
        tbody tr:hover td{background:var(--bg2)}
        .r{text-align:right} .nw{white-space:nowrap}
        .sm{font-size:12.5px;color:var(--muted)}
        .badge{display:inline-flex;align-items:center;gap:6px;padding:2px 10px;border-radius:99px;font-size:12.5px;font-weight:600;white-space:nowrap;background:var(--bg2);color:var(--muted)}
        .badge.win{background:var(--amber-soft);color:var(--amber)}
        .badge.top{background:var(--mint-soft);color:var(--mint)}
        .badge.mode{background:transparent;border:1px solid var(--line2);color:var(--ink);font-weight:500}
        .chests{display:flex;align-items:center;justify-content:flex-end;gap:10px}
        .chests .mini{width:70px;height:6px;border-radius:3px;background:var(--bg2);overflow:hidden}
        .chests .mini i{display:block;height:100%;background:var(--amber);border-radius:3px}
        .chests b{min-width:28px;text-align:right}
        .star{color:var(--rose);font-weight:600}
        .tlist{display:flex;flex-direction:column;gap:2px}
        tr.unusual td{background:var(--amber-soft)}
        .flag{display:flex;gap:12px;align-items:flex-start;margin-top:28px;padding:14px 18px;border:1px solid var(--amber);border-radius:14px;background:var(--amber-soft)}
        .flag .ic{color:var(--amber);font-size:18px;line-height:1.2}
        .empty{padding:28px 20px;text-align:center;color:var(--muted)}

        /* Carga */
        .loading{display:flex;gap:16px;align-items:center;background:var(--card);border:1px solid var(--cyan);border-radius:16px;padding:16px 18px;margin-bottom:22px;box-shadow:var(--shadow)}
        .spin{width:30px;height:30px;flex:none;border:3px solid var(--line2);border-top-color:var(--cyan);border-radius:50%;animation:spin .8s linear infinite}
        @keyframes spin{to{transform:rotate(360deg)}}
        .loading .grow{flex:1;min-width:0}
        .pbar{height:8px;background:var(--line);border-radius:4px;overflow:hidden;margin:8px 0 6px}
        .pbar i{display:block;height:100%;background:var(--cyan);border-radius:4px}

        /* Pie */
        footer{margin-top:36px;padding-top:18px;border-top:1px solid var(--line);color:var(--muted);font-size:12.5px;line-height:1.65}
        footer h3{margin:0 0 6px;font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:none;color:var(--muted)}
        footer p{margin:0 0 6px;max-width:860px}

        /* Pestañas: barra fija arriba; sin JavaScript se ven todas las secciones seguidas */
        .tabs{position:sticky;top:0;z-index:20;display:flex;gap:4px;overflow-x:auto;scrollbar-width:none;margin:0 -16px 24px;padding:10px 16px;
          background:color-mix(in srgb,var(--bg) 88%,transparent);-webkit-backdrop-filter:blur(12px);backdrop-filter:blur(12px);border-bottom:1px solid var(--line)}
        .tabs::-webkit-scrollbar{display:none}
        html:not(.js) .tabs{display:none}
        .tab{appearance:none;border:0;background:transparent;color:var(--muted);font:inherit;font-size:13.5px;font-weight:600;padding:8px 14px;border-radius:10px;
          cursor:pointer;display:inline-flex;align-items:center;gap:8px;white-space:nowrap;transition:background .12s,color .12s}
        .tab:hover{color:var(--ink);background:var(--card)}
        .tab[aria-selected="true"]{color:var(--ink);background:var(--card);box-shadow:inset 0 -2px 0 var(--cyan),0 0 0 1px var(--line)}
        .tab .c{font-size:11.5px;font-weight:700;padding:1px 7px;border-radius:99px;background:var(--bg2);color:var(--muted);font-variant-numeric:tabular-nums}
        .tab[aria-selected="true"] .c{background:var(--cyan-soft);color:var(--cyan)}
        .tab:focus-visible,.more:focus-visible{outline:2px solid var(--cyan);outline-offset:2px}
        .panel>:first-child{margin-top:0}
        html:not(.js) .panel+.panel{margin-top:40px}
        .panel[hidden]{display:none}
        .more{font-size:13px;font-weight:600;color:var(--cyan);text-decoration:none;padding:4px 10px;border-radius:8px}
        .more:hover{background:var(--cyan-soft)}

        /* Piezas nuevas */
        .split b.acc{color:var(--amber)} .split b.mnt{color:var(--mint)}
        .row.rose .track i{background:var(--rose)}
        .row.off .track i{background:var(--line2)}
        .row .lbl{display:flex;flex-wrap:wrap;align-items:center;gap:6px 10px}
        .row .badge{font-size:11.5px;padding:1px 8px}
        .minis{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin-bottom:28px}
        .mini-kpi{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px 16px;box-shadow:var(--shadow);min-width:0}
        .mini-kpi b{display:block;font-size:24px;line-height:1.15;font-variant-numeric:tabular-nums}
        .mini-kpi span{font-size:12.5px;color:var(--muted)}
        .ros{color:var(--rose)} .acc{color:var(--amber)} .cy{color:var(--cyan)}
        .flag{margin:0 0 20px}
        .empty.big{padding:56px 24px;font-size:14.5px}
        .empty.big .star{display:block;font-size:30px;margin-bottom:8px}
        table.matches tr.won td{background:var(--amber-soft)} table.matches tr.won td:first-child{box-shadow:inset 3px 0 0 var(--amber)}
        .team{margin-top:4px;max-width:340px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        td.foot{text-align:center;padding:14px 16px}
        .callout{display:flex;gap:12px;align-items:flex-start;padding:13px 16px;margin-bottom:24px;border:1px solid var(--line2);border-radius:14px;background:var(--card);color:var(--muted);font-size:13px;line-height:1.55}
        .callout .ic{flex:none;width:20px;height:20px;border-radius:50%;display:grid;place-items:center;background:var(--cyan-soft);color:var(--cyan);font-weight:700;font-size:12px;font-style:italic;font-family:Georgia,serif}
        .mates{display:grid;grid-template-columns:repeat(auto-fill,minmax(270px,1fr));gap:12px}
        .mate{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:16px 18px;box-shadow:var(--shadow);min-width:0}
        .who{display:flex;align-items:center;gap:12px;min-width:0}
        .who .grow{min-width:0}
        .av{width:40px;height:40px;flex:none;border-radius:50%;display:grid;place-items:center;font-weight:700;font-size:17px;color:#0b0e14}
        .av.sm-av{width:26px;height:26px;font-size:12px}
        .who .nm{font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .mate .nm{font-size:15.5px}
        .mate .st{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;margin-top:14px;padding-top:12px;border-top:1px solid var(--line)}
        .mate .st b{display:block;font-size:20px;line-height:1.2;font-variant-numeric:tabular-nums}
        .mate .st b small{font-size:12px;font-weight:600;color:var(--muted);margin-right:2px}
        .mate .st span{display:block;font-size:11.5px;color:var(--muted);line-height:1.35}
        .boards{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:12px}
        .board{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:14px 16px 12px;box-shadow:var(--shadow);min-width:0}
        .board h3{margin:0 0 8px;font-size:12px;font-weight:700;letter-spacing:.07em;text-transform:none;color:var(--muted)}
        .board ol{list-style:none;margin:0;padding:0}
        .board li{display:grid;grid-template-columns:22px 26px minmax(0,1fr) auto;align-items:center;gap:2px 8px;padding:7px 0;border-bottom:1px solid var(--line)}
        .board li:last-child{border-bottom:none}
        .board .rk{font-size:12px;font-weight:700;color:var(--faint);text-align:center;font-variant-numeric:tabular-nums}
        .board .rk.medal{font-size:16px}
        .board .who2{display:flex;flex-direction:column;min-width:0}
        .board .who2 .nm{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .board .who2 .sub{font-size:11.5px;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .board li b{font-size:15px;font-variant-numeric:tabular-nums;white-space:nowrap}
        .board li b small{font-size:11px;font-weight:600;color:var(--muted);margin-right:2px}
        .board li.first b{color:var(--amber);font-size:17px}
        .board .bar{grid-column:3/-1;height:4px;border-radius:2px;background:var(--bg2);overflow:hidden}
        .board .bar i{display:block;height:100%;background:var(--cyan);border-radius:2px}
        .board li.first .bar i{background:var(--amber)}
        .bfoot{margin-top:8px;font-size:12px;color:var(--muted)}
        .mates-line{display:flex;flex-wrap:wrap;gap:4px 6px;margin-top:5px}
        .chip{display:inline-flex;align-items:center;gap:5px;padding:2px 9px 2px 3px;border-radius:99px;background:var(--bg2);font-size:12px;white-space:nowrap;max-width:100%}
        .matches .chip{padding-left:9px}
        .chip .cn{color:var(--muted);overflow:hidden;text-overflow:ellipsis;max-width:130px}
        .chip b{font-variant-numeric:tabular-nums}
        .chip .sm-av{width:20px;height:20px;font-size:10.5px}
        .mini-kpi b small{font-size:13px;font-weight:600;color:var(--muted);margin-right:2px}
        .teambar{display:flex;align-items:center;justify-content:flex-end;gap:10px}
        .teambar .tb{position:relative;width:90px;height:6px;border-radius:3px;background:var(--bg2);overflow:hidden}
        .teambar .tb i{position:absolute;left:0;top:0;bottom:0;border-radius:3px;background:var(--cyan)}
        .teambar .tb i.me{background:var(--amber);z-index:1}
        .teambar b{min-width:44px;text-align:right}
        .teambar b small{font-size:11px;color:var(--muted);margin-right:2px}
        @media (max-width:520px){.teambar .tb{display:none}}
        @media (max-width:860px){.minis{grid-template-columns:repeat(2,minmax(0,1fr))}}
        @media (max-width:860px){.kpis{grid-template-columns:repeat(2,minmax(0,1fr))}.grid2{grid-template-columns:1fr}.kpi.hero .v{font-size:52px}}
        @media (max-width:520px){main{padding:20px 16px 40px}.kpis{grid-template-columns:1fr 1fr}.kpi.hero{grid-row:auto}.kpi .v{font-size:24px}
          .kpi.hero .v{font-size:44px}th,td{padding:10px 12px}.chests .mini{display:none}.yaxis,.plot{height:150px}}
        @media (prefers-reduced-motion:reduce){.dot,.spin{animation:none}}
        """;

    // Pestañas del informe. Se abre siempre en Resumen; la pestaña elegida y la posición solo se recuerdan en esa
    // pestaña del navegador mientras la página se recarga sola. Sin JavaScript se ven todas las secciones seguidas.
    private const string TabsScript = """
        (function(){var d=document,tabs=[].slice.call(d.querySelectorAll('[data-tab]'));if(!tabs.length)return;d.documentElement.classList.add('js');
        function get(k,s){try{return (s?sessionStorage:localStorage).getItem(k)}catch(e){return null}}
        function put(k,v,s){try{(s?sessionStorage:localStorage).setItem(k,v)}catch(e){}}
        function show(id,save){var t=null;tabs.forEach(function(b){var on=b.getAttribute('data-tab')===id;b.setAttribute('aria-selected',on?'true':'false');b.tabIndex=on?0:-1;if(on)t=b});
          if(!t)return false;var bar=t.parentNode;bar.scrollLeft=t.offsetLeft-(bar.clientWidth-t.offsetWidth)/2;[].forEach.call(d.querySelectorAll('.panel'),function(p){p.hidden=p.id!=='t-'+id});if(save)put('cc-tab',id,true);return true}
        var h=location.hash.slice(1);if(!(h&&show(h,true))&&!show(get('cc-tab',true)||'',false))show(tabs[0].getAttribute('data-tab'),false);
        function go(id){if(show(id,true)){window.scrollTo(0,0);try{history.replaceState(null,'','#'+id)}catch(e){}}}
        tabs.forEach(function(b,i){b.addEventListener('click',function(){go(b.getAttribute('data-tab'))});
          b.addEventListener('keydown',function(e){var k=e.key==='ArrowRight'?1:e.key==='ArrowLeft'?-1:0;if(!k)return;e.preventDefault();var n=tabs[(i+k+tabs.length)%tabs.length];go(n.getAttribute('data-tab'));n.focus()})});
        [].forEach.call(d.querySelectorAll('[data-go]'),function(a){a.addEventListener('click',function(e){e.preventDefault();go(a.getAttribute('data-go'))})});
        var y=get('cc-y',true);if(y)window.scrollTo(0,+y);addEventListener('pagehide',function(){put('cc-y',String(window.scrollY),true)});
        })();
        """;

    private static string ChestIcon => Branding.ChestHtml;

    public static string Report(Config config, TrackerData data, IReadOnlyList<Profile> profiles, int initialChests = 0)
    {
        var progress = Progress;
        var matches = data.Matches.OrderByDescending(When).ToList();
        var countedChests = matches.Sum(m => m.ChestsOpened);
        var totalChests = countedChests + Math.Max(0, initialChests);
        var reported = HistoryCompatibility.Reported(data);
        var totalMatches = matches.Count + reported.Matches;
        var avg = HistoryCompatibility.Average(data);
        var best = matches.Count > 0 ? matches.Max(m => m.ChestsOpened) : 0;
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        var sessionMatches = matches.Where(IsThisSession).ToList();
        var session = sessionMatches.Sum(m => m.ChestsOpened);
        var wins = matches.Count(m => m.Placement == 1);
        var player = matches.Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";
        string N(int n) => n.ToString("#,0", Loc);

        var sb = new StringBuilder();
        var title = L.T("Contador de cofres", "Chest Counter");
        var updated = L.F("Actualizado {0} · en directo", "Updated {0} · live",
            DateTime.Now.ToString(L.En ? "MMM d, yyyy HH:mm" : "dd/MM/yyyy HH:mm", Loc));
        var heading = player.Length > 0 ? H(player) : title;
        sb.Append($"""
            <!doctype html><html lang="{L.Code}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="dark light">
            <meta http-equiv="refresh" content="{(progress != null ? 2 : 15)}">
            <title>{title}{(player.Length > 0 ? " · " + H(player) : "")}</title><style>{Style}{Branding.ReportCss}</style></head><body><main>
            <header class="masthead">
              <div class="brand"><div class="logo">{ChestIcon}</div>
                <div><div class="eyebrow">{(player.Length > 0 ? title : L.T("Informe", "Report"))}</div><h1>{heading}</h1></div></div>
              <div class="upd"><span class="dot"></span>{updated}</div>
            </header>
            {LoadingCard(progress)}{ProfilesLine(profiles, player)}
            """);

        // ---- Datos que comparten varias pestañas -----------------------------------------------------
        var extraKinds = config.ExtraKinds();
        var treasureRows = matches.SelectMany(m => m.TreasureFinds.Select(f => (m, f))).ToList();
        (string Html, int Count)? mates = config.ShowTeammates ? TeammatesPanel(matches, extraKinds, N) : null;
        var top10 = matches.Count(m => m.Placement is > 1 and <= 10);
        var bestDay = matches.GroupBy(m => When(m).Date).Select(g => (Day: g.Key, Chests: g.Sum(m => m.ChestsOpened)))
            .OrderByDescending(d => d.Chests).FirstOrDefault();

        // ---- Pestañas ---------------------------------------------------------------------------------
        var tabs = new List<(string Id, string Label, string? Count)>
        {
            ("resumen", L.T("Resumen", "Overview"), null),
            ("cofres", L.T("Cofres", "Chests"), null),
            ("tesoros", L.T("Tesoros", "Treasures"), treasureRows.Count > 0 ? N(treasureRows.Count) : null),
            ("partidas", L.T("Partidas", "Matches"), N(matches.Count)),
        };
        if (mates != null)
        {
            tabs.Add(("companeros", L.T("Compañeros", "Teammates"), mates.Value.Count > 0 ? N(mates.Value.Count) : null));
        }
        sb.Append($"""<nav class="tabs" role="tablist" aria-label="{L.T("Secciones del informe", "Report sections")}">""");
        foreach (var (id, label, count) in tabs)
        {
            sb.Append($"""<button class="tab" role="tab" type="button" id="tab-{id}" data-tab="{id}" aria-controls="t-{id}">{label}{(count != null ? $"<span class=\"c\">{count}</span>" : "")}</button>""");
        }
        sb.Append("</nav>");

        // ================================ Resumen ================================
        sb.Append("""<div class="panel" id="t-resumen" role="tabpanel" aria-labelledby="tab-resumen">""");
        var initialNote = initialChests > 0
            ? L.F("Incluye {0} estimados de antes de usar la herramienta", "Includes {0} estimated from before the tool", N(initialChests))
            : L.T("Cofres abiertos o destruidos por ti, según tus replays", "Chests you opened or destroyed, from your replays");
        if (reported.Matches > 0) initialNote = WebUtility.HtmlEncode(HistoryCompatibility.ReportedNote(data));
        var matesAdded = matches.Sum(m => m.TeammatesAdded);
        if (config.CountTeammateChests && matesAdded > 0)
        {
            initialNote += " · " + L.F("incluye ≥ {0} de tus compañeros", "includes ≥ {0} from your teammates", N(matesAdded));
        }
        var winRate = matches.Count > 0 ? 100.0 * wins / matches.Count : 0;
        sb.Append($"""
            <div class="kpis">
              <div class="kpi hero">
                <div><div class="l">{L.T("Cofres en total", "Total chests")}</div><div class="v">{N(totalChests)}</div><div class="s">{initialNote}</div></div>
                <div class="split">
                  <div><b>{N(session)}</b><span>{L.T("en esta sesión", "this session")}{(sessionMatches.Count > 0 ? " · " + L.Count(sessionMatches.Count, "partida", "partidas", "match", "matches") : "")}</span></div>
                  <div><b class="acc">{N(wins)}</b><span>{L.T(wins == 1 ? "victoria real" : "victorias reales", wins == 1 ? "Victory Royale" : "Victory Royales")}{(wins > 0 ? $" · {winRate.ToString("0", Loc)} %" : "")}</span></div>
                  <div><b class="mnt">{N(top10)}</b><span>{L.T("top 10 sin ganar", "top 10 without winning")}</span></div>
                </div>
              </div>
              <div class="kpi"><div class="l">{L.T("Partidas", "Matches")}</div><div class="v">{N(totalMatches)}</div><div class="s">{(reported.Matches > 0 ? L.F("{0} locales + {1} otro PC", "{0} local + {1} other PC", N(matches.Count), N(reported.Matches)) : L.T("analizadas", "analyzed"))}</div></div>
              <div class="kpi"><div class="l">{L.T("Media", "Average")}</div><div class="v acc">{avg.ToString("0.0", Loc)}</div><div class="s">{L.T("cofres por partida", "chests per match")}</div></div>
              <div class="kpi"><div class="l">{L.T("Récord", "Best match")}</div><div class="v">{N(best)}</div><div class="s">{(bestDay.Chests > 0 ? L.F("mejor día: {0} ({1})", "best day: {0} ({1})", N(bestDay.Chests), bestDay.Day.ToString(L.En ? "MMM d" : "dd/MM", Loc)) : L.T("cofres en una partida", "chests in one match"))}</div></div>
              <div class="kpi"><div class="l">{L.T("Tesoros", "Treasures")}</div><div class="v ros">{N(treasures)}</div><div class="s">{L.T("encontrados", "found")}</div></div>
            </div>
            """);

        // Últimos 30 días: una barra por día, con escala y la media diaria.
        var today = DateTime.Now.Date;
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).ToList();
        var perDay = days.Select(d => matches.Where(m => When(m).Date == d).Sum(m => m.ChestsOpened)).ToList();
        var top = NiceMax(perDay.Max());
        var sum30 = perDay.Sum();
        var activeDays = perDay.Count(v => v > 0);
        var dayAvg = activeDays > 0 ? (double)sum30 / activeDays : 0;
        sb.Append($"""
            <section><div class="sh"><h2>{L.T("Cofres por día", "Chests per day")}</h2>
            <span class="meta">{L.T("Últimos 30 días", "Last 30 days")} · {N(sum30)} {L.T("cofres", "chests")} · {L.Count(activeDays, "día jugado", "días jugados", "day played", "days played")}</span></div>
            <div class="card pad"><div class="chart">
            <div class="yaxis"><span>{N(top)}</span><span>{N(top / 2)}</span><span>0</span></div>
            <div class="plot">
            """);
        if (dayAvg > 0)
        {
            var avgPct = 100.0 * dayAvg / top;
            sb.Append($"""<div class="avg" style="bottom:{avgPct.ToString("0.##", CultureInfo.InvariantCulture)}%"><span>{L.T("media", "avg")} {dayAvg.ToString("0", Loc)}</span></div>""");
        }
        for (var i = 0; i < days.Count; i++)
        {
            var v = perDay[i];
            var h = 100.0 * v / top;
            var tip = $"{days[i].ToString(L.En ? "ddd MMM d" : "ddd dd/MM", Loc)} · {L.Count(v, "cofre", "cofres", "chest", "chests")}";
            var hs = h.ToString("0.##", CultureInfo.InvariantCulture);
            var tipAt = Math.Max(h, 1.5).ToString("0.##", CultureInfo.InvariantCulture);
            sb.Append($"<div class=\"col{(i == days.Count - 1 ? " today" : "")}\" aria-label=\"{H(tip)}\"><span class=\"tip\" style=\"bottom:calc({tipAt}% + 6px)\">{H(tip)}</span><div class=\"bar{(v == 0 ? " zero" : "")}\" style=\"height:{hs}%\"></div></div>");
        }
        sb.Append("</div><div class=\"xaxis\">");
        for (var i = 0; i < days.Count; i++)
        {
            var label = i == days.Count - 1 ? $"<span class=\"t\">{L.T("hoy", "today")}</span>"
                : (days.Count - 1 - i) % 7 == 0 ? $"<span>{days[i].ToString(L.En ? "MMM d" : "dd/MM", Loc)}</span>"
                : "<span></span>";
            sb.Append(label);
        }
        sb.Append("</div></div></div></section>");

        // Las últimas partidas, en corto, con enlace a la pestaña completa.
        var withMates = mates != null;
        var shownMax = Math.Max(1, matches.Take(100).Select(m => m.ChestsOpened).DefaultIfEmpty(0).Max());
        sb.Append($"""<section><div class="sh"><h2>{L.T("Últimas partidas", "Latest matches")}</h2>{(matches.Count > 6 ? $"<a class=\"more\" href=\"#partidas\" data-go=\"partidas\">{L.T("Ver todas", "See all")} →</a>" : "")}</div>""");
        AppendMatchTable(sb, matches.Take(6).ToList(), shownMax, withMates, extraKinds);
        sb.Append("</section></div>");

        // ================================ Cofres ================================
        sb.Append("""<div class="panel" id="t-cofres" role="tabpanel" aria-labelledby="tab-cofres">""");
        var byKind = matches.SelectMany(m => m.Chests).GroupBy(c => c.Kind).Select(g => (Kind: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        var otherKinds = matches
            .SelectMany(m => m.OtherInteractions.Count > 0
                ? m.OtherInteractions
                : new Dictionary<string, int> { [ChestKinds.AmmoBox] = m.AmmoBoxesOpened, [ChestKinds.OtherContainer] = m.OtherContainersOpened })
            .GroupBy(kv => kv.Key)
            .Select(g => (Kind: g.Key, Count: g.Sum(kv => kv.Value)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ToList();
        var kindTotal = Math.Max(1, byKind.Sum(k => k.Count));
        var kindMax = Math.Max(1, byKind.Select(k => k.Count).DefaultIfEmpty(0).Max());

        sb.Append("<div class=\"grid2\">");
        sb.Append($"<section><div class=\"sh\"><h2>{L.T("Por tipo de cofre", "By chest type")}</h2><span class=\"meta\">{L.Count(byKind.Sum(k => k.Count), "cofre", "cofres", "chest", "chests")}</span></div><div class=\"card pad\"><div class=\"rows\">");
        if (byKind.Count == 0)
        {
            sb.Append($"<div class=\"empty\">{L.T("Aún no hay cofres registrados.", "No chests recorded yet.")}</div>");
        }
        foreach (var (kind, count) in byKind)
        {
            var share = 100.0 * count / kindTotal;
            sb.Append($"<div class=\"row\"><span>{H(L.Name(kind))}</span><span class=\"n\">{N(count)}<small>{share.ToString("0", Loc)} %</small></span><div class=\"track\"><i style=\"width:{(100.0 * count / kindMax).ToString("0.##", CultureInfo.InvariantCulture)}%\"></i></div></div>");
        }
        var destroyedTotal = matches.Sum(m => m.Chests.Count(c => c.Destroyed));
        if (destroyedTotal > 0)
        {
            sb.Append($"<div class=\"row sub\"><span>↳ {L.T("de ellos, destruidos en vez de abiertos", "of which destroyed instead of opened")}</span><span class=\"n\">{N(destroyedTotal)}</span></div>");
        }
        sb.Append("</div></div></section>");

        // Cajas de munición, de comida y cañones: aparte, con una etiqueta que dice si suman al total.
        sb.Append($"<section><div class=\"sh\"><h2>{L.T("Otros contenedores", "Other containers")}</h2><span class=\"meta\">{L.T("se activan en Ajustes", "turned on in Settings")}</span></div><div class=\"card pad\"><div class=\"rows\">");
        if (otherKinds.Count == 0)
        {
            sb.Append($"<div class=\"empty\">{L.T("Aún no hay cajas ni cañones registrados.", "No boxes or cannons recorded yet.")}</div>");
        }
        var otherMax = Math.Max(1, otherKinds.Select(k => k.Count).DefaultIfEmpty(0).Max());
        foreach (var (kind, count) in otherKinds)
        {
            var added = extraKinds.Contains(kind);
            var pill = added ? $"<span class=\"badge top\">{L.T("suma al total", "added to the total")}</span>" : $"<span class=\"badge\">{L.T("fuera del total", "not in the total")}</span>";
            sb.Append($"<div class=\"row{(added ? "" : " off")}\"><span class=\"lbl\">{H(L.Name(kind))} {pill}</span><span class=\"n\">{N(count)}</span><div class=\"track\"><i style=\"width:{(100.0 * count / otherMax).ToString("0.##", CultureInfo.InvariantCulture)}%\"></i></div></div>");
        }
        sb.Append("</div></div></section></div>");

        // Por modo de juego: dónde abres más cofres.
        var byMode = matches.GroupBy(m => Pretty.Playlist(m.Playlist))
            .Select(g => (Mode: g.Key, Matches: g.Count(), Chests: g.Sum(m => m.ChestsOpened), Best: g.Max(m => m.ChestsOpened), Wins: g.Count(m => m.Placement == 1)))
            .OrderByDescending(x => x.Matches).ToList();
        if (byMode.Count > 0)
        {
            var modeMax = Math.Max(0.1, byMode.Max(x => (double)x.Chests / x.Matches));
            sb.Append($"""
                <section><div class="sh"><h2>{L.T("Por modo de juego", "By game mode")}</h2><span class="meta">{L.Count(byMode.Count, "modo", "modos", "mode", "modes")}</span></div>
                <div class="card tw"><table><thead><tr><th>{L.T("Modo", "Mode")}</th><th class="r">{L.T("Partidas", "Matches")}</th><th class="r">{L.T("Media", "Average")}</th><th class="r">{L.T("Récord", "Best")}</th><th class="r">{L.T("Cofres", "Chests")}</th><th class="r">{L.T("Victorias", "Wins")}</th></tr></thead><tbody>
                """);
            foreach (var x in byMode)
            {
                var a = (double)x.Chests / x.Matches;
                var w = (100.0 * a / modeMax).ToString("0.##", CultureInfo.InvariantCulture);
                sb.Append($"<tr><td><span class=\"badge mode\">{H(x.Mode)}</span></td><td class=\"r\">{N(x.Matches)}</td><td><div class=\"chests\"><span class=\"mini\"><i style=\"width:{w}%\"></i></span><b>{a.ToString("0.0", Loc)}</b></div></td><td class=\"r\">{N(x.Best)}</td><td class=\"r\">{N(x.Chests)}</td><td class=\"r\">{(x.Wins > 0 ? $"<span class=\"badge win\">1.º {N(x.Wins)}</span>" : "<span class=\"muted\">—</span>")}</td></tr>");
            }
            sb.Append("</tbody></table></div></section>");
        }
        sb.Append("</div>");

        // ================================ Tesoros ================================
        sb.Append("""<div class="panel" id="t-tesoros" role="tabpanel" aria-labelledby="tab-tesoros">""");
        if (treasureRows.Count > 0)
        {
            var unusual = treasureRows.Where(r => !r.f.SourceIsChest).ToList();
            if (unusual.Count > 0)
            {
                var kinds = H(string.Join(", ", unusual.Select(r => L.Name(r.f.SourceKind)).Distinct()));
                sb.Append($"<div class=\"flag\"><span class=\"ic\">⚑</span><div><b>{L.Count(unusual.Count, "tesoro obtenido", "tesoros obtenidos", "treasure obtained", "treasures obtained")} {L.T("de otra forma que con un cofre", "other than from a chest")}</b>: {kinds}. {L.T("Resaltados en la tabla de abajo.", "Highlighted in the table below.")}</div></div>");
            }

            var golden = treasureRows.Count(r => r.f.FromGoldenChest);
            var certain = treasureRows.Count(r => r.f.SourceCertain);
            var treasureMatches = treasureRows.Select(r => r.m).Distinct().Count();
            sb.Append($"""
                <div class="minis">
                  <div class="mini-kpi"><b class="ros">{N(treasureRows.Count)}</b><span>{L.T("tesoros en total", "treasures in total")}</span></div>
                  <div class="mini-kpi"><b class="acc">{N(golden)}</b><span>{L.T("de cofres dorados", "from golden chests")}</span></div>
                  <div class="mini-kpi"><b>{N(certain)}</b><span>{L.T("con origen seguro", "with a certain source")}</span></div>
                  <div class="mini-kpi"><b>{(matches.Count > 0 ? (100.0 * treasureMatches / matches.Count).ToString("0", Loc) : "0")} %</b><span>{L.T("de partidas con tesoro", "of matches with a treasure")}</span></div>
                </div>
                """);

            var bySource = treasureRows.GroupBy(r => r.f.SourceLabel).OrderByDescending(g => g.Count()).ToList();
            var srcMax = Math.Max(1, bySource.Max(g => g.Count()));
            sb.Append($"<section><div class=\"sh\"><h2>{L.T("De dónde vienen los tesoros", "Where the treasures come from")}</h2><span class=\"meta\">{L.T("seguros / total", "certain / total")}</span></div><div class=\"card pad\"><div class=\"rows\">");
            foreach (var g in bySource)
            {
                var isChest = g.First().f.SourceIsChest;
                sb.Append($"<div class=\"row{(isChest ? " rose" : " warn")}\"><span>{(isChest ? "" : "⚑ ")}{H(g.Key)}</span><span class=\"n\">{g.Count(r => r.f.SourceCertain)}<small>/ {g.Count()}</small></span><div class=\"track\"><i style=\"width:{(100.0 * g.Count() / srcMax).ToString("0.##", CultureInfo.InvariantCulture)}%\"></i></div></div>");
            }
            sb.Append("</div></div></section>");

            sb.Append($"""
                <section><div class="sh"><h2>{L.T("Tesoros obtenidos", "Treasures obtained")}</h2><span class="meta">{L.Count(treasureRows.Count, "tesoro", "tesoros", "treasure", "treasures")}</span></div>
                <div class="card tw"><table><thead><tr><th>{L.T("Partida", "Match")}</th><th>{L.T("Tesoro", "Treasure")}</th><th>{L.T("Momento", "Time")}</th><th>{L.T("Origen", "Source")}</th></tr></thead><tbody>
                """);
            foreach (var (m, f) in treasureRows)
            {
                var id = f.PrizeId != null ? $"<div class=\"sm\">{H(f.PrizeId)}</div>" : (f.RawItem.StartsWith("WID_") ? $"<div class=\"sm\">{H(f.RawItem)}</div>" : "");
                var when = f.ObtainedAtSeconds is float t ? (f.TimeExact ? Pretty.Clock(t) : $"<span class=\"sm\">{L.T("hacia", "around")}</span> {Pretty.Clock(t)}") : "<span class=\"muted\">—</span>";
                var source = $"{(f.SourceIsChest ? "" : "⚑ ")}{H(f.SourceLabel)}"
                    + (f.SourceCertain ? "" : $" <span class=\"sm\">({Pretty.Likely})</span>")
                    + (!f.SourceIsChest && !string.IsNullOrEmpty(f.SourceBlueprint) ? $"<div class=\"sm\">{H(f.SourceBlueprint)}</div>" : "")
                    + (f.AlsoPossible.Count > 0 ? $"<div class=\"sm\">{L.T("también posible: ", "also possible: ")}{H(string.Join(", ", f.AlsoPossible.Select(L.Name)))}</div>" : "");
                sb.Append($"<tr{(f.SourceIsChest ? "" : " class=\"unusual\"")}><td class=\"nw\">{L.DateTimeShort(When(m))}<div class=\"sm\">{H(Pretty.Playlist(m.Playlist))}</div></td><td><span class=\"star\">{(f.FromGoldenChest ? "★★" : "★")} {H(f.DisplayName)}</span>{id}</td><td class=\"nw num\">{when}</td><td>{source}</td></tr>");
            }
            sb.Append("</tbody></table></div></section>");
        }
        else
        {
            sb.Append($"<div class=\"card\"><div class=\"empty big\"><div class=\"star\">★</div>{L.T("Todavía no has encontrado tesoros. Aparecerán aquí con el momento y el cofre de origen.", "No treasures yet. They will show up here with the time and the chest they came from.")}</div></div>");
        }
        sb.Append(HistoryCompatibility.TeamTreasures(matches));
        sb.Append("</div>");

        // ================================ Partidas ================================
        var shown = matches.Take(100).ToList();
        sb.Append($"""
            <div class="panel" id="t-partidas" role="tabpanel" aria-labelledby="tab-partidas">
            <section><div class="sh"><h2>{L.T("Últimas partidas", "Latest matches")}</h2><span class="meta">{(matches.Count > shown.Count ? L.F("las {0} más recientes de {1}", "latest {0} of {1}", shown.Count, N(matches.Count)) : L.Count(matches.Count, "partida", "partidas", "match", "matches"))}</span></div>
            """);
        AppendMatchTable(sb, shown, shownMax, withMates, extraKinds);
        sb.Append("</section></div>");

        // ================================ Compañeros ================================
        if (mates != null)
        {
            sb.Append($"""<div class="panel" id="t-companeros" role="tabpanel" aria-labelledby="tab-companeros">{mates.Value.Html}</div>""");
        }

        var footer = L.Big("footer",
            """
            <footer><h3>Cómo se cuenta</h3>
            <p>Cuenta los cofres que abriste o destruiste tú mismo, según tus archivos de replay (analizados tras cada partida). Las cajas de munición, las cajas de comida y los cañones de botín solo cuentan si lo activas en Ajustes.</p>
            <p><span class="star">★★</span> cofre dorado de la caza del tesoro (cosmético, acceso a la tienda…), detectado con seguridad. <span class="star">★</span> tesoro recibido gracias a uno de tus cofres (momento exacto), con nombre cuando el objeto se puede identificar.</p>
            <p>Los nombres de los premios se pueden editar en <code>treasures.json</code>. Esta página se actualiza sola.</p></footer>
            </main></body></html>
            """,
            """
            <footer><h3>How it counts</h3>
            <p>Counts the chests you opened or destroyed yourself, from your replay files (analyzed after each match). Ammo boxes, food boxes and loot cannons only count if you turn it on in Settings.</p>
            <p><span class="star">★★</span> golden treasure hunt chest (cosmetic, shop access…), detected for sure. <span class="star">★</span> treasure received from one of your own chests (exact moment), named when the item can be identified.</p>
            <p>Prize names can be edited in <code>treasures.json</code>. This page refreshes by itself.</p></footer>
            </main></body></html>
            """);
        sb.Append(footer.Replace("</footer>", $"<p><a href=\"{Branding.TWITCH_URL}\" target=\"_blank\" rel=\"noopener noreferrer\">{Branding.TWITCH_NAME} ↗</a></p></footer>")
            .Replace("</main></body></html>", $"</main><script>{TabsScript}</script></body></html>"));
        return sb.ToString();
    }

    /// <summary>
    /// Tabla de partidas (fecha, modo, puesto, cofres, tesoros). Con «Compañeros»: tus cofres, el equipo (≥) y, bajo
    /// el modo, los cofres vistos de cada compañero.
    /// </summary>
    private static void AppendMatchTable(StringBuilder sb, List<MatchRecord> rows, int chestMax, bool withMates, IReadOnlyCollection<string> kinds)
    {
        var hasElims = rows.Any(m => m.Eliminations is > 0);
        var cols = 5 + (hasElims ? 1 : 0) + (withMates ? 1 : 0);
        var chestsHead = withMates ? L.T("Tú", "You") : L.T("Cofres", "Chests");
        sb.Append($"""<div class="card tw"><table class="matches"><thead><tr><th>{L.T("Fecha", "Date")}</th><th>{L.T("Modo", "Mode")}</th><th>{L.T("Puesto", "Placement")}</th>{(hasElims ? $"<th class=\"r\">{L.T("Elim.", "Elims")}</th>" : "")}<th class="r">{chestsHead}</th>{(withMates ? $"<th class=\"r\">{L.T("Equipo", "Team")}</th>" : "")}<th>{L.T("Tesoros", "Treasures")}</th></tr></thead><tbody>""");
        if (rows.Count == 0)
        {
            sb.Append($"<tr><td colspan=\"{cols}\" class=\"empty\">{L.T("Aún no hay partidas analizadas. Juega una y aparecerá aquí.", "No matches analyzed yet. Play one and it will show up here.")}</td></tr>");
        }
        foreach (var m in rows)
        {
            var treasure = m.TreasureFinds.Count == 0 ? "<span class=\"muted\">—</span>"
                : "<div class=\"tlist\">" + string.Concat(m.TreasureFinds.Select(f => $"<span class=\"star\">{(f.FromGoldenChest ? "★★" : "★")} {H(f.DisplayName)}</span>")) + "</div>";
            var placeClass = m.Placement switch { 1 => " win", > 0 and <= 10 => " top", _ => "" };
            var placeText = m.Placement == 1 ? "1.º " + H(Pretty.Placement(m)) : H(Pretty.Placement(m));
            var own = withMates ? m.OwnChests : m.ChestsOpened;
            var barW = (100.0 * own / Math.Max(1, chestMax)).ToString("0.##", CultureInfo.InvariantCulture);
            var team = withMates && m.Teammates is { Count: > 0 } t
                ? "<div class=\"mates-line\">" + string.Concat(t.OrderByDescending(x => MatchRecord.Seen(x, kinds))
                    .Select(x => $"<span class=\"chip\"><span class=\"cn\">{H(x.Name)}</span> <b>≥ {MatchRecord.Seen(x, kinds)}</b></span>")) + "</div>"
                : "";
            var teamCell = !withMates ? ""
                : m.Teammates is { Count: > 0 }
                    ? $"<td class=\"r nw\"><span class=\"sm\">≥</span> <b class=\"cy\">{N0(m.OwnChests + m.TeammatesSeen(kinds))}</b></td>"
                    : "<td class=\"r\"><span class=\"muted\">—</span></td>";
            var elims = hasElims ? $"<td class=\"r\">{(m.Eliminations is int e ? N0(e) : "<span class=\"muted\">—</span>")}</td>" : "";
            sb.Append($"<tr{(m.Placement == 1 ? " class=\"won\"" : "")}><td class=\"nw\">{L.DateTimeShort(When(m))}</td><td><span class=\"badge mode\">{H(Pretty.Playlist(m.Playlist))}</span>{team}</td><td><span class=\"badge{placeClass}\">{placeText}</span></td>{elims}<td><div class=\"chests\"><span class=\"mini\"><i style=\"width:{barW}%\"></i></span><b>{own}</b></div></td>{teamCell}<td>{treasure}</td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static string N0(int n) => n.ToString("#,0", Loc);

    /// <summary>
    /// Pestaña «Compañeros» (opción de Ajustes): con quién juegas, partidas juntos, victorias y cofres que se les vio
    /// abrir. Los cofres salen de tu replay, que solo tiene lo que pasó cerca de ti, así que son un mínimo.
    /// Devuelve el HTML y cuántos compañeros habituales (o mostrados) hay, para la etiqueta de la pestaña.
    /// </summary>
    private static (string Html, int Count) TeammatesPanel(List<MatchRecord> matches, IReadOnlyCollection<string> extraKinds, Func<int, string> N)
    {
        int Seen(TeammateRecord t) => MatchRecord.Seen(t, extraKinds);
        var all = matches
            .Where(m => m.Teammates != null)
            .SelectMany(m => m.Teammates!.Select(t => (m, t)))
            .GroupBy(r => r.t.EpicId ?? r.t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => When(r.m)).First();
                var together = g.Select(r => r.m).Distinct().ToList();
                return (
                    Key: g.Key,
                    Name: latest.t.Name,
                    Matches: together.Count,
                    Chests: g.Sum(r => Seen(r.t)),
                    Mine: together.Sum(m => m.OwnChests),
                    Best: g.Max(r => Seen(r.t)),
                    Wins: together.Count(m => m.Placement == 1),
                    Last: When(latest.m),
                    Team: together.Sum(m => m.OwnChests + m.TeammatesSeen(extraKinds)));
            })
            .OrderByDescending(r => r.Matches).ThenByDescending(r => r.Chests)
            .ToList();

        var sb = new StringBuilder();
        var tracked = matches.Count(m => m.Teammates is { Count: > 0 });
        string[] tones = { "var(--cyan)", "var(--amber)", "var(--mint)", "var(--rose)", "var(--blue)" };
        string Tone(string key) => tones[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(key) % tones.Length)];
        string Initial(string name) => H(name.Trim().Length > 0 ? char.ToUpperInvariant(name.Trim()[0]).ToString() : "?");
        string Pct(int part, int whole) => whole > 0 ? (100.0 * part / whole).ToString("0", Loc) + " %" : "—";
        sb.Append($"""
            <div class="callout"><span class="ic">i</span><div>{L.Big("teammatesNote",
                "Los cofres de tus compañeros no son exactos: salen de tu replay, y Fortnite solo graba lo que pasa a menos de 1.087 m de ti. Si un compañero se aleja más, sus cofres no se cuentan. Son un mínimo, no su total. «Tus cofres» son los tuyos en esas mismas partidas, para comparar.",
                "Your teammates' chests are not exact: they come from your replay, and Fortnite only records what happens within 1,087 m of you. If a teammate goes further, their chests are not counted. They are a minimum, not their total. \"Your chests\" are yours in those same matches, for comparison.")}</div></div>
            """);
        if (all.Count == 0)
        {
            sb.Append($"""<div class="card"><div class="empty big">{L.T("Aún no hay partidas en equipo analizadas. Aparecerán aquí tras tu próxima partida en dúos, tríos o escuadrones.", "No team matches analyzed yet. They will show up here after your next duos, trios or squads match.")}</div></div>""");
            return (sb.ToString(), 0);
        }

        // Cofres del equipo: tú + lo que se vio abrir a tus compañeros, en las partidas con compañeros.
        var teamMatches = matches.Where(m => m.Teammates is { Count: > 0 }).ToList();
        var teamMine = teamMatches.Sum(m => m.OwnChests);
        var teamMates = teamMatches.Sum(m => m.TeammatesSeen(extraKinds));
        var teamAll = teamMine + teamMates;
        string Avg(int total, int count) => count > 0 ? ((double)total / count).ToString("0.0", Loc) : "—";
        sb.Append($"""
            <section><div class="sh"><h2>{L.T("Cofres del equipo", "Team chests")}</h2><span class="meta">{L.Count(teamMatches.Count, "partida en equipo", "partidas en equipo", "team match", "team matches")}</span></div>
            <div class="minis">
              <div class="mini-kpi"><b class="cy"><small>≥</small>{N(teamAll)}</b><span>{L.T("cofres del equipo en total", "team chests in total")}</span></div>
              <div class="mini-kpi"><b class="cy"><small>≥</small>{Avg(teamAll, teamMatches.Count)}</b><span>{L.T("del equipo por partida", "per match for the team")}</span></div>
              <div class="mini-kpi"><b class="acc">{Avg(teamMine, teamMatches.Count)}</b><span>{L.T("tuyos por partida", "yours per match")}</span></div>
              <div class="mini-kpi"><b><small>≥</small>{(teamAll > 0 ? (100.0 * teamMates / teamAll).ToString("0", Loc) : "0")} %</b><span>{L.T("aportan tus compañeros", "from your teammates")}</span></div>
            </div></section>
            """);

        // Top de compañeros: clasificaciones de 5 puestos (cofres vistos, media, récord y victorias juntos).
        var medals = new[] { "🥇", "🥈", "🥉" };
        void Board(StringBuilder b, string title, string? foot, List<(string Key, string Name, double Value, string Text, string Sub)> items)
        {
            if (items.Count == 0)
            {
                return;
            }
            var max = Math.Max(0.0001, items.Max(i => i.Value));
            b.Append($"<div class=\"board\"><h3>{title}</h3><ol>");
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var rank = i < medals.Length ? $"<span class=\"rk medal\">{medals[i]}</span>" : $"<span class=\"rk\">{i + 1}</span>";
                var w = (100.0 * it.Value / max).ToString("0.##", CultureInfo.InvariantCulture);
                b.Append($"<li{(i == 0 ? " class=\"first\"" : "")}>{rank}<span class=\"av sm-av\" style=\"background:{Tone(it.Key)}\">{Initial(it.Name)}</span>"
                    + $"<span class=\"who2\"><span class=\"nm\" title=\"{H(it.Name)}\">{H(it.Name)}</span><span class=\"sub\">{it.Sub}</span></span><b>{it.Text}</b>"
                    + $"<span class=\"bar\"><i style=\"width:{w}%\"></i></span></li>");
            }
            b.Append("</ol>");
            if (foot != null)
            {
                b.Append($"<div class=\"bfoot\">{foot}</div>");
            }
            b.Append("</div>");
        }
        string Games(int n) => L.Count(n, "partida", "partidas", "match", "matches");
        var minAvg = all.Count(r => r.Matches >= 3) >= 3 ? 3 : 2;
        var boards = new StringBuilder();
        Board(boards, L.T("Más cofres vistos", "Most chests seen"), null, all.OrderByDescending(r => r.Chests).ThenByDescending(r => r.Matches).Take(5)
            .Select(r => (r.Key, r.Name, (double)r.Chests, $"<small>≥</small>{N(r.Chests)}", Games(r.Matches))).ToList());
        Board(boards, L.T("Mejor media por partida", "Best average per match"),
            L.F("Tu media en partidas en equipo: {0}", "Your average in team matches: {0}", Avg(teamMine, teamMatches.Count)),
            all.Where(r => r.Matches >= minAvg).OrderByDescending(r => (double)r.Chests / r.Matches).Take(5)
                .Select(r => (r.Key, r.Name, (double)r.Chests / r.Matches, $"<small>≥</small>{((double)r.Chests / r.Matches).ToString("0.0", Loc)}", Games(r.Matches))).ToList());
        Board(boards, L.T("Récord en una partida", "Best single match"), null, all.Where(r => r.Best > 0).OrderByDescending(r => r.Best).ThenByDescending(r => r.Matches).Take(5)
            .Select(r => (r.Key, r.Name, (double)r.Best, $"<small>≥</small>{N(r.Best)}", L.T("cofres en una partida", "chests in one match"))).ToList());
        Board(boards, L.T("Más victorias juntos", "Most wins together"), null, all.Where(r => r.Wins > 0).OrderByDescending(r => r.Wins).ThenBy(r => r.Matches).Take(5)
            .Select(r => (r.Key, r.Name, (double)r.Wins, $"1.º {N(r.Wins)}", L.F("{0} de {1}", "{0} of {1}", Pct(r.Wins, r.Matches), Games(r.Matches)))).ToList());
        if (boards.Length > 0)
        {
            sb.Append($"""<section><div class="sh"><h2>{L.T("Top de compañeros", "Top teammates")}</h2><span class="meta">{L.T("cofres vistos: un mínimo", "chests seen: a minimum")}</span></div><div class="boards">{boards}</div></section>""");
        }

        // Por partida: tú, cada compañero y el equipo (las 30 más recientes con compañeros).
        var recentTeam = teamMatches.Take(30).ToList();
        if (recentTeam.Count > 0)
        {
            var teamMax = Math.Max(1, recentTeam.Max(m => m.OwnChests + m.TeammatesSeen(extraKinds)));
            sb.Append($"""
                <section><div class="sh"><h2>{L.T("Por partida", "Per match")}</h2><span class="meta">{L.F("las {0} más recientes", "latest {0}", recentTeam.Count)}</span></div>
                <div class="card tw"><table><thead><tr><th>{L.T("Fecha", "Date")}</th><th>{L.T("Puesto", "Placement")}</th><th class="r">{L.T("Tú", "You")}</th><th>{L.T("Compañeros", "Teammates")}</th><th class="r">{L.T("Equipo", "Team")}</th></tr></thead><tbody>
                """);
            foreach (var m in recentTeam)
            {
                var mates = string.Concat(m.Teammates!.OrderByDescending(t => Seen(t)).Select(t =>
                    $"<span class=\"chip\"><span class=\"av sm-av\" style=\"background:{Tone(t.EpicId ?? t.Name)}\">{Initial(t.Name)}</span><span class=\"cn\">{H(t.Name)}</span> <b>≥ {N(Seen(t))}</b></span>"));
                var total = m.OwnChests + m.TeammatesSeen(extraKinds);
                var placeClass = m.Placement switch { 1 => " win", > 0 and <= 10 => " top", _ => "" };
                var place = m.Placement == 1 ? "1.º " + H(Pretty.Placement(m)) : H(Pretty.Placement(m));
                var w = (100.0 * total / teamMax).ToString("0.##", CultureInfo.InvariantCulture);
                var mineW = (100.0 * m.OwnChests / teamMax).ToString("0.##", CultureInfo.InvariantCulture);
                sb.Append($"<tr><td class=\"nw\">{L.DateTimeShort(When(m))}<div class=\"sm\">{H(Pretty.Playlist(m.Playlist))}</div></td><td><span class=\"badge{placeClass}\">{place}</span></td>"
                    + $"<td class=\"r\"><b class=\"acc\">{N(m.OwnChests)}</b></td><td><div class=\"mates-line\">{mates}</div></td>"
                    + $"<td><div class=\"teambar\"><span class=\"tb\"><i class=\"me\" style=\"width:{mineW}%\"></i><i style=\"width:{w}%\"></i></span><b class=\"cy\"><small>≥</small>{N(total)}</b></div></td></tr>");
            }
            sb.Append("</tbody></table></div></section>");
        }

        // Compañeros de relleno con una sola partida: se resumen en una línea en vez de una fila cada uno.
        var rows = all.Where(r => r.Matches >= 2).ToList();
        var oneOff = all.Count - rows.Count;
        if (rows.Count == 0)
        {
            rows = all.Take(20).ToList();
            oneOff = Math.Max(0, all.Count - 20);
        }

        // Los tres con los que más juegas, en tarjetas.

        sb.Append($"""<section><div class="sh"><h2>{L.T("Con quién más juegas", "Who you play with most")}</h2><span class="meta">{L.Count(all.Count, "compañero", "compañeros", "teammate", "teammates")} · {L.Count(tracked, "partida en equipo", "partidas en equipo", "team match", "team matches")}</span></div><div class="mates">""");
        foreach (var r in rows.Take(3))
        {
            var avg = (double)r.Chests / r.Matches;
            sb.Append($"""
                <div class="mate">
                  <div class="who"><div class="av" style="background:{Tone(r.Key)}">{Initial(r.Name)}</div>
                    <div class="grow"><div class="nm" title="{H(r.Name)}">{H(r.Name)}</div><div class="sm">{L.Count(r.Matches, "partida juntos", "partidas juntos", "match together", "matches together")} · {L.F("aporta ≥ {0} del equipo", "brings ≥ {0} of the team", Pct(r.Chests, r.Team))}</div></div></div>
                  <div class="st">
                    <div><b class="acc">{N(r.Wins)}</b><span>{L.T("victorias", "wins")} · {Pct(r.Wins, r.Matches)}</span></div>
                    <div><b><small>≥</small>{avg.ToString("0.0", Loc)}</b><span>{L.T("cofres por partida", "chests per match")}</span></div>
                    <div><b class="cy">{((double)r.Mine / r.Matches).ToString("0.0", Loc)}</b><span>{L.T("tu media juntos", "your average together")}</span></div>
                  </div>
                </div>
                """);
        }
        sb.Append("</div></section>");

        var maxAvg = Math.Max(0.1, rows.Max(r => (double)r.Chests / r.Matches));
        sb.Append($"""
            <section><div class="sh"><h2>{L.T("Todos tus compañeros", "All your teammates")}</h2><span class="meta">{L.T("2 partidas o más", "2 matches or more")}</span></div>
            <div class="card tw"><table><thead><tr><th>{L.T("Compañero", "Teammate")}</th><th class="r">{L.T("Partidas juntos", "Matches together")}</th><th class="r">{L.T("Victorias", "Wins")}</th><th class="r">{L.T("Cofres vistos", "Chests seen")}</th><th class="r">{L.T("Media", "Average")}</th><th class="r">{L.T("Récord", "Best")}</th><th class="r">{L.T("Aporta", "Share")}</th><th class="r">{L.T("Tu media", "Your average")}</th><th>{L.T("Última partida", "Last match")}</th></tr></thead><tbody>
            """);
        foreach (var r in rows)
        {
            var avg = (double)r.Chests / r.Matches;
            var barW = (100.0 * avg / maxAvg).ToString("0.##", CultureInfo.InvariantCulture);
            var wins = r.Wins > 0 ? $"<span class=\"badge win\">1.º {N(r.Wins)}</span> <span class=\"sm\">{Pct(r.Wins, r.Matches)}</span>" : "<span class=\"muted\">—</span>";
            sb.Append($"<tr><td><div class=\"who\"><span class=\"av sm-av\" style=\"background:{Tone(r.Key)}\">{Initial(r.Name)}</span><span class=\"nm\">{H(r.Name)}</span></div></td><td class=\"r\">{N(r.Matches)}</td><td class=\"r nw\">{wins}</td><td class=\"r nw\"><span class=\"sm\">≥</span> <b>{N(r.Chests)}</b></td>"
                + $"<td><div class=\"chests\"><span class=\"mini\"><i style=\"width:{barW}%\"></i></span><b>{avg.ToString("0.0", Loc)}</b></div></td>"
                + $"<td class=\"r\">{N(r.Best)}</td><td class=\"r nw\"><span class=\"sm\">≥</span> {Pct(r.Chests, r.Team)}</td><td class=\"r\">{((double)r.Mine / r.Matches).ToString("0.0", Loc)}</td><td class=\"nw sm\">{L.DateTimeShort(r.Last)}</td></tr>");
        }
        if (oneOff > 0)
        {
            var more = L.Count(oneOff, "compañero", "compañeros", "teammate", "teammates");
            sb.Append($"<tr><td colspan=\"9\" class=\"sm foot\">{(all.Count(r => r.Matches >= 2) > 0
                ? L.F("Y {0} con una sola partida (relleno de equipo), no mostrados.", "And {0} with a single match (team fill), not shown.", more)
                : L.F("Y {0} más.", "And {0} more.", more))}</td></tr>");
        }
        sb.Append("</tbody></table></div></section>");
        return (sb.ToString(), rows.Count);
    }

    /// <summary>Tope "redondo" para la escala del gráfico (1, 2, 2.5, 5 × 10ⁿ), siempre ≥ el valor.</summary>
    private static int NiceMax(int value)
    {
        if (value <= 4)
        {
            return 4;
        }
        var mag = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (step * mag >= value)
            {
                var nice = (int)Math.Round(step * mag);
                return nice % 2 == 0 ? nice : nice + 1;
            }
        }
        return value;
    }

    private static string LoadingCard(ScanProgress? p)
    {
        if (p == null)
        {
            return "";
        }
        var pct = p.Total > 0 ? (int)Math.Round(100.0 * p.Done / p.Total) : 0;
        var head = L.F("Analizando tus replays… {0} / {1}", "Analyzing your replays… {0} / {1}", p.Done, p.Total);
        var note = L.T("Esta página se actualiza sola: las cifras de abajo aumentan a medida que se leen los replays.",
            "This page updates by itself: the numbers below grow as the replays are read.");
        return $"""<div class="loading"><div class="spin"></div><div class="grow"><b>{head}</b><div class="pbar"><i style="width:{pct}%"></i></div><span class="muted">{note}</span></div></div>""";
    }

    private static string ProfilesLine(IReadOnlyList<Profile> profiles, string player)
    {
        var others = profiles.Where(p => p.Name != player).Select(p => H(p.Name)).ToList();
        if (others.Count == 0)
        {
            return "";
        }
        return $"""<div class="profiles">{L.T("Otros perfiles en este PC: ", "Other profiles on this PC: ")}{string.Join(", ", others)} · {L.T("cambia de perfil desde los ajustes de la herramienta", "switch profile from the tool's settings")}</div>""";
    }

    /// <summary>Rewrites only the OBS overlay page (called on every live update).</summary>
    public static void WriteOverlay(Store store, OverlayView view)
    {
        try
        {
            WriteAtomic(store.OverlayPath, Overlay(view));
        }
        catch
        {
            // the page is a convenience: never stop the tracker for it
        }
    }

    private static readonly string OverlayFont = "@font-face{font-family:'Barlow Condensed';font-style:normal;font-weight:700;src:url(data:font/ttf;base64,"
        + Convert.ToBase64String(OverlayAppearance.Font.Value) + ") format('truetype')}";
    private static string OverlayStyle => OverlayFont + """
        html,body{margin:0;background:transparent;color:#ededed;font:500 16px/1.25 "Segoe UI",system-ui,sans-serif}
        .box{position:relative;display:inline-block;min-width:230px;padding:10px 14px 10px 18px;border-radius:14px;overflow:hidden;
          background:rgba(25,25,27,.94);border:1px solid #303033;box-shadow:none}
        .box:before{display:none;content:"";position:absolute;left:0;top:0;bottom:0;width:4px;background:#ffb84d}
        .box.live:before{background:#3ddc97}
        .head{display:flex;align-items:center;justify-content:space-between;gap:16px;font:700 12px "Segoe UI",sans-serif;letter-spacing:.12em;color:#7a8496}
        .pill{display:inline-flex;align-items:center;gap:6px;padding:3px 10px;border-radius:99px;background:#143a2c;color:#3ddc97;letter-spacing:.06em}
        .pill:before{content:"";width:8px;height:8px;border-radius:50%;background:#3ddc97;animation:none}
        @keyframes pulse{50%{opacity:.35}}
        .main{display:flex;align-items:center;justify-content:space-between;gap:16px;margin:6px 0 8px}
        .big{display:block;font-size:34px;font-weight:800;line-height:1.05;font-variant-numeric:tabular-nums}
        .total .big{color:#e3b461}.total{position:relative;padding-left:56px}.total .chest-art{position:absolute;left:0;top:0;width:48px;height:48px}
        .cap{display:block;font-size:11px;font-weight:700;letter-spacing:.1em;color:#7a8496;text-transform:none}
        .chip{min-width:84px;padding:5px 12px;border-radius:11px;background:#1a202d;text-align:center}
        .chip.live .big,.chip.live .cap{color:#3ddc97}.chip.none .big{color:#505868}
        .stats{display:flex;justify-content:space-between;gap:14px;border-top:1px solid #222938;padding-top:7px}
        .stat .cap{font-size:10px}.stat b{display:block;font-size:16px;font-weight:700}
        .stat.t b{color:#ff80aa}.stat small{display:block;font-size:11px;font-weight:600;color:#7a8496}
        .big,.stat b{font-family:'Barlow Condensed',sans-serif;font-weight:700}
        """;

    /// <summary>The overlay card (shared by overlay.html and the OBS page served by the tool).</summary>
    public static string OverlayBox(OverlayView v)
    {
        var stats = new StringBuilder();
        foreach (var r in v.Rows)
        {
            stats.Append($"<div class=\"stat{(r.Treasure ? " t" : "")}\"><span class=\"cap\">{H(r.Label)}</span><b>{H(r.Value)}</b></div>");
        }
        var badge = v.Badge != null ? $"<span class=\"pill\">{H(v.Badge.TrimStart('●', ' '))}</span>" : "";
        var chip = v.Current == null ? "none" : v.CurrentLive ? "live" : "";
        return $"""
            <div class="box{(v.CurrentLive ? " live" : "")}">
            <div class="head"><span>{L.T("Cofres", "Chests")}</span>{badge}</div>
            <div class="main">
              <div class="total">{Branding.ChestHtml}<span class="big">{H(v.Total)}</span><span class="cap">{H(v.TotalCaption)}</span></div>
              <div class="chip {chip}"><span class="big">{H(v.Current ?? "—")}</span><span class="cap">{H(v.CurrentCaption)}</span></div>
            </div>
            <div class="stats">{stats}</div>
            </div>
            """;
    }

    /// <summary>overlay.html (a file: reloads itself every 10 s).</summary>
    public static string Overlay(OverlayView v) => $"""
        <!doctype html><html lang="{L.Code}"><head><meta charset="utf-8"><meta http-equiv="refresh" content="10">
        <title>{L.T("Overlay de cofres", "Chest overlay")}</title><style>{OverlayStyle}</style></head>
        <body>{OverlayBox(v)}</body></html>
        """;

    /// <summary>OBS page served by the tool: fetches the card every 2 s and swaps it only when it changed (no flicker).</summary>
    public static string OverlayLivePage(string box) => $$"""
        <!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8">
        <title>{{L.T("Overlay de cofres", "Chest overlay")}}</title><style>{{OverlayStyle}}</style></head>
        <body><div id="o">{{box}}</div>
        <script>
        let last = document.getElementById("o").innerHTML;
        setInterval(async () => {
          try {
            const r = await fetch("/view", { cache: "no-store" });
            if (!r.ok) return;
            const html = await r.text();
            if (html !== last) { document.getElementById("o").innerHTML = html; last = html; }
          } catch (e) { /* tool closed: keep the last figures */ }
        }, 2000);
        </script></body></html>
        """;

    public static string Csv(TrackerData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("fecha;modo;jugador;puesto;jugadores;cofres;cajas_municion;tesoros;archivo;partidas_registro",
            "date;mode;player;placement;players;chests;ammo_boxes;treasures;file;reported_matches"));

        foreach (var m in data.Matches.Where(Profiles.IsSupportedVersion).OrderBy(When))
        {
            var t = string.Join(" + ", m.TreasureFinds.Select(f => f.DisplayName));
            sb.AppendLine(string.Join(";",
                When(m).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Cell(Pretty.Playlist(m.Playlist)), Cell(m.PlayerName), m.Placement, m.TotalPlayers,
                m.ChestsOpened, m.AmmoBoxesOpened, Cell(t), Cell(m.ReplayFile), 1));
        }
        sb.Append(HistoryCompatibility.ReportedCsv(data));
        return sb.ToString();
    }

    /// <summary>
    /// Texto seguro para una celda: sin separadores ni saltos de línea, y sin que Excel lo lea como fórmula.
    /// </summary>
    private static string Cell(string? value)
    {
        var s = (value ?? "").Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
        var trimmed = s.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@' or '\t' or '|' ? "'" + s : s;
    }
}
