using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// One row of the overlay list: a live thumbnail of the badge, its name and a
/// summary of how it looks, plus the actions for it. The thumbnail is painted
/// through the same <see cref="OverlayRenderer"/> the real badge uses, so the
/// list shows the actual thing rather than a generic icon.
/// </summary>
internal sealed class OverlayCard : Panel
{
    private static readonly Font NameFont = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Font SummaryFont = new("Segoe UI", 8.75f);

    private static readonly Color CardBorder = Color.FromArgb(222, 222, 222);
    private static readonly Color SummaryColor = Color.FromArgb(110, 110, 110);
    private static readonly Color ThumbBack = Color.FromArgb(248, 248, 248);

    public const int CardHeight = 88;
    private const int ThumbSize = 54;

    private readonly OverlayItem _item;
    private readonly Bitmap _defaultIcon;
    private readonly Bitmap _thumbIcon;

    private readonly Label _name = new();
    private readonly Label _summary = new();
    private readonly CheckBox _enabled = new();
    private readonly ContextMenuStrip _menu;

    public event Action<bool>? EnabledToggled;
    public event Action? EditRequested;
    public event Action? DuplicateRequested;
    public event Action? RemoveRequested;

    public OverlayCard(OverlayItem item, Bitmap defaultIcon, bool canRemove)
    {
        _item = item;
        _defaultIcon = defaultIcon;
        _thumbIcon = OverlayIcon.Load(item.Normal, defaultIcon);

        BackColor = Color.White;
        Padding = new Padding(1);
        DoubleBuffered = true;

        _name.Text = item.Name;
        _name.Font = NameFont;
        _name.AutoSize = false;
        _name.AutoEllipsis = true;
        _name.TextAlign = ContentAlignment.BottomLeft;

        _summary.Text = Describe(item);
        _summary.Font = SummaryFont;
        _summary.ForeColor = SummaryColor;
        _summary.AutoSize = false;
        _summary.AutoEllipsis = true;
        _summary.TextAlign = ContentAlignment.TopLeft;

        _enabled.Text = "Enabled";
        _enabled.AutoSize = false;
        _enabled.Checked = item.Enabled;
        _enabled.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _enabled.CheckedChanged += (_, _) => EnabledToggled?.Invoke(_enabled.Checked);

        Button edit = MakeButton("Edit", () => EditRequested?.Invoke());

        // Duplicate and Remove live behind an overflow menu: spelled out as
        // buttons they crowd the name and summary off the card, and this leaves
        // somewhere for later actions to go.
        var more = MakeButton("⋯", null);
        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem("Duplicate", null, (_, _) => DuplicateRequested?.Invoke()));
        var removeItem = new ToolStripMenuItem("Remove", null, (_, _) => RemoveRequested?.Invoke());
        // Removing the last overlay is refused rather than undone: the model
        // guarantees a non-empty list, so it would immediately reappear as a
        // brand-new default badge, which reads as a bug.
        removeItem.Enabled = canRemove;
        removeItem.ToolTipText = canRemove ? null : "CatFoil always keeps at least one overlay.";
        _menu.Items.Add(removeItem);
        more.Click += (_, _) => _menu.Show(more, new Point(0, more.Height));

        Controls.AddRange(new Control[] { _name, _summary, _enabled, edit, more });
        LayoutChildren(edit, more);
        Resize += (_, _) => LayoutChildren(edit, more);
    }

    private static Button MakeButton(string text, Action? onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabStop = false,
        };
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    // Hand-placed rather than a TableLayoutPanel: the row is a fixed height with
    // one flexible cell (the name), which is more work to express than to do.
    private void LayoutChildren(Button edit, Button more)
    {
        const int Gap = 6;
        int right = ClientSize.Width - 12;
        int top = (CardHeight - 32) / 2;

        more.Bounds = new Rectangle(right - 40, top, 40, 32);
        edit.Bounds = new Rectangle(more.Left - Gap - 64, top, 64, 32);
        _enabled.Bounds = new Rectangle(edit.Left - Gap - 92, top + 5, 92, 22);

        int textLeft = 12 + ThumbSize + 14;
        int textWidth = Math.Max(40, _enabled.Left - Gap - textLeft);
        _name.Bounds = new Rectangle(textLeft, 20, textWidth, 23);
        _summary.Bounds = new Rectangle(textLeft, 46, textWidth, 20);
    }

    private static string Describe(OverlayItem item)
    {
        string icon = item.Normal.IconSource switch
        {
            OverlayIconSource.Custom when !string.IsNullOrWhiteSpace(item.Normal.CustomIconFile) => "custom image",
            OverlayIconSource.Gallery => IconGallery.Find(item.Normal.GalleryIconId)?.Label.ToLowerInvariant()
                                         ?? "built-in icon",
            _ => "default cat",
        };
        string fullscreen = item.Fullscreen.Visible ? "shown in fullscreen" : "hidden in fullscreen";
        return $"{item.Normal.ClampedSize()} px · {icon} · {fullscreen}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        using (var border = new Pen(CardBorder))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        var thumb = new Rectangle(12, (CardHeight - ThumbSize) / 2, ThumbSize, ThumbSize);
        using (var back = new SolidBrush(ThumbBack))
            g.FillRectangle(back, thumb);

        // Drawn even when the badge is hidden in this state — the thumbnail says
        // what the overlay looks like, and the summary line says when it shows.
        // (Draw itself doesn't consult Visible; only callers do.)
        OverlayRenderer.Draw(g, thumb, _item.Normal, _thumbIcon, null, flashOn: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Load() hands back the fallback itself when there is no custom
            // image, and that one belongs to the page.
            if (_thumbIcon != _defaultIcon) _thumbIcon.Dispose();
            // Not parented to this control, so the tree's Dispose won't reach it.
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
