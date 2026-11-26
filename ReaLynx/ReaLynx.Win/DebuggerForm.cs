using System.Globalization;
using System.Text;
using ReaLynx.Core;
using ReaLynx.Core.Cpu;

namespace ReaLynx.Win;

/// <summary>
/// Integrated Atari Lynx debugger window:
/// CPU registers, 65SC02 disassembly, single-step, breakpoints, bounded instruction trace,
/// Mikey timers/interrupts, Suzy registers/math, and 64KB memory hex viewer.
/// </summary>
public sealed class DebuggerForm : Form
{
    private readonly LynxSystem _system;
    private readonly object _syncRoot;
    private readonly Action _onStep;
    private readonly Action _onTogglePause;
    private readonly Func<bool> _isPaused;

    private readonly TextBox _cpuText;
    private readonly TextBox _disasmText;
    private readonly TextBox _mikeyText;
    private readonly TextBox _suzyText;
    private readonly TextBox _memText;
    private readonly TextBox _traceText;
    private readonly TextBox _memAddrInput;
    private readonly TextBox _bpInput;
    private readonly ListBox _bpList;
    private readonly CheckBox _traceCheck;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private ushort _memBaseAddr = 0x0000;

    public DebuggerForm(
        LynxSystem system,
        object syncRoot,
        Action onStep,
        Action onTogglePause,
        Func<bool> isPaused)
    {
        _system = system;
        _syncRoot = syncRoot;
        _onStep = onStep;
        _onTogglePause = onTogglePause;
        _isPaused = isPaused;

        Text = "ReaLynx Hardware Debugger";
        Size = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;

        var mono = new Font("Consolas", 9.5f);

        // Top toolbar
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(6, 5, 6, 5)
        };
        var btnStep = new Button { Text = "Step (F11)", Width = 90 };
        btnStep.Click += (_, _) => { _onStep(); RefreshAll(); };
        var btnPauseResume = new Button { Text = "Pause / Resume", Width = 115 };
        btnPauseResume.Click += (_, _) => { _onTogglePause(); RefreshAll(); };
        var btnRefresh = new Button { Text = "Refresh", Width = 75 };
        btnRefresh.Click += (_, _) => RefreshAll();

        _traceCheck = new CheckBox { Text = "Enable Trace", AutoSize = true, Margin = new Padding(12, 6, 6, 0) };
        _traceCheck.CheckedChanged += (_, _) =>
        {
            lock (_syncRoot) { _system.TraceEnabled = _traceCheck.Checked; }
        };

        toolbar.Controls.AddRange([btnStep, btnPauseResume, btnRefresh, _traceCheck]);

