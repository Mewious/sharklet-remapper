using System.Text.Json;

namespace TailwindRemapper;

internal sealed class McduSettings
{
    public int Side { get; set; } = 1;
    public string FontPath { get; set; } = McduDisplay.DefaultFontPath;
    public string LogDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
    public int X { get; set; } = 52;
    public int Y { get; set; } = 37;
    public bool ScreenOutput { get; set; } = true;
    public bool KeyOutput { get; set; } = true;
}

internal sealed class McduForm : Form
{
    private readonly InputDevices inputs = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly ComboBox usb = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    private readonly ComboBox keyboard = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    private readonly ComboBox side = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox fontPath = new() { Width = 510 };
    private readonly TextBox logDirectory = new() { Width = 510 };
    private readonly NumericUpDown x = new() { Maximum = 200, Width = 65 };
    private readonly NumericUpDown y = new() { Maximum = 200, Width = 65 };
    private readonly CheckBox screenOutput = new() { Text = "Send screen and lights to USB", AutoSize = true };
    private readonly CheckBox keyOutput = new() { Text = "Send keys to Roblox", AutoSize = true };
    private readonly SharkletButton start = new() { Text = "Start MCDU bridge", Primary = true };
    private readonly SharkletButton test = new() { Text = "Test screen + lights (5 seconds)", Enabled = false };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(700, 0) };
    private readonly TextBox preview = new() { Multiline = true, ReadOnly = true, Font = new Font("Consolas", 12), Width = 340, Height = 305, BackColor = Color.Black, ForeColor = Color.Lime };
    private readonly FlowLayoutPanel settingsPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailwindRemapper", "mcdu.json");
    private McduInput? keyInput;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private int testRequested;

    public McduForm(bool autoStart = false)
    {
        Text = "Sharklet • WINCTRL MCDU";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Theme.ApplyWindow(this);
        Size = new Size(780, 940);
        MinimumSize = new Size(740, 700);
        AutoScroll = true;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "WINCTRL MCDU-32", Font = Theme.DisplayFont(20), AutoSize = true });
        layout.Controls.Add(new Label { Text = "Close SimAppPro / SimLogic before USB output. Tailwind reads screen data from local Roblox logs.\nKeys use a virtual Xbox controller and require ViGEmBus. Roblox must have focus.", AutoSize = true, MaximumSize = new Size(710, 0), ForeColor = Theme.MutedText });
        layout.Controls.Add(settingsPanel);
        side.Items.AddRange(["Captain / MCDU 1", "First officer / MCDU 2"]);
        Row("Aircraft unit", side);
        Row("USB screen", usb);
        Row("Button device", keyboard);
        var refresh = new SharkletButton { Text = "Refresh devices" };
        refresh.Click += (_, _) => Guard(RefreshDevices);
        Row("", refresh);
        var browse = new SharkletButton { Text = "Browse font" };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "SimAppPro font (*.wwf)|*.wwf", FileName = fontPath.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) fontPath.Text = dialog.FileName;
        };
        Row("SimAppPro .wwf font", fontPath, browse);
        Row("Roblox log folder", logDirectory);
        Row("Screen offset", new Label { Text = "X", AutoSize = true }, x, new Label { Text = "Y", AutoSize = true }, y);
        Row("", screenOutput, keyOutput);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(start);
        buttons.Controls.Add(test);
        layout.Controls.Add(buttons);
        layout.Controls.Add(status);
        layout.Controls.Add(preview);
        Theme.Apply(this);
        start.Click += (_, _) => Guard(() => { if (cancellation == null) StartBridge(); else StopBridge(); });
        test.Click += (_, _) => Interlocked.Exchange(ref testRequested, 1);
        timer.Tick += (_, _) =>
        {
            try { keyInput?.Update(inputs.Read()); }
            catch (Exception ex) { StopBridge(); status.Text = $"Stopped: {ex.Message}"; }
        };
        Shown += (_, _) => Guard(() => { LoadSettings(); RefreshDevices(); if (autoStart) StartBridge(); });
        FormClosing += (_, _) => { StopBridge(); inputs.Dispose(); timer.Dispose(); };
    }

    private void Row(string title, params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 4, 0, 4) };
        if (title != "") row.Controls.Add(new Label { Text = title, AutoSize = true, Width = 140, Padding = new Padding(0, 6, 0, 0) });
        row.Controls.AddRange(controls);
        settingsPanel.Controls.Add(row);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private void LoadSettings()
    {
        McduSettings settings = new();
        if (File.Exists(settingsPath))
        {
            try { settings = JsonSerializer.Deserialize<McduSettings>(File.ReadAllText(settingsPath)) ?? settings; }
            catch (Exception ex) { status.Text = $"MCDU settings could not be read: {ex.Message}"; }
        }
        side.SelectedIndex = settings.Side == 2 ? 1 : 0;
        fontPath.Text = settings.FontPath;
        logDirectory.Text = settings.LogDirectory;
        x.Value = Math.Clamp(settings.X, 0, 200);
        y.Value = Math.Clamp(settings.Y, 0, 200);
        screenOutput.Checked = settings.ScreenOutput;
        keyOutput.Checked = settings.KeyOutput;
    }

    private void RefreshDevices()
    {
        inputs.Refresh(Handle);
        usb.DataSource = McduDisplay.FindDevices();
        usb.DisplayMember = nameof(McduUsbDevice.Label);
        // Product GUID starts with the little-endian HID PID/VID. Do not auto-bind other WINCTRL panels.
        keyboard.DataSource = inputs.Controllers.Where(d => McduDisplay.IsMcduController(d.ProductGuid)).ToList();
        keyboard.DisplayMember = nameof(InputDeviceInfo.Name);
        status.Text = usb.Items.Count == 0 ? "MCDU-32 not detected. Connect its USB cable and refresh. Turn off both outputs to monitor logs only."
            : "MCDU detected. Start the bridge, then load Tailwind in Roblox.";
    }

    private void StartBridge()
    {
        var settings = new McduSettings { Side = side.SelectedIndex + 1, FontPath = fontPath.Text,
            LogDirectory = logDirectory.Text, X = (int)x.Value, Y = (int)y.Value,
            ScreenOutput = screenOutput.Checked, KeyOutput = keyOutput.Checked };
        var device = usb.SelectedItem as McduUsbDevice;
        if (settings.ScreenOutput)
        {
            if (device == null) throw new InvalidOperationException("Connect an MCDU-32 and refresh devices, or turn off USB screen output.");
            McduDisplay.CheckSimAppProClosed();
            McduDisplay.ReadFonts(settings.FontPath, settings.X, settings.Y);
        }
        if (settings.KeyOutput && keyboard.SelectedItem is not InputDeviceInfo)
            throw new InvalidOperationException("Select the MCDU button device, or turn off key output.");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        if (settings.KeyOutput) keyInput = new McduInput(((InputDeviceInfo)keyboard.SelectedItem!).Id, settings.Side);
        var source = new CancellationTokenSource();
        cancellation = source;
        Interlocked.Exchange(ref testRequested, 0);
        settingsPanel.Enabled = false;
        start.Text = "Stop MCDU bridge";
        test.Enabled = settings.ScreenOutput;
        status.Text = "Starting MCDU bridge...";
        worker = Task.Run(() => RunBridge(settings, device, source));
        timer.Start();
    }

    private void RunBridge(McduSettings settings, McduUsbDevice? device, CancellationTokenSource source)
    {
        try
        {
            using var reader = new McduLogReader(settings.Side, settings.LogDirectory);
            using var display = settings.ScreenOutput ? new McduDisplay(device!.Path, settings.FontPath, settings.X, settings.Y, source.Token) : null;
            var blank = new McduFrame { Side = settings.Side, Brightness = 0 };
            McduFrame? latest = null;
            long testUntil = 0;
            while (!source.Token.WaitHandle.WaitOne(100))
            {
                var frame = reader.Poll();
                if (frame != null) latest = frame;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (Interlocked.Exchange(ref testRequested, 0) != 0) testUntil = now + 5000;
                var testing = now < testUntil;
                var current = testing ? McduDisplayProtocol.TestFrame(settings.Side) : latest ?? blank;
                display?.Send(current);
                var cells = McduDisplayProtocol.Cells(current);
                var text = string.Join(Environment.NewLine, Enumerable.Range(0, 14).Select(row => string.Concat(cells.Skip(row * 24).Take(24).Select(c => c.Text))));
                var message = testing ? "Screen and annunciator test. Live Roblox output resumes in 5 seconds."
                    : latest != null ? $"MCDU {settings.Side} / frame {latest.Sequence} / brightness {latest.Brightness:0}\n{Path.GetFileName(reader.CurrentLogPath)}"
                    : "Waiting for a Tailwind screen change. Load an aircraft or change its MCDU page.\nUnchanged pages stay on screen without repeated log messages.";
                Post(source, () => { status.Text = message; if (preview.Text != text) preview.Text = text; });
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Post(source, () => { StopBridge(); status.Text = $"Stopped: {ex.Message}"; });
        }
    }

    private void Post(CancellationTokenSource source, Action action)
    {
        if (source.IsCancellationRequested || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (cancellation == source && !IsDisposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    private void StopBridge()
    {
        timer.Stop();
        var source = cancellation;
        cancellation = null;
        source?.Cancel();
        try { keyInput?.Dispose(); }
        catch (Exception ex) { status.Text = ex.Message; }
        keyInput = null;
        // USB work stays off the flight-control timer. Writes have a one-second timeout.
        if (worker != null && !worker.Wait(3000))
        {
            status.Text = "Waiting for the USB connection to close...";
            start.Enabled = false;
            var finishing = worker;
            _ = finishing.ContinueWith(_ =>
            {
                source?.Dispose();
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(() => { start.Enabled = true; status.Text = "MCDU bridge stopped."; }); }
                catch (InvalidOperationException) { }
            });
        }
        else source?.Dispose();
        worker = null;
        settingsPanel.Enabled = true;
        test.Enabled = false;
        start.Text = "Start MCDU bridge";
        if (start.Enabled) status.Text = "MCDU bridge stopped.";
    }
}
