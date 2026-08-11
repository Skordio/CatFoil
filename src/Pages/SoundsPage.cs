using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Audio cues for locking, unlocking and blocked keys. Each is independent, and
/// each can be a Windows scheme sound or a file of the user's own.
/// </summary>
internal sealed class SoundsPage : SettingsPage
{
    // The scheme sounds are the user's own, so if they've set those events to
    // "(None)" nothing plays — point at the real knob rather than looking broken.
    private const string SystemTip =
        "Uses your Windows system sounds (Exclamation, Asterisk and Critical Stop).\n" +
        "If you hear nothing, check those events in Windows Settings > Sound > More sound settings.";

    public override string Title => "Sounds";

    public SoundsPage(SettingsSession session) : base(session)
    {
        AddSection("When the keyboard locks");
        AddCue(Settings.LockSound, "Play a sound when locking", "lock", () => Sounds.PreviewLock(Settings));

        AddSection("When the keyboard unlocks");
        AddCue(Settings.UnlockSound, "Play a sound when unlocking", "unlock", () => Sounds.PreviewUnlock(Settings));

        AddSection("When a key is blocked");
        AddCue(Settings.BlockedSound, "Play a sound when a key is blocked while locked", "blocked",
               () => Sounds.PreviewBlocked(Settings));
        AddHint("Throttled, so a cat leaning on a key can't machine-gun it.");
    }

    /// <summary>One cue's block of controls: on/off, where the audio comes from,
    /// how loud, and a way to hear it.</summary>
    private void AddCue(SoundSetting cue, string checkText, string eventName, Action preview)
    {
        CheckBox enabled = AddCheck(checkText, cue.Enabled,
            v => Session.Apply(_ => cue.Enabled = v), SystemTip);

        var useSystem = new RadioButton
        {
            Text = "Windows sound",
            AutoSize = true,
            Font = BodyFont,
            Checked = cue.Source == SoundSource.System,
            Padding = new Padding(0, 4, 0, 4),
            Margin = new Padding(0, 0, 16, 0),
        };
        var useCustom = new RadioButton
        {
            Text = "My own file",
            AutoSize = true,
            Font = BodyFont,
            Checked = cue.Source == SoundSource.Custom,
            Padding = new Padding(0, 4, 0, 4),
            Margin = new Padding(0, 0, 12, 0),
        };
        var choose = new Button
        {
            Text = "Choose…",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 6),
            TabStop = false,
        };
        AddRow(Row(useSystem, useCustom, choose), indent: 22);

        var file = new Label
        {
            Text = FileLabel(cue),
            AutoSize = true,
            Font = BodyFont,
            ForeColor = Color.FromArgb(110, 110, 110),
        };
        AddRow(file, indent: 22, topGap: 0);

        var volumeLabel = new Label { Text = "Volume", AutoSize = true, Font = BodyFont, Margin = new Padding(0, 10, 10, 0) };
        var volume = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = cue.ClampedVolume(),
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Size = new Size(190, 45),
            Margin = new Padding(0, 0, 10, 0),
        };
        var volumeValue = new Label
        {
            Text = cue.ClampedVolume() + " %",
            AutoSize = true,
            Font = BodyFont,
            Margin = new Padding(0, 10, 16, 0),
        };
        var test = new Button
        {
            Text = "Test",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(0, 6, 0, 0),
            TabStop = false,
        };
        volume.Scroll += (_, _) =>
        {
            volumeValue.Text = volume.Value + " %";
            Session.Apply(_ => cue.Volume = volume.Value);
        };
        test.Click += (_, _) => preview();
        AddRow(Row(volumeLabel, volume, volumeValue, test), indent: 22, topGap: 0);

        // Volume only means anything for a file we play ourselves; a Windows
        // scheme sound plays at whatever the system mixer says.
        void Sync()
        {
            bool on = enabled.Checked;
            bool custom = useCustom.Checked;
            useSystem.Enabled = on;
            useCustom.Enabled = on;
            choose.Enabled = on && custom;
            file.Enabled = on && custom;
            volumeLabel.Enabled = on && custom;
            volume.Enabled = on && custom;
            volumeValue.Enabled = on && custom;
            test.Enabled = on;
        }
        Sync();

        enabled.CheckedChanged += (_, _) => Sync();
        useSystem.CheckedChanged += (_, _) =>
        {
            if (!useSystem.Checked) return;
            Session.Apply(_ => cue.Source = SoundSource.System);
            Sync();
        };
        useCustom.CheckedChanged += (_, _) =>
        {
            if (!useCustom.Checked) return;
            Session.Apply(_ => cue.Source = SoundSource.Custom);
            Sync();
        };
        choose.Click += (_, _) => ChooseFile(cue, eventName, file);
    }

    private static string FileLabel(SoundSetting cue) =>
        cue.Source == SoundSource.Custom && !string.IsNullOrWhiteSpace(cue.File)
            ? "Using your own file."
            : "No file chosen yet.";

    private void ChooseFile(SoundSetting cue, string eventName, Label file)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a sound",
            Filter = SoundStore.PickerFilter,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            string stored = SoundStore.Import(dlg.FileName, eventName);
            Session.Apply(_ =>
            {
                cue.File = stored;
                cue.Source = SoundSource.Custom;
            });
            file.Text = "Using " + Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this,
                "Could not copy that sound into CatFoil's folder:\n\n" + ex.Message,
                "Sound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
