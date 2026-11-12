using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ChestCounter;

internal static class TreasureChecks
{
    private const uint PRIZE_CHANNEL = 7;
    private const uint RECIPIENT_PAWN = 42;
    private const int RESIZE_MULTIPLIER = 2;
    private const int MIN_GOLD_RED = 180;
    private const int EXTENDED_STYLE = -20;
    private const long NO_ACTIVATE = 0x08000000;
    private const int OFFSCREEN_POSITION = -30_000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    public static void Run(Action<bool, string> check)
    {
        SharedTotals(check);
        PrizeExports(check);
        TokenIdentity(check);
        TokenRecipients(check);
        Report(check);
        Windows(check);
    }

    private static MatchRecord Fixture() => new()
    {
        EpicId = "demo",
        PlayerName = "Example",
        GameVersion = "++Fortnite+Release-43.10",
        Playlist = "Playlist_DefaultSquad",
        MatchStartUtc = DateTime.UtcNow,
        ChestsOpened = 3,
        TreasureFinds = [new TreasureFind { Item = "Own", RawItem = "Own" }],
        TeamTreasureFinds = [new TreasureFind { Item = "Shared", Teammate = "Mate", RawItem = "Shared" }],
        Teammates = [new TeammateRecord { Name = "Mate", ChestsSeen = 4 }],
    };

    private static void SharedTotals(Action<bool, string> check)
    {
        var match = Fixture();
        var before = JsonSerializer.Serialize(match);
        var config = new Config();
        check(Profiles.Adjust(match, config)!.TreasureFinds.Count == 1, "Shared treasures default to off.");
        config.CountTeammateTreasures = true;
        var adjusted = Profiles.Adjust(match, config)!;
        check(adjusted.TreasureFinds.Count == 2 && adjusted.OwnTreasureFinds.Count() == 1, "Own and shared rewards retain their scopes.");
        check(adjusted.TeamTreasureFinds.Count == 1 && adjusted.ChestsOpened == 3, "Shared treasure setting preserves chest totals.");
        var data = new TrackerData { Matches = [match] };
        check(OverlayModel.Build(Profiles.View(data, config), null, false).Treasures == "2", "The normal overlay entry point includes shared treasures.");
        config.CountTeammateChests = true;
        adjusted = Profiles.Adjust(match, config)!;
        check(adjusted.ChestsOpened == 7 && adjusted.TreasureFinds.Count == 2, "Team chest and reward settings work independently.");
        config.CountTeammateTreasures = false;
        check(Profiles.Adjust(match, config)!.TreasureFinds.Count == 1, "Shared reward selection is reversible.");
        check(JsonSerializer.Serialize(match) == before, "Changing reward views preserves the saved ledger.");
        var loaded = JsonSerializer.Deserialize<MatchRecord>(before)!;
        check(loaded.TeamTreasureFinds.Count == 1 && loaded.OwnTreasureFinds.Count() == 1, "Legacy team rewards load with correct scopes.");
    }

    private static void PrizeExports(Action<bool, string> check)
    {
        var log = new TreasurePrizeLog();
        log.Record(PRIZE_CHANNEL, 10, null, RECIPIENT_PAWN, "", null);
        check(!log.Events.Single().HasPrize, "An incomplete export does not invent a reward.");
        log.Record(PRIZE_CHANNEL, 11, TreasureCatalog.PirateShopPrizeId, null, "Chest", true);
        var prize = log.Events.Single();
        check(prize.HasPrize && prize.Pawn == RECIPIENT_PAWN && prize.Time == 10 && prize.Container == "Chest", "Later fields complete the original prize and keep its timestamp.");
        log.Record(PRIZE_CHANNEL, 12, null, 0, "", null);
        check(log.Events.Single() == prize, "Empty partial exports preserve the recipient, prize and success.");
        log.Record(PRIZE_CHANNEL, 13, null, null, "", false);
        check(!log.Events.Single().HasPrize, "A late failure excludes a provisional award.");
        log.ResetChannel(PRIZE_CHANNEL);
        log.Record(PRIZE_CHANNEL, 14, "DelilahPrize:unknown123", RECIPIENT_PAWN, "", true);
        check(log.Events.Count == 2 && log.Events.Last().HasPrize, "Reused channels create a separate reward event.");
        log.Record(99, float.NaN, "invalid", null, "", true);
        check(log.Events.Count == 2, "Malformed timestamps do not enter the event ledger.");
        check(ChestKinds.Classify("B_SpritePosters_Booty_Chest_C") == ChestKinds.PosterChest, "Spirit poster reward chests are counted.");
        check(!ChestKinds.IsChest(ChestKinds.Classify("B_SpritePosters_Poster_C")), "Spirit posters do not inflate chest counts.");
    }

