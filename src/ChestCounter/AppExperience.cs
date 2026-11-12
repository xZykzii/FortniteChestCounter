using System.Drawing;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Desktop lifecycle and explicit career estimates, independent of saved replay counts.</summary>
public sealed class AppExperience : IDisposable
{
    private const int MAX_CAREER_MATCHES = 9_999_999;
    private const int MIN_SAMPLE_MATCHES = 10;
    private readonly Form _form;
    private readonly Monitor _monitor;
    private readonly NotifyIcon _tray;
    private readonly EventWaitHandle _activation;
    private readonly RegisteredWaitHandle _wait;
    private bool _isExiting;

    public AppExperience(Form form, Monitor monitor, Store store)
    {
        _form = form;
        _monitor = monitor;
        _tray = new NotifyIcon { Icon = Branding.WindowIcon(), Text = "Contador de cofres", Visible = true };
        var menu = new ContextMenuStrip { BackColor = Theme.Card, ForeColor = Theme.Ink };
        menu.Items.Add("Abrir contador", null, (_, _) => Restore());
        menu.Items.Add("Abrir informe", null, (_, _) => Branding.Open(store.ReportPath));
        menu.Items.Add("Salir", null, (_, _) => { _isExiting = true; form.Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Restore();
        form.Resize += OnResize;
        form.FormClosing += OnClosing;
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationName(store.Folder));
        _wait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => ActivateOnUi(), null, Timeout.Infinite, false);
    }

    private static string ActivationName(string folder) => AppInfo.InstanceMutexFor(folder) + ".Show";

    public static bool ActivateExisting(string folder)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ActivationName(folder));
            return signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (Exception ex) { ErrorLog.Write("No se pudo activar la ventana existente", ex); return false; }
    }

    private void ActivateOnUi()
    {
        try { if (_form.IsHandleCreated && !_form.IsDisposed) _form.BeginInvoke(Restore); }
        catch (InvalidOperationException ex) { ErrorLog.Write("La ventana ya se estaba cerrando", ex); }
    }

    private void Restore()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void OnResize(object? sender, EventArgs e)
    {
        if (_form.WindowState == FormWindowState.Minimized && _monitor.Config.MinimizeToTray) _form.Hide();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing || _isExiting || _monitor.AutoMode || _monitor.RestartRequested || _monitor.RestartAfterUpdate) return;
        var choice = MessageBox.Show(_form,
            "¿Cerrar el contador? Sí: salir. No: seguir contando en la bandeja. Cancelar: volver.",
            "Contador de cofres", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
        e.Cancel = choice != DialogResult.Yes;
        if (choice == DialogResult.No) { _form.Hide(); _form.WindowState = FormWindowState.Minimized; }
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _activation.Dispose();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _form.Resize -= OnResize;
        _form.FormClosing -= OnClosing;
    }

    public static void Help(Form owner)
    {
        MessageBox.Show(owner,
            "El contador lee tus replays al terminar cada partida. El historial permanece aunque Fortnite borre esos archivos.\n\n" +
            "E: alternar total personal/equipo. Ctrl+Mayús+O: overlay. Ctrl+Mayús+L: bloquear overlay.\n\n" +
            "Informe: probabilidades, colección, eventos y avisos de vehículos. Las tasas son estimaciones del catálogo y los horarios previstos pueden cambiar.\n\n" +
            "Desinstalar: desactiva «Abrir con Fortnite», sal desde la bandeja y elimina la carpeta del programa y su acceso directo. Conserva la carpeta de datos para mantener tu historial; elimínala solo si quieres borrarlo.",
            "Ayuda · Contador de cofres", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static (int Matches, int Chests) CareerSample(TrackerData data, Config config)
    {
        var key = Profiles.Current(data, config)?.Key;
        var matches = data.Matches.Where(m => Profiles.Key(m) == key).ToList();
        return (matches.Count, matches.Sum(m => m.ChestsOpened));
    }

    public static int Estimate(int careerMatches, int recordedMatches, int recordedChests, ReportedTotals reported)
    {
        if (careerMatches < 0 || careerMatches > MAX_CAREER_MATCHES || recordedMatches < MIN_SAMPLE_MATCHES || recordedChests < 0)
            throw new ArgumentOutOfRangeException(nameof(careerMatches));
        var missing = Math.Max(0, careerMatches - recordedMatches - reported.Matches);
        var estimate = missing * ((double)recordedChests / recordedMatches);
        return (int)Math.Clamp(Math.Round(estimate), 0, MAX_CAREER_MATCHES);
    }

    public static void EstimateCareer(Form owner, Monitor monitor, Store store)
    {
        var sample = CareerSample(store.LoadData(), monitor.Config);
        if (sample.Matches < MIN_SAMPLE_MATCHES)
        {
            MessageBox.Show(owner, "Necesitas al menos 10 partidas registradas para estimar tu media.", "Estimación", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = CreateEstimateDialog();
        var heading = Branding.Label(dialog, "Partidas de Carrera en BR de esta temporada", 11);
        heading.SetBounds(24, 20, 480, 30);
        var amount = new NumericUpDown { Minimum = sample.Matches + monitor.ReportedTotals.Matches, Maximum = MAX_CAREER_MATCHES,
            Value = Math.Min(MAX_CAREER_MATCHES, sample.Matches + monitor.ReportedTotals.Matches), Location = new Point(24, 62), Width = 448,
            BackColor = Theme.Panel, ForeColor = Theme.Ink, Font = Theme.Text(12) };
        dialog.Controls.Add(amount);
        var preview = Branding.Label(dialog, "", 10, Theme.Muted);
        preview.SetBounds(24, 110, 448, 100);
        void UpdatePreview()
        {
            var total = Estimate((int)amount.Value, sample.Matches, sample.Chests, monitor.ReportedTotals);
            preview.Text = $"Media personal: {(double)sample.Chests / sample.Matches:N1} cofres en {sample.Matches} partidas.\nEstimación adicional: {total:N0} cofres.\nLas partidas y cofres de otro PC ya se descuentan.\nSustituye la estimación anterior; no crea partidas.";
        }
        amount.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        var apply = Branding.Button(dialog, "Aplicar estimación", () =>
        {
            monitor.SetInitialChests(Estimate((int)amount.Value, sample.Matches, sample.Chests, monitor.ReportedTotals));
            dialog.DialogResult = DialogResult.OK;
        });
        apply.SetBounds(24, 222, 230, 40);
        var cancel = Branding.Button(dialog, "Cancelar", () => dialog.DialogResult = DialogResult.Cancel);
        cancel.SetBounds(270, 222, 202, 40);
        dialog.CancelButton = cancel;
        dialog.ShowDialog(owner);
    }

    private static Form CreateEstimateDialog() => new()
    {
        Text = "Estimar cofres anteriores", ClientSize = new Size(500, 286),
        BackColor = Theme.Bg, ForeColor = Theme.Ink, Font = Theme.Text(10), Icon = Branding.WindowIcon(),
        FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
        StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
    };
}
