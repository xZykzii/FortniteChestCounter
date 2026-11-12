using System.Drawing;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Three focused settings pages; every change retains the existing data store.</summary>
public sealed class SettingsForm : Form
{
    private const int ROW_HEIGHT = 52;
    private const int CONTENT_WIDTH = 520;
    private const int MAX_INITIAL = 9_999_999;
    private readonly Monitor _monitor;
    private readonly Store _store;
    private readonly Panel[] _pages = new Panel[3];
    private readonly PillButton[] _tabs = new PillButton[3];
    private readonly Panel _content = new();

    public SettingsForm(Monitor monitor, Store store)
    {
        _monitor = monitor;
        _store = store;
        Text = "Ajustes · Contador de cofres";
        Icon = Branding.WindowIcon();
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        Font = Theme.Text(10);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(808, 680);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        var title = Branding.Label(this, "Ajustes", 22);
        title.SetBounds(24, 20, 200, 40);
        var version = Branding.Label(this, "Versión " + AppInfo.VersionText + " · edición personal", 10, Theme.Muted);
        version.SetBounds(24, 64, 600, 24);
        BuildNavigation();
        BuildGeneral(_pages[0]);
        BuildOverlay(_pages[1]);
        BuildHistory(_pages[2]);
        SelectPage(0);
        var close = Branding.Button(this, "Listo", Close, true);
        close.SetBounds(652, 616, 132, 40);
        CancelButton = close;
        var twitch = Branding.Twitch(this);
        twitch.SetBounds(24, 616, 216, 40);
    }