    private static void TokenIdentity(Action<bool, string> check)
    {
        var token = new TreasureFind { PrizeId = TreasureCatalog.PirateShopPrizeId, FromGoldenChest = true };
        check(token.IsPirateToken, "The catalog GUID identifies a confirmed token.");
        check(TreasureStatus.IsTokenId("DelilahPrize:" + TreasureCatalog.PirateShopPrizeId), "Namespaced token GUIDs resolve.");
        check(!TreasureStatus.IsTokenId("DelilahPrize:dpid_item16"), "Unknown numeric prize ids remain unknown.");
        var export = new TreasureFind { RawItem = "WID_PirateShopAccess", Evidence = "handler-export" };
        check(!export.IsPirateToken, "An exported class alone is insufficient for token confirmation.");
        export.Evidence = "recipient-handler";
        check(export.IsPirateToken, "A recipient grant identifies an explicit token item.");
        check(!new TreasureFind { RawItem = "WID_SmugglerExtractionKey", Evidence = "recipient-handler" }.IsPirateToken, "Extraction keys are distinct from shop access.");
        var seen = new List<SeenPrize>();
        TreasureStatus.AddObservedToken(seen, "GA_TreasureHandler_PirateShop", "Opponent", 20);
        TreasureStatus.AddObservedToken(seen, "GA_TreasureHandler_PirateShop", "Opponent", 21);
        check(seen.Count == 1 && seen[0].Evidence == "recipient-handler", "One nearby token receipt is not counted twice.");
        TreasureStatus.AddObservedToken(seen, "GA_TreasureHandler_Unknown", "Opponent", 30);
        check(seen.Count == 1, "Generic handlers do not identify a token.");
    }

    private static void TokenRecipients(Action<bool, string> check)
    {
        var old = Fixture();
        old.SeenPrizes = [new SeenPrize { Player = "Opponent", PrizeId = TreasureCatalog.PirateShopPrizeId }];
        check(TreasureStatus.Current(null, [old]).Text.Contains("Opponent"), "The last-match token identifies an observed opponent.");
        var live = Fixture();
        live.TeamTreasureFinds[0].PrizeId = TreasureCatalog.PirateShopPrizeId;
        live.TeamTreasureFinds[0].FromGoldenChest = true;
        var status = TreasureStatus.Current(live, [old]);
        check(status.HasConfirmedToken && status.Text.Contains("esta partida") && status.Text.Contains("Mate"), "A live squad token takes precedence over the previous match.");
        var adjusted = live.WithExtras([], false, true);
        var sb = new StringBuilder();
        TreasureStatus.AppendReport(sb, [adjusted], true);
        check(sb.ToString().Contains("1 detecciones") && !sb.ToString().Contains("2 detecciones"), "Shared view does not duplicate token detections.");
        var newer = Fixture();
        newer.MatchStartUtc = old.MatchStartUtc!.Value.AddMinutes(1);
        check(!TreasureStatus.Current(null, [old, newer]).HasConfirmedToken, "Old tokens do not persist as last-match alerts.");
        var roundtrip = JsonSerializer.Deserialize<MatchRecord>(JsonSerializer.Serialize(live))!;
        check(TreasureStatus.Current(roundtrip, []).HasConfirmedToken, "Token evidence survives persistence.");
    }

