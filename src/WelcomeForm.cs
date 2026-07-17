using System;
using System.Drawing;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Shown once, on the very first launch: a quick tour of everything a new
/// user needs to know before locking their keyboard for the first time.
/// </summary>
public sealed class WelcomeForm : Form
{
    public WelcomeForm(Settings settings)
    {
        Text = "Welcome to CatFoil";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 540);
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.FromArgb(245, 245, 245);

        string hotkey = string.Join(" + ", SettingsForm.ActiveHotkeyParts(settings));

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 14, 20, 0),
        };

        AddTitle(flow, "Welcome to CatFoil 🐱");
        AddBody(flow,
            "CatFoil locks your keyboard so a cat walking across your desk can't type — " +
            "while your mouse keeps working the whole time.");

        AddHeader(flow, "Locking");
        AddBody(flow,
            $"Lock the keyboard with the big button, the tray menu, or the hotkey {hotkey}.");

        AddHeader(flow, "Unlocking");
        AddBody(flow,
            $"Press {hotkey} again — it works even while the keyboard is locked — or click " +
            "Unlock with the mouse. If you're ever stuck, Ctrl + Alt + Del always reaches Windows.");

        AddHeader(flow, "The cat badge");
        AddBody(flow,
            "While locked, a small cat badge floats on your screen as a reminder. Drag it " +
            "anywhere you like; click it to open CatFoil. It hides itself during fullscreen apps.");

        AddHeader(flow, "The tray icon");
        AddBody(flow,
            "Closing this window doesn't quit CatFoil — it keeps running in the system tray, " +
            "next to the clock. Right-click the tray icon to lock, open settings, or exit.");

        var ok = new Button
        {
            Text = "Get started",
            Dock = DockStyle.Bottom,
            Height = 48,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            DialogResult = DialogResult.OK,
            TabStop = false,
        };
        AcceptButton = ok;

        Controls.Add(flow);     // added first so DockStyle.Fill gets the remaining space
        Controls.Add(ok);

        // Size the window to the content: the text length varies (the hotkey
        // string is user-configurable), so a hard-coded height can clip it.
        int contentHeight = flow.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        int maxHeight = (Screen.PrimaryScreen?.WorkingArea.Height ?? 900) - 80;
        ClientSize = new Size(ClientSize.Width,
            Math.Min(contentHeight + 8 + ok.Height, maxHeight));   // AutoScroll kicks in if capped
    }

    private static void AddTitle(FlowLayoutPanel flow, string text) =>
        flow.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        });

    private static void AddHeader(FlowLayoutPanel flow, string text) =>
        flow.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 2),
        });

    private static void AddBody(FlowLayoutPanel flow, string text) =>
        flow.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(456, 0),   // wrap inside the dialog
            ForeColor = Color.FromArgb(60, 60, 60),
            Margin = new Padding(0, 0, 0, 2),
        });
}