    private void BuildNavigation()
    {
        _content.SetBounds(236, 108, 548, 488);
        Controls.Add(_content);
        var names = new[] { "General", "Overlay", "Historial y archivos" };
        for (var i = 0; i < _pages.Length; i++)
        {
            var index = i;
            var tab = Branding.Button(this, names[i], () => SelectPage(index));
            tab.SetBounds(24, 108 + i * 48, 188, 40);
            tab.TextAlign = ContentAlignment.MiddleLeft;
            tab.Padding = new Padding(12, 0, 0, 0);
            _tabs[i] = tab;
            _pages[i] = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, Padding = Padding.Empty };
            _content.Controls.Add(_pages[i]);
        }
    }

    private void SelectPage(int index)
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            var isSelected = i == index;
            _pages[i].Visible = isSelected;
            _tabs[i].BackColor = isSelected ? Theme.PanelHover : Theme.Bg;
            _tabs[i].ForeColor = isSelected ? Theme.Gold : Theme.Muted;
            _tabs[i].Invalidate();
        }
        _pages[index].BringToFront();
    }

    private void BuildGeneral(Panel page)
    {
        var y = Heading(page, "Inicio", "Cuándo abrir el contador", 0);
        y = Toggle(page, "Abrir con Fortnite", "Iniciar al detectar el juego", y, _monitor.Config.StartWithFortnite,
            value => { if (!_monitor.SetStartWithFortnite(value)) throw new IOException("No se pudo guardar el inicio."); });
        y = Toggle(page, "Abrir el informe al iniciar", "Usa tu navegador predeterminado", y, _monitor.Config.OpenReportOnStart,
            value => _monitor.Change(c => c.OpenReportOnStart = value));
        y = Heading(page, "Equipo y conteo", "Los compañeros son un mínimo observado", y + 20);
        y = Toggle(page, "Mostrar compañeros", "Incluye el desglose y el ranking", y, _monitor.Config.ShowTeammates, _monitor.SetShowTeammates);
        y = Toggle(page, "Sumar cofres del equipo", "Puedes alternarlo con E en la ventana", y, _monitor.Config.CountTeammateChests, _monitor.SetCountTeammates);
        y = Toggle(page, "Cajas de munición", "Añadirlas al conteo mostrado", y, _monitor.Config.CountAmmoBoxes,
            value => _monitor.SetCountExtra(c => c.CountAmmoBoxes = value));
        y = Toggle(page, "Cajas de comida", "Añadirlas al conteo mostrado", y, _monitor.Config.CountFoodBoxes,
            value => _monitor.SetCountExtra(c => c.CountFoodBoxes = value));
        y = Toggle(page, "Cañones de botín", "Añadirlos al conteo mostrado", y, _monitor.Config.CountLootCannons,
            value => _monitor.SetCountExtra(c => c.CountLootCannons = value));
        y = Heading(page, "Idioma del informe", "Las preferencias anteriores se conservan", y + 20);
        var language = Branding.Select(page, new Point(0, y), 300);
        foreach (var item in L.Languages) language.Items.Add(new Choice(item.Code, item.Name));
        language.SelectedIndex = L.Languages.ToList().FindIndex(item => item.Code == L.Code);
        var apply = Branding.Button(page, "Aplicar", () =>
        {
            if (language.SelectedItem is not Choice selected) return;
            _monitor.Change(c => c.Language = selected.Key);
            _monitor.Restart();
            Close();
        });
        apply.SetBounds(316, y - 4, 120, 36);
        page.AutoScrollMinSize = new Size(0, y + 56);
    }

    private void BuildOverlay(Panel page)
    {
        var y = Heading(page, "Sobre el juego", "Usa pantalla completa en ventana en Fortnite", 0);
        y = Toggle(page, "Mostrar overlay", "Ctrl+Mayús+O para mostrar u ocultar", y, _monitor.OverlayVisible,
            value => { if (value != _monitor.OverlayVisible) _monitor.ToggleOverlay(); });
        y = Toggle(page, "Bloquear posición", "Ctrl+Mayús+L; bloqueado deja pasar el ratón", y, _monitor.Config.OverlayLocked,
            value => { if (value != NativeOverlay.IsLocked) NativeOverlay.ToggleLock(); });
        y = Heading(page, "Aspecto", "El mismo cofre que enviaste, sin fondo", y + 20);
        var style = Selector(page, "Estilo", y, new[] { new Choice("hud", "HUD transparente"), new Choice("classic", "Clásico · Barlow") }, _monitor.Config.OverlayStyle);
        style.SelectedIndexChanged += (_, _) => { if (style.SelectedItem is Choice chosen) _monitor.SetOverlayStyle(chosen.Key); };
        y += ROW_HEIGHT;
        var size = Selector(page, "Tamaño", y, OverlayAppearance.Sizes.Select(n => new Choice(n.ToString(), n + " %")), _monitor.Config.OverlaySize.ToString());
        size.SelectedIndexChanged += (_, _) => ApplyAppearance(size, true);
        y += ROW_HEIGHT;
        var opacity = Selector(page, "Opacidad", y, OverlayAppearance.Opacities.Select(n => new Choice(n.ToString(), n + " %")), _monitor.Config.OverlayOpacity.ToString());
        opacity.SelectedIndexChanged += (_, _) => ApplyAppearance(opacity, false);
        y = Heading(page, "OBS", "Fuente de navegador con fondo transparente", y + ROW_HEIGHT + 20);
        var url = "http://localhost:" + (_monitor.Config.ObsPort > 0 ? _monitor.Config.ObsPort : ObsServer.DefaultPort) + "/";
        TextAt(page, url, 11, 0, y, 360, 32);
        var copy = Branding.Button(page, "Copiar", () =>
        {
            try { Clipboard.SetText(url); }
            catch (Exception ex) { ErrorLog.Write("No se pudo copiar el enlace OBS", ex); }
        });
        copy.SetBounds(388, y, 120, 36);
        page.AutoScrollMinSize = new Size(0, y + 60);
    }

    private void ApplyAppearance(ComboBox select, bool isSize)
    {
        if (select.SelectedItem is not Choice choice || !int.TryParse(choice.Key, out var amount)) return;
        _monitor.Change(c => { if (isSize) c.OverlaySize = amount; else c.OverlayOpacity = amount; });
        NativeOverlay.SetAppearance(_monitor.Config.OverlaySize, _monitor.Config.OverlayOpacity);
    }

    private void BuildHistory(Panel page)
    {
        var reported = _monitor.ReportedTotals;
        var note = reported.Matches > 0 ? $"Otro PC conservado: {reported.Chests} cofres en {reported.Matches} partidas" : "Cada partida se guarda aunque se borre su replay";
        var y = Heading(page, "Tu historial", note, 0);
        TextAt(page, "Cofres anteriores adicionales", 10, 0, y, 320, 24);
        var initial = new NumericUpDown { Minimum = 0, Maximum = MAX_INITIAL, Value = Math.Clamp(_monitor.InitialChests, 0, MAX_INITIAL),
            Location = new Point(320, y), Width = 188, BackColor = Theme.Panel, ForeColor = Theme.Ink, Font = Theme.Text(11) };
        page.Controls.Add(initial);
        initial.ValueChanged += (_, _) => _monitor.SetInitialChests((int)initial.Value);
        y += ROW_HEIGHT;
        TextAt(page, "Mostrar partidas desde", 10, 0, y, 280, 28);
        var from = new DateTimePicker { ShowCheckBox = true, Format = DateTimePickerFormat.Short,
            Location = new Point(280, y), Width = 228, Checked = _monitor.Config.CountFrom.HasValue,
            Value = _monitor.Config.CountFrom ?? DateTime.Today, Font = Theme.Text(10) };
        page.Controls.Add(from);
        from.ValueChanged += (_, _) => _monitor.SetCountFrom(from.Checked ? from.Value.Date : null);
        y = Heading(page, "Archivos", "Tus datos se conservan en la misma carpeta", y + ROW_HEIGHT + 20);
        var folder = TextAt(page, _monitor.Config.DemosFolder, 9, 0, y, 360, 52, Theme.Muted);
        var browse = Branding.Button(page, "Cambiar", () => ChooseFolder(folder));
        browse.SetBounds(388, y + 4, 120, 36);
        y += 64;
        var open = Branding.Button(page, "Abrir carpeta de datos", () => Branding.Open(_store.Folder));
        open.SetBounds(0, y, 244, 40);
        y = Heading(page, "Actualizaciones", "Edición personal: actualización manual para conservar tus cambios", y + 64);
        TextAt(page, "Antes de cada actualización se guarda un respaldo", 10, 0, y, CONTENT_WIDTH, 40, Theme.Muted);
        y += 64;
        var reset = Branding.Button(page, "Restablecer estadísticas…", Reset);
        reset.ForeColor = Theme.Coral;
        reset.SetBounds(0, y, 256, 40);
        page.AutoScrollMinSize = new Size(0, y + 64);
    }

    private void ChooseFolder(Label label)
    {
        using var picker = new FolderBrowserDialog { Description = "Carpeta de repeticiones de Fortnite", UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_monitor.Config.DemosFolder) ? _monitor.Config.DemosFolder : "" };
        if (picker.ShowDialog(this) != DialogResult.OK || !Directory.Exists(picker.SelectedPath)) return;
        _monitor.ChangeDemosFolder(picker.SelectedPath);
        label.Text = picker.SelectedPath;
    }

    private void Reset()
    {
        if (MessageBox.Show(this, "Se guardará una copia antes de empezar un historial nuevo. ¿Quieres restablecer las estadísticas?",
                "Restablecer historial", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _monitor.ResetStats();
        Close();
    }

    private int Toggle(Panel page, string title, string detail, int y, bool isChecked, Action<bool> save)
    {
        TextAt(page, title, 11, 0, y, 420, 24);
        TextAt(page, detail, 9, 0, y + 24, 440, 20, Theme.Muted);
        var toggle = new ToggleSwitch { Location = new Point(468, y + 8), Checked = isChecked };
        page.Controls.Add(toggle);
        var isReverting = false;
        toggle.CheckedChanged += (_, _) =>
        {
            if (isReverting) return;
            try { save(toggle.Checked); }
            catch (Exception ex)
            {
                ErrorLog.Write("No se pudo guardar un ajuste", ex);
                isReverting = true;
                toggle.Checked = !toggle.Checked;
                isReverting = false;
                MessageBox.Show(this, "No se pudo guardar este ajuste. Inténtalo de nuevo.", "Ajustes", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        return y + ROW_HEIGHT;
    }

    private static ComboBox Selector(Panel page, string title, int y, IEnumerable<Choice> choices, string selected)
    {
        TextAt(page, title, 11, 0, y, 220, 32);
        var select = Branding.Select(page, new Point(228, y), 280);
        foreach (var choice in choices) select.Items.Add(choice);
        for (var i = 0; i < select.Items.Count; i++) if (((Choice)select.Items[i]).Key == selected) select.SelectedIndex = i;
        return select;
    }

    private static int Heading(Panel page, string title, string description, int y)
    {
        TextAt(page, title, 16, 0, y, CONTENT_WIDTH, 32);
        TextAt(page, description, 9, 0, y + 32, CONTENT_WIDTH, 40, Theme.Muted);
        return y + 80;
    }

    private static Label TextAt(Control parent, string text, float size, int x, int y, int width, int height, Color? color = null)
    {
        var label = Branding.Label(parent, text, size, color);
        label.SetBounds(x, y, width, height);
        return label;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(Handle); }
    private sealed record Choice(string Key, string Title) { public override string ToString() => Title; }
}
