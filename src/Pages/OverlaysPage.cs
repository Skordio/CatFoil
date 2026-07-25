using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// The list of on-screen badges shown while the keyboard is locked: one card
/// per overlay, with the actions for it, plus the master switch over all of them.
/// </summary>
internal sealed class OverlaysPage : SettingsPage
{
    private Bitmap? _defaultIcon;

    public override string Title => "Overlays";

    public OverlaysPage(SettingsSession session) : base(session)
    {
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // The form's Icon is assigned after construction, so the default badge
        // image can only be resolved once this page is parented and shown.
        _defaultIcon ??= LoadDefaultIcon();
        Rebuild();   // no event to unwind here, so build the list straight away
    }

    private Bitmap LoadDefaultIcon()
    {
        Icon source = FindForm()?.Icon ?? SystemIcons.Application;
        using var sized = new Icon(source, 256, 256);
        return sized.ToBitmap();
    }

    private void Rebuild()
    {
        if (_defaultIcon is null) return;

        // Rebuilt wholesale on every change: the list is a handful of rows, and
        // reconciling card state in place would be more code than redrawing it.
        SuspendLayout();
        ClearRows();

        AddSection("Locked-keyboard badges");
        AddCheck("Show overlays while the keyboard is locked",
            Settings.ShowOverlay,
            v => Session.Apply(s => s.ShowOverlay = v));
        AddHint("Drag a badge anywhere on screen; CatFoil remembers where you put each one.");

        List<OverlayItem> items = Settings.EnsureOverlays();
        bool canRemove = items.Count > 1;

        foreach (OverlayItem item in items)
        {
            OverlayItem captured = item;
            var card = new OverlayCard(captured, _defaultIcon, canRemove);
            card.EnabledToggled += v => OnEnabledToggled(captured, v);
            card.EditRequested += () => OnEdit(captured);
            card.DuplicateRequested += () => OnDuplicate(captured);
            card.RemoveRequested += () => OnRemove(captured);
            AddStretchRow(card, OverlayCard.CardHeight);
        }

        var add = new Button
        {
            Text = "Add overlay",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            TabStop = false,
        };
        add.Click += (_, _) => OnAdd();
        AddRow(add, topGap: 12);

        ResumeLayout();
    }

    // Every action below is raised from a card's own Click handler, and Rebuild
    // disposes those cards. Unwind the event first, or the button that was
    // clicked is disposed underneath WinForms while it is still handling it.
    private void RebuildLater()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(Rebuild); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private void OnEnabledToggled(OverlayItem item, bool enabled)
    {
        // No rebuild at all: the card already shows the new state, so redrawing
        // the list would only make the checkbox flicker.
        Session.Apply(_ => item.Enabled = enabled);
    }

    private void OnEdit(OverlayItem item)
    {
        Form? owner = FindForm();
        using var editor = new OverlaySettingsForm(item, Settings, owner?.Icon ?? SystemIcons.Application);
        // That dialog saves itself; re-announce so the live badge picks the
        // change up, then refresh the card's thumbnail and summary.
        editor.SettingsSaved += Session.NotifyChanged;
        if (owner is not null) editor.ShowDialog(owner); else editor.ShowDialog();
        RebuildLater();
    }

    private void OnAdd()
    {
        var item = new OverlayItem { Name = NextName() };
        Session.Apply(s => s.Overlays.Add(item));
        RebuildLater();
    }

    private void OnDuplicate(OverlayItem source)
    {
        var copy = new OverlayItem
        {
            Name = source.Name + " (copy)",
            Enabled = source.Enabled,
            Normal = source.Normal.Clone(),
            Fullscreen = source.Fullscreen.Clone(),
            // Position deliberately left null so the copy cascades to its own
            // spot instead of landing exactly on top of the original.
        };

        // Give the copy its own image files. Sharing the original's relative
        // path would survive today's sweep, but it makes one overlay's edits
        // silently change another's appearance.
        copy.Normal.CustomIconFile = IconStore.Duplicate(source.Normal.CustomIconFile, copy.Id, "normal");
        copy.Fullscreen.CustomIconFile = IconStore.Duplicate(source.Fullscreen.CustomIconFile, copy.Id, "fullscreen");

        Session.Apply(s => s.Overlays.Add(copy));
        RebuildLater();
    }

    private void OnRemove(OverlayItem item)
    {
        var confirm = MessageBox.Show(this,
            $"Remove the overlay \"{item.Name}\"?",
            "Remove overlay", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        // Its images stay on disk until the settings window closes, so changing
        // your mind within this visit costs nothing.
        Session.Apply(s => s.Overlays.RemoveAll(o => o.Id == item.Id));
        RebuildLater();
    }

    // "Overlay 2", "Overlay 3", … — the lowest number not already taken, so
    // adding after a removal reuses the gap instead of climbing forever.
    private string NextName()
    {
        var taken = new HashSet<string>(Settings.Overlays.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = n == 1 ? "Overlay" : $"Overlay {n}";
            if (taken.Add(candidate)) return candidate;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _defaultIcon?.Dispose();
        base.Dispose(disposing);
    }
}
