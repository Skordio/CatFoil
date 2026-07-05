using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// The overlay customization menu, opened from the main settings window. Lets
/// the user set the badge icon, size, and background independently for the
/// normal state and the fullscreen-app state, each with a live preview.
/// </summary>
public sealed class OverlaySettingsForm : Form
{
    private readonly Settings _settings;
    private readonly Bitmap _defaultIcon;
    private readonly StateEditor _normalEditor;
    private readonly StateEditor _fullscreenEditor;

    public event Action? SettingsSaved;

    public OverlaySettingsForm(Settings settings, Icon appIcon)
    {
        _settings = settings;
        _defaultIcon = new Icon(appIcon, 256, 256).ToBitmap();

        Text = "Overlay Appearance";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(652, 746);
        Font = new Font("Segoe UI", 9.5f);

        var intro = new Label
        {
            Text = "Choose how the locked-keyboard badge looks. You can set it separately " +
                   "for when a fullscreen app is running (games, videos). The preview shows the real size.",
            Location = new Point(14, 12),
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = Color.FromArgb(70, 70, 70),
        };

        _normalEditor = new StateEditor("Normal (no fullscreen app)", _settings.OverlayNormal, _defaultIcon)
        { Location = new Point(12, 84) };
        _fullscreenEditor = new StateEditor("When a fullscreen app is running", _settings.OverlayFullscreen, _defaultIcon)
        { Location = new Point(12, 392) };

        var btnOk = new Button { Text = "OK", Bounds = new Rectangle(464, 704, 85, 30) };
        btnOk.Click += OnOk;
        var btnCancel = new Button { Text = "Cancel", Bounds = new Rectangle(555, 704, 85, 30) };
        btnCancel.Click += (_, _) => Close();
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[] { intro, _normalEditor, _fullscreenEditor, btnOk, btnCancel });
    }

    private void OnOk(object? sender, EventArgs e)
    {
        _normalEditor.CommitTo(_settings.OverlayNormal, "overlay-normal");
        _fullscreenEditor.CommitTo(_settings.OverlayFullscreen, "overlay-fullscreen");
        _settings.Save();
        SettingsSaved?.Invoke();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _defaultIcon.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>One state's editor: a group box with the controls plus a preview.</summary>
    private sealed class StateEditor : GroupBox
    {
        private readonly Bitmap _defaultIcon;
        private readonly OverlayStateSettings _working;

        private readonly CheckBox _chkShow = new();
        private readonly RadioButton _rbDefault = new();
        private readonly RadioButton _rbCustom = new();
        private readonly Button _btnBrowse = new();
        private readonly Label _lblFile = new();
        private readonly TrackBar _size = new();
        private readonly Label _lblSize = new();
        private readonly CheckBox _chkBackground = new();
        private readonly PreviewBox _preview;

        // The icon currently shown in the preview, and — if the user browsed to
        // a new file this session — the source path to copy in on commit.
        private Bitmap _previewIcon;
        private string? _pendingSourcePath;

        public StateEditor(string title, OverlayStateSettings source, Bitmap defaultIcon)
        {
            _defaultIcon = defaultIcon;
            _working = source.Clone();
            _previewIcon = defaultIcon;

            Text = title;
            Size = new Size(628, 300);

            _chkShow.Text = "Show the overlay in this state";
            _chkShow.AutoSize = true;
            _chkShow.Location = new Point(16, 24);
            _chkShow.Checked = _working.Visible;
            _chkShow.CheckedChanged += (_, _) => { _working.Visible = _chkShow.Checked; SyncEnabled(); Refresh2(); };

            _rbDefault.Text = "Default cat icon";
            _rbDefault.AutoSize = true;
            _rbDefault.Location = new Point(20, 52);
            _rbDefault.Checked = !_working.UseCustomIcon;
            _rbDefault.CheckedChanged += (_, _) => { if (_rbDefault.Checked) SetCustom(false); };

            _rbCustom.Text = "Custom image";
            _rbCustom.AutoSize = true;
            _rbCustom.Location = new Point(20, 80);
            _rbCustom.Checked = _working.UseCustomIcon;
            _rbCustom.CheckedChanged += (_, _) => { if (_rbCustom.Checked) SetCustom(true); };

            _btnBrowse.Text = "Browse…";
            _btnBrowse.Bounds = new Rectangle(170, 77, 92, 27);
            _btnBrowse.Click += OnBrowse;

            _lblFile.AutoSize = false;
            _lblFile.Bounds = new Rectangle(20, 108, 320, 18);
            _lblFile.ForeColor = Color.FromArgb(110, 110, 110);
            _lblFile.Text = _working.UseCustomIcon && _working.CustomIconFile is not null
                ? _working.CustomIconFile : "(no file chosen)";

            _chkBackground.Text = "Show background box";
            _chkBackground.AutoSize = true;
            _chkBackground.Location = new Point(20, 136);
            _chkBackground.Checked = _working.ShowBackground;
            _chkBackground.CheckedChanged += (_, _) => { _working.ShowBackground = _chkBackground.Checked; Refresh2(); };

            var lblSizeCaption = new Label { Text = "Size:", AutoSize = true, Location = new Point(16, 172) };
            _size.Minimum = OverlayStateSettings.MinSize;
            _size.Maximum = OverlayStateSettings.MaxSize;
            _size.TickFrequency = 32;
            _size.SmallChange = 4;
            _size.LargeChange = 32;
            _size.Value = _working.ClampedSize();
            _size.Bounds = new Rectangle(52, 168, 200, 40);
            _size.Scroll += (_, _) => { _working.Size = _size.Value; _lblSize.Text = $"{_size.Value} px"; Refresh2(); };

            _lblSize.AutoSize = true;
            _lblSize.Location = new Point(260, 178);
            _lblSize.Text = $"{_working.ClampedSize()} px";

            // Big enough to show the full 32–256 px size range at true scale.
            _preview = new PreviewBox { Bounds = new Rectangle(352, 24, 268, 268) };

            Controls.AddRange(new Control[]
            {
                _chkShow, _rbDefault, _rbCustom, _btnBrowse, _lblFile,
                lblSizeCaption, _size, _lblSize, _chkBackground, _preview,
            });

            LoadPreviewIcon();
            SyncEnabled();
            Refresh2();
        }

        private void SetCustom(bool custom)
        {
            _working.UseCustomIcon = custom;
            LoadPreviewIcon();
            SyncEnabled();
            Refresh2();
        }

        private void OnBrowse(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Choose an overlay image",
                Filter = "Images|*.png;*.ico;*.jpg;*.jpeg;*.bmp|All files|*.*",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _pendingSourcePath = dlg.FileName;
            _lblFile.Text = Path.GetFileName(dlg.FileName);
            _rbCustom.Checked = true;
            _working.UseCustomIcon = true;
            LoadPreviewIcon();
            SyncEnabled();
            Refresh2();
        }

        // Load whatever the preview should show: a freshly browsed file, the
        // already-stored custom file, or the default icon (with fallback).
        private void LoadPreviewIcon()
        {
            SwapPreviewIcon(_defaultIcon);
            if (!_working.UseCustomIcon) return;

            string? path = _pendingSourcePath;
            if (path is null && _working.CustomIconFile is not null)
            {
                string stored = Path.Combine(Settings.Directory, _working.CustomIconFile);
                if (File.Exists(stored)) path = stored;
            }
            if (path is null || !File.Exists(path)) return;

            try
            {
                using var fromFile = new Bitmap(path);
                SwapPreviewIcon(new Bitmap(fromFile));
            }
            catch
            {
                SwapPreviewIcon(_defaultIcon);   // unreadable — show default
            }
        }

        private void SwapPreviewIcon(Bitmap next)
        {
            if (_previewIcon != _defaultIcon) _previewIcon.Dispose();
            _previewIcon = next;
        }

        private void SyncEnabled()
        {
            bool on = _chkShow.Checked;
            foreach (Control c in Controls)
                if (c != _chkShow) c.Enabled = on;
            if (on) _btnBrowse.Enabled = _rbCustom.Checked;
        }

        private void Refresh2() => _preview.Show(_working, _previewIcon);

        /// <summary>Writes this editor's state into <paramref name="target"/>,
        /// copying any newly chosen custom image into the CatFoil folder.</summary>
        public void CommitTo(OverlayStateSettings target, string baseName)
        {
            if (_working.UseCustomIcon && _pendingSourcePath is not null)
            {
                string ext = Path.GetExtension(_pendingSourcePath).ToLowerInvariant();
                if (ext is not (".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp")) ext = ".png";
                string destName = baseName + ext;
                System.IO.Directory.CreateDirectory(Settings.Directory);
                File.Copy(_pendingSourcePath, Path.Combine(Settings.Directory, destName), overwrite: true);
                _working.CustomIconFile = destName;
            }

            target.Visible = _working.Visible;
            target.UseCustomIcon = _working.UseCustomIcon;
            target.CustomIconFile = _working.CustomIconFile;
            target.Size = _working.ClampedSize();
            target.ShowBackground = _working.ShowBackground;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _previewIcon != _defaultIcon) _previewIcon.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A checkerboard-backed preview that paints exactly like the overlay.</summary>
    private sealed class PreviewBox : Control
    {
        private OverlayStateSettings _state = new();
        private Bitmap? _icon;

        public PreviewBox()
        {
            DoubleBuffered = true;
        }

        public void Show(OverlayStateSettings state, Bitmap icon)
        {
            _state = state;
            _icon = icon;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            // Checkerboard so "no background" reads as transparency.
            const int cell = 10;
            using (var light = new SolidBrush(Color.FromArgb(235, 235, 235)))
            using (var dark = new SolidBrush(Color.FromArgb(210, 210, 210)))
            {
                g.FillRectangle(light, ClientRectangle);
                for (int y = 0; y < Height; y += cell)
                    for (int x = 0; x < Width; x += cell)
                        if (((x / cell) + (y / cell)) % 2 == 0)
                            g.FillRectangle(dark, x, y, cell, cell);
            }
            using (var border = new Pen(Color.FromArgb(180, 180, 180)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            if (_icon is null || !_state.Visible) return;

            // Draw the badge centered at its real size, capped to the preview box.
            int side = Math.Min(_state.ClampedSize(), Math.Min(Width, Height) - 8);
            var bounds = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);
            OverlayRenderer.Draw(g, bounds, _state, _icon, null, flashOn: false);
        }
    }
}
