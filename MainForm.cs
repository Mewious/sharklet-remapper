using System.Diagnostics;
using System.Globalization;

namespace TailwindRemapper;

public sealed class MainForm : Form
{
    private readonly InputDevices inputs = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly DataGridView grid = new();
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1120, 0) };
    private readonly Label monitor = new() { AutoSize = true, MaximumSize = new Size(1120, 0), ForeColor = Theme.MutedText };
    private readonly Label volantaStatus = new() { AutoSize = true, MaximumSize = new Size(1120, 0), ForeColor = Theme.MutedText, Text = "Volanta tracking is off." };
    private readonly SharkletButton volanta = new() { Text = "Connect Volanta", Primary = true };
    private readonly VolantaBridge volantaBridge = new();
    private readonly FlowLayoutPanel updateBanner = new() { AutoSize = true, WrapContents = false, Visible = false, BackColor = Theme.Selection, Padding = new Padding(10, 8, 2, 0), Margin = new Padding(0, 0, 0, 12) };
    private readonly Label updateText = new() { AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
    private readonly SharkletButton updateButton = new() { Text = "Update now", Primary = true };
    private readonly SharkletButton start = new() { Text = "Start Xbox output", Primary = true };
    private readonly FlowLayoutPanel editButtons = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly string profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailwindRemapper", "profile.json");
    private Profile profile = new();
    private XboxOutput? output;
    private XboxOutput? auxiliaryOutput;
    private Dictionary<string, double> readings = [];
    private Dictionary<string, double>? learnBaseline;
    private int learningRow = -1;
    private DateTime learnDeadline;
    private int tickCount;
    private McduForm? mcduForm;

    public MainForm()
    {
        Text = "Sharklet • Flight hardware";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Theme.ApplyWindow(this);
        MinimumSize = new Size(1040, 700);
        Size = new Size(1250, 850);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        updateBanner.Controls.Add(updateText);
        updateButton.Click += async (_, _) => await InstallUpdate();
        updateBanner.Controls.Add(updateButton);
        AddButton(updateBanner, "Later", () => updateBanner.Visible = false);
        layout.Controls.Add(updateBanner);
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
        header.Controls.Add(new PictureBox { Image = Icon == null ? null : new Icon(Icon, 64, 64).ToBitmap(), Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 10, 0) });
        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        titles.Controls.Add(new Label { Text = "Sharklet", Font = Theme.DisplayFont(24), AutoSize = true, Margin = new Padding(0) });
        titles.Controls.Add(new Panel { BackColor = Theme.Accent, Size = new Size(54, 3), Margin = new Padding(3, 0, 0, 4) });
        titles.Controls.Add(new Label { Text = "Flight hardware → Xbox controller", ForeColor = Theme.MutedText, AutoSize = true, Margin = new Padding(2, 0, 0, 0) });
        header.Controls.Add(titles);
        layout.Controls.Add(header);
        layout.Controls.Add(editButtons);
        AddButton(editButtons, "Refresh devices", RefreshDevices);
        AddButton(editButtons, "Learn selected input", BeginLearn);
        AddButton(editButtons, "Clear binding", () => { CancelLearn(); grid.CurrentRow?.Cells["Source"].SetValue(""); });
        AddButton(editButtons, "Capture min", () => CaptureCalibration("Minimum"));
        AddButton(editButtons, "Capture center", () => CaptureCalibration("Center"));
        AddButton(editButtons, "Capture max", () => CaptureCalibration("Maximum"));
        AddButton(editButtons, "Save profile", () => { ReadGrid().Write(profilePath); SetStatus("Profile saved. It will load next time."); });
        AddButton(editButtons, "Import…", ImportProfile);
        AddButton(editButtons, "Export…", ExportProfile);
        AddButton(editButtons, "Share profile", ShareProfile);
        SetupGrid();
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        card.Controls.Add(grid);
        layout.Controls.Add(card);
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(1120, 0), Margin = new Padding(0, 10, 0, 10), ForeColor = Theme.MutedText,
            Text = "Capture the lowest raw value at Min, neutral at Center, and highest at Max. Use Invert to reverse an axis or switch. LT / RT, brakes, flaps and spoilers use Min and Max only. Flaps: 0 retracted, 1 FULL. Spoilers: 0 retracted, 1 full. Switches: 1 on/armed."
        });
        var outputButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        start.Click += (_, _) => Guard(ToggleOutput);
        outputButtons.Controls.Add(start);
        volanta.Click += (_, _) => Guard(ToggleVolanta);
        outputButtons.Controls.Add(volanta);
        AddButton(outputButtons, "WINCTRL MCDU", () =>
        {
            if (mcduForm == null || mcduForm.IsDisposed) mcduForm = new McduForm();
            mcduForm.Show(this);
            mcduForm.Activate();
        });
        AddButton(outputButtons, "Windows controller test", () => Process.Start(new ProcessStartInfo("control.exe", "joy.cpl") { UseShellExecute = true }));
        AddButton(outputButtons, "Official driver download", () =>
        {
            MessageBox.Show(this, "Xbox output requires ViGEmBus. This driver is retired and receives no updates. Use only the publisher's GitHub release. Install it yourself, restart Windows if requested, then start output. The app never installs drivers automatically.", "ViGEmBus", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Process.Start(new ProcessStartInfo("https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0") { UseShellExecute = true });
        });
        layout.Controls.Add(outputButtons);
        var footer = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        footer.Controls.Add(status);
        footer.Controls.Add(monitor);
        footer.Controls.Add(volantaStatus);
        layout.Controls.Add(footer);
        Theme.Apply(this);
        Shown += (_, _) => Guard(() =>
        {
            if (File.Exists(profilePath))
            {
                try { profile = Profile.Read(profilePath); }
                catch (Exception ex) { MessageBox.Show(this, $"Your saved profile could not be loaded. It has not been changed.\n\n{ex.Message}"); }
            }
            RefreshDevices();
            timer.Start();
            _ = CheckForUpdate();
        });
        timer.Tick += (_, _) => Tick();
        volantaBridge.StatusChanged += text => volantaStatus.Text = text;
        FormClosing += (_, _) => { timer.Stop(); StopOutput(); volantaBridge.Dispose(); inputs.Dispose(); timer.Dispose(); };
    }

    private void AddButton(FlowLayoutPanel parent, string text, Action action)
    {
        var button = new SharkletButton { Text = text };
        button.Click += (_, _) => Guard(action);
        parent.Controls.Add(button);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Sharklet", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SetStatus(string text) => status.Text = text;

    private async Task CheckForUpdate()
    {
        var update = await UpdateChecker.Check();
        if (update == null || IsDisposed) return;
        updateButton.Tag = update;
        updateText.Text = $"Sharklet {update.Version} is out. You have {UpdateChecker.CurrentVersion}.";
        updateBanner.Visible = true;
    }

    private async Task InstallUpdate()
    {
        if (updateButton.Tag is not UpdateInfo update) return;
        updateButton.Enabled = false;
        updateText.Text = $"Downloading Sharklet {update.Version}…";
        try
        {
            await UpdateChecker.Install(update);
            Close();
        }
        catch (Exception ex)
        {
            updateText.Text = "The update didn't download.";
            updateButton.Enabled = true;
            MessageBox.Show(this, ex.Message, "Sharklet update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleVolanta()
    {
        if (volantaBridge.IsRunning) volantaBridge.Stop();
        else volantaBridge.Start();
        volanta.Text = volantaBridge.IsRunning ? "Disconnect Volanta" : "Connect Volanta";
    }

    private void SetupGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Theme.Card;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Theme.CardBorder;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 38;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card, ForeColor = Theme.MutedText, SelectionBackColor = Theme.Card, SelectionForeColor = Theme.MutedText,
            Font = Theme.DisplayFont(8.5f), Padding = new Padding(4, 0, 0, 0)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card, ForeColor = Theme.Text, SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Text,
            Font = Theme.Body, Padding = new Padding(4, 0, 0, 0)
        };
        grid.RowTemplate.Height = 30;
        grid.EditingControlShowing += (_, e) =>
        {
            e.CellStyle.BackColor = Theme.Field;
            e.CellStyle.ForeColor = Theme.Text;
            if (e.Control is ComboBox combo) Theme.DarkScrollbars(combo);
        };
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Source" || e.Graphics == null) return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.All);
            var x = e.CellBounds.Right - 16;
            var y = e.CellBounds.Top + e.CellBounds.Height / 2 - 2;
            using var chevron = new Pen(Theme.MutedText, 1.5f);
            var smoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(chevron, [new PointF(x, y), new PointF(x + 4, y + 4), new PointF(x + 8, y)]);
            e.Graphics.SmoothingMode = smoothing;
            e.Handled = true;
        };
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Source" || grid.ReadOnly) return;
            grid.BeginEdit(true);
            if (grid.EditingControl is ComboBox combo) combo.DroppedDown = true;
        };
        grid.AddText("Target", "Xbox / Tailwind", 150, true);
        grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Source", HeaderText = "Hardware input", FillWeight = 330, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, FlatStyle = FlatStyle.Flat, DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(4, 0, 24, 0) } });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Invert", HeaderText = "Invert", FillWeight = 45, FlatStyle = FlatStyle.Flat });
        grid.AddText("Minimum", "Min", 65);
        grid.AddText("Center", "Center", 65);
        grid.AddText("Maximum", "Max", 65);
        grid.AddText("Deadzone", "Deadzone", 70);
        grid.AddText("Curve", "Curve", 55);
        grid.AddText("Raw", "Raw", 65, true);
        grid.AddText("Output", "Output", 65, true);
        foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.DataError += (_, e) => { e.ThrowException = false; SetStatus("That value is invalid. Choose an available input or enter a number."); };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    private void FillGrid()
    {
        grid.Rows.Clear();
        foreach (var binding in profile.Bindings)
        {
            var description = binding.Target switch
            {
                "LX" => "LX · Roll", "LY" => "LY · Pitch", "RX" => "RX · Rudder",
                "RY" => "RY · Combined brakes", "LT" => "LT · Left throttle", "RT" => "RT · Right throttle",
                "BrakeL" => "Brake L → RY", "BrakeR" => "Brake R → RY",
                "Flaps" => "Flaps · Lever", "Spoilers" => "Spoilers · Lever",
                "ParkingBrake" => "Parking brake · Switch", "SpoilersArm" => "Spoilers arm · Switch",
                "EngineMaster1" => "ENG MASTER 1 · Switch", "EngineMaster2" => "ENG MASTER 2 · Switch",
                "IgnitionCrank" => "Ignition · CRANK", "IgnitionStart" => "Ignition · IGN/START",
                "GearToggle" => "Landing gear · Toggle", _ => binding.Target
            };
            var row = grid.Rows[grid.Rows.Add(description, null, binding.Invert, binding.Minimum, binding.Center, binding.Maximum, binding.Deadzone, binding.Curve, "—", "—")];
            row.Tag = binding.Target;
            var sourceCell = (DataGridViewComboBoxCell)row.Cells["Source"];
            var choices = inputs.Sources.Where(s => s.IsAxis == binding.IsAxis).Prepend(new InputSource("", "Unassigned", binding.IsAxis)).ToList();
            if (binding.Source != "" && choices.All(s => s.Id != binding.Source))
                choices.Add(new(binding.Source, $"Missing: {binding.SourceLabel}", binding.IsAxis));
            sourceCell.DisplayMember = "Label";
            sourceCell.ValueMember = "Id";
            sourceCell.DataSource = choices;
            sourceCell.Value = binding.Source;
            if (!binding.IsAxis)
                foreach (var name in new[] { "Minimum", "Center", "Maximum", "Deadzone", "Curve" })
                {
                    row.Cells[name].ReadOnly = true;
                    row.Cells[name].Style.ForeColor = Theme.DisabledText;
                    row.Cells[name].Style.SelectionForeColor = Theme.MutedText;
                }
        }
    }

    private Profile ReadGrid(bool endEdit = true)
    {
        if (endEdit) grid.EndEdit();
        var result = new Profile { Bindings = [] };
        foreach (DataGridViewRow row in grid.Rows)
        {
            string TextOf(string name) => Convert.ToString(row.Cells[name].Value, CultureInfo.CurrentCulture) ?? "";
            double Number(string name) => double.TryParse(TextOf(name), out var value) ? value : throw new InvalidDataException($"{row.Tag}: enter a number for {name}.");
            var source = TextOf("Source");
            result.Bindings.Add(new Binding
            {
                Target = (string)row.Tag!, Source = source,
                SourceLabel = inputs.Sources.FirstOrDefault(s => s.Id == source)?.Label ?? profile.Bindings.First(b => b.Target == (string)row.Tag!).SourceLabel,
                Invert = row.Cells["Invert"].Value is true,
                Minimum = Number("Minimum"), Center = Number("Center"), Maximum = Number("Maximum"),
                Deadzone = Number("Deadzone"), Curve = Number("Curve")
            });
        }
        result.Validate();
        return result;
    }

    private void RefreshDevices()
    {
        CancelLearn();
        if (grid.Rows.Count > 0) profile = ReadGrid();
        inputs.Refresh(Handle);
        readings = inputs.Read();
        FillGrid();
        SetStatus(inputs.Count == 0 ? "No flight controllers detected. Connect the devices, then click Refresh devices." : $"{inputs.Count} input device(s) ready. Xbox output is stopped.");
        if (inputs.Messages.Count > 0) SetStatus(string.Join(" | ", inputs.Messages));
    }

    private void BeginLearn()
    {
        if (grid.CurrentRow == null) return;
        readings = inputs.Read();
        learnBaseline = new(readings);
        learningRow = grid.CurrentRow.Index;
        learnDeadline = DateTime.UtcNow.AddSeconds(10);
        SetStatus($"Learning {grid.CurrentRow.Tag}: move just one control. Listening for 10 seconds…");
    }

    private void CancelLearn() { learningRow = -1; learnBaseline = null; }

    private void CaptureCalibration(string field)
    {
        if (grid.CurrentRow is not { } row) return;
        if (row.Cells[field].ReadOnly) return;
        var source = row.Cells["Source"].Value as string ?? "";
        if (!readings.TryGetValue(source, out var value)) throw new InvalidOperationException("Bind a connected axis first.");
        row.Cells[field].Value = value;
        SetStatus($"Captured {field} = {value:0} for {row.Tag}.");
    }

    private void ToggleOutput()
    {
        if (output != null) { StopOutput(); SetStatus("Stopped. Xbox controller disconnected."); return; }
        CancelLearn();
        profile = ReadGrid();
        var assigned = profile.Bindings.Where(b => b.Source != "").ToList();
        if (assigned.Count == 0) throw new InvalidOperationException("Bind at least one input before starting.");
        readings = inputs.Read();
        foreach (var binding in assigned)
        {
            if (!readings.ContainsKey(binding.Source)) throw new InvalidOperationException($"{binding.Target}: the assigned hardware is missing. Reconnect it and refresh devices.");
            if (!inputs.Sources.Any(s => s.Id == binding.Source && s.IsAxis == binding.IsAxis)) throw new InvalidDataException($"{binding.Target}: bind an {(binding.IsAxis ? "axis" : "input button or hat")}.");
        }
        profile.Write(profilePath);
        try
        {
            output = new XboxOutput();
            if (assigned.Any(b => b.IsAuxiliary)) auxiliaryOutput = new XboxOutput();
        }
        catch (Exception ex)
        {
            StopOutput();
            throw new InvalidOperationException($"Could not create the Xbox controller. Install the official ViGEmBus driver using the download button, then retry.\n\n{ex.Message}", ex);
        }
        grid.ReadOnly = true;
        editButtons.Enabled = false;
        start.Text = "Stop Xbox output";
        SetStatus("Running. Keep this app open while playing. Extra flight controls use a second Xbox controller detected by Tailwind. A missing device stops all output.");
    }

    private void StopOutput()
    {
        var previous = output;
        var previousAuxiliary = auxiliaryOutput;
        output = null;
        auxiliaryOutput = null;
        try
        {
            try { previousAuxiliary?.Dispose(); }
            finally { previous?.Dispose(); }
        }
        catch (Exception ex) { SetStatus($"Output stopped: {ex.Message}"); }
        grid.ReadOnly = false;
        editButtons.Enabled = true;
        start.Text = "Start Xbox output";
    }

    private void Tick()
    {
        try
        {
            readings = inputs.Read();
            if (learningRow >= 0 && learnBaseline != null)
            {
                var row = grid.Rows[learningRow];
                var isAxis = new Binding { Target = (string)row.Tag! }.IsAxis;
                var moved = inputs.Sources.Where(s => s.IsAxis == isAxis && readings.ContainsKey(s.Id) && learnBaseline.ContainsKey(s.Id))
                    .Select(s => (Source: s, Delta: Math.Abs(readings[s.Id] - learnBaseline[s.Id]) / (s.IsAxis ? 65535 : 1)))
                    .OrderByDescending(s => s.Delta).FirstOrDefault();
                if (moved.Source != null && moved.Delta > 0.25 && (isAxis || readings[moved.Source.Id] > 0))
                {
                    row.Cells["Source"].Value = moved.Source.Id;
                    SetStatus($"Bound {row.Tag} to {moved.Source.Label}.");
                    CancelLearn();
                }
                else if (DateTime.UtcNow > learnDeadline) { CancelLearn(); SetStatus("No input learned. Try again or choose an input from the list."); }
            }
            var current = output != null ? profile : ReadGrid(false);
            var mapped = new Dictionary<string, double>();
            foreach (var binding in current.Bindings)
            {
                if (binding.Source == "") continue;
                if (!readings.TryGetValue(binding.Source, out var raw))
                {
                    if (output != null) { StopOutput(); SetStatus($"Stopped: {binding.SourceLabel} disconnected. Reconnect, refresh, and start again."); return; }
                    continue;
                }
                mapped[binding.Target] = binding.Map(raw);
            }
            var xboxValues = new Dictionary<string, double>(mapped);
            Profile.CombineBrakes(xboxValues);
            output?.Send(xboxValues);
            auxiliaryOutput?.Send(Profile.AuxiliaryValues(mapped, Environment.TickCount64 / 250 % 2 == 0));
            if (++tickCount % 6 != 0) return;
            foreach (DataGridViewRow row in grid.Rows)
            {
                var binding = current.Bindings.First(b => b.Target == (string)row.Tag!);
                row.Cells["Raw"].Value = readings.TryGetValue(binding.Source, out var raw) ? raw.ToString("0") : "—";
                var displayedValues = binding.Target == "RY" ? xboxValues : mapped;
                row.Cells["Output"].Value = displayedValues.TryGetValue(binding.Target, out var value) ? value.ToString("0.000") : "—";
            }
            monitor.Text = grid.CurrentRow is { } selected ? $"Selected: {selected.Cells["Source"].FormattedValue}    Raw: {selected.Cells["Raw"].Value}    Xbox: {selected.Cells["Output"].Value}" : "";
        }
        catch (Exception ex)
        {
            if (output != null) StopOutput();
            SetStatus(ex.Message);
        }
    }

    private void ImportProfile()
    {
        using var dialog = new OpenFileDialog { Filter = "Tailwind profile (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var imported = Profile.Read(dialog.FileName);
        CancelLearn();
        profile = imported;
        FillGrid();
        SetStatus("Profile imported. Save it to use it next time.");
    }

    // Puts the profile on the clipboard as a file, so it pastes straight into Discord or an email.
    private void ShareProfile()
    {
        var path = Path.Combine(Path.GetDirectoryName(profilePath)!, "Share", "Sharklet profile.json");
        ReadGrid().Write(path);
        var files = new System.Collections.Specialized.StringCollection { path };
        Clipboard.SetFileDropList(files);
        SetStatus("Profile copied. Paste it (Ctrl+V) into Discord or anywhere you can send a file. They load it with Import…");
    }

    private void ExportProfile()
    {
        using var dialog = new SaveFileDialog { Filter = "Tailwind profile (*.json)|*.json", FileName = "flight-controls.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { ReadGrid().Write(dialog.FileName); SetStatus("Profile exported."); }
    }
}

internal static class GridHelpers
{
    public static void AddText(this DataGridView grid, string name, string title, float weight, bool readOnly = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, ReadOnly = readOnly });
    public static void SetValue(this DataGridViewCell cell, object value) => cell.Value = value;
}
