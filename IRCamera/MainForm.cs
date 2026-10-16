using System.Diagnostics;

namespace IRCamera;

internal sealed record UpscalingOption(string Label, int Factor)
{
    public override string ToString() => Label;
}

internal sealed class MainForm : Form
{
    private const string PointerHint = "Hover for temperature · Right-click for actions";
    private readonly ThermalCanvas canvas = new() { Name = "ThermalCanvas" };
    private readonly Button connect = new() { Name = "ConnectCamera", Text = "Connect", AutoSize = true };
    private readonly Button freeze = new() { Name = "FreezeView", Text = "Freeze", AutoSize = true, Enabled = false };
    private readonly CheckBox autoCalibration = new()
    {
        Name = "AutoCalibration", Text = "Auto calibration", AutoSize = true,
        Enabled = false, CheckState = CheckState.Indeterminate, Margin = new Padding(8, 7, 3, 3),
        AccessibleName = "Automatic camera calibration"
    };
    private readonly CheckBox trackExtrema = new()
    {
        Name = "TrackExtrema", Text = "Track min/max", AutoSize = true, Checked = true,
        Margin = new Padding(8, 7, 3, 3), AccessibleName = "Track lowest and highest temperatures"
    };
    private readonly CheckBox fadeExtrema = new()
    {
        Name = "FadeExtrema", Text = "Fade min/max", AutoSize = true, Checked = true,
        Margin = new Padding(8, 7, 3, 3), AccessibleName = "Fade previous minimum and maximum positions"
    };
    private readonly ComboBox palette = new PaletteComboBox { Name = "Palette", Width = 125 };
    private readonly ComboBox upscaling = new() { Name = "Upscaling", DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly ComboBox filter = new() { Name = "Filter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly TrackBar sharpening = new()
    {
        Name = "Sharpening", Minimum = 0, Maximum = 100, Value = 50,
        TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 26,
        SmallChange = 5, LargeChange = 10, AccessibleName = "Sharpening strength"
    };
    private readonly Label status = new BufferedLabel { Name = "ConnectionStatus", AutoEllipsis = true, Dock = DockStyle.Fill, Text = "Disconnected", Padding = new Padding(8) };
    private readonly Label pointer = new BufferedLabel { Name = "PixelTemperature", AutoEllipsis = true, Dock = DockStyle.Fill, Text = PointerHint, Padding = new Padding(8, 0, 8, 6) };
    private readonly Label notice = new BufferedLabel { Name = "CalibrationNotice", AutoEllipsis = true, Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 8) };
    private readonly TemperatureReadout[] temperatureLabels = new TemperatureReadout[4];
    private Label? measurementCaption;
    private static Color SurfaceColor => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(247, 248, 250);
    private static Color ReadingColor => SystemInformation.HighContrast ? SystemColors.Window : Color.White;
    private static Color InkColor => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(38, 49, 61);
    private readonly Font temperatureFont = new("Segoe UI", 15F, FontStyle.Bold);
    private readonly Font readingCaptionFont = new("Segoe UI", 8F);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };
    private readonly ToolTip tips = new() { InitialDelay = 1000, ReshowDelay = 500, AutoPopDelay = 6000 };
    private readonly ContextMenuStrip viewMenu = new();
    private readonly Stopwatch sinceFrame = new();
    private readonly Stopwatch frameRate = new();
    private readonly SessionDiagnostics diagnostics = new();
    private readonly Func<SessionDiagnostics, CameraSession> sessionFactory;
    private readonly bool showErrorDialogs;
    private CameraSession? session;
    private CameraSession? retiringSession;
    private bool connected;
    private bool connecting;
    private readonly CalibrationRetryPolicy calibrationRetries = new();
    private bool closing;
    private sealed record PendingFrame(CameraSession Owner, CameraFrame Frame);
    private PendingFrame? pending;
    private ThermalFrame? current;
    private Thermometry? thermometry;
    private TemperatureFrame? temperatures;
    private TemperatureSettings temperatureSettings = new();
    private string? temperatureError;
    private string? lastMeasurementError;
    private Point? hoverPoint;
    private int rotation;
    private bool frozen;
    private int frames;
    private double fps;
    private bool updatingAutoCalibration;
    private bool? confirmedAutoCalibration;
    private bool updatingImageOptions;

