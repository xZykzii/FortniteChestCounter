using System.Drawing;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Inventory ledger: count, session, daily activity and the observed squad.</summary>
public sealed class MainForm : Form
{
    private const int TEAM_HEIGHT = 116;
    private const int TEAM_COLUMNS = 4;
    private readonly Monitor _monitor;
    private readonly Store _store;
    private readonly TableLayoutPanel _page = new();
    private readonly Label _total, _caption, _external, _current, _currentCaption, _session;
    private readonly Label _matches, _average, _best, _treasures, _chartNote, _result, _today, _notice;
    private readonly Label _teamTitle, _teamTotal;
    private readonly Label[] _names = new Label[TEAM_COLUMNS], _counts = new Label[TEAM_COLUMNS];
    private readonly ComboBox _profiles;
    private readonly PillButton _mode, _overlay;
    private readonly DailyChart _chart;
    private readonly Card _team;
    private bool _isUpdatingProfile;
    private string? _profileSignature;

    public MainForm(Monitor monitor, Store store)
    {
        _monitor = monitor;
        _store = store;
        Text = "Contador de cofres · " + AppInfo.VersionText;
        Icon = Branding.WindowIcon();
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        Font = Theme.Text(10);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(896, 724);
        MinimumSize = new Size(912, 764);
        StartPosition = FormStartPosition.CenterScreen;
        BuildPage();
        _profiles = BuildHeader();
        (_total, _caption, _external, _current, _currentCaption, _session, _mode) = BuildHero();
        (_matches, _average, _best, _treasures) = BuildMetrics();
        (_chart, _chartNote, _result, _today, _notice) = BuildActivity();
        (_team, _teamTitle, _teamTotal) = BuildTeam();
        _overlay = BuildFooter();
        HistoryCompatibility.ConfigureWindow(this, monitor, _total, _caption, _overlay);
        monitor.Changed += OnChanged;
        Shown += (_, _) => { monitor.RequestPublish(); if (monitor.AutoMode) WindowState = FormWindowState.Minimized; };
        FormClosed += (_, _) => monitor.Changed -= OnChanged;
    }

    private void BuildPage()
    {
        _page.Dock = DockStyle.Fill;
        _page.Padding = new Padding(24);
        _page.ColumnCount = 1;
        _page.RowCount = 6;
        foreach (var height in new[] { 60, 176, 92 }) _page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _page.RowStyles.Add(new RowStyle(SizeType.Absolute, TEAM_HEIGHT));
        _page.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        Controls.Add(_page);
    }

