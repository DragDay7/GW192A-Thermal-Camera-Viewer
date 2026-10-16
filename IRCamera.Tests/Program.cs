using System.Buffers.Binary;
using IRCamera;

var passed = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
    passed++;
}
void Reject(CameraFrame frame, string name)
{
    try { ThermalDecoder.Decode(frame); }
    catch (InvalidDataException) { Assert(true, name); return; }
    throw new Exception($"FAIL: {name}: invalid payload was accepted.");
}

var initializationReads = new List<int>();
var negotiated = false;
byte[] ReadRepluggedCamera(int selector)
{
    initializationReads.Add(selector);
    if (selector == 4)
    {
        negotiated = true;
        return [(byte)'2', (byte)'.', (byte)'0', 0];
    }
    return new byte[negotiated ? 2 : 512];
}
Assert(ReadRepluggedCamera(5).Length == 512, "replugged camera fixture starts with legacy command-control sizing");
initializationReads.Clear();
CameraProtocol.Initialize(ReadRepluggedCamera);
Assert(initializationReads.SequenceEqual(new[] { 4 }) && ReadRepluggedCamera(5).Length == 2,
    "protocol-version discovery precedes command access and switches the replugged camera to 2-byte commands");
foreach (var unsupportedVersion in new[] { Array.Empty<byte>(), new byte[] { (byte)'1', (byte)'.', (byte)'0', 0 }, new byte[512] })
{
    try { CameraProtocol.Initialize(_ => unsupportedVersion); throw new Exception("Unsupported camera protocol accepted"); }
    catch (NotSupportedException) { Assert(true, "missing or unsupported protocol discovery prevents camera commands"); }
}

ThermometryRegression.Run(Assert);
CameraSessionRegression.Run(Assert);
ViewerSessionRegression.Run(Assert);

foreach (var length in new[] { 79, 226 })
{
    var settings = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
    settings[0x4e] = 2;
    var snapshot = (byte[])settings.Clone();
    Assert(CameraCalibrationProtocol.IsEnabled(settings), $"automatic calibration enabled mode decoded ({length} bytes)");
    var disabled = CameraCalibrationProtocol.WithAutoCalibration(settings, false);
    Assert(!CameraCalibrationProtocol.IsEnabled(disabled) && disabled[0x4e] == 1,
        $"automatic calibration disabled mode encoded ({length} bytes)");
    Assert(settings.SequenceEqual(snapshot) && disabled.Where((value, index) => index != 0x4e).SequenceEqual(settings.Where((value, index) => index != 0x4e)),
        $"automatic calibration changes only its setting byte and preserves input ({length} bytes)");
    Assert(CameraCalibrationProtocol.WithAutoCalibration(disabled, true).SequenceEqual(snapshot),
        $"automatic calibration toggle round trip preserves every setting ({length} bytes)");
}
foreach (var invalid in new[] { new byte[0], new byte[78], new byte[79] })
{
    try { CameraCalibrationProtocol.WithAutoCalibration(invalid, true); throw new Exception("Unsupported automatic-calibration settings accepted"); }
    catch (NotSupportedException) { Assert(true, "missing or unsupported automatic-calibration mode cannot be overwritten"); }
}

var bytes = new byte[ThermalDecoder.PayloadByteCount];
for (var i = 0; i < ThermalDecoder.PixelCount; i++)
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)(5000 + i % 100));
var header = bytes.AsSpan(ThermalDecoder.RawByteCount + 192);
BinaryPrimitives.WriteUInt32LittleEndian(header, 0xAABBCCDD);
BinaryPrimitives.WriteInt32LittleEndian(header[4..], 210);
BinaryPrimitives.WriteInt32LittleEndian(header[8..], 96);
BinaryPrimitives.WriteInt32LittleEndian(header[12..], 96);
for (var i = 0; i < ThermalDecoder.PixelCount; i++) bytes[ThermalDecoder.ImageOffset + i] = (byte)(i % 256);
var input = new CameraFrame(bytes, 96, 176, 1.25);
var decoded = ThermalDecoder.Decode(input);
Assert(decoded.Luminance.Length == 9216 && decoded.Raw.Length == 9216, "96×96 planes extracted");
Assert(decoded.RawMinimum == 5000 && decoded.RawMaximum == 5099, "16-bit sensor values preserved");
Assert(decoded.RawCenter == 5000 + (48 * 96 + 48) % 100, "center pixel");
Assert(decoded.Luminance[0] == 0 && decoded.Luminance[255] == 255 && decoded.Luminance[9215] == 255, "independent luma plane offset and boundaries");
Assert(decoded.SensorType == 210 && decoded.Timestamp == 1.25, "sensor identity and timestamp");
bytes[ThermalDecoder.ImageOffset] = 250;
Assert(decoded.Luminance[0] == 0, "decoded plane owns its storage");
Reject(input with { Data = bytes[..^1] }, "truncated payload rejected");
Reject(input with { Data = new byte[bytes.Length + 1] }, "oversized payload rejected");
Reject(input with { Width = 192 }, "incorrect capture width rejected");
Reject(input with { Height = 100 }, "incorrect capture height rejected");
var corrupt = (byte[])bytes.Clone();
corrupt[ThermalDecoder.RawByteCount + 192] = 0;
Reject(input with { Data = corrupt }, "invalid header rejected");
corrupt = (byte[])bytes.Clone();
corrupt[ThermalDecoder.RawByteCount + 196] = 209;
Reject(input with { Data = corrupt }, "unsupported sensor rejected");
corrupt = (byte[])bytes.Clone();
corrupt[ThermalDecoder.RawByteCount + 200] = 192;
Reject(input with { Data = corrupt }, "unexpected sensor dimensions rejected");
using (var gray = ThermalRenderer.Render(decoded, ThermalPalette.Grayscale, 0))
{
    Assert(gray.GetPixel(0, 0).R == 0 && gray.GetPixel(63, 2).R == 255, "grayscale rendering");
    using var rotated = ThermalRenderer.Render(decoded, ThermalPalette.Grayscale, 1);
    Assert(rotated.GetPixel(95, 0) == gray.GetPixel(0, 0)
        && rotated.GetPixel(0, 95) == gray.GetPixel(95, 95), "clockwise pixel rotation");
}
foreach (var palette in Enum.GetValues<ThermalPalette>())
{
    using var image = ThermalRenderer.Render(decoded, palette, 0);
    Assert(image.Width == 96 && image.Height == 96, $"{palette} renders native dimensions");
}
var originalLuma = (byte[])decoded.Luminance.Clone();
var originalRaw = (ushort[])decoded.Raw.Clone();
foreach (var factor in Enumerable.Range(2, 5))
{
    using var image = ThermalRenderer.Render(decoded, ThermalPalette.Iron, 0, factor);
    Assert(image.Width == 96 * factor && image.Height == 96 * factor, $"xBRZ {factor}× dimensions");
}
Assert(decoded.Luminance.SequenceEqual(originalLuma) && decoded.Raw.SequenceEqual(originalRaw),
    "xBRZ leaves image input and sensor values unchanged");
