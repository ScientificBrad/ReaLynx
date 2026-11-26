namespace ReaLynx.Win;

public sealed class ControlsConfigForm : Form
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, Button> _buttons = [];
    private string? _capturingAction;

    private static readonly (string Key, string Label)[] Actions =
    [
        ("Up", "D-Pad Up"),
        ("Down", "D-Pad Down"),
        ("Left", "D-Pad Left"),
        ("Right", "D-Pad Right"),
        ("A", "Button A (Outside)"),
        ("B", "Button B (Inside)"),
        ("Option1", "Option 1"),
        ("Option2", "Option 2"),
        ("Pause", "Pause"),
    ];

    public ControlsConfigForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Keyboard Controls (XInput Gamepad Always Active)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 390);
        KeyPreview = true;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 330,
            ColumnCount = 2,
            RowCount = Actions.Length,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        for (int i = 0; i < Actions.Length; i++)
        {
            var (actionKey, label) = Actions[i];
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            var lbl = new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };
            var btn = new Button
            {
                Text = _settings.KeyBindings.TryGetValue(actionKey, out var k) ? k.ToString() : "None",
                Dock = DockStyle.Fill,
                Tag = actionKey
            };
            btn.Click += (_, _) =>
            {
                _capturingAction = actionKey;
                btn.Text = "Press any key...";
            };
            _buttons[actionKey] = btn;
            table.Controls.Add(lbl, 0, i);
            table.Controls.Add(btn, 1, i);
        }

        var btnClose = new Button
        {
            Text = "Done",
            DialogResult = DialogResult.OK,
            Location = new Point(280, 345),
            Size = new Size(85, 30)
        };
        var btnDefaults = new Button
        {
            Text = "Defaults",
            Location = new Point(15, 345),
            Size = new Size(85, 30)
        };
        btnDefaults.Click += (_, _) =>
        {
            var def = new AppSettings().KeyBindings;
            foreach (var kv in def)
            {
                _settings.KeyBindings[kv.Key] = kv.Value;
                if (_buttons.TryGetValue(kv.Key, out var b))
                    b.Text = kv.Value.ToString();
            }
            _settings.Save();
        };

        Controls.Add(table);
        Controls.Add(btnDefaults);
        Controls.Add(btnClose);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturingAction != null)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key != Keys.Escape)
            {
                _settings.KeyBindings[_capturingAction] = key;
                _settings.Save();
            }
            _buttons[_capturingAction].Text = _settings.KeyBindings[_capturingAction].ToString();
            _capturingAction = null;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