        // Main split: Left = CPU + Disasm + Breakpoints, Right = Tabs (Mikey, Suzy, Memory, Trace)
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 420
        };

        // Left panel
        _cpuText = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 135,
            Multiline = true,
            ReadOnly = true,
            Font = mono,
            BackColor = Color.FromArgb(24, 24, 28),
            ForeColor = Color.FromArgb(220, 235, 255)
        };

        _disasmText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = mono,
            BackColor = Color.FromArgb(20, 20, 24),
            ForeColor = Color.FromArgb(230, 230, 230)
        };

        // Breakpoint bar at bottom of left panel
        var bpPanel = new Panel { Dock = DockStyle.Bottom, Height = 125 };
        var bpTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30 };
        bpTop.Controls.Add(new Label { Text = "BP Hex:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
        _bpInput = new TextBox { Width = 65, Text = "FE00", Font = mono };
        var btnAddBp = new Button { Text = "Add", Width = 50 };
        var btnRemBp = new Button { Text = "Remove", Width = 65 };
        var btnClrBp = new Button { Text = "Clear", Width = 55 };
        bpTop.Controls.AddRange([_bpInput, btnAddBp, btnRemBp, btnClrBp]);

        _bpList = new ListBox { Dock = DockStyle.Fill, Font = mono };
        bpPanel.Controls.Add(_bpList);
        bpPanel.Controls.Add(bpTop);

        btnAddBp.Click += (_, _) =>
        {
            if (ushort.TryParse(_bpInput.Text.Trim().TrimStart('$'), NumberStyles.HexNumber, null, out ushort addr))
            {
                lock (_syncRoot) { _system.AddBreakpoint(addr); }
                RefreshBreakpoints();
            }
        };
        btnRemBp.Click += (_, _) =>
        {
            if (_bpList.SelectedItem is string s &&
                ushort.TryParse(s.TrimStart('$'), NumberStyles.HexNumber, null, out ushort addr))
            {
                lock (_syncRoot) { _system.RemoveBreakpoint(addr); }
                RefreshBreakpoints();
            }
        };
        btnClrBp.Click += (_, _) =>
        {
            lock (_syncRoot) { _system.ClearBreakpoints(); }
            RefreshBreakpoints();
        };

        split.Panel1.Controls.Add(_disasmText);
        split.Panel1.Controls.Add(_cpuText);
        split.Panel1.Controls.Add(bpPanel);

        // Right panel tabs
        var tabs = new TabControl { Dock = DockStyle.Fill };

        _mikeyText = CreateReadOnlyMonoBox(mono);
        var tabMikey = new TabPage("Mikey (Timers/IRQ/Audio)");
        tabMikey.Controls.Add(_mikeyText);

        _suzyText = CreateReadOnlyMonoBox(mono);
        var tabSuzy = new TabPage("Suzy (Sprites/Math/IO)");
        tabSuzy.Controls.Add(_suzyText);

        var tabMem = new TabPage("Memory Hex");
        var memBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        memBar.Controls.Add(new Label { Text = "Address ($):", AutoSize = true, Margin = new Padding(4, 7, 4, 0) });
        _memAddrInput = new TextBox { Width = 70, Text = "2000", Font = mono };
        var btnGoMem = new Button { Text = "Go", Width = 50 };
        btnGoMem.Click += (_, _) =>
        {
            if (ushort.TryParse(_memAddrInput.Text.Trim().TrimStart('$'), NumberStyles.HexNumber, null, out ushort a))
            {
                _memBaseAddr = (ushort)(a & 0xFFF0);
                RefreshAll();
            }
        };
        memBar.Controls.AddRange([_memAddrInput, btnGoMem]);
        _memText = CreateReadOnlyMonoBox(mono);
        tabMem.Controls.Add(_memText);
        tabMem.Controls.Add(memBar);

        _traceText = CreateReadOnlyMonoBox(mono);
        var tabTrace = new TabPage("Instruction Trace");
        tabTrace.Controls.Add(_traceText);

        tabs.TabPages.AddRange([tabMikey, tabSuzy, tabMem, tabTrace]);
        split.Panel2.Controls.Add(tabs);

        Controls.Add(split);
        Controls.Add(toolbar);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();

        FormClosed += (_, _) => _refreshTimer.Stop();
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11)
            {
                _onStep();
                RefreshAll();
                e.Handled = true;
            }
        };

        RefreshAll();
    }

    private static TextBox CreateReadOnlyMonoBox(Font font) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = font,
        BackColor = Color.FromArgb(20, 20, 24),
        ForeColor = Color.FromArgb(225, 225, 225)
    };

    private void RefreshBreakpoints()
    {
        _bpList.Items.Clear();
        lock (_syncRoot)
        {
            foreach (ushort bp in _system.Breakpoints.OrderBy(x => x))
                _bpList.Items.Add($"${bp:X4}");
        }
    }

    public void RefreshAll()
    {
        if (IsDisposed) return;

        CpuState cpu;
        ReaLynx.Core.Mikey.MikeySnapshot mikey;
        ReaLynx.Core.Suzy.SuzySnapshot suzy;
        byte mapCtl;
        long totalTicks;
        var disasmSb = new StringBuilder();
        var memSb = new StringBuilder();
        var traceSb = new StringBuilder();

        lock (_syncRoot)
        {
            cpu = _system.Cpu.GetState();
            mikey = _system.Mikey.GetSnapshot();
            suzy = _system.Suzy.GetSnapshot();
            mapCtl = _system.MapCtl;
            totalTicks = _system.TotalMasterTicks;

            // Disassemble 24 instructions starting at PC
            ushort addr = cpu.PC;
            for (int i = 0; i < 24; i++)
            {
                var d = Disassembler65SC02.Disassemble(_system, addr);
                string marker = i == 0 ? "=>" : "  ";
                disasmSb.AppendLine($"{marker} ${d.Address:X4}: {d.BytesHex,-8} {d.Mnemonic}");
                addr = (ushort)(addr + d.Length);
            }

            // Memory dump (256 bytes from _memBaseAddr)
            for (int row = 0; row < 16; row++)
            {
                ushort rowAddr = (ushort)(_memBaseAddr + row * 16);
                memSb.Append($"${rowAddr:X4}: ");
                var ascii = new char[16];
                for (int col = 0; col < 16; col++)
                {
                    byte b = _system.Peek((ushort)(rowAddr + col));
                    memSb.Append($"{b:X2} ");
                    ascii[col] = b is >= 32 and <= 126 ? (char)b : '.';
                }
                memSb.Append(" |");
                memSb.Append(ascii);
                memSb.AppendLine("|");
            }

            // Last 40 trace entries
            var entries = _system.TraceLog.ToArray();
            int start = Math.Max(0, entries.Length - 40);
            for (int i = start; i < entries.Length; i++)
            {
                var t = entries[i];
                traceSb.AppendLine($"[{t.MasterTicks,10}] ${t.PC:X4}: {t.BytesHex,-8} {t.Mnemonic,-14} A={t.A:X2} X={t.X:X2} Y={t.Y:X2} SP={t.SP:X2} P={t.P:X2}");
            }
        }

        string flags =
            ((cpu.P & 0x80) != 0 ? "N" : "n") +
            ((cpu.P & 0x40) != 0 ? "V" : "v") + "-" +
            ((cpu.P & 0x10) != 0 ? "B" : "b") +
            ((cpu.P & 0x08) != 0 ? "D" : "d") +
            ((cpu.P & 0x04) != 0 ? "I" : "i") +
            ((cpu.P & 0x02) != 0 ? "Z" : "z") +
            ((cpu.P & 0x01) != 0 ? "C" : "c");

        _cpuText.Text =
            $"State: {(_isPaused() ? "PAUSED" : "RUNNING")}   Master Ticks: {totalTicks:N0}\r\n" +
            $"PC: ${cpu.PC:X4}   SP: ${cpu.SP:X2}   P: ${cpu.P:X2} [{flags}]\r\n" +
            $"A:  ${cpu.A:X2}     X:  ${cpu.X:X2}   Y: ${cpu.Y:X2}\r\n" +
            $"MAPCTL: ${mapCtl:X2}  IRQ: {cpu.IrqLine}  Sleep: {cpu.IsSleeping}\r\n" +
            $"CPU Cycles: {cpu.TotalCycles:N0}";

        _disasmText.Text = disasmSb.ToString();

        var mSb = new StringBuilder();
        mSb.AppendLine($"INTRST/SET: ${mikey.InterruptFlags:X2}   SYSCTL1: ${mikey.SysCtl1:X2}");
        mSb.AppendLine($"IODIR: ${mikey.IoDir:X2}  IODAT: ${mikey.IoDat:X2}  SERCTL: ${mikey.SerCtl:X2}  SERDAT: ${mikey.SerDat:X2}");
        mSb.AppendLine($"DISPCTL: ${mikey.DispCtl:X2}  DISPADR: ${mikey.DispAdr:X4}  MSTEREO: ${mikey.MStereo:X2}  MPAN: ${mikey.MPan:X2}");
        mSb.AppendLine();
        mSb.AppendLine("Timers:");
        foreach (var t in mikey.Timers)
        {
            mSb.AppendLine($"  T{t.Index}: BKUP=${t.Backup:X2} CNT=${t.Count:X2} CTLA=${t.ControlA:X2} CTLB=${t.ControlB:X2} EN={t.Enabled} RLD={t.Reload} IRQ={t.InterruptEnabled} CLK={t.ClockSource}");
        }
        mSb.AppendLine();
        mSb.AppendLine("Audio Channels:");
        foreach (var a in mikey.Audio)
        {
            mSb.AppendLine($"  A{a.Index}: VOL={a.Volume,4} OUT={a.Output,4} FEED=${a.Feedback:X2} SHIFT=${a.ShiftRegister:X3} BKUP=${a.Backup:X2} CNT=${a.Count:X2} CTLA=${a.ControlA:X2}");
        }
        _mikeyText.Text = mSb.ToString();

        _suzyText.Text =
            $"VIDBAS:  ${suzy.VidBas:X4}    COLLBAS: ${suzy.CollBas:X4}\r\n" +
            $"SCBNEXT: ${suzy.ScbNext:X4}    HOFF: {suzy.HOff}  VOFF: {suzy.VOff}\r\n" +
            $"SPRCTL0: ${suzy.SprCtl0:X2}      SPRCTL1: ${suzy.SprCtl1:X2}      SPRCOLL: ${suzy.SprColl:X2}\r\n" +
            $"SPRSYS:  ${suzy.SprSys:X2}      SUZYBUSEN: ${suzy.SuzyBusEn:X2}    SPRGO: ${suzy.SprGo:X2}\r\n" +
            $"JOYSTICK: ${suzy.Joystick:X2}     SWITCHES: ${suzy.Switches:X2}\r\n" +
            $"MATH Prod/Div: ${suzy.MathProductOrDividend:X8}   Accum/Rem: ${suzy.MathAccumOrRemainder:X8}\r\n" +
            $"Last Sprite Count: {suzy.LastSpriteCount}";

        _memText.Text = memSb.ToString();
        _traceText.Text = traceSb.ToString();
    }
}