var diagonal = new byte[ThermalDecoder.PixelCount];
for (var y = 0; y < 96; y++)
    for (var x = 0; x < 96; x++) diagonal[y * 96 + x] = x > y ? (byte)255 : (byte)0;
var diagonalFrame = decoded with { Luminance = diagonal };
using (var scaled = ThermalRenderer.Render(diagonalFrame, ThermalPalette.Grayscale, 0, 4))
{
    Assert(Enumerable.Range(100, 12).Any(y => Enumerable.Range(100, 12)
        .Any(x => scaled.GetPixel(x, y).R is > 0 and < 255)), "xBRZ blends diagonal edges rather than nearest-neighbor enlargement");
    using var rotated = ThermalRenderer.Render(diagonalFrame, ThermalPalette.Grayscale, 1, 4);
    Assert(rotated.GetPixel(383 - 103, 107) == scaled.GetPixel(107, 103), "rotation composes with xBRZ");
}
foreach (var factor in new[] { 0, 7 })
{
    try
    {
        using var invalid = ThermalRenderer.Render(decoded, ThermalPalette.Iron, 0, factor);
        throw new Exception("FAIL: unsupported xBRZ factor accepted");
    }
    catch (ArgumentOutOfRangeException) { Assert(true, $"xBRZ factor {factor} rejected"); }
}
foreach (var rotation in new[] { -1, 4 })
{
    try { using var invalid = ThermalRenderer.Render(decoded, ThermalPalette.Iron, rotation); throw new Exception("Invalid rotation accepted"); }
    catch (ArgumentOutOfRangeException) { Assert(true, "unsupported rotation rejected before bitmap allocation"); }
}
try { using var invalid = ThermalRenderer.Render(decoded, (ThermalPalette)99, 0); throw new Exception("Invalid palette accepted"); }
catch (ArgumentOutOfRangeException) { Assert(true, "unsupported palette rejected instead of silently using Rainbow"); }
foreach (var filter in Enum.GetValues<ImageFilter>())
{
    var flat = Enumerable.Repeat((byte)77, 64).ToArray();
    Assert(ImageProcessing.Resize(flat, 8, 23, filter).All(value => value == 77), $"{filter} preserves flat fields when enlarging");
    Assert(ImageProcessing.Resize(flat, 8, 3, filter).All(value => value == 77), $"{filter} preserves flat fields when reducing");
    Assert(ImageProcessing.Resize(flat, 8, 8, filter).SequenceEqual(flat), $"{filter} leaves native-size image unchanged");
}
var nearest = ImageProcessing.Resize([0, 60, 120, 240], 2, 4, ImageFilter.Nearest);
Assert(nearest.SequenceEqual(new byte[] { 0, 0, 60, 60, 0, 0, 60, 60, 120, 120, 240, 240, 120, 120, 240, 240 }),
    "nearest-neighbor pixel centers and borders");
Assert(ImageProcessing.Resize([0, 255, 0, 255], 2, 8, ImageFilter.Bicubic)
    .Any(value => value is > 0 and < 255), "bicubic interpolates grayscale");
var softEdge = new byte[64];
for (var y = 0; y < 8; y++)
    for (var x = 0; x < 8; x++) softEdge[y * 8 + x] = x < 4 ? (byte)80 : (byte)160;
var sharpEdge = ImageProcessing.Sharpen(softEdge, 8, 20);
Assert(sharpEdge[3] < softEdge[3] && sharpEdge[4] > softEdge[4], "mild unsharp mask increases edge contrast");
Assert(ImageProcessing.Sharpen(softEdge, 8, 0).SequenceEqual(softEdge), "sharpening zero is an exact bypass");
Assert(ImageProcessing.Sharpen(Enumerable.Repeat((byte)77, 64).ToArray(), 8, 100).All(value => value == 77),
    "sharpening preserves flat fields including borders");
Assert(ImageProcessing.Sharpen([0, 255, 0, 255], 2, 100).SequenceEqual(new byte[] { 0, 255, 0, 255 }),
    "strong sharpening clamps without byte overflow");
foreach (var filter in Enum.GetValues<ImageFilter>())
{
    using var image = ThermalRenderer.Render(decoded, ThermalPalette.Iron, 0,
        filter: filter, outputSize: 173, sharpening: 20);
    var expectedGray = ImageProcessing.Resize(ImageProcessing.Sharpen(decoded.Luminance, 96, 20), 96, 173, filter);
    Assert(image.Width == 173 && image.Height == 173, $"{filter} renders at exact canvas size");
    Assert(image.GetPixel(81, 71).ToArgb() == ThermalRenderer.ColorAt(expectedGray[71 * 173 + 81], ThermalPalette.Iron).ToArgb(),
        $"{filter} applies sharpening and resampling before palette lookup");
    using var rotated = ThermalRenderer.Render(decoded, ThermalPalette.Iron, 1,
        filter: filter, outputSize: 173, sharpening: 20);
    Assert(rotated.GetPixel(172 - 71, 81) == image.GetPixel(81, 71), $"{filter} composes with rotation");
}
Assert(decoded.Luminance.SequenceEqual(originalLuma) && decoded.Raw.SequenceEqual(originalRaw),
    "sharpening and resampling leave input and sensor data unchanged");
