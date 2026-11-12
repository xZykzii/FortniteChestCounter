using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>Profile button and accessible menu, without the native blue edit selection.</summary>
public sealed class ProfileSelector : Control
{
    private const int CONTROL_HEIGHT = 48;
    private const int MENU_ROW_HEIGHT = 52;
    private const int MENU_MIN_WIDTH = 280;
    private const int MENU_MAX_HEIGHT = 360;
    private readonly ContextMenuStrip _menu = new();
    private readonly List<Profile> _profiles = new();
    private string? _selectedKey;
    private bool _isHovered;
    private bool _isMenuOpen;
    public event Action<string>? ProfileSelected;

    public ProfileSelector()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(336, CONTROL_HEIGHT);
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = "Perfil activo";
        AccessibleRole = AccessibleRole.ButtonDropDown;
        _menu.AutoSize = false;
        _menu.ShowImageMargin = false;
        _menu.BackColor = Theme.Card;
        _menu.ForeColor = Theme.Ink;
        _menu.Renderer = new ProfileMenuRenderer();
        _menu.Closed += (_, _) => { _isMenuOpen = false; Invalidate(); };
    }

    public void SetProfiles(IEnumerable<Profile> profiles, string? selectedKey)
    {
        var items = profiles.ToList();
        if (_selectedKey == selectedKey && _profiles.SequenceEqual(items)) return;
        _profiles.Clear();
        _profiles.AddRange(items);
        _selectedKey = selectedKey;
        var current = _profiles.FirstOrDefault(p => p.Key == selectedKey);
        Text = current?.Name ?? "Sin partidas todavía";
        AccessibleDescription = current != null ? $"{current.Name}, {current.Matches} partidas. Pulsa Enter para cambiar de perfil." : Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Shape.Rounded(bounds, 8);
        using var surface = new SolidBrush(_isHovered || _isMenuOpen ? Theme.PanelHover : Theme.Panel);
        using var border = new Pen(Focused || _isMenuOpen ? Theme.Gold : Theme.CardEdge);
        g.FillPath(surface, path);
        g.DrawPath(border, path);
        DrawProfile(g);
        var x = Width - 24;
        var y = Height / 2;
        using var pen = new Pen(Theme.Muted, 1.5f);
        g.DrawLines(pen, _isMenuOpen
            ? new Point[] {new Point(x - 4, y + 2), new Point(x, y - 2), new Point(x + 4, y + 2)}
            : new Point[] {new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2)});
    }

    private void DrawProfile(Graphics g)
    {
        var current = _profiles.FirstOrDefault(p => p.Key == _selectedKey);
        using var small = Theme.Text(8);
        using var name = Theme.Text(10.5f);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, "Perfil activo", small, new Rectangle(16, 6, Width - 56, 16), Theme.Muted, flags);
        TextRenderer.DrawText(g, current?.Name ?? "Sin partidas todavía", name,
            new Rectangle(16, 22, Width - 156, 22), Theme.Ink, flags);
        var matches = current != null ? L.N(current.Matches) + " partidas" : "";
        TextRenderer.DrawText(g, matches, small, new Rectangle(Width - 142, 24, 100, 18), Theme.Muted,
            flags | TextFormatFlags.Right);
    }

    private void ShowProfiles()
    {
        if (_profiles.Count == 0 || _isMenuOpen) return;
        foreach (var item in _menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _menu.Items.Clear();
        var width = Math.Max(MENU_MIN_WIDTH, Width);
        foreach (var profile in _profiles)
        {
            var choice = profile;
            var item = new ToolStripMenuItem(profile.Name)
            {
                Tag = profile, Checked = profile.Key == _selectedKey,
                AutoSize = false, Size = new Size(width - 12, MENU_ROW_HEIGHT),
                Font = Theme.Text(10), Padding = new Padding(12, 4, 12, 4),
                AccessibleName = $"{profile.Name}, {profile.Matches} partidas",
            };
            item.Click += (_, _) => SelectProfile(choice.Key);
            _menu.Items.Add(item);
        }
        _menu.Size = new Size(width, Math.Min(MENU_MAX_HEIGHT, _profiles.Count * MENU_ROW_HEIGHT + 8));
        _isMenuOpen = true;
        _menu.Show(this, new Point(0, Height + 4));
        Invalidate();
    }

    private void SelectProfile(string key)
    {
        if (_profiles.All(p => p.Key != key)) return;
        _selectedKey = key;
        ProfileSelected?.Invoke(key);
        Invalidate();
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); ShowProfiles(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _isHovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _isHovered = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down)
        {
            ShowProfiles();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Down or Keys.Up || base.IsInputKey(keyData);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ProfileMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Card);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Theme.CardEdge);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var isCurrent = e.Item is ToolStripMenuItem { Checked: true };
            using var brush = new SolidBrush(e.Item.Selected ? Theme.PanelHover : Theme.Card);
            e.Graphics.FillRectangle(brush, new Rectangle(4, 0, e.Item.Width - 4, e.Item.Height));
            if (!isCurrent) return;
            using var accent = new SolidBrush(Theme.Gold);
            e.Graphics.FillRectangle(accent, 4, 12, 2, e.Item.Height - 24);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Tag is not Profile profile) { base.OnRenderItemText(e); return; }
            using var title = Theme.Text(10);
            using var meta = Theme.Text(8.5f);
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, profile.Name, title, new Rectangle(18, 5, e.Item.Width - 34, 22), Theme.Ink, flags);
            TextRenderer.DrawText(e.Graphics, $"{L.N(profile.Matches)} partidas · {L.N(profile.Chests)} cofres", meta,
                new Rectangle(18, 28, e.Item.Width - 34, 18), Theme.Muted, flags);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
    }
}