    public bool HasLiveFrame => current is not null && connected;
    public bool HasTemperatureFrame => temperatures is not null && HasLiveFrame;
    public int TemperatureFramesReceived { get; private set; }
    public string DiagnosticSummary => diagnostics.Snapshot();

    public MainForm(Func<SessionDiagnostics, CameraSession>? sessionFactory = null, bool showErrorDialogs = true)
    {
        this.sessionFactory = sessionFactory ?? (log => new CameraSession(log));
        this.showErrorDialogs = showErrorDialogs;
        Text = "GW192A Thermal Camera Viewer";
        ClientSize = new Size(590, 685);
        MinimumSize = new Size(480, 530);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = SurfaceColor;
        ForeColor = InkColor;

        var root = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // Dynamic text must never take space from the image or resize the window.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, readingCaptionFont.Height + temperatureFont.Height + 24));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height * 2 + notice.Padding.Vertical + notice.Margin.Vertical));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 18));
        Controls.Add(root);

        var toolbar = new BufferedTableLayoutPanel
        {
            Name = "ViewerControls", Dock = DockStyle.Fill, Padding = new Padding(6),
            ColumnCount = 1, Margin = Padding.Empty
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var controlRows = new List<FlowLayoutPanel>();
        FlowLayoutPanel ControlRow(string name, string title)
        {
            var group = new ControlGroupBox
            {
                Name = name + "Group", AccessibleName = title + " controls", Dock = DockStyle.Fill,
                Text = title, Margin = new Padding(0, 0, 0, 2), Padding = new Padding(6, 0, 6, 4),
                TabStop = false, TabIndex = controlRows.Count
            };
            var row = new BufferedFlowLayoutPanel
            {
                Name = name, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty,
                BackColor = ReadingColor
            };
            group.Controls.Add(row);
            controlRows.Add(row);
            toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            toolbar.Controls.Add(group);
            return row;
        }
        var cameraRow = ControlRow("CameraControls", "Camera");
        cameraRow.Parent!.Text = "Camera · Rated range: −20 to 400 °C";
        var imageRow = ControlRow("ImageControls", "Image");
        var trackingRow = ControlRow("TrackingControls", "Tracking");
        void ConstrainRows()
        {
            palette.Width = 105 * DeviceDpi / 96;
            sharpening.Width = 90 * DeviceDpi / 96;
            var totalHeight = toolbar.Padding.Vertical;
            for (var i = 0; i < controlRows.Count; i++)
            {
                var row = controlRows[i];
                var group = (GroupBox)row.Parent!;
                var width = Math.Max(1, root.ClientSize.Width - toolbar.Padding.Horizontal - group.Padding.Horizontal);
                row.MaximumSize = new Size(width, 0);
                var height = row.GetPreferredSize(new Size(width, 0)).Height
                    + group.Font.Height + group.Padding.Vertical + group.Margin.Vertical;
                toolbar.RowStyles[i].Height = height;
                totalHeight += height;
            }
            root.RowStyles[0].Height = totalHeight;
        }
        root.ClientSizeChanged += (_, _) => ConstrainRows();
        ConstrainRows();
        var rotate = new Button { Text = "Rotate", AutoSize = true };
        connect.MinimumSize = new Size(Math.Max(75, TextRenderer.MeasureText("Disconnect", Font).Width + 18), 0);
        freeze.MinimumSize = new Size(Math.Max(75, TextRenderer.MeasureText("Resume", Font).Width + 18), 0);
        var temperatureOptions = new Button { Text = "Temperature…", Name = "TemperatureSettings", AutoSize = true };
        foreach (var button in new[] { connect, freeze, rotate, temperatureOptions })
        {
            if (!SystemInformation.HighContrast)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(202, 208, 216);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 237, 244);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(215, 227, 238);
                button.BackColor = Color.White;
                button.ForeColor = InkColor;
            }
        }
        if (!SystemInformation.HighContrast)
        {
            connect.BackColor = Color.FromArgb(234, 242, 251);
            connect.ForeColor = Color.FromArgb(36, 77, 120);
            connect.FlatAppearance.BorderColor = Color.FromArgb(166, 190, 215);
        }
        var sharpeningLabel = new Label { Name = "SharpeningCaption", Text = "Sharpen" };
        var sharpeningValue = new BufferedLabel
        {
            Name = "SharpeningValue", Text = "50%", BackColor = ReadingColor,
            TextAlign = ContentAlignment.MiddleRight, Bounds = new Rectangle(148, 0, 35, 26),
            AccessibleName = "Sharpening percentage"
        };
        palette.AccessibleName = "Display palette";
        filter.AccessibleName = "Image resampling filter";
        upscaling.AccessibleName = "Edge-aware upscaling";
        palette.Items.AddRange(Enum.GetNames<ThermalPalette>());
        palette.SelectedIndex = 0;
        upscaling.Items.AddRange([
            new UpscalingOption("xBRZ off", 1),
            new UpscalingOption("xBRZ 2×", 2),
            new UpscalingOption("xBRZ 6×", 6)
        ]);
        upscaling.SelectedIndex = 1;
        filter.Items.AddRange(Enum.GetNames<ImageFilter>());
        filter.SelectedItem = nameof(ImageFilter.Bicubic);
        tips.SetToolTip(autoCalibration, "Connect the camera to read this setting.");
        tips.SetToolTip(fadeExtrema, "Keeps five positions at 100%, 80%, 60%, 40%, and 20% opacity. Pauses with Freeze.");
        cameraRow.Controls.AddRange([connect, freeze, rotate, temperatureOptions, autoCalibration]);
        for (var i = 0; i < cameraRow.Controls.Count; i++) cameraRow.Controls[i].TabIndex = i;
        var sharpeningControls = new Panel
        {
            Name = "SharpeningControls", Size = new Size(187, 26),
            Margin = new Padding(3), BackColor = ReadingColor, Anchor = AnchorStyles.Left
        };
        sharpeningLabel.AutoSize = false;
        sharpeningLabel.Size = new Size(56, 26);
        sharpeningLabel.TextAlign = ContentAlignment.MiddleLeft;
        sharpeningLabel.Margin = Padding.Empty;
        sharpening.Margin = Padding.Empty;
        // Native TrackBar leaves extra space below its thumb even without ticks.
        // Offset it within the caption's fixed-height cell instead of aligning
        // the top edges of controls with different visual centers.
        sharpening.Bounds = new Rectangle(56, 3, 90, 23);
        sharpeningControls.Controls.AddRange([sharpeningLabel, sharpening, sharpeningValue]);
        imageRow.Controls.AddRange([palette, filter, upscaling, sharpeningControls]);
        for (var i = 0; i < imageRow.Controls.Count; i++) imageRow.Controls[i].TabIndex = i;
        sharpening.TabIndex = 0;
        trackingRow.Controls.AddRange([trackExtrema, fadeExtrema]);
        trackExtrema.TabIndex = 0;
        fadeExtrema.TabIndex = 1;
        foreach (var check in new[] { autoCalibration, trackExtrema, fadeExtrema })
        {
            check.Margin = new Padding(3, 3, 12, 3);
            check.Anchor = AnchorStyles.Left;
        }
        foreach (var combo in new[] { palette, filter, upscaling }) combo.Anchor = AnchorStyles.Left;
        ConstrainRows();
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(canvas, 0, 1);
        toolbar.TabIndex = 0;
        canvas.TabIndex = 1;