    private ComboBox BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _page.Controls.Add(header, 0, 0);
        var title = Branding.Label(header, "Contador de cofres", 18);
        title.SetBounds(0, 0, 360, 32);
        var subtitle = Branding.Label(header, "Tu registro de partidas en Fortnite", 10, Theme.Muted);
        subtitle.SetBounds(0, 32, 360, 20);
        var profiles = Branding.Select(header, new Point(512, 10), 336);
        header.Resize += (_, _) => profiles.Left = header.Width - profiles.Width;
        profiles.SelectedIndexChanged += (_, _) =>
        {
            if (!_isUpdatingProfile && profiles.SelectedItem is ProfileChoice item) _monitor.SetProfile(item.Key);
        };
        return profiles;
    }

    private (Label, Label, Label, Label, Label, Label, PillButton) BuildHero()
    {
        var split = Split(68, 32);
        _page.Controls.Add(split, 0, 1);
        var hero = Branding.Section(split);
        split.SetCellPosition(hero, new TableLayoutPanelCellPosition(0, 0));
        hero.Margin = new Padding(0, 0, 16, 16);
        var caption = TextAt(hero, "Cofres personales", 11, 24, 16, 320, 24, Theme.Muted);
        var total = TextAt(hero, "0", 44, 20, 40, 320, 76, Theme.Gold);
        var external = TextAt(hero, "", 9, 24, 120, 360, 24, Theme.Muted);
        var art = new PictureBox { Image = Branding.Chest, SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(112, 112), Location = new Point(416, 24), BackColor = Color.Transparent };
        hero.Controls.Add(art);
        hero.Resize += (_, _) => art.Left = hero.Width - art.Width - 20;
        var currentCard = Branding.Section(split);
        split.SetCellPosition(currentCard, new TableLayoutPanelCellPosition(1, 0));
        var currentCaption = TextAt(currentCard, "Última partida", 11, 20, 16, 200, 24, Theme.Muted);
        var current = TextAt(currentCard, "—", 32, 16, 44, 152, 56);
        var session = TextAt(currentCard, "Sesión · 0", 10, 20, 116, 140, 28, Theme.Muted);
        var mode = Branding.Button(currentCard, "Personal", () => _monitor.SetCountTeammates(!_monitor.Config.CountTeammateChests));
        mode.SetBounds(160, 112, 92, 32);
        currentCard.Resize += (_, _) => mode.Left = currentCard.Width - mode.Width - 20;
        return (total, caption, external, current, currentCaption, session, mode);
    }

    private (Label, Label, Label, Label) BuildMetrics()
    {
        var row = Split(25, 25, 25, 25);
        row.Margin = new Padding(0, 0, 0, 16);
        _page.Controls.Add(row, 0, 2);
        Label Metric(string title, int column)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            row.Controls.Add(panel, column, 0);
            TextAt(panel, title, 10, 4, 0, 180, 24, Theme.Muted);
            return TextAt(panel, "—", 24, 0, 24, 180, 44);
        }
        return (Metric("Partidas registradas", 0), Metric("Media por partida", 1), Metric("Mejor partida", 2), Metric("Tesoros", 3));
    }

    private (DailyChart, Label, Label, Label, Label) BuildActivity()
    {
        var split = Split(68, 32);
        _page.Controls.Add(split, 0, 3);
        var card = Branding.Section(split);
        split.SetCellPosition(card, new TableLayoutPanelCellPosition(0, 0));
        card.Margin = new Padding(0, 0, 16, 16);
        TextAt(card, "Últimos 7 días", 11, 20, 12, 240, 28);
        var note = TextAt(card, "", 9, 300, 12, 232, 28, Theme.Muted);
        note.TextAlign = ContentAlignment.MiddleRight;
        var chart = new DailyChart { Location = new Point(20, 48), Font = Theme.Text(9) };
        card.Controls.Add(chart);
        card.Resize += (_, _) => { chart.Size = new Size(Math.Max(80, card.Width - 40), Math.Max(64, card.Height - 64)); note.Left = card.Width - note.Width - 20; };
        chart.Size = new Size(Math.Max(80, card.Width - 40), Math.Max(64, card.Height - 64));
        var resultCard = Branding.Section(split);
        split.SetCellPosition(resultCard, new TableLayoutPanelCellPosition(1, 0));
        var today = TextAt(resultCard, "Hoy · —", 16, 20, 16, 220, 32);
        var result = TextAt(resultCard, "", 9, 20, 52, 220, 60, Theme.Muted);
        var notice = TextAt(resultCard, "", 8, 20, 124, 220, 20, Theme.Muted);
        notice.Cursor = Cursors.Hand;
        notice.Click += (_, _) => _monitor.KeepOpen();
        resultCard.Resize += (_, _) => notice.Top = resultCard.Height - notice.Height - 16;
        return (chart, note, result, today, notice);
    }

    private (Card, Label, Label) BuildTeam()
    {
        var card = Branding.Section(_page, new Padding(16));
        _page.SetCellPosition(card, new TableLayoutPanelCellPosition(0, 4));
        var title = TextAt(card, "Equipo observado", 11, 20, 12, 400, 24);
        var total = TextAt(card, "", 10, 576, 12, 248, 24, Theme.Muted);
        total.TextAlign = ContentAlignment.MiddleRight;
        card.Resize += (_, _) => total.Left = card.Width - total.Width - 20;
        var row = Split(25, 25, 25, 25);
        row.Dock = DockStyle.Bottom;
        row.Height = 44;
        card.Controls.Add(row);
        for (var i = 0; i < TEAM_COLUMNS; i++)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            row.Controls.Add(panel, i, 0);
            _names[i] = TextAt(panel, "", 9, 0, 0, 168, 18, Theme.Muted);
            _counts[i] = TextAt(panel, "", 16, 0, 18, 168, 26);
        }
        return (card, title, total);
    }

    private PillButton BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _page.Controls.Add(footer, 0, 5);
        var report = Branding.Button(footer, "Abrir informe", () => Branding.Open(_store.ReportPath), true);
        report.SetBounds(0, 8, 152, 40);
        var overlay = Branding.Button(footer, "Overlay", _monitor.ToggleOverlay);
        overlay.SetBounds(160, 8, 120, 40);
        var settings = Branding.Button(footer, "Ajustes", () => { using var dialog = new SettingsForm(_monitor, _store); dialog.ShowDialog(this); });
        settings.SetBounds(288, 8, 112, 40);
        var twitch = Branding.Twitch(footer);
        twitch.SetBounds(648, 8, 200, 40);
        footer.Resize += (_, _) => twitch.Left = footer.Width - twitch.Width;
        return overlay;
    }

    private static Label TextAt(Control parent, string text, float size, int x, int y, int width, int height, Color? color = null)
    {
        var label = Branding.Label(parent, text, size, color);
        label.SetBounds(x, y, width, height);
        return label;
    }

    private static TableLayoutPanel Split(params float[] widths)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, RowCount = 1, ColumnCount = widths.Length };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var width in widths) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        return row;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(Handle); }

    private void OnChanged(MonitorState state)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => Render(state))); }
        catch (InvalidOperationException ex) { ErrorLog.Write("Ventana cerrándose", ex); }
    }

    private void Render(MonitorState state)
    {
        if (IsDisposed) return;
        var isTeam = _monitor.Config.CountTeammateChests;
        _total.Text = state.View.Total;
        _caption.Text = isTeam ? "Personal + equipo observado" : "Cofres personales";
        var reported = _monitor.ReportedTotals;
        _external.Text = reported.Matches > 0 ? $"Incluye {L.N(reported.Chests)} cofres de tu otro PC" : "Historial guardado en este PC";
        _current.Text = state.View.Current ?? "—";
        _currentCaption.Text = state.View.CurrentLive ? "Partida en curso" : "Última partida";
        _session.Text = "Sesión · " + state.View.Session;
        _mode.Text = isTeam ? "Equipo" : "Personal";
        _matches.Text = L.N(state.Stats.HistoryMatches);
        _average.Text = state.Stats.Matches > 0 ? state.Stats.Average.ToString("0.0", L.Culture) : "—";
        _best.Text = L.N(state.Stats.BestMatch);
        _treasures.Text = L.N(state.Stats.Treasures);
        _today.Text = "Hoy · " + L.N(state.Stats.Today);
        _chartNote.Text = state.Stats.BestDay is { } day ? $"Mejor día · {day:dd/MM}" : "";
        _chart.SetDays(state.Stats.LastDays);
        _result.Text = state.Stats.LastMatch is { } last ? Result(last, isTeam) : "Todavía no hay partidas";
        _notice.Text = state.AutoCloseOffer ? "Mantener el contador abierto" : state.Notice ?? "Repeticiones guardadas localmente";
        _overlay.Text = _monitor.OverlayVisible ? "Overlay activo" : "Overlay";
        RenderProfiles(state);
        RenderTeam(state.Team);
    }

    private static string Result(LastMatchSummary match, bool isTeam)
    {
        var parts = new List<string>();
        if (match.Placement is { } place) parts.Add(place == 1 ? "Victoria real" : $"Puesto {place}");
        if (match.Eliminations is { } eliminations) parts.Add($"{eliminations} eliminaciones");
        if (match.Destroyed > 0) parts.Add($"{match.Destroyed} cofres destruidos");
        return string.Join("\n", parts);
    }

    private void RenderProfiles(MonitorState state)
    {
        var signature = string.Join("|", state.Profiles.Select(p => $"{p.Key}:{p.Matches}:{p.Chests}"));
        _isUpdatingProfile = true;
        if (_profileSignature != signature)
        {
            _profiles.Items.Clear();
            foreach (var profile in state.Profiles) _profiles.Items.Add(new ProfileChoice(profile.Key, $"{profile.Name} · {profile.Matches} partidas"));
            _profileSignature = signature;
        }
        for (var i = 0; i < _profiles.Items.Count; i++)
            if (_profiles.Items[i] is ProfileChoice item && item.Key == state.CurrentKey) _profiles.SelectedIndex = i;
        _isUpdatingProfile = false;
    }

    private void RenderTeam(TeamSummary? team)
    {
        _team.Visible = team != null;
        _page.RowStyles[4].Height = team != null ? TEAM_HEIGHT : 0;
        if (team == null) return;
        _teamTitle.Text = team.Live ? "Equipo · partida en curso" : "Equipo · última partida";
        _teamTotal.Text = team.Unknown ? "Sin información del equipo" : "Mínimo observado · " + L.N(team.Total);
        _names[0].Text = "Tú";
        _counts[0].Text = L.N(team.Mine);
        for (var i = 1; i < TEAM_COLUMNS; i++)
        {
            var mate = i - 1 < team.Mates.Count ? team.Mates[i - 1] : null;
            _names[i].Text = mate?.Name ?? "";
            _counts[i].Text = mate != null ? "≥ " + L.N(mate.Chests) : "";
        }
    }

    private sealed record ProfileChoice(string Key, string Label) { public override string ToString() => Label; }
}
