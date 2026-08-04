using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Base for the panes hosted by <see cref="SettingsShell"/>.
///
/// Content is a single auto-sizing column, so adding a setting means appending
/// a row — nothing below it has to be re-measured the way every control in the
/// old fixed-pixel dialog did. The page scrolls if it outgrows the window.
/// </summary>
internal abstract class SettingsPage : UserControl
{
    // Shared statics: a settings window can be opened and closed repeatedly, and
    // WinForms never disposes a Font assigned to a control, so per-page fonts
    // would leak a handle every time.
    protected static readonly Font BodyFont = new("Segoe UI", 9.5f);
    protected static readonly Font SectionFont = new("Segoe UI", 9.5f, FontStyle.Bold);

    private static readonly Color SectionColor = Color.FromArgb(40, 40, 40);
    private static readonly Color HintColor = Color.FromArgb(110, 110, 110);

    private readonly TableLayoutPanel _stack;

    protected SettingsSession Session { get; }
    protected Settings Settings => Session.Settings;
    protected ToolTip Tip { get; } = new() { AutoPopDelay = 20000 };

    /// <summary>Name shown in the navigation list and the page header.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// The shell this page is parented under, or null before first show. Walks
    /// the parent chain rather than <see cref="Control.FindForm"/>: the shell is
    /// a UserControl, so the form it sits in isn't the settings UI any more.
    /// </summary>
    protected SettingsShell? Shell
    {
        get
        {
            for (Control? c = Parent; c is not null; c = c.Parent)
                if (c is SettingsShell shell) return shell;
            return null;
        }
    }

    protected SettingsPage(SettingsSession session)
    {
        Session = session;

        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = Color.White;
        Font = BodyFont;
        // Generous bottom padding on purpose: it keeps the last row clear of the
        // scroll viewport's edge, so a page that continues below never ends on a
        // control sliced in half.
        Padding = new Padding(28, 12, 28, 40);

        _stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        Controls.Add(_stack);
    }

    /// <summary>A bold group heading. The first one on a page sits flush to the top.</summary>
    protected void AddSection(string title)
    {
        _stack.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = SectionFont,
            ForeColor = SectionColor,
            Margin = new Padding(0, _stack.Controls.Count == 0 ? 0 : 30, 0, 12),
        });
    }

    protected CheckBox AddCheck(string text, bool value, Action<bool> onChanged,
                                string? tip = null, int indent = 0)
    {
        var check = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Font = BodyFont,
            Checked = value,
            // Padding grows the control itself, so the clickable area grows with
            // it — margin alone would only push neighbours away and leave the
            // same thin strip to hit.
            Padding = new Padding(0, 6, 0, 6),
            Margin = new Padding(indent, 5, 0, 5),
        };
        check.CheckedChanged += (_, _) => onChanged(check.Checked);
        if (tip is not null) Tip.SetToolTip(check, tip);
        _stack.Controls.Add(check);
        return check;
    }

    /// <summary>Small grey explanatory text under a setting.</summary>
    protected Label AddHint(string text, int indent = 0)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = HintColor,
            Margin = new Padding(indent, 2, 0, 12),
        };
        _stack.Controls.Add(label);
        return label;
    }

    /// <summary>Appends an arbitrary control (a composed row, a button, …).</summary>
    protected T AddRow<T>(T control, int indent = 0, int topGap = 8) where T : Control
    {
        control.Margin = new Padding(indent, topGap, 0, 8);
        _stack.Controls.Add(control);
        return control;
    }

    /// <summary>
    /// Appends a control stretched to the full width of the page, for rows that
    /// are a block rather than a label — the overlay cards. Unlike
    /// <see cref="AddRow"/> this keeps a right margin, so the control doesn't sit
    /// flush against the edge of the scroll area.
    /// </summary>
    protected T AddStretchRow<T>(T control, int height, int topGap = 6) where T : Control
    {
        control.Height = height;
        control.Margin = new Padding(0, topGap, 2, 0);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        // The row auto-sizes to the control, so Dock.Fill would collapse it.
        control.Width = Math.Max(1, _stack.ClientSize.Width - control.Margin.Horizontal);
        _stack.Controls.Add(control);
        return control;
    }

    /// <summary>Removes everything added so far, so a page can rebuild itself.</summary>
    protected void ClearRows()
    {
        // Dispose as we go: these controls are leaving the tree for good, and
        // the page itself may live for the whole settings session.
        while (_stack.Controls.Count > 0)
        {
            Control c = _stack.Controls[0];
            _stack.Controls.RemoveAt(0);
            c.Dispose();
        }
    }

    /// <summary>A left-to-right row of controls that sizes to its contents.</summary>
    protected static FlowLayoutPanel Row(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        flow.Controls.AddRange(controls);
        return flow;
    }

    protected override void Dispose(bool disposing)
    {
        // ToolTip is handle-backed and isn't parented to this control, so the
        // control tree's Dispose won't reach it.
        if (disposing) Tip.Dispose();
        base.Dispose(disposing);
    }
}
