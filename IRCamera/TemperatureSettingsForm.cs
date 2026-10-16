namespace IRCamera;

internal sealed class TemperatureSettingsForm : Form
{
    private readonly NumericUpDown emissivity = Number(0.01M, 1, 2, 0.01M);
    private readonly NumericUpDown distance = Number(0, 100, 2, 0.1M);
    private readonly NumericUpDown ambient = Number(-40, 80, 1, 1);
    private readonly NumericUpDown reflected = Number(-40, 500, 1, 1);
    private readonly NumericUpDown humidity = Number(0, 100, 0, 5);
    private readonly Label help = new BufferedLabel
    {
        Name = "TemperatureFieldHelp", Dock = DockStyle.Fill, AutoSize = false,
        Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 10, 0, 8)
    };

    public TemperatureSettings Settings => new((float)emissivity.Value, (float)distance.Value,
        (float)ambient.Value, (float)reflected.Value, (float)humidity.Value / 100);

    public TemperatureSettingsForm(TemperatureSettings settings)
    {
        Text = "Temperature conversion settings";
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 360);
        DoubleBuffered = true;
        BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(247, 248, 250);
        help.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(233, 239, 245);
        help.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(49, 65, 82);

        LoadSettings(settings);
        var table = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(16, 12, 16, 0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        var fields = new (string Label, NumericUpDown Control, string Help)[]
        {
            ("Object emissivity", emissivity, "Emissivity · 0.01–1.00\nUse a value appropriate to the object's surface. Shiny metals can reflect surrounding heat and give misleading readings."),
            ("Object distance (m)", distance, "Distance · 0–100 m\nDistance from the camera to the measured object, used for atmospheric compensation."),
            ("Ambient temperature (°C)", ambient, "Ambient temperature · −40–80 °C\nAir temperature around the scene, not the temperature of the object being measured."),
            ("Reflected temperature (°C)", reflected, "Reflected temperature · −40–500 °C\nApparent temperature of the surroundings reflected by the object. This is not always the same as the air temperature."),
            ("Relative humidity (%)", humidity, "Relative humidity · 0–100%\nHumidity of the air between the camera and the object, used for atmospheric compensation.")
        };
        var tabIndex = 0;
        foreach (var (label, control, description) in fields)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
            table.Controls.Add(caption);
            control.AccessibleName = label;
            control.AccessibleDescription = description;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(0, 4, 0, 4);
            control.TextAlign = HorizontalAlignment.Right;
            control.TabIndex = tabIndex++;
            control.Enter += (_, _) =>
            {
                help.Text = description;
                control.Select(0, control.Text.Length);
            };
            table.Controls.Add(control);
        }
        help.Text = fields[0].Help;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        table.Controls.Add(help);
        table.SetColumnSpan(help, 2);
        var note = new Label
        {
            Text = "Changes take effect only after Apply. No camera settings are written.",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(87, 101, 116),
            Margin = Padding.Empty
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        table.Controls.Add(note);
        table.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var ok = new Button { Name = "ApplyTemperatureSettings", Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Name = "CancelTemperatureSettings", Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var defaults = new Button { Name = "TemperatureDefaults", Text = "Defaults", AutoSize = true };
        defaults.Click += (_, _) =>
        {
            LoadSettings(new TemperatureSettings());
            help.Text = "Defaults restored\nSelect Apply to use these values, or Cancel to keep your previous settings.";
        };
        buttons.Controls.AddRange([ok, cancel, defaults]);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(table);
        Controls.Add(buttons);
        Shown += (_, _) => emissivity.Focus();
    }

    private void LoadSettings(TemperatureSettings settings)
    {
        emissivity.Value = (decimal)settings.Emissivity;
        distance.Value = (decimal)settings.Distance;
        ambient.Value = (decimal)settings.Ambient;
        reflected.Value = (decimal)settings.Reflected;
        humidity.Value = (decimal)settings.Humidity * 100;
    }

    private static NumericUpDown Number(decimal minimum, decimal maximum, int places, decimal increment)
        => new() { Minimum = minimum, Maximum = maximum, DecimalPlaces = places, Increment = increment };

}
