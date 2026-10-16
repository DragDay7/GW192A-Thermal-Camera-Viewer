namespace IRCamera;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--smoke-settings"))
        {
            using var dialog = new TemperatureSettingsForm(new TemperatureSettings());
            using var screenshotTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            screenshotTimer.Tick += (_, _) =>
            {
                screenshotTimer.Stop();
                Directory.CreateDirectory("artifacts");
                using var screenshot = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
                screenshot.Save("artifacts/settings.png", System.Drawing.Imaging.ImageFormat.Png);
                dialog.Close();
            };
            screenshotTimer.Start();
            Application.Run(dialog);
            return;
        }
        using var form = new MainForm(showErrorDialogs: !args.Any(arg => arg.StartsWith("--smoke", StringComparison.Ordinal)));
        if (args.Contains("--smoke") || args.Contains("--smoke-xbrz") || args.Contains("--smoke-thermometry") || args.Contains("--smoke-auto-calibration") || args.Contains("--smoke-tracking") || args.Contains("--smoke-reconnect"))
        {
            if (args.Contains("--compact")) form.Size = form.MinimumSize;
            if (args.Contains("--smoke-reconnect"))
            {
                // Geometry assertions require a fixed test viewport. Normal
                // application windows remain freely resizable.
                form.MaximizeBox = false;
                form.MinimumSize = form.MaximumSize = form.Size;
            }
            if (args.Contains("--smoke-xbrz"))
                ((ComboBox)form.Controls.Find("Upscaling", true).Single()).SelectedIndex = 2;
            if (args.Contains("--smoke-tracking"))
            {
                ((CheckBox)form.Controls.Find("TrackExtrema", true).Single()).Checked = true;
                if (args.Contains("--fade-tracking"))
                    ((CheckBox)form.Controls.Find("FadeExtrema", true).Single()).Checked = true;
            }
            using var timer = new System.Windows.Forms.Timer { Interval = 4000 };
            var autoCalibrationVerified = false;
            string? autoCalibrationError = null;
            var toggleAttempted = false;
            var reconnectCount = 0;
            var reconnectPassed = true;
            var reconnectLog = new List<string>();
            Rectangle? reconnectViewBounds = null;
            var reconnectViewResizes = 0;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (args.Contains("--smoke-reconnect") && reconnectCount < 2)
                {
                    reconnectPassed &= await WaitForAsync(() => form.HasLiveFrame && form.TemperatureFramesReceived >= 10, form);
                    var view = (ThermalCanvas)form.Controls.Find("ThermalCanvas", true).Single();
                    if (reconnectViewBounds is null)
                    {
                        reconnectViewBounds = view.Bounds;
                        view.SizeChanged += (_, _) => reconnectViewResizes++;
                    }
                    reconnectLog.Add($"Before cycle {reconnectCount + 1}: live={form.HasLiveFrame}, temperature frames={form.TemperatureFramesReceived}, current temperatures={form.HasTemperatureFrame}.");
                    reconnectPassed &= form.HasLiveFrame && form.TemperatureFramesReceived >= 10;
                    var connection = (Button)form.Controls.Find("ConnectCamera", true).Single();
                    connection.PerformClick();
                    reconnectPassed &= await WaitForAsync(() => !form.HasLiveFrame && connection.Enabled, form);
                    reconnectLog.Add($"Disconnect action: {connection.Text}; {form.Controls.Find("ConnectionStatus", true).Single().Text}");
                    reconnectPassed &= !form.HasLiveFrame && !form.HasTemperatureFrame;
                    reconnectPassed &= view.Bounds == reconnectViewBounds;
                    connection.PerformClick();
                    reconnectPassed &= await WaitForAsync(() => connection.Enabled && form.HasLiveFrame && form.TemperatureFramesReceived >= 10, form);
                    reconnectLog.Add($"Connect action: {connection.Text}; {form.Controls.Find("ConnectionStatus", true).Single().Text}");
                    reconnectPassed &= view.Bounds == reconnectViewBounds;
                    reconnectCount++;
                    timer.Start();
                    return;
                }
                if (args.Contains("--smoke-auto-calibration") && !toggleAttempted)
                {
                    toggleAttempted = true;
                    var checkbox = (CheckBox)form.Controls.Find("AutoCalibration", true).Single();
                    try
                    {
                        if (!checkbox.Enabled || checkbox.CheckState == CheckState.Indeterminate)
                            throw new IOException("Automatic-calibration checkbox is unavailable.");
                        using var extension = new CameraExtension();
                        var original = extension.ReadAutoCalibration();
                        try
                        {
                            checkbox.Checked = !original;
                            if (!await WaitForAsync(() => checkbox.Enabled, form)) throw new TimeoutException("Calibration checkbox did not finish its command.");
                            if (extension.ReadAutoCalibration() != !original || checkbox.Checked != !original)
                                throw new IOException("Checkbox did not change the camera setting.");
                        }
                        finally
                        {
                            checkbox.Checked = original;
                            await WaitForAsync(() => checkbox.Enabled, form);
                            if (extension.ReadAutoCalibration() != original)
                                extension.SetAutoCalibration(original);
                        }
                        autoCalibrationVerified = checkbox.Enabled && checkbox.Checked == original;
                    }
                    catch (Exception ex) { autoCalibrationError = ex.Message; }
                    timer.Start();
                    return; // Allow live playback to continue after changing the camera setting.
                }
                timer.Stop();
                Directory.CreateDirectory("artifacts");
                if (args.Contains("--freeze-view"))
                    ((Button)form.Controls.Find("FreezeView", true).Single()).PerformClick();
                using var screenshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
                screenshot.Save("artifacts/viewer.png", System.Drawing.Imaging.ImageFormat.Png);
                var mode = args.Contains("--smoke-xbrz") ? "xBRZ 6×" : "xBRZ 2×";
                mode += ", Bicubic, sharpen 50%";
                if (args.Contains("--freeze-view")) mode += ", frozen view";
                if (((CheckBox)form.Controls.Find("TrackExtrema", true).Single()).Checked)
                {
                    mode += ", min/max tracking";
                    if (((CheckBox)form.Controls.Find("FadeExtrema", true).Single()).Checked) mode += ", five-frame fading";
                }
                var needsTemperatures = args.Contains("--smoke-thermometry") || args.Contains("--smoke-tracking") || args.Contains("--smoke-reconnect");
                var passed = await WaitForAsync(() => form.HasLiveFrame && (!needsTemperatures || form.TemperatureFramesReceived >= 10), form);
                if (args.Contains("--smoke-reconnect"))
                {
                    passed &= reconnectPassed && form.TemperatureFramesReceived >= 10 && reconnectViewResizes == 0;
                    reconnectLog.Add($"After both cycles: live={form.HasLiveFrame}, temperature frames={form.TemperatureFramesReceived}, current temperatures={form.HasTemperatureFrame}.");
                    reconnectLog.Add($"Image viewport resize events during reconnects: {reconnectViewResizes}.");
                    File.WriteAllLines("artifacts/reconnect-smoke.txt", reconnectLog);
                }
                if (args.Contains("--smoke-auto-calibration"))
                    passed &= autoCalibrationVerified && form.HasTemperatureFrame;
                File.WriteAllText("artifacts/smoke.txt", passed
                    ? $"PASS: camera frames decoded and displayed ({mode}); temperatures {(form.HasTemperatureFrame ? "available" : "unavailable")}."
                        + (autoCalibrationVerified ? " Automatic-calibration checkbox toggled the camera and restored its original state." : "")
                        + (args.Contains("--smoke-reconnect") && reconnectPassed ? " Temperatures recovered after both disconnect/connect cycles; image viewport remained fixed." : "")
                    : $"FAIL: required live image, temperature data, or calibration toggle is unavailable. {autoCalibrationError}");
                Environment.ExitCode = passed ? 0 : 1;
                File.WriteAllText("artifacts/session-diagnostics.txt", form.DiagnosticSummary);
                form.Close();
            };
            timer.Start();
            Application.Run(form);
        }
        else Application.Run(form);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, Form form)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!form.IsDisposed && started.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

}