        var readings = new BufferedTableLayoutPanel
        {
            Name = "TemperatureReadings", Dock = DockStyle.Fill, ColumnCount = 4,
            Padding = new Padding(0, 4, 0, 2), Margin = new Padding(11, 5, 11, 5), BackColor = ReadingColor
        };
        readings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        readings.Paint += (_, e) =>
        {
            using var divider = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(227, 231, 236));
            for (var column = 1; column < 4; column++)
            {
                var x = readings.ClientSize.Width * column / 4;
                e.Graphics.DrawLine(divider, x, 8, x, Math.Max(8, readings.ClientSize.Height - 8));
            }
        };
        var titles = new[] { "MINIMUM", "CENTER", "MAXIMUM", "AVERAGE" };
        for (var i = 0; i < titles.Length; i++)
        {
            readings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var reading = new BufferedTableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1, Margin = new Padding(3, 0, 3, 0), BackColor = ReadingColor
            };
            reading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            reading.RowStyles.Add(new RowStyle(SizeType.Absolute, readingCaptionFont.Height));
            reading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var caption = new Label
            {
                Text = titles[i][0] + titles[i][1..].ToLowerInvariant(), Font = readingCaptionFont,
                ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(87, 101, 116),
                Dock = DockStyle.Fill, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty
            };
            if (i == 1) measurementCaption = caption;
            reading.Controls.Add(caption, 0, 0);
            var label = new TemperatureReadout
            {
                Name = $"Temperature{titles[i]}", Text = "— °C", Font = temperatureFont,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 2, 0, 0), AccessibleName = $"{titles[i].ToLowerInvariant()} temperature",
                BackColor = ReadingColor, ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : InkColor
            };
            temperatureLabels[i] = label;
            reading.Controls.Add(label, 0, 1);
            readings.Controls.Add(reading);
        }
        root.Controls.Add(readings, 0, 2);
        notice.Text = "Connect the camera to load its factory temperature calibration.";
        root.Controls.Add(notice, 0, 3);
        var footer = new BufferedTableLayoutPanel
        {
            Name = "ViewerFooter", Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(3, 0, 3, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pointer.Margin = status.Margin = Padding.Empty;
        pointer.Padding = new Padding(8, 0, 4, 0);
        status.Padding = new Padding(4, 0, 8, 0);
        pointer.TextAlign = ContentAlignment.MiddleLeft;
        status.TextAlign = ContentAlignment.MiddleRight;
        footer.Controls.Add(pointer, 0, 0);
        footer.Controls.Add(status, 1, 0);
        root.Controls.Add(footer, 0, 4);
        notice.BackColor = pointer.BackColor = status.BackColor = SurfaceColor;
        pointer.ForeColor = InkColor;
        sharpening.BackColor = ReadingColor;
        status.TextChanged += (_, _) => status.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText
            : status.Text.StartsWith("Live", StringComparison.Ordinal) ? Color.DarkGreen
            : status.Text.StartsWith("Frozen", StringComparison.Ordinal) ? Color.FromArgb(130, 70, 0)
            : status.Text.StartsWith("Cannot", StringComparison.Ordinal) || status.Text.StartsWith("No frames", StringComparison.Ordinal) ? Color.Firebrick
            : SystemColors.GrayText;

        connect.Click += async (_, _) => { if (session is null) await ConnectAsync(); else await DisconnectAsync(); };
        freeze.Click += (_, _) =>
        {
            frozen = !frozen;
            freeze.Text = frozen ? "Resume" : "Freeze";
            UpdateFreezeAppearance();
            status.Text = frozen ? "Frozen" : "Resuming…";
        };
        palette.SelectedIndexChanged += (_, _) =>
        {
            canvas.Palette = (ThermalPalette)Math.Max(0, palette.SelectedIndex);
            UpdateTemperatureLabels();
            RenderCurrent();
        };
        upscaling.SelectedIndexChanged += (_, _) => RenderCurrent();
        rotate.Click += (_, _) => { rotation = (rotation + 1) % 4; RenderCurrent(); };
        filter.SelectedIndexChanged += (_, _) => RenderCurrent();
        sharpening.ValueChanged += (_, _) =>
        {
            sharpeningValue.Text = $"{sharpening.Value}%";
            RenderCurrent();
        };
        canvas.ClientSizeChanged += (_, _) => RenderCurrent();
        temperatureOptions.Click += (_, _) =>
        {
            using var dialog = new TemperatureSettingsForm(temperatureSettings);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            temperatureSettings = dialog.Settings;
            canvas.SetTemperatureMarkers(null, rotation, fadeExtrema.Checked);
            UpdateTemperatures();
            ShowPointer(hoverPoint);
        };
        canvas.ImageHovered += point => { hoverPoint = point; ShowPointer(point); };
        canvas.MeasurementPointChanged += _ => UpdateTemperatureLabels();
        autoCalibration.CheckedChanged += async (_, _) => await ChangeAutoCalibrationAsync();
        trackExtrema.CheckedChanged += (_, _) =>
        {
            fadeExtrema.Enabled = trackExtrema.Checked;
            UpdateTemperatureLabels();
        };
        fadeExtrema.CheckedChanged += (_, _) => UpdateTemperatureLabels();
        KeyDown += (_, e) =>
        {
            if (e.KeyData == (Keys.Control | Keys.Space)) freeze.PerformClick();
            else if (e.KeyData == (Keys.Control | Keys.R)) rotate.PerformClick();
            else if (e.KeyData == (Keys.Control | Keys.T)) temperatureOptions.PerformClick();
            else if (e.KeyData == (Keys.Control | Keys.Shift | Keys.C)) CopyTemperatureReadings();
            else return;
            e.SuppressKeyPress = true;
        };
        var resetPoint = new ToolStripMenuItem("Reset measurement point", null,
            (_, _) => canvas.SetMeasurementPoint(ThermalCanvas.DefaultMeasurementPoint)) { Name = "ResetMeasurementPoint" };
        viewMenu.Items.Add(resetPoint);
        var copyReadings = new ToolStripMenuItem("Copy temperature readings", null, (_, _) => CopyTemperatureReadings())
        {
            Name = "CopyTemperatureReadings", ShortcutKeyDisplayString = "Ctrl+Shift+C"
        };
        viewMenu.Items.Add(copyReadings);
        viewMenu.Items.Add(new ToolStripMenuItem("Reset image adjustments", null, (_, _) => ResetImageAdjustments()) { Name = "ResetImageAdjustments" });
        viewMenu.Items.Add(new ToolStripSeparator());
        viewMenu.Items.Add(new ToolStripMenuItem("Copy diagnostics", null, (_, _) => CopyDiagnostics()) { Name = "CopyDiagnostics" });
        viewMenu.Opening += (_, _) =>
        {
            copyReadings.Enabled = temperatures is not null;
            resetPoint.Enabled = canvas.MeasurementPoint != ThermalCanvas.DefaultMeasurementPoint;
        };
        canvas.ContextMenuStrip = viewMenu;
        timer.Tick += (_, _) => UpdateFrame();
        Shown += async (_, _) => await ConnectAsync();
        FormClosing += async (_, e) =>
        {
            if (closing || session is null) return;
            e.Cancel = true;
            closing = true;
            await DisconnectAsync();
            Close();
        };
        timer.Start();
    }

    private async Task ConnectAsync()
    {
        if (connecting || retiringSession is not null || closing) return;
        connecting = true;
        connect.Enabled = false;
        status.Text = "Opening GW192A…";
        canvas.SetEmptyMessage("Connecting to GW192A…", "Reading camera calibration and starting live capture.");
        CameraSession? candidate = null;
        try
        {
            var owner = sessionFactory(diagnostics);
            candidate = owner;
            session = owner;
            owner.FrameReceived += frame =>
            {
                if (ReferenceEquals(Volatile.Read(ref session), owner)
                    && Interlocked.Exchange(ref pending, new PendingFrame(owner, frame)) is not null)
                    diagnostics.FrameSuperseded();
            };
            var device = await candidate.OpenAsync();
            if (!ReferenceEquals(session, candidate)) return;
            diagnostics.Record($"Selected camera: {device.Name}; {device.Path}");
            temperatureError = null;
            thermometry = null;
            calibrationRetries.Reset();
            connected = true;
            sinceFrame.Restart();
            frameRate.Restart();
            frames = 0;
            fps = 0;
            connect.Text = "Disconnect";
            freeze.Enabled = true;
            status.Text = "Waiting for camera frames…";
            canvas.SetEmptyMessage("Waiting for camera frames…", "The live image will appear when the camera is ready.");
            await TryLoadThermometryAsync(candidate);
            if (ReferenceEquals(session, candidate)) await RefreshAutoCalibrationAsync(candidate);
        }
        catch (Exception ex)
        {
            diagnostics.Error("Connect", ex);
            if (ReferenceEquals(session, candidate))
            {
                await DisconnectAsync();
                if (!IsDisposed)
                {
                    status.Text = $"Cannot open camera: {ex.Message}";
                    canvas.SetEmptyMessage("Camera unavailable", "Check the USB connection, close other camera apps, then select Connect.");
                }
            }
        }
        finally
        {
            connecting = false;
            if (!IsDisposed)
            {
                connect.Enabled = retiringSession is null && !closing;
                if (connected) canvas.Focus();
            }
        }
    }

    private async Task DisconnectAsync()
    {
        var owned = session;
        session = null;
        connected = false;
        calibrationRetries.Cancel();
        connect.Enabled = false;
        retiringSession = owned ?? retiringSession;
        Interlocked.Exchange(ref pending, null);
        connect.Text = "Connect";
        freeze.Enabled = false;
        freeze.Text = "Freeze";
        frozen = false;
        UpdateFreezeAppearance();
        status.Text = "Disconnected";
        SetAutoCalibrationState(null, "Connect the camera to read its automatic-calibration setting.");
        current = null;
        hoverPoint = null;
        thermometry = null;
        temperatureError = null;
        lastMeasurementError = null;
        temperatures = null;
        TemperatureFramesReceived = 0;
        UpdateTemperatureLabels();
        notice.Text = "Connect the camera to load its factory temperature calibration.";
        notice.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(87, 101, 116);
        canvas.SetImage(null);
        canvas.SetEmptyMessage("Camera not connected", "Plug in the GW192A, then select Connect.");
        pointer.Text = PointerHint;
        if (owned is not null)
        {
            if (!await owned.StopAsync())
            {
                if (!IsDisposed) status.Text = "Camera is still closing. Reconnect will be available when the driver returns.";
                _ = FinishRetirementAsync(owned);
                return;
            }
            if (ReferenceEquals(retiringSession, owned)) retiringSession = null;
        }
        if (!IsDisposed) connect.Enabled = !connecting && retiringSession is null && !closing;
    }

    private async Task FinishRetirementAsync(CameraSession owned)
    {
        await owned.Completion;
        if (IsDisposed || Disposing || !ReferenceEquals(retiringSession, owned)) return;
        retiringSession = null;
        connect.Enabled = !connecting && !closing;
        status.Text = "Disconnected";
    }

    private void UpdateFrame()
    {
        var next = Interlocked.Exchange(ref pending, null);
        // A callback may have passed its producer-side identity check just as
        // Disconnect cleared the mailbox. Check ownership again when consuming.
        var frame = next is not null && connected && ReferenceEquals(next.Owner, session) ? next.Frame : null;
        if (frame is null)
        {
            if (connected && sinceFrame.Elapsed.TotalSeconds > 4)
            {
                _ = DisconnectAsync();
                diagnostics.Record("Capture stalled: no complete frames for four seconds.");
                status.Text = "No frames. Check the camera connection and close other camera apps.";
                canvas.SetEmptyMessage("Camera disconnected", "Check the USB connection, then select Connect to resume.");
            }
            return;
        }
        sinceFrame.Restart();
        frames++;
        if (frameRate.Elapsed.TotalSeconds >= 1)
        {
            fps = frames / frameRate.Elapsed.TotalSeconds;
            frames = 0;
            frameRate.Restart();
        }
        if (frozen)
        {
            status.Text = "Frozen";
            return;
        }
        try
        {
            current = ThermalDecoder.Decode(frame);
            if (session is not null && thermometry is null && calibrationRetries.Remaining > 0)
                _ = TryLoadThermometryAsync(session);
            UpdateTemperatures();
            RenderCurrent();
            if (temperatures is not null) TemperatureFramesReceived++;
            status.Text = $"Live · {fps:F1} fps";
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Error("Decode camera frame", ex);
            _ = DisconnectAsync();
            status.Text = ex.Message;
        }
    }

    private void RenderCurrent()
    {
        if (updatingImageOptions) return;
        canvas.Palette = (ThermalPalette)Math.Max(0, palette.SelectedIndex);
        canvas.SetTemperatureMarkers(trackExtrema.Checked ? temperatures : null, rotation, fadeExtrema.Checked);
        var factor = (upscaling.SelectedItem as UpscalingOption)?.Factor ?? 1;
        var selectedFilter = Enum.TryParse<ImageFilter>(filter.SelectedItem?.ToString(), out var value)
            ? value : ImageFilter.Bicubic;
        if (current is not null)
        {
            var started = Stopwatch.GetTimestamp();
            canvas.SetImage(ThermalRenderer.Render(current, canvas.Palette, rotation,
                factor, selectedFilter, outputSize: canvas.ImageSide, sharpening: sharpening.Value));
            diagnostics.FrameRendered(Stopwatch.GetElapsedTime(started));
        }
        ShowPointer(hoverPoint);
    }

    private void ResetImageAdjustments()
    {
        updatingImageOptions = true;
        try
        {
            palette.SelectedIndex = 0;
            filter.SelectedItem = nameof(ImageFilter.Bicubic);
            upscaling.SelectedIndex = 1;
            sharpening.Value = 50;
            rotation = 0;
        }
        finally { updatingImageOptions = false; }
        RenderCurrent();
    }

    private void UpdateFreezeAppearance()
    {
        if (SystemInformation.HighContrast) return;
        freeze.BackColor = frozen ? Color.FromArgb(255, 244, 219) : Color.White;
        freeze.ForeColor = frozen ? Color.FromArgb(120, 77, 12) : InkColor;
        freeze.FlatAppearance.BorderColor = frozen ? Color.FromArgb(208, 168, 94) : Color.FromArgb(202, 208, 216);
    }

    private void CopyTemperatureReadings()
    {
        if (temperatures is null) return;
        try
        {
            Clipboard.SetText(TemperatureReport.Format(temperatures, temperatureSettings, frozen, canvas.MeasurementPoint));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try copying the readings again.",
                "Copy temperature readings", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void CopyDiagnostics()
    {
        try { Clipboard.SetText(diagnostics.Snapshot()); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try copying diagnostics again.",
                "Copy diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task TryLoadThermometryAsync(CameraSession owner)
    {
        if (!ReferenceEquals(session, owner) || !calibrationRetries.TryBegin()) return;
        var permanentFailure = false;
        try
        {
            var calibration = await owner.ReadCalibrationAsync();
            if (!ReferenceEquals(session, owner)) return;
            try { thermometry = new Thermometry(calibration); }
            catch (InvalidDataException) { permanentFailure = true; throw; }
            temperatureError = null;
            if (current is not null)
            {
                UpdateTemperatures();
                RenderCurrent();
            }
            if (connected && !confirmedAutoCalibration.HasValue && !connecting) await RefreshAutoCalibrationAsync(owner);
        }
        catch (Exception ex)
        {
            diagnostics.Error("Load thermometry", ex);
            if (!ReferenceEquals(session, owner)) return;
            thermometry = null;
            permanentFailure |= ex is NotSupportedException || owner.IsFaulted;
            var retry = !permanentFailure && calibrationRetries.Remaining > 0 ? " Retrying…" : " Disconnect and reconnect to retry.";
            temperatureError = $"Temperature calibration unavailable: {ex.Message}{retry}";
            if (owner.IsFaulted) await DisconnectAsync();
        }
        finally
        {
            if (ReferenceEquals(session, owner))
            {
                calibrationRetries.Complete(thermometry is not null, permanentFailure);
            }
        }
    }

    private void SetAutoCalibrationState(bool? enabled, string? explanation = null)
    {
        updatingAutoCalibration = true;
        try
        {
            confirmedAutoCalibration = enabled;
            autoCalibration.CheckState = enabled switch
            {
                true => CheckState.Checked,
                false => CheckState.Unchecked,
                null => CheckState.Indeterminate
            };
            autoCalibration.Enabled = enabled.HasValue;
            tips.SetToolTip(autoCalibration, explanation
                ?? "Controls the camera's shutter correction; brief image pauses are normal. Changes remain after closing the viewer.");
        }
        finally { updatingAutoCalibration = false; }
    }

    private async Task RefreshAutoCalibrationAsync(CameraSession owner)
    {
        try
        {
            var enabled = await owner.ReadAutoCalibrationAsync();
            if (ReferenceEquals(session, owner)) SetAutoCalibrationState(enabled);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(session, owner)) return;
            SetAutoCalibrationState(null, $"Automatic calibration unavailable: {ex.Message}");
            if (owner.IsFaulted) await DisconnectAsync();
        }
    }

    private async Task ChangeAutoCalibrationAsync()
    {
        var owner = session;
        if (updatingAutoCalibration || owner is null || !connected || !confirmedAutoCalibration.HasValue) return;
        var requested = autoCalibration.Checked;
        autoCalibration.Enabled = false;
        connect.Enabled = false;
        try
        {
            var confirmed = await owner.SetAutoCalibrationAsync(requested);
            if (ReferenceEquals(session, owner)) SetAutoCalibrationState(confirmed);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(session, owner)) return;
            await RefreshAutoCalibrationAsync(owner);
            if (IsDisposed || closing || !ReferenceEquals(session, owner)) return;
            if (showErrorDialogs)
                MessageBox.Show(this, $"Could not confirm automatic calibration: {ex.Message}",
                    "Camera calibration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else diagnostics.Error("Automatic calibration command", ex);
        }
        finally { if (!IsDisposed) connect.Enabled = !connecting && retiringSession is null && !closing; }
    }

    private void UpdateTemperatures()
    {
        temperatures = null;
        if (current is not null && thermometry is not null)
        {
            try
            {
                temperatures = thermometry.Measure(current, temperatureSettings);
                lastMeasurementError = null;
                var s = temperatureSettings;
                notice.Text = $"Emissivity {s.Emissivity:F2} · Distance {s.Distance:F2} m · Ambient {s.Ambient:F1} °C\n"
                    + $"Reflected {s.Reflected:F1} °C · Humidity {s.Humidity:P0} · Factory calibration";
                notice.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(87, 101, 116);
                if (temperatures.LimitedCount > 0 || temperatures.InvalidCount > 0)
                {
                    notice.Text += $" · {temperatures.LimitedCount} range-limited / {temperatures.InvalidCount} invalid pixels";
                    notice.ForeColor = Color.FromArgb(130, 70, 0);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                if (lastMeasurementError != ex.Message) diagnostics.Error("Measure frame", ex);
                lastMeasurementError = ex.Message;
                notice.ForeColor = Color.FromArgb(130, 70, 0);
                notice.Text = current.Telemetry?.IsCalibrating == true
                    ? "Calibrating… temperature readings are temporarily unavailable."
                    : $"Temperatures unavailable: {ex.Message}";
            }
        }
        else
        {
            notice.ForeColor = Color.FromArgb(130, 70, 0);
            notice.Text = temperatureError ?? "Temperature calibration has not been loaded.";
        }
        UpdateTemperatureLabels();
    }

    private void UpdateTemperatureLabels()
    {
        var point = canvas.MeasurementPoint;
        if (measurementCaption is not null)
            measurementCaption.Text = point == ThermalCanvas.DefaultMeasurementPoint ? "Center" : $"Spot ({point.X}, {point.Y})";
        temperatureLabels[1].AccessibleName = point == ThermalCanvas.DefaultMeasurementPoint ? "center temperature" : $"temperature at pixel {point.X}, {point.Y}";
        float[] values = temperatures is null ? [float.NaN, float.NaN, float.NaN, float.NaN]
            : [temperatures.Minimum, temperatures.TemperatureAt(point), temperatures.Maximum, temperatures.Average];
        for (var i = 0; i < temperatureLabels.Length; i++)
        {
            var quality = temperatures is null ? MeasurementQuality.Invalid : i switch
            {
                0 => temperatures.QualityAt(temperatures.MinimumPosition),
                1 => temperatures.QualityAt(point),
                2 => temperatures.QualityAt(temperatures.MaximumPosition),
                _ => MeasurementQuality.Valid
            };
            temperatureLabels[i].Text = (i == 3 && temperatures?.LimitedCount > 0 ? "≈" : "")
                + TemperatureText.Format(values[i], quality);
            temperatureLabels[i].ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText
                : float.IsFinite(values[i]) ? InkColor : Color.FromArgb(109, 120, 133);
            temperatureLabels[i].MarkerColor = trackExtrema.Checked ? i switch
            {
                0 => canvas.ColdMarkerColor,
                2 => canvas.HotMarkerColor,
                _ => (Color?)null
            } : null;
        }
        canvas.SetTemperatureMarkers(trackExtrema.Checked ? temperatures : null, rotation, fadeExtrema.Checked);
    }

    private void ShowPointer(Point? point)
    {
        if (current is null || point is null)
        {
            pointer.Text = PointerHint;
            return;
        }
        var (x, y) = (point.Value.X, point.Value.Y);
        var native = ThermalCanvas.NativePoint(new Point(x, y), rotation);
        (x, y) = (native.X, native.Y);
        var temperature = temperatures is null ? "temperature unavailable"
            : TemperatureText.Format(temperatures.TemperatureAt(native), temperatures.QualityAt(native));
        pointer.Text = $"Pixel ({x}, {y}) · {temperature}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop();
            timer.Dispose();
            if (session is not null) _ = session.StopAsync();
            tips.Dispose();
            viewMenu.Dispose();
            temperatureFont.Dispose();
            readingCaptionFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
