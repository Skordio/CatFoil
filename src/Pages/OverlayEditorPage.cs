using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// The appearance editor for one overlay, shown as a sub-page of the settings
/// shell. Only one of the two states is on screen at a time — a selector says
/// which is being edited — so both keep the full set of controls without the
/// page growing to twice the length.
/// </summary>
internal sealed class OverlayEditorPage : SettingsPage
{
    private const int BodyWidth = 340;
    private const int PreviewSize = 240;
    private const int BodyHeight = 406;

    private readonly OverlayItem _item;
    private readonly Panel _body = new();

    private Bitmap? _defaultIcon;
    private Bitmap? _previewIcon;
    private PreviewBox? _preview;
    private bool _editingFullscreen;

    // Drives the preview's blocked-key demonstration. Without it the two
    // settings that only apply while blocking would have nothing to preview.
    private readonly System.Windows.Forms.Timer _flash = new() { Interval = 120 };
    private int _flashTicks;
    private bool _demoBlocked;

    public override string Title => _item.Name;

    public OverlayEditorPage(SettingsSession session, OverlayItem item) : base(session)
    {
        _item = item;
        _flash.Tick += (_, _) => { _flashTicks++; RefreshPreview(); };
    }

    private OverlayStateSettings State => _editingFullscreen ? _item.Fullscreen : _item.Normal;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_defaultIcon is not null) return;

        Icon source = FindForm()?.Icon ?? SystemIcons.Application;
        using (var sized = new Icon(source, 256, 256)) _defaultIcon = sized.ToBitmap();

        BuildChrome();
        BuildBody();
    }

    private void BuildChrome()
    {
        AddSection("Name");
        var name = new TextBox { Text = _item.Name, Width = 280, Font = BodyFont };
        name.TextChanged += (_, _) => Session.Apply(_ => _item.Name = name.Text);
        AddRow(name);

        AddSection("Editing");
        var normal = new RadioButton { Text = "Normal", AutoSize = true, Checked = true, Font = BodyFont };
        var fullscreen = new RadioButton
        {
            Text = "When a fullscreen app is running",
            AutoSize = true,
            Margin = new Padding(18, 0, 0, 0),
            Font = BodyFont,
        };
        normal.CheckedChanged += (_, _) =>
        {
            if (!normal.Checked) return;
            _editingFullscreen = false;
            BuildBody();
        };
        fullscreen.CheckedChanged += (_, _) =>
        {
            if (!fullscreen.Checked) return;
            _editingFullscreen = true;
            BuildBody();
        };
        AddRow(Row(normal, fullscreen));
        AddHint("Each state has its own look.");

        _body.Size = new Size(BodyWidth + 24 + PreviewSize, BodyHeight);
        AddRow(_body, topGap: 10);
    }

    // Rebuilt outright when the edited state changes: every control's value
    // comes from the model, so rebinding a live control tree would be more code
    // and more ways to fire a change handler while doing it.
    private void BuildBody()
    {
        _body.SuspendLayout();
        foreach (Control c in _body.Controls) c.Dispose();
        _body.Controls.Clear();

        OverlayStateSettings s = State;
        int y = 0;

        var show = new CheckBox
        {
            Text = _editingFullscreen ? "Show the badge while a fullscreen app is running" : "Show the badge in this state",
            AutoSize = true,
            Checked = s.Visible,
            Font = BodyFont,
            Location = new Point(0, y),
        };
        show.CheckedChanged += (_, _) => Edit(x => x.Visible = show.Checked);
        _body.Controls.Add(show);
        y += 30;

        y = Section("Icon", y);
        var useDefault = new RadioButton
        {
            Text = "Default cat", AutoSize = true, Font = BodyFont,
            Checked = s.IconSource == OverlayIconSource.Default, Location = new Point(4, y),
        };
        _body.Controls.Add(useDefault);
        y += 26;

        var useGallery = new RadioButton
        {
            Text = "Built-in icon", AutoSize = true, Font = BodyFont,
            Checked = s.IconSource == OverlayIconSource.Gallery, Location = new Point(4, y),
            Enabled = IconGallery.IsAvailable,
        };
        _body.Controls.Add(useGallery);
        y += 26;

        var strip = BuildGalleryStrip(s, new Point(18, y));
        _body.Controls.Add(strip);
        y += strip.Height + 6;

        var tintLabel = new Label { Text = "Icon colour", AutoSize = true, Font = BodyFont, Location = new Point(18, y + 6) };
        var tint = new Button
        {
            Bounds = new Rectangle(115, y, 60, 26),
            BackColor = HexColor.Parse(s.IconColor, Color.White),
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
        };
        _body.Controls.Add(tintLabel);
        _body.Controls.Add(tint);
        y += 34;

        var useCustom = new RadioButton
        {
            Text = "Custom image", AutoSize = true, Font = BodyFont,
            Checked = s.IconSource == OverlayIconSource.Custom, Location = new Point(4, y),
        };
        var browse = new Button
        {
            Text = "Choose…", Bounds = new Rectangle(150, y - 4, 90, 27),
            Enabled = s.IconSource == OverlayIconSource.Custom, TabStop = false,
        };
        _body.Controls.Add(useCustom);
        _body.Controls.Add(browse);
        y += 28;

        var file = new Label
        {
            Text = s.IconSource == OverlayIconSource.Custom && !string.IsNullOrWhiteSpace(s.CustomIconFile)
                ? "(custom image)" : "(no image chosen)",
            AutoSize = false,
            Bounds = new Rectangle(4, y, BodyWidth - 8, 18),
            ForeColor = Color.FromArgb(110, 110, 110),
            Font = BodyFont,
        };
        _body.Controls.Add(file);
        y += 26;

        void SyncIconEnabled(OverlayIconSource source)
        {
            strip.Enabled = source == OverlayIconSource.Gallery;
            tint.Enabled = source == OverlayIconSource.Gallery;
            tintLabel.Enabled = source == OverlayIconSource.Gallery;
            browse.Enabled = source == OverlayIconSource.Custom;
        }
        SyncIconEnabled(s.IconSource);

        useDefault.CheckedChanged += (_, _) =>
        {
            if (!useDefault.Checked) return;
            SyncIconEnabled(OverlayIconSource.Default);
            SetIconSource(OverlayIconSource.Default);
        };
        useGallery.CheckedChanged += (_, _) =>
        {
            if (!useGallery.Checked) return;
            SyncIconEnabled(OverlayIconSource.Gallery);
            SetIconSource(OverlayIconSource.Gallery);
        };
        useCustom.CheckedChanged += (_, _) =>
        {
            if (!useCustom.Checked) return;
            SyncIconEnabled(OverlayIconSource.Custom);
            SetIconSource(OverlayIconSource.Custom);
        };
        browse.Click += (_, _) => ChooseImage(file);
        tint.Click += (_, _) => ChooseIconColour(tint, strip);

        if (!IconGallery.IsAvailable)
        {
            AddNote("Built-in icons need a Windows symbol font that isn't installed here.",
                    new Point(18, strip.Top + 4));
        }

        y = Section("Appearance", y);
        y = Slider(y, "Size", OverlayStateSettings.MinSize, OverlayStateSettings.MaxSize,
                   s.ClampedSize(), " px", v => Edit(x => x.Size = v));
        y = Slider(y, "Opacity", OverlayStateSettings.MinOpacity, 100,
                   s.ClampedOpacity(), " %", v => Edit(x => x.Opacity = v));

        var blocked = new CheckBox
        {
            Text = "Different opacity while blocking a key",
            AutoSize = true, Checked = s.BlockedOpacityEnabled, Font = BodyFont,
            Location = new Point(0, y),
        };
        _body.Controls.Add(blocked);
        y += 28;

        int blockedSliderTop = y;
        y = Slider(y, "When blocking", OverlayStateSettings.MinOpacity, 100,
                   Math.Clamp(s.BlockedOpacity, OverlayStateSettings.MinOpacity, 100), " %",
                   v => Edit(x => x.BlockedOpacity = v), indent: 18, out Control[] blockedRow);
        foreach (Control c in blockedRow) c.Enabled = s.BlockedOpacityEnabled;
        blocked.CheckedChanged += (_, _) =>
        {
            foreach (Control c in blockedRow) c.Enabled = blocked.Checked;
            Edit(x => x.BlockedOpacityEnabled = blocked.Checked);
        };
        _ = blockedSliderTop;

        y = Slider(y, "Blocked ring", 0, 100, s.ClampedRingOpacity(), " %",
                   v => Edit(x => x.RingOpacity = v));

        y = Section("Background", y);
        var shapeLabel = new Label { Text = "Shape", AutoSize = true, Font = BodyFont, Location = new Point(0, y + 4) };
        var shape = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(115, y, 180, 24),
            Font = BodyFont,
        };
        shape.Items.AddRange(new object[] { "Rounded square", "Square", "Circle", "None" });
        shape.SelectedIndex = s.Shape switch
        {
            OverlayShape.Square => 1,
            OverlayShape.Circle => 2,
            OverlayShape.None => 3,
            _ => 0,
        };
        shape.SelectedIndexChanged += (_, _) => Edit(x => x.Shape = shape.SelectedIndex switch
        {
            1 => OverlayShape.Square,
            2 => OverlayShape.Circle,
            3 => OverlayShape.None,
            _ => OverlayShape.RoundedSquare,
        });
        _body.Controls.Add(shapeLabel);
        _body.Controls.Add(shape);
        y += 34;

        var colourLabel = new Label { Text = "Colour", AutoSize = true, Font = BodyFont, Location = new Point(0, y + 6) };
        var colour = new Button
        {
            Bounds = new Rectangle(115, y, 60, 26),
            BackColor = HexColor.Parse(s.BackgroundColor, DefaultBackground),
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
        };
        var colourName = new Label
        {
            Text = HexColor.ToHex(HexColor.Parse(s.BackgroundColor, DefaultBackground)),
            AutoSize = true, Font = BodyFont,
            ForeColor = Color.FromArgb(110, 110, 110),
            Location = new Point(185, y + 6),
        };
        colour.Click += (_, _) => ChooseColour(colour, colourName);
        _body.Controls.Add(colourLabel);
        _body.Controls.Add(colour);
        _body.Controls.Add(colourName);

        BuildPreview();

        // Size to whatever was actually laid out rather than to a hand-counted
        // constant, so adding a row later can't quietly clip the last one.
        int bottom = 0;
        foreach (Control c in _body.Controls) bottom = Math.Max(bottom, c.Bottom);
        _body.Height = bottom + 8;

        _body.ResumeLayout();
        RefreshPreview();
    }

    private static readonly Color DefaultBackground = Color.FromArgb(45, 45, 48);

    // The picker: one small button per built-in icon, drawn with the glyph
    // itself so the choice is made by recognising it rather than reading a name.
    private FlowLayoutPanel BuildGalleryStrip(OverlayStateSettings s, Point location)
    {
        var strip = new FlowLayoutPanel
        {
            Location = location,
            Size = new Size(BodyWidth - 24, 76),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
        };

        string selected = s.GalleryIconId ?? IconGallery.DefaultId;
        Color tint = HexColor.Parse(s.IconColor, Color.White);

        foreach (IconGallery.Entry entry in IconGallery.Icons)
        {
            // Tinted for the swatch only if the chosen colour is dark enough to
            // read on a light button; otherwise the picker would be blank.
            Color swatch = tint.GetBrightness() > 0.75f ? Color.FromArgb(60, 60, 65) : tint;
            var button = new Button
            {
                Size = new Size(34, 34),
                Margin = new Padding(0, 0, 4, 4),
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                Image = IconGallery.Render(entry.Id, swatch, 22),
                Tag = entry.Id,
            };
            button.FlatAppearance.BorderSize = entry.Id == selected ? 2 : 1;
            button.FlatAppearance.BorderColor = entry.Id == selected
                ? Color.FromArgb(58, 110, 190)
                : Color.FromArgb(210, 210, 210);
            Tip.SetToolTip(button, entry.Label);

            string id = entry.Id;
            button.Click += (_, _) =>
            {
                foreach (Control c in strip.Controls)
                    if (c is Button b)
                    {
                        bool on = (string?)b.Tag == id;
                        b.FlatAppearance.BorderSize = on ? 2 : 1;
                        b.FlatAppearance.BorderColor = on
                            ? Color.FromArgb(58, 110, 190)
                            : Color.FromArgb(210, 210, 210);
                    }
                Edit(x => { x.IconSource = OverlayIconSource.Gallery; x.GalleryIconId = id; });
                LoadPreviewIcon();
                RefreshPreview();
            };
            strip.Controls.Add(button);
        }
        return strip;
    }

    private void AddNote(string text, Point location) => _body.Controls.Add(new Label
    {
        Text = text,
        AutoSize = false,
        Bounds = new Rectangle(location.X, location.Y, BodyWidth - 24, 34),
        ForeColor = Color.FromArgb(150, 90, 40),
        Font = BodyFont,
    });

    private void SetIconSource(OverlayIconSource source)
    {
        Edit(x =>
        {
            x.IconSource = source;
            // A gallery icon needs an id the first time it is chosen.
            if (source == OverlayIconSource.Gallery && string.IsNullOrWhiteSpace(x.GalleryIconId))
                x.GalleryIconId = IconGallery.DefaultId;
        });
        LoadPreviewIcon();
        RefreshPreview();
    }

    private void ChooseIconColour(Button swatch, Control strip)
    {
        using var dlg = new ColorDialog
        {
            Color = HexColor.Parse(State.IconColor, Color.White),
            FullOpen = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        swatch.BackColor = dlg.Color;
        Edit(x => x.IconColor = HexColor.ToHex(dlg.Color));
        LoadPreviewIcon();
        RefreshPreview();
        // Redraw the picker so its swatches follow the new colour.
        BuildBody();
        _ = strip;
    }

    private void BuildPreview()
    {
        _preview = new PreviewBox
        {
            Bounds = new Rectangle(BodyWidth + 24, 0, PreviewSize, PreviewSize),
        };
        _body.Controls.Add(_preview);

        var demo = new CheckBox
        {
            Text = "Preview a blocked key",
            AutoSize = true,
            Checked = _demoBlocked,
            Font = BodyFont,
            Location = new Point(BodyWidth + 24, PreviewSize + 8),
        };
        demo.CheckedChanged += (_, _) =>
        {
            _demoBlocked = demo.Checked;
            _flashTicks = 0;
            if (_demoBlocked) _flash.Start(); else _flash.Stop();
            RefreshPreview();
        };
        _body.Controls.Add(demo);

        LoadPreviewIcon();
    }

    private int Section(string title, int y)
    {
        _body.Controls.Add(new Label
        {
            Text = title, AutoSize = true, Font = SectionFont,
            ForeColor = Color.FromArgb(40, 40, 40),
            Location = new Point(0, y + 6),
        });
        return y + 30;
    }

    private int Slider(int y, string caption, int min, int max, int value, string suffix, Action<int> onChange,
                       int indent = 0) => Slider(y, caption, min, max, value, suffix, onChange, indent, out _);

    // A caption, a track and a live value readout, as one row.
    private int Slider(int y, string caption, int min, int max, int value, string suffix, Action<int> onChange,
                       int indent, out Control[] row)
    {
        var label = new Label { Text = caption, AutoSize = true, Font = BodyFont, Location = new Point(indent, y + 6) };
        var track = new TrackBar
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            TickFrequency = Math.Max(1, (max - min) / 8),
            SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10),
            Bounds = new Rectangle(115, y, 165, 40),
        };
        var readout = new Label
        {
            Text = track.Value + suffix, AutoSize = true, Font = BodyFont,
            Location = new Point(288, y + 6),
        };
        track.Scroll += (_, _) =>
        {
            readout.Text = track.Value + suffix;
            onChange(track.Value);
        };
        _body.Controls.Add(label);
        _body.Controls.Add(track);
        _body.Controls.Add(readout);
        row = new Control[] { label, track, readout };
        return y + 40;
    }

    // Applies an edit to whichever state is being edited. The state object is
    // replaced rather than mutated, so the overlay window's reference-based
    // repaint check sees the change.
    private void Edit(Action<OverlayStateSettings> change)
    {
        Session.Apply(_ =>
        {
            OverlayStateSettings next = State.Clone();
            change(next);
            if (_editingFullscreen) _item.Fullscreen = next; else _item.Normal = next;
        });
        RefreshPreview();
    }

    private void ChooseImage(Label file)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose an overlay image",
            Filter = "Images|*.png;*.ico;*.jpg;*.jpeg;*.bmp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            string stored = IconStore.Import(dlg.FileName, _item.Id, _editingFullscreen ? "fullscreen" : "normal");
            Edit(x => { x.UseCustomIcon = true; x.CustomIconFile = stored; });
            file.Text = Path.GetFileName(dlg.FileName);
            LoadPreviewIcon();
            RefreshPreview();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this,
                "Could not copy that image into CatFoil's folder:\n\n" + ex.Message,
                "Overlay image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ChooseColour(Button swatch, Label readout)
    {
        using var dlg = new ColorDialog
        {
            Color = HexColor.Parse(State.BackgroundColor, DefaultBackground),
            FullOpen = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string hex = HexColor.ToHex(dlg.Color);
        swatch.BackColor = dlg.Color;
        readout.Text = hex;
        Edit(x => x.BackgroundColor = hex);
    }

    private void LoadPreviewIcon()
    {
        if (_defaultIcon is null) return;
        if (_previewIcon is not null && _previewIcon != _defaultIcon) _previewIcon.Dispose();
        _previewIcon = OverlayIcon.Load(State, _defaultIcon);
    }

    private void RefreshPreview()
    {
        if (_preview is null || _previewIcon is null) return;
        bool flashOn = _demoBlocked && _flashTicks % 2 == 1;
        _preview.Show(State, _previewIcon, flashOn, _demoBlocked);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flash.Dispose();
            if (_previewIcon is not null && _previewIcon != _defaultIcon) _previewIcon.Dispose();
            _defaultIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A checkerboard-backed preview that paints exactly like the badge.</summary>
    private sealed class PreviewBox : Control
    {
        // Hoisted out of OnPaint: dragging a slider repaints on every tick.
        private static readonly SolidBrush LightCell = new(Color.FromArgb(235, 235, 235));
        private static readonly SolidBrush DarkCell = new(Color.FromArgb(210, 210, 210));
        private static readonly Pen BorderPen = new(Color.FromArgb(180, 180, 180));

        private OverlayStateSettings _state = new();
        private Bitmap? _icon;
        private bool _flashOn;
        private bool _blocked;

        public PreviewBox() { DoubleBuffered = true; }

        public void Show(OverlayStateSettings state, Bitmap icon, bool flashOn, bool blocked)
        {
            _state = state;
            _icon = icon;
            _flashOn = flashOn;
            _blocked = blocked;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            // Checkerboard, so "no background" reads as transparency rather than
            // as white.
            const int cell = 10;
            g.FillRectangle(LightCell, ClientRectangle);
            for (int y = 0; y < Height; y += cell)
                for (int x = 0; x < Width; x += cell)
                    if (((x / cell) + (y / cell)) % 2 == 0)
                        g.FillRectangle(DarkCell, x, y, cell, cell);
            g.DrawRectangle(BorderPen, 0, 0, Width - 1, Height - 1);

            if (_icon is null || !_state.Visible) return;

            int side = Math.Min(_state.ClampedSize(), Math.Min(Width, Height) - 8);
            var bounds = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);
            OverlayRenderer.Draw(g, bounds, _state, _icon, null, _flashOn, _blocked);
        }
    }
}