using (var combined = ThermalRenderer.Render(decoded, ThermalPalette.Iron, 0, 2, ImageFilter.Bicubic, 173, 50))
    Assert(combined.Width == 173 && combined.Height == 173, "xBRZ 2× composes with Bicubic and sharpening 50%");
var watch = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < 10; i++)
{
    using var image = ThermalRenderer.Render(decoded, ThermalPalette.Iron, 0,
        xbrzFactor: 2, filter: ImageFilter.Bicubic, outputSize: 450, sharpening: 50);
}
Console.WriteLine($"Bicubic + sharpening 50%, 450×450: {watch.Elapsed.TotalMilliseconds / 10:F1} ms/frame (local check).");
Exception? dialogError = null;
var dialogThread = new Thread(() =>
{
    try
    {
        using var viewer = new MainForm();
        viewer.PerformLayout();
        Assert(viewer.ClientSize == new Size(590, 685)
            && viewer.Controls.Find("ViewerControls", true).Single().Height <= 180
            && viewer.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ThermalCanvas>().Single().ImageSide >= 330,
            "compact footer gives the image more room within the original grouped window size");
        var mainReadout = (Label)viewer.Controls.Find("TemperatureCENTER", true).Single();
        var cameraControls = viewer.Controls.Find("CameraControls", true).Single();
        var imageControls = viewer.Controls.Find("ImageControls", true).Single();
        var trackingControls = viewer.Controls.Find("TrackingControls", true).Single();
        Assert(cameraControls.Parent!.Text == "Camera · Rated range: −20 to 400 °C",
            "camera specification is labeled as a rated range outside the image");
        Assert(new[] { "ConnectCamera", "FreezeView", "TemperatureSettings", "AutoCalibration" }
            .All(name => viewer.Controls.Find(name, true).Single().Parent == cameraControls),
            "camera actions, temperature settings, and automatic calibration are grouped together");
        Assert(new[] { "Palette", "Filter", "Upscaling", "Sharpening" }
            .All(name => imageControls.Contains(viewer.Controls.Find(name, true).Single())),
            "palette and display processing controls are grouped together");
        Assert(viewer.Controls.Find("Sharpening", true).Single().Parent?.Name == "SharpeningControls",
            "sharpening caption and slider wrap together as one control");
        Assert(new[] { "TrackExtrema", "FadeExtrema" }
            .All(name => viewer.Controls.Find(name, true).Single().Parent == trackingControls),
            "marker tracking and fading share a dedicated group");
        Assert(new[] { cameraControls, imageControls, trackingControls }.All(row =>
            row.Parent is GroupBox group && !string.IsNullOrWhiteSpace(group.Text)),
            "each related control set is enclosed in a titled native group box");
        Assert(new[] { cameraControls, imageControls, trackingControls }.All(row => row.Parent is ControlGroupBox),
            "group borders use buffered rendering while retaining native group-box semantics");
        Assert(cameraControls.Controls.Cast<Control>().Select(control => control.TabIndex).SequenceEqual(Enumerable.Range(0, cameraControls.Controls.Count))
            && imageControls.Controls.Cast<Control>().Select(control => control.TabIndex).SequenceEqual(Enumerable.Range(0, imageControls.Controls.Count))
            && trackingControls.Controls.Cast<Control>().Select(control => control.TabIndex).SequenceEqual(Enumerable.Range(0, trackingControls.Controls.Count)),
            "keyboard navigation follows the visible control order in every group");
        Assert(mainReadout.Font.Size == 15 && mainReadout.Font.Bold,
            "temperature values use larger bold text with separate captions");
        var defaultSize = viewer.Size;
        viewer.Size = viewer.MinimumSize;
        viewer.PerformLayout();
        var rowsFit = true;
        foreach (var name in new[] { "CameraControls", "ImageControls", "TrackingControls" })
        {
            var row = viewer.Controls.Find(name, true).Single();
            foreach (Control control in row.Controls)
                rowsFit &= control.Right <= row.ClientSize.Width && control.Bottom <= row.ClientSize.Height;
        }
        Assert(rowsFit, "all camera, image, and tracking controls fit at minimum window size");
        using (var headingFont = new Font(cameraControls.Parent.Font, FontStyle.Bold))
            Assert(TextRenderer.MeasureText(cameraControls.Parent.Text, headingFont).Width <= cameraControls.Parent.Width - 24,
                "rated camera range fits the group heading at minimum window size");
        Assert(TextRenderer.MeasureText("700.0 °C", mainReadout.Font).Width <= mainReadout.Width,
            "larger temperature values fit at minimum window size");
        viewer.Size = defaultSize;
        viewer.PerformLayout();
        foreach (var testSize in new[] { viewer.MinimumSize, defaultSize, new Size(900, 800) })
        {
            viewer.Size = testSize;
            viewer.PerformLayout();
            var contained = true;
            foreach (var row in new[] { cameraControls, imageControls, trackingControls })
            foreach (Control control in row.Controls)
                contained &= control.Left >= 0 && control.Top >= 0 && control.Right <= row.ClientSize.Width && control.Bottom <= row.ClientSize.Height;
            var sharpPanel = viewer.Controls.Find("SharpeningControls", true).Single();
            foreach (Control control in sharpPanel.Controls)
                contained &= control.Left >= 0 && control.Right <= sharpPanel.ClientSize.Width && control.Bottom <= sharpPanel.ClientSize.Height;
            Assert(contained, $"grouped controls and the complete sharpening strip fit at {testSize.Width}×{testSize.Height}");
        }
        viewer.Size = defaultSize;
        viewer.PerformLayout();
        Assert(((TrackBar)viewer.Controls.Find("Sharpening", true).Single()).Value == 50
            && ((UpscalingOption)((ComboBox)viewer.Controls.Find("Upscaling", true).Single()).SelectedItem!).Factor == 2,
            "viewer defaults to sharpening 50% and xBRZ 2×");
        Assert(((ComboBox)viewer.Controls.Find("Filter", true).Single()).Items.Cast<string>().SequenceEqual(new[] { "Nearest", "Bicubic" }),
            "filter dropdown contains only Nearest and Bicubic");
        Assert(((Label)viewer.Controls.Find("PixelTemperature", true).Single()).Text == "Hover for temperature · Right-click for actions",
            "bottom readout prompts for temperature without raw sensor values");
        var automatic = (CheckBox)viewer.Controls.Find("AutoCalibration", true).Single();
        var connectForAlignment = (Button)viewer.Controls.Find("ConnectCamera", true).Single();
        Assert(Math.Abs(automatic.Top + automatic.Height / 2.0 - connectForAlignment.Top - connectForAlignment.Height / 2.0) <= 1,
            "automatic calibration is vertically centered with the camera buttons");
        var sharpeningForAlignment = (TrackBar)viewer.Controls.Find("Sharpening", true).Single();
        var sharpeningCell = sharpeningForAlignment.Parent!;
        var sharpeningCaption = (Label)sharpeningCell.Controls.Find("SharpeningCaption", false).Single();
        var sharpeningPercentage = (Label)sharpeningCell.Controls.Find("SharpeningValue", false).Single();
        var scalingForAlignment = viewer.Controls.Find("Filter", true).Single();
        Assert(sharpeningForAlignment.Left == sharpeningCaption.Right
            && sharpeningCaption.Width <= 56 && sharpeningCaption.Text == "Sharpen"
            && TextRenderer.MeasureText(sharpeningCaption.Text, sharpeningCaption.Font).Width <= sharpeningCaption.Width
            && sharpeningPercentage.Left >= sharpeningForAlignment.Right && sharpeningPercentage.Right <= sharpeningCell.Width,
            "sharpening caption, slider, and separate percentage form a compact single control");
        var originalSliderBounds = sharpeningForAlignment.Bounds;
        foreach (var strength in new[] { 0, 5, 50, 100 })
        {
            sharpeningForAlignment.Value = strength;
            Assert(sharpeningPercentage.Text == $"{strength}%" && sharpeningForAlignment.Bounds == originalSliderBounds,
                $"sharpening percentage updates at {strength}% without moving the slider");
        }
        sharpeningForAlignment.Value = 50;
        Assert(sharpeningForAlignment.Top == 3 && sharpeningForAlignment.Bottom <= sharpeningCell.Height
            && sharpeningCaption.Top == 0 && sharpeningCaption.Height == sharpeningCell.Height
            && Math.Abs(sharpeningCell.Top + sharpeningCell.Height / 2.0 - scalingForAlignment.Top - scalingForAlignment.Height / 2.0) <= 2,
            "sharpening uses a centered caption cell and compensates for native slider padding without clipping");
        var viewerTips = (ToolTip)typeof(MainForm).GetField("tips", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewer)!;
        IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        var controlsWithTips = Descendants(viewer).Where(control => !string.IsNullOrEmpty(viewerTips.GetToolTip(control))).ToArray();
        Assert(controlsWithTips.Length == 2 && controlsWithTips.All(control => control.Name is "AutoCalibration" or "FadeExtrema"),
            "only non-obvious calibration and fading behavior has custom hover help");
        Assert(viewerTips.InitialDelay >= 1000,
            "remaining contextual help waits before appearing");
        Assert(!automatic.Enabled && automatic.CheckState == CheckState.Indeterminate,
            "automatic calibration starts unknown and disabled until the camera state is read");
        var trackingOption = (CheckBox)viewer.Controls.Find("TrackExtrema", true).Single();
        Assert(trackingOption.Checked, "minimum/maximum tracking is enabled by default and can be opted out");
        var fadeOption = (CheckBox)viewer.Controls.Find("FadeExtrema", true).Single();
        Assert(fadeOption.Checked && fadeOption.Enabled, "marker fading is enabled by default and can be opted out");
        trackingOption.Checked = false;
        Assert(!fadeOption.Enabled, "opting out of tracking disables its fading option");
        trackingOption.Checked = true;
        Assert(fadeOption.Enabled, "enabling tracking makes the fading option available");
        var minimumReadout = (TemperatureReadout)viewer.Controls.Find("TemperatureMINIMUM", true).Single();
        var maximumReadout = (TemperatureReadout)viewer.Controls.Find("TemperatureMAXIMUM", true).Single();
        var paletteChooser = (ComboBox)viewer.Controls.Find("Palette", true).Single();
        var neutralText = minimumReadout.ForeColor;
        var neutralBackground = minimumReadout.BackColor;
        foreach (var palette in Enum.GetValues<ThermalPalette>())
        {
            paletteChooser.SelectedItem = palette.ToString();
            Assert(minimumReadout.MarkerColor == ThermalRenderer.ColorAt(255, palette)
                && maximumReadout.MarkerColor == ThermalRenderer.ColorAt(0, palette)
                && minimumReadout.ForeColor == neutralText && maximumReadout.ForeColor == neutralText
                && minimumReadout.BackColor == neutralBackground && maximumReadout.BackColor == neutralBackground,
                $"{palette} uses matching marker keys without changing temperature-text contrast");
        }
        trackingOption.Checked = false;
        Assert(minimumReadout.MarkerColor is null && maximumReadout.MarkerColor is null,
            "palette keys disappear when tracking is disabled");
        trackingOption.Checked = true;
        paletteChooser.SelectedIndex = 0;
        Assert(paletteChooser.DrawMode == DrawMode.OwnerDrawFixed
            && paletteChooser.Items.Cast<string>().SequenceEqual(Enum.GetNames<ThermalPalette>()),
            "palette previews retain the named native dropdown choices");
        var liveCanvas = (ThermalCanvas)viewer.Controls.Find("ThermalCanvas", true).Single();
        var selectedValues = Enumerable.Repeat(21F, 9216).ToArray();
        selectedValues[9 * 96 + 7] = 36F;
        var selectedFrame = new TemperatureFrame(selectedValues, 21, 21, 36, 21, 0, 0, Point.Empty, new Point(7, 9));
        var temperatureField = typeof(MainForm).GetField("temperatures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        temperatureField.SetValue(viewer, selectedFrame);
        liveCanvas.SetMeasurementPoint(new Point(7, 9));
        Assert(mainReadout.Text == $"{36F:F1} °C" && mainReadout.Parent!.Controls.OfType<Label>().Any(label => label.Text == "Spot (7, 9)"),
            "moving the measurement point updates the spot temperature and coordinate caption immediately");
        liveCanvas.ContextMenuStrip!.Items.Find("ResetMeasurementPoint", false).OfType<ToolStripMenuItem>().Single().PerformClick();
        Assert(liveCanvas.MeasurementPoint == new Point(48, 48) && mainReadout.Text == $"{21F:F1} °C"
            && mainReadout.Parent!.Controls.OfType<Label>().Any(label => label.Text == "Center"),
            "context-menu reset restores the center cross, caption, and measurement");
        Assert(TemperatureReport.Format(selectedFrame, new TemperatureSettings(), true, new Point(7, 9))
            .Contains($"Spot: {36F:F1} °C · pixel (7, 9)"), "copied readings use the selected measurement point");
        var selectedQuality = new MeasurementQuality[9216];
        selectedQuality[9 * 96 + 7] = MeasurementQuality.AboveRange;
        temperatureField.SetValue(viewer, selectedFrame with { Quality = selectedQuality, LimitedCount = 1 });
        liveCanvas.SetMeasurementPoint(new Point(7, 9));
        Assert(mainReadout.Text.StartsWith("≥")
            && viewer.Controls.Find("TemperatureAVERAGE", true).Single().Text.StartsWith("≈"),
            "spot and average UI readouts visibly distinguish range-limited measurements");
        liveCanvas.SetMeasurementPoint(ThermalCanvas.DefaultMeasurementPoint);
        temperatureField.SetValue(viewer, null);
        trackingOption.Checked = false;
        trackingOption.Checked = true;
        var pauseButton = (Button)viewer.Controls.Find("FreezeView", true).Single();
        var normalPauseColor = pauseButton.BackColor;
        var originalViewBounds = liveCanvas.Bounds;
        var clickButton = typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        clickButton.Invoke(pauseButton, new object[] { EventArgs.Empty });
        Assert(pauseButton.Text == "Resume" && (SystemInformation.HighContrast || pauseButton.BackColor != normalPauseColor)
            && liveCanvas.Bounds == originalViewBounds,
            "paused state highlights the Resume button without changing the image viewport");
        clickButton.Invoke(pauseButton, new object[] { EventArgs.Empty });
        Assert(pauseButton.Text == "Freeze" && pauseButton.BackColor == normalPauseColor && liveCanvas.Bounds == originalViewBounds,
            "resuming restores the neutral button appearance without layout changes");
        Assert(liveCanvas.ContextMenuStrip is not null
            && liveCanvas.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Select(item => item.Name)
                .SequenceEqual(new[] { "ResetMeasurementPoint", "CopyTemperatureReadings", "ResetImageAdjustments", "CopyDiagnostics" }),
            "image context menu contains only reset and copy actions, without duplicated toolbar controls");
        ((ComboBox)viewer.Controls.Find("Filter", true).Single()).SelectedItem = nameof(ImageFilter.Nearest);
        ((ComboBox)viewer.Controls.Find("Upscaling", true).Single()).SelectedIndex = 2;
        ((TrackBar)viewer.Controls.Find("Sharpening", true).Single()).Value = 85;
        liveCanvas.ContextMenuStrip!.Items.Find("ResetImageAdjustments", false).OfType<ToolStripMenuItem>().Single().PerformClick();
        Assert(((ComboBox)viewer.Controls.Find("Filter", true).Single()).SelectedItem?.ToString() == nameof(ImageFilter.Bicubic)
            && ((UpscalingOption)((ComboBox)viewer.Controls.Find("Upscaling", true).Single()).SelectedItem!).Factor == 2
            && ((TrackBar)viewer.Controls.Find("Sharpening", true).Single()).Value == 50
            && trackingOption.Checked && fadeOption.Checked && !automatic.Enabled,
            "image reset restores display defaults without changing tracking or camera calibration options");
        var calibrationNotice = (Label)viewer.Controls.Find("CalibrationNotice", true).Single();
        var connectionStatus = (Label)viewer.Controls.Find("ConnectionStatus", true).Single();
        var pixelReadout = (Label)viewer.Controls.Find("PixelTemperature", true).Single();
        var connectionButton = (Button)viewer.Controls.Find("ConnectCamera", true).Single();
        var originalNotice = calibrationNotice.Text;
        var originalStatus = connectionStatus.Text;
        var originalPixel = pixelReadout.Text;
        var originalTemperature = mainReadout.Text;
        Assert(connectionStatus.Parent == pixelReadout.Parent && connectionStatus.Parent?.Name == "ViewerFooter"
            && connectionStatus.TextAlign == ContentAlignment.MiddleRight && pixelReadout.TextAlign == ContentAlignment.MiddleLeft,
            "hover measurement and connection state share one compact, consistently aligned footer");
        Assert(connectionStatus.Parent!.Height <= viewer.Font.Height + 18 && connectionStatus.Bounds.IntersectsWith(pixelReadout.Bounds) == false,
            "the single-line footer saves image space without overlapping its two readouts");
        bool UsesBufferedPainting(Control control) => (bool)typeof(Control)
            .GetMethod("GetStyle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, new object[] { ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint })!;
        Assert(new Control[] { calibrationNotice, connectionStatus, pixelReadout, mainReadout,
                viewer.Controls.OfType<TableLayoutPanel>().Single(), viewer.Controls.Find("TemperatureReadings", true).Single(),
                viewer.Controls.Find("CameraControls", true).Single() }.All(UsesBufferedPainting),
            "dynamic labels and layout containers buffer background and foreground painting together");
        Assert(new[] { cameraControls.Parent!, imageControls.Parent!, trackingControls.Parent! }.All(UsesBufferedPainting),
            "styled group headings and borders are double-buffered");
        using (var groupImage = new Bitmap(cameraControls.Parent!.Width, cameraControls.Parent.Height))
        {
            cameraControls.Parent.DrawToBitmap(groupImage, new Rectangle(Point.Empty, groupImage.Size));
            Assert(cameraControls.Parent is GroupBox && cameraControls.Parent.Controls.Contains(cameraControls),
                "styled group renders without replacing or moving its interactive controls");
        }
        trackingOption.Checked = false;
        Assert(new[] { "MINIMUM", "CENTER", "MAXIMUM", "AVERAGE" }.All(title =>
            viewer.Controls.Find($"Temperature{title}", true).Single().BackColor.A == 255),
            "temperature labels use opaque backgrounds instead of repeatedly repainting their parents");
        trackingOption.Checked = true;
        foreach (var windowSize in new[] { defaultSize, viewer.MinimumSize })
        {
            viewer.Size = windowSize;
            viewer.PerformLayout();
            var imageBounds = liveCanvas.Bounds;
            var imageRectangle = liveCanvas.ImageBounds;
            var connectionButtonSize = connectionButton.Size;
            var stable = true;
            foreach (var state in new[] { "Disconnected", "Live", "Calibrating", "Long error" })
            {
                calibrationNotice.Text = state switch
                {
                    "Disconnected" => "Connect the camera to load its factory temperature calibration.",
                    "Live" => "Factory calibration · ε 0.97 · 1.00 m · ambient 20.0 °C\nReflected 20.0 °C · RH 60%",
                    "Calibrating" => "Calibrating… temperature readings are temporarily unavailable.",
                    _ => "Temperature calibration unavailable: " + new string('X', 1000)
                };
                connectionStatus.Text = state == "Long error" ? new string('X', 1000) : state;
                pixelReadout.Text = state == "Live" ? "Pixel (95, 95) · -40.0 °C" : "Move over image for temperature";
                mainReadout.Text = state == "Live" ? "700.0 °C" : "— °C";
                connectionButton.Text = state == "Disconnected" ? "Connect" : "Disconnect";
                viewer.PerformLayout();
                stable &= liveCanvas.Bounds == imageBounds && liveCanvas.ImageBounds == imageRectangle
                    && viewer.Size == windowSize && connectionButton.Size == connectionButtonSize;
            }
            Assert(stable, $"image and button geometry remain fixed through connection states and long footer errors at {windowSize.Width}×{windowSize.Height}");
        }
        Assert(!calibrationNotice.AutoSize && calibrationNotice.AutoEllipsis && !connectionStatus.AutoSize && connectionStatus.AutoEllipsis
            && !pixelReadout.AutoSize && pixelReadout.AutoEllipsis,
            "footer labels truncate within reserved space instead of resizing the view");
        calibrationNotice.Text = originalNotice;
        connectionStatus.Text = originalStatus;
        Assert(string.IsNullOrEmpty(viewerTips.GetToolTip(calibrationNotice))
            && string.IsNullOrEmpty(viewerTips.GetToolTip(connectionStatus))
            && string.IsNullOrEmpty(viewerTips.GetToolTip(pixelReadout)),
            "footer updates do not create duplicate custom tooltips");
        pixelReadout.Text = originalPixel;
        mainReadout.Text = originalTemperature;
        connectionButton.Text = "Connect";
        viewer.Size = defaultSize;
        viewer.PerformLayout();
        using var canvas = new ThermalCanvas { Size = new Size(200, 200) };
        using (var clickCanvas = new ThermalCanvas { Size = new Size(252, 222) })
        using (var clickImage = new Bitmap(96, 96))
        {
            clickCanvas.SetImage((Bitmap)clickImage.Clone());
            var mouseDown = typeof(ThermalCanvas).GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            void Click(Point point, MouseButtons button = MouseButtons.Left, int clicks = 1) => mouseDown.Invoke(clickCanvas,
                new object[] { new MouseEventArgs(button, clicks, point.X, point.Y, 0) });
            for (var rotation = 0; rotation < 4; rotation++)
            {
                clickCanvas.SetTemperatureMarkers(null, rotation);
                var wanted = new Point(17, 69);
                Click(ThermalCanvas.MarkerLocation(wanted, clickCanvas.ImageBounds, rotation));
                Assert(clickCanvas.MeasurementPoint == wanted, $"left-click places measurement on the correct native pixel at rotation {rotation}");
                using (var marked = new Bitmap(clickCanvas.Width, clickCanvas.Height))
                {
                    clickCanvas.DrawToBitmap(marked, new Rectangle(Point.Empty, marked.Size));
                    var location = ThermalCanvas.MarkerLocation(wanted, clickCanvas.ImageBounds, rotation);
                    Assert(marked.GetPixel(location.X, location.Y).ToArgb() == Color.White.ToArgb()
                        && marked.GetPixel(location.X + 7, location.Y).ToArgb() == Color.Black.ToArgb(),
                        $"selected white cross and complete outline render at the selected pixel at rotation {rotation}");
                }
                Click(Point.Empty);
                Click(new Point(clickCanvas.ImageBounds.Right + 14, clickCanvas.ImageBounds.Top));
                Click(ThermalCanvas.MarkerLocation(new Point(30, 40), clickCanvas.ImageBounds, rotation), MouseButtons.Right);
                Assert(clickCanvas.MeasurementPoint == wanted, $"margins, range bar, and right-click do not move the measurement at rotation {rotation}");
                Click(ThermalCanvas.MarkerLocation(wanted, clickCanvas.ImageBounds, rotation), clicks: 2);
                Assert(clickCanvas.MeasurementPoint == ThermalCanvas.DefaultMeasurementPoint,
                    $"left double-click resets the measurement point at rotation {rotation}");
            }
            clickCanvas.SetTemperatureMarkers(null, 0);
            foreach (var edge in new[] { new Point(0, 0), new Point(95, 95) })
            {
                Click(ThermalCanvas.MarkerLocation(edge, clickCanvas.ImageBounds, 0));
                Assert(clickCanvas.MeasurementPoint == edge, "edge pixels can be selected without leaving sensor bounds");
            }
            clickCanvas.SetImage(null);
            Click(clickCanvas.ImageBounds.Location);
            Assert(clickCanvas.MeasurementPoint == new Point(95, 95), "clicking an empty camera view does not change the measurement");
        }
        Assert(canvas.Cursor == Cursors.Cross, "camera view uses the Windows precision crosshair cursor");
        using var background = new Bitmap(96, 96);
        using (var graphics = Graphics.FromImage(background)) graphics.Clear(Color.Gray);
        canvas.SetImage((Bitmap)background.Clone());
        using var painted = new Bitmap(200, 200);
        canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
        var cx = canvas.ImageBounds.Left + canvas.ImageSide / 2;
        var cy = 100;
        var crossMatches = true;
        for (var y = -8; y <= 8; y++)
        for (var x = -8; x <= 8; x++)
        {
            var white = (y == 0 && Math.Abs(x) <= 6) || (x == 0 && Math.Abs(y) <= 6);
            var black = (Math.Abs(y) <= 1 && Math.Abs(x) <= 7) || (Math.Abs(x) <= 1 && Math.Abs(y) <= 7);
            var expected = white ? Color.White : black ? Color.Black : Color.Gray;
            crossMatches &= painted.GetPixel(cx + x, cy + y).ToArgb() == expected.ToArgb();
        }
        Assert(crossMatches, "center cross has symmetric arms and a complete one-pixel outline including every tip");
        Assert(painted.GetPixel(canvas.ImageBounds.Left + 9, canvas.ImageBounds.Top + 9).ToArgb() == Color.Gray.ToArgb(),
            "live image is not covered by status text or badges");
        var markerFrame = new TemperatureFrame(new float[9216], 10, 20, 30, 20, 0, 0, new Point(12, 24), new Point(75, 60));
        var report = TemperatureReport.Format(markerFrame, new TemperatureSettings(), frozen: true);
        Assert(report.Contains("Frozen temperature readings")
            && report.Contains($"Minimum: {10F:F1} °C · pixel (12, 24)")
            && report.Contains($"Center: {20F:F1} °C")
            && report.Contains($"Maximum: {30F:F1} °C · pixel (75, 60)")
            && report.Contains($"Average: {20F:F1} °C"),
            "copyable report includes displayed readings, marker locations, and frozen state");
        Assert(report.Contains($"Emissivity: {0.97F:F2}") && report.Contains("Distance:")
            && report.Contains("Ambient:") && report.Contains("Reflected:") && report.Contains("Humidity:"),
            "copyable readings include the conversion settings needed for comparison");
        var incompleteReport = TemperatureReport.Format(markerFrame with { Center = float.NaN, InvalidCount = 1, LimitedCount = 4 }, new TemperatureSettings(), false);
        Assert(incompleteReport.Contains("Live temperature readings") && incompleteReport.Contains("Center: Unavailable")
            && incompleteReport.Contains("Invalid pixels: 1 · Range-limited pixels: 4"),
            "copyable report keeps unavailable readings and quality limitations explicit");
        using (var valueLabel = new TemperatureReadout { Size = new Size(110, 36), Font = mainReadout.Font, Text = "700.0 °C", BackColor = Color.White, ForeColor = Color.Black })
        using (var valueImage = new Bitmap(110, 36))
        {
            valueLabel.DrawToBitmap(valueImage, new Rectangle(Point.Empty, valueImage.Size));
            var inkPixels = 0;
            for (var y = 0; y < valueImage.Height; y++)
            for (var x = 0; x < valueImage.Width; x++)
                if (valueImage.GetPixel(x, y).ToArgb() != Color.White.ToArgb()) inkPixels++;
            Assert(inkPixels > 100 && valueLabel.Text == "700.0 °C",
                "number/unit typography renders while preserving the complete accessible temperature text");
            valueLabel.Text = "— °C";
            valueLabel.DrawToBitmap(valueImage, new Rectangle(Point.Empty, valueImage.Size));
            Assert(valueImage.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(),
                "updating temperature typography retains an opaque background");
        }
        var target = canvas.ImageBounds;
        foreach (var palette in Enum.GetValues<ThermalPalette>())
        for (var rotation = 0; rotation < 4; rotation++)
        {
            canvas.Palette = palette;
            canvas.SetTemperatureMarkers(markerFrame, rotation);
            canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
            var cold = ThermalCanvas.MarkerLocation(markerFrame.MinimumPosition, target, rotation);
            var hot = ThermalCanvas.MarkerLocation(markerFrame.MaximumPosition, target, rotation);
            Assert(painted.GetPixel(cold.X, cold.Y).ToArgb() == ThermalRenderer.ColorAt(255, palette).ToArgb()
                && painted.GetPixel(hot.X, hot.Y).ToArgb() == ThermalRenderer.ColorAt(0, palette).ToArgb()
                && painted.GetPixel(cold.X + 3, cold.Y).ToArgb() == canvas.ColdMarkerColor.ToArgb()
                && painted.GetPixel(hot.X + 3, hot.Y).ToArgb() == canvas.HotMarkerColor.ToArgb()
                && painted.GetPixel(cold.X - 3, cold.Y).ToArgb() == canvas.ColdMarkerColor.ToArgb()
                && painted.GetPixel(hot.X - 3, hot.Y).ToArgb() == canvas.HotMarkerColor.ToArgb()
                && painted.GetPixel(cold.X + 4, cold.Y).ToArgb() == Color.Gray.ToArgb()
                && painted.GetPixel(hot.X + 4, hot.Y).ToArgb() == Color.Gray.ToArgb()
                && painted.GetPixel(cold.X, cold.Y + 3).ToArgb() == canvas.ColdMarkerColor.ToArgb()
                && painted.GetPixel(hot.X, hot.Y + 3).ToArgb() == canvas.HotMarkerColor.ToArgb()
                && painted.GetPixel(cold.X, cold.Y + 4).ToArgb() == Color.Gray.ToArgb()
                && painted.GetPixel(hot.X, hot.Y + 4).ToArgb() == Color.Gray.ToArgb()
                && painted.GetPixel(cold.X + 3, cold.Y + 1).ToArgb() == Color.Gray.ToArgb()
                && painted.GetPixel(hot.X + 3, hot.Y + 1).ToArgb() == Color.Gray.ToArgb(),
                $"{palette} tracking crosses are equally sized 7×7 with inverted colors and no outlines at rotation {rotation}");
        }
        Assert(ThermalCanvas.MarkerLocation(new Point(0, 0), new Rectangle(0, 0, 192, 192), 1) == new Point(191, 1)
            && ThermalCanvas.MarkerLocation(new Point(0, 0), new Rectangle(0, 0, 192, 192), 2) == new Point(191, 191)
            && ThermalCanvas.MarkerLocation(new Point(0, 0), new Rectangle(0, 0, 192, 192), 3) == new Point(1, 191),
            "tracking maps native pixel centers correctly through every rotation");
        canvas.SetTemperatureMarkers(null, 0);
        canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
        var formerCold = ThermalCanvas.MarkerLocation(markerFrame.MinimumPosition, target, 0);
        Assert(painted.GetPixel(formerCold.X, formerCold.Y).ToArgb() == Color.Gray.ToArgb(),
            "disabling tracking or losing temperature data removes the additional crosses");
        Assert(canvas.Controls.Count == 0, "range bar has no added temperature labels");
        Assert(canvas.ImageSide == 140 && canvas.ImageBounds.Left == 19,
            "image layout reserves only the original range-bar spacing");
        canvas.Palette = ThermalPalette.Grayscale;
        var trailFrames = Enumerable.Range(0, 6).Select(i => markerFrame with
        {
            MinimumPosition = new Point(10 + 15 * i, 12),
            MaximumPosition = new Point(10 + 15 * i, 80)
        }).ToArray();
        for (var i = 0; i < 5; i++) canvas.SetTemperatureMarkers(trailFrames[i], 0, fade: true);
        // Repainting, changing display settings, and Freeze must not age the trail.
        for (var i = 0; i < 10; i++) canvas.SetTemperatureMarkers(trailFrames[4], 0, fade: true);
        canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
        for (var i = 0; i < 5; i++)
        {
            var alpha = (i + 1) * 51;
            var cold = ThermalCanvas.MarkerLocation(trailFrames[i].MinimumPosition, target, 0);
            var hot = ThermalCanvas.MarkerLocation(trailFrames[i].MaximumPosition, target, 0);
            var expectedCold = 128 + (255 - 128) * alpha / 255;
            var expectedHot = 128 * (255 - alpha) / 255;
            Assert(Math.Abs(painted.GetPixel(cold.X, cold.Y).R - expectedCold) <= 1
                && Math.Abs(painted.GetPixel(hot.X, hot.Y).R - expectedHot) <= 1
                && painted.GetPixel(cold.X, cold.Y) == painted.GetPixel(cold.X + 2, cold.Y),
                $"both marker trails use {(i + 1) * 20}% opacity with a singly blended center, unchanged by repeated repaints");
        }
        canvas.SetTemperatureMarkers(trailFrames[5], 0, fade: true);
        canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
        var expired = ThermalCanvas.MarkerLocation(trailFrames[0].MinimumPosition, target, 0);
        Assert(painted.GetPixel(expired.X, expired.Y).ToArgb() == Color.Gray.ToArgb(),
            "old marker position disappears after five newer displayed frames");
        canvas.SetTemperatureMarkers(trailFrames[5], 1, fade: true);
        canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
        var rotatedTrail = ThermalCanvas.MarkerLocation(trailFrames[1].MinimumPosition, target, 1);
        Assert(Math.Abs(painted.GetPixel(rotatedTrail.X, rotatedTrail.Y).R - (128 + 127 / 5)) <= 1,
            "historical positions rotate with the image without advancing their age");
        canvas.SetTemperatureMarkers(trailFrames[5], 0, fade: false);
        canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
        var previousTrail = ThermalCanvas.MarkerLocation(trailFrames[4].MinimumPosition, target, 0);
        Assert(painted.GetPixel(previousTrail.X, previousTrail.Y).ToArgb() == Color.Gray.ToArgb(),
            "turning fading off immediately removes previous positions");
        canvas.SetTemperatureMarkers(null, 0, fade: true);
        canvas.SetTemperatureMarkers(trailFrames[0], 0, fade: true);
        canvas.DrawToBitmap(painted, new Rectangle(0, 0, 200, 200));
        Assert(painted.GetPixel(previousTrail.X, previousTrail.Y).ToArgb() == Color.Gray.ToArgb(),
            "unavailable temperatures clear history before tracking resumes");
        using var dialog = new TemperatureSettingsForm(new TemperatureSettings());
        Assert(Math.Abs(dialog.Settings.Emissivity - 0.97F) < 0.00001F && dialog.Settings.Distance == 1,
            "temperature dialog initializes conversion settings");
        var controls = dialog.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<NumericUpDown>().ToArray();
        controls.Single(control => control.AccessibleName == "Object emissivity").Value = 0.85M;
        controls.Single(control => control.AccessibleName == "Relative humidity (%)").Value = 75;
        dialog.Settings.Validate();
        Assert(dialog.Settings.Emissivity == 0.85F && dialog.Settings.Humidity == 0.75F,
            "temperature dialog returns edited emissivity and humidity in correct units");
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(dialog.Controls.Find("TemperatureDefaults", true).Single(), new object[] { EventArgs.Empty });
        Assert(dialog.Settings == new TemperatureSettings(), "temperature defaults restore all pending conversion settings");
        var settingsHelp = (Label)dialog.Controls.Find("TemperatureFieldHelp", true).Single();
        Assert(settingsHelp.Text.StartsWith("Defaults restored", StringComparison.Ordinal),
            "defaults action explains that Apply is still required");
        var helpBounds = settingsHelp.Bounds;
        foreach (var control in controls)
        {
            typeof(Control).GetMethod("OnEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(control, new object[] { EventArgs.Empty });
            Assert(!string.IsNullOrWhiteSpace(control.AccessibleDescription)
                && settingsHelp.Text == control.AccessibleDescription && settingsHelp.Bounds == helpBounds,
                $"{control.AccessibleName} exposes contextual help without resizing the dialog");
        }
        dialog.PerformLayout();
        var settingsTable = dialog.Controls.OfType<TableLayoutPanel>().Single();
        var settingsButtons = dialog.Controls.OfType<FlowLayoutPanel>().Single();
        Assert(settingsTable.Bottom <= settingsButtons.Top,
            "temperature field help and notice do not overlap the action buttons");
        Assert(((Button)dialog.AcceptButton!).DialogResult == DialogResult.OK
            && ((Button)dialog.CancelButton!).DialogResult == DialogResult.Cancel,
            "Enter applies and Escape cancels temperature settings");
    }
    catch (Exception ex) { dialogError = ex; }
});
dialogThread.SetApartmentState(ApartmentState.STA);
dialogThread.Start();
if (!dialogThread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Temperature settings dialog test timed out.");
if (dialogError is not null) throw dialogError;
Console.WriteLine($"{passed} checks passed.");