    private static void Report(Action<bool, string> check)
    {
        var match = Fixture();
        match.SeenPrizes = [new SeenPrize { Player = "<script>bad()</script>", PrizeId = TreasureCatalog.PirateShopPrizeId }];
        var sb = new StringBuilder();
        TreasureStatus.AppendReport(sb, [match], false);
        var html = sb.ToString();
        check(html.Contains("&lt;script&gt;bad()&lt;/script&gt;") && !html.Contains("<script>bad()</script>"), "Observed recipient names are escaped.");
        check(html.Contains("Propios") && html.Contains("Equipo") && html.Contains("Otros observados"), "Report separates reward recipients.");
        var config = new Config { Profile = "demo", CountTeammateTreasures = true, ShowTeammates = true };
        var data = new TrackerData { Matches = [match] };
        html = ReportWriter.Report(config, Profiles.View(data, config), Profiles.List(data, config));
        check(html.Contains("premios propios y compartidos") && html.Contains("Mate"), "The integrated report labels the selected reward scope and recipient.");
        check(!html.Contains("1.087 m") && !html.Contains("1,087 m"), "Report coverage does not promise a fixed radius.");
        var output = Environment.GetEnvironmentVariable("CHESTCOUNTER_QA_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "treasure-report.html"), html);
        }
    }

    private static void Windows(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ChestWindowChecks-" + Guid.NewGuid().ToString("N"));
        var store = new Store(folder);
        var config = new Config();
        var data = new TrackerData();
        var tracker = new Tracker(store, config, data);
        var monitor = new ChestCounter.Monitor(store, config, data, tracker, folder);
        using var settings = new SettingsForm(monitor, store);
        ShowForCapture(settings);
        var initial = settings.ClientSize;
        var content = settings.Controls.OfType<Panel>().Single();
        var before = content.Size;
        settings.ClientSize = new Size(initial.Width * RESIZE_MULTIPLIER, initial.Height * RESIZE_MULTIPLIER);
        settings.PerformLayout();
        check(settings.MaximizeBox && settings.FormBorderStyle == FormBorderStyle.Sizable, "Settings support resize and maximize.");
        check(content.Width > before.Width && content.Height > before.Height, "Settings content expands with the window.");
        var buttons = settings.Controls.OfType<PillButton>().Where(c => c.Text == "Listo").ToList();
        check(buttons.Count == 1 && buttons[0].Bottom <= settings.ClientSize.Height, "The close button stays accessible after resizing.");
        settings.ClientSize = initial;
        using var main = new MainForm(monitor, store);
        ShowForCapture(main);
        var status = new TokenStatus("★ TOKEN PIRATA · Detectado en esta partida\nEquipo · Example", true);
        var state = new MonitorState(OverlayModel.Build(data, null, false), [], null, null, null, false, ChestStats.Empty) { Token = status };
        var render = typeof(MainForm).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!;
        render.Invoke(main, [state]);
        var label = Descendants(main).OfType<Label>().Single(c => c.Text == status.Text);
        check(label.ForeColor.R >= MIN_GOLD_RED && label.ForeColor.G > label.ForeColor.B && label.Parent!.Bounds.Height > 0, "Confirmed tokens appear in a dedicated gold status card.");
        var textHeight = TextRenderer.MeasureText(label.Text, label.Font).Height;
        check(label.ClientSize.Height - label.Padding.Vertical >= textHeight, "The token card has room for both alert and recipient lines.");
        check(Descendants(main).OfType<PillButton>().Any(c => c.Text == "Abrir informe"), "The token card preserves the report action.");
        Capture(main, "main-token.png");
        Capture(settings, "settings.png");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(Form form, string name)
    {
        var folder = Environment.GetEnvironmentVariable("CHESTCOUNTER_QA_OUTPUT");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(folder, name));
    }

    private static void ShowForCapture(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(OFFSCREEN_POSITION, OFFSCREEN_POSITION);
        var style = GetWindowLongPtr(form.Handle, EXTENDED_STYLE).ToInt64();
        SetWindowLongPtr(form.Handle, EXTENDED_STYLE, new IntPtr(style | NO_ACTIVATE));
        form.Show();
        form.PerformLayout();
    }
}
