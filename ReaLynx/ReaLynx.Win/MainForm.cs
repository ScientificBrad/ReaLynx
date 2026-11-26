using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ReaLynx.Core;
using ReaLynx.Core.Cartridge;
using ReaLynx.Core.Mikey;
using ReaLynx.Core.Suzy;

namespace ReaLynx.Win;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly LynxSystem _system = new();
    private readonly object _emuLock = new();
    private readonly WaveOutAudio _audio;

    // Framebuffer presentation (double-buffered copy from emu thread)
    private readonly object _fbLock = new();
    private uint[] _presentPixels = new uint[MikeyChip.ScreenWidth * MikeyChip.ScreenHeight];
    private Bitmap? _displayBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    // Emulation thread & pacing
    private readonly ManualResetEventSlim _wakeEvent = new(false);
    private Thread? _emuThread;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _fastForward;
    private double _emulationSpeedPct;
    private double _emulatedFps;

    // Fullscreen state
    private bool _isFullscreen;
    private FormBorderStyle _prevBorderStyle;
    private FormWindowState _prevWindowState;
    private Rectangle _prevBounds;

    // Keyboard state
    private readonly HashSet<Keys> _pressedKeys = [];

    // UI controls
    private readonly Panel _viewport;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusState;
    private readonly ToolStripStatusLabel _statusSpeed;
    private readonly ToolStripStatusLabel _statusCart;
    private readonly ToolStripStatusLabel _statusDiag;
    private readonly ToolStripMenuItem _recentMenu;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _integerScaleMenuItem;
    private readonly ToolStripMenuItem _muteMenuItem;
    private readonly ToolStripMenuItem _rotAutoItem;
    private readonly ToolStripMenuItem _rotNoneItem;
    private readonly ToolStripMenuItem _rotLeftItem;
    private readonly ToolStripMenuItem _rotRightItem;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private DebuggerForm? _debuggerForm;

    public MainForm(string? cliBootRom = null, string? cliCartridge = null)
    {
        _settings = AppSettings.Load();
        if (!string.IsNullOrEmpty(cliBootRom))
            _settings.BootRomPath = cliBootRom;

        _audio = new WaveOutAudio(MikeyChip.SampleRate)
        {
            Volume = _settings.Volume,
            Muted = _settings.Muted
        };

        Text = "ReaLynx — Atari Lynx Emulator";
        BackColor = Color.FromArgb(16, 16, 20);
        ClientSize = new Size(800, 560);
        MinimumSize = new Size(360, 280);
        KeyPreview = true;
        DoubleBuffered = true;
        AllowDrop = true;

        // Menu Bar
        var menu = new MenuStrip();

        // File menu
        var fileMenu = new ToolStripMenuItem("&File");
        var openCartItem = new ToolStripMenuItem("&Open Cartridge...", null, (_, _) => OpenCartridgeDialog()) { ShortcutKeys = Keys.Control | Keys.O };
        var selectBootItem = new ToolStripMenuItem("Select &Boot ROM...", null, (_, _) => SelectBootRomDialog());
        _recentMenu = new ToolStripMenuItem("&Recent Files");
        var screenshotItem = new ToolStripMenuItem("Save &Screenshot...", null, (_, _) => SaveScreenshotDialog()) { ShortcutKeys = Keys.F12 };
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        fileMenu.DropDownItems.AddRange([openCartItem, selectBootItem, _recentMenu, new ToolStripSeparator(), screenshotItem, new ToolStripSeparator(), exitItem]);

        // Emulation menu
        var emuMenu = new ToolStripMenuItem("&Emulation");
        _pauseMenuItem = new ToolStripMenuItem("&Pause", null, (_, _) => TogglePause()) { ShortcutKeys = Keys.F5 };
        var resetItem = new ToolStripMenuItem("&Reset", null, (_, _) => ResetConsole()) { ShortcutKeys = Keys.Control | Keys.R };
        emuMenu.DropDownItems.AddRange([_pauseMenuItem, resetItem]);

        // Video menu
        var videoMenu = new ToolStripMenuItem("&Video");
        var fullscreenItem = new ToolStripMenuItem("&Fullscreen", null, (_, _) => ToggleFullscreen()) { ShortcutKeys = Keys.Alt | Keys.Enter };
        _integerScaleMenuItem = new ToolStripMenuItem("&Integer Scaling", null, (_, _) =>
        {
            _settings.IntegerScaling = !_settings.IntegerScaling;
            if (_integerScaleMenuItem != null) _integerScaleMenuItem.Checked = _settings.IntegerScaling;
            _settings.Save();
            _viewport?.Invalidate();
        })
        { Checked = _settings.IntegerScaling };

        var rotMenu = new ToolStripMenuItem("&Rotation");
        var rotCycleItem = new ToolStripMenuItem("&Cycle Rotation", null, (_, _) => CycleRotation()) { ShortcutKeys = Keys.F8 };
        _rotAutoItem = new ToolStripMenuItem("&Auto (from cartridge)", null, (_, _) => SetRotationMode(RotationOverrideMode.Auto));
        _rotNoneItem = new ToolStripMenuItem("&None (Horizontal)", null, (_, _) => SetRotationMode(RotationOverrideMode.None));
        _rotLeftItem = new ToolStripMenuItem("Rotate &Left (90° CCW)", null, (_, _) => SetRotationMode(RotationOverrideMode.Left));
        _rotRightItem = new ToolStripMenuItem("Rotate &Right (90° CW)", null, (_, _) => SetRotationMode(RotationOverrideMode.Right));
        rotMenu.DropDownItems.AddRange([rotCycleItem, new ToolStripSeparator(), _rotAutoItem, _rotNoneItem, _rotLeftItem, _rotRightItem]);
        videoMenu.DropDownItems.AddRange([fullscreenItem, _integerScaleMenuItem, rotMenu]);

        // Audio menu
        var audioMenu = new ToolStripMenuItem("&Audio");
        _muteMenuItem = new ToolStripMenuItem("&Mute", null, (_, _) => ToggleMute())
        {
            Checked = _settings.Muted,
            ShortcutKeys = Keys.Control | Keys.M
        };
        var volUpItem = new ToolStripMenuItem("Volume &Up (+10%)", null, (_, _) => AdjustVolume(+0.10f));
        var volDownItem = new ToolStripMenuItem("Volume &Down (-10%)", null, (_, _) => AdjustVolume(-0.10f));
        audioMenu.DropDownItems.AddRange([_muteMenuItem, new ToolStripSeparator(), volUpItem, volDownItem]);

        // Input menu
        var inputMenu = new ToolStripMenuItem("&Input");
        var cfgControlsItem = new ToolStripMenuItem("&Configure Keyboard Controls...", null, (_, _) => OpenControlsDialog());
        inputMenu.DropDownItems.Add(cfgControlsItem);

        // Debug menu
        var debugMenu = new ToolStripMenuItem("&Debug");
        var openDebuggerItem = new ToolStripMenuItem("Open Hardware &Debugger...", null, (_, _) => OpenDebugger()) { ShortcutKeys = Keys.F9 };
        debugMenu.DropDownItems.Add(openDebuggerItem);

        menu.Items.AddRange([fileMenu, emuMenu, videoMenu, audioMenu, inputMenu, debugMenu]);
        MainMenuStrip = menu;

        // Viewport Panel (double buffered)
        _viewport = new DoubleBufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            AllowDrop = true
        };
        _viewport.Paint += Viewport_Paint;
        _viewport.Resize += (_, _) => _viewport.Invalidate();
        _viewport.DragEnter += MainForm_DragEnter;
        _viewport.DragDrop += MainForm_DragDrop;

        // Status Bar
        _statusStrip = new StatusStrip();
        _statusState = new ToolStripStatusLabel("Ready") { Width = 90, AutoSize = false };
        _statusSpeed = new ToolStripStatusLabel("0% (0.0 FPS)") { Width = 140, AutoSize = false };
        _statusCart = new ToolStripStatusLabel("No Cartridge") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _statusDiag = new ToolStripStatusLabel("Audio: 0/8") { Width = 220, AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
        _statusStrip.Items.AddRange([_statusState, _statusSpeed, _statusCart, _statusDiag]);

        Controls.Add(_viewport);
        Controls.Add(menu);
        Controls.Add(_statusStrip);
        _viewport.BringToFront();

        UpdateRotationChecks();
        RebuildRecentMenu();

        // Load Boot ROM (if found) and optional CLI cartridge
        TryLoadBootRomSilent();
        if (!string.IsNullOrEmpty(cliCartridge) && File.Exists(cliCartridge))
        {
            LoadCartridgePath(cliCartridge);
        }
        else if (_system.BootRomLoaded)
        {
            lock (_emuLock) { _system.Reset(); }
        }

        // UI repaint & status timer (decoupled from emulation thread)
        _uiTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _uiTimer.Tick += (_, _) =>
        {
            _viewport.Invalidate();
            UpdateStatusBar();
        };
        _uiTimer.Start();

        // Start background emulation thread
        _running = true;
        _emuThread = new Thread(EmulationLoop)
        {
            Name = "ReaLynx-Emulation",
            IsBackground = true
        };
        _emuThread.Start();

        KeyDown += MainForm_KeyDown;
        KeyUp += MainForm_KeyUp;
        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;
        Deactivate += (_, _) => { lock (_pressedKeys) { _pressedKeys.Clear(); } };
        FormClosing += MainForm_FormClosing;
    }

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && File.Exists(files[0]))
            {
                e.Effect = DragDropEffects.Copy;
                return;
            }
        }
        e.Effect = DragDropEffects.None;
    }

    private void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        string path = files[0];
        if (!File.Exists(path)) return;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        long size = new FileInfo(path).Length;

        if (size == LynxSystem.BootRomSize && ext is ".img" or ".rom" or ".bin")
        {
            try
            {
                lock (_emuLock)
                {
                    _system.LoadBootRomFromFile(path);
                    _system.Reset();
                }
                _settings.BootRomPath = path;
                _settings.Save();
                _audio.Reset();
                if (_paused) TogglePause();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Boot ROM Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        LoadCartridgePath(path);
        if (_paused) TogglePause();
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
    }

    private CartRotation GetEffectiveRotation()
    {
        if (_settings.RotationMode != RotationOverrideMode.Auto)
        {
            return _settings.RotationMode switch
            {
                RotationOverrideMode.Left  => CartRotation.Left,
                RotationOverrideMode.Right => CartRotation.Right,
                _                          => CartRotation.None
            };
        }

        var cart = _system.Cartridge;
        if (cart != null && _settings.PerCartRotation.TryGetValue(cart.Info.Sha256, out int savedRot))
        {
            return (CartRotation)Math.Clamp(savedRot, 0, 2);
        }

        return cart?.Info.Rotation ?? CartRotation.None;
    }

    private void CycleRotation()
    {
        CartRotation next = GetEffectiveRotation() switch
        {
            CartRotation.None  => CartRotation.Left,
            CartRotation.Left  => CartRotation.Right,
            _                  => CartRotation.None
        };

        var cart = _system.Cartridge;
        if (cart != null)
        {
            _settings.PerCartRotation[cart.Info.Sha256] = (int)next;
            _settings.RotationMode = RotationOverrideMode.Auto;
        }
        else
        {
            _settings.RotationMode = next switch
            {
                CartRotation.Left  => RotationOverrideMode.Left,
                CartRotation.Right => RotationOverrideMode.Right,
                _                  => RotationOverrideMode.None
            };
        }

        _settings.Save();
        UpdateRotationChecks();
        _viewport.Invalidate();
    }

    private void SetRotationMode(RotationOverrideMode mode)
    {
        _settings.RotationMode = mode;
        var cart = _system.Cartridge;
        if (cart != null)
        {
            if (mode == RotationOverrideMode.Auto)
                _settings.PerCartRotation.Remove(cart.Info.Sha256);
            else
                _settings.PerCartRotation[cart.Info.Sha256] = (int)GetEffectiveRotation();
        }
        _settings.Save();
        UpdateRotationChecks();
        _viewport.Invalidate();
    }

    private void UpdateRotationChecks()
    {
        _rotAutoItem.Checked = _settings.RotationMode == RotationOverrideMode.Auto;
        _rotNoneItem.Checked = _settings.RotationMode == RotationOverrideMode.None;
        _rotLeftItem.Checked = _settings.RotationMode == RotationOverrideMode.Left;
        _rotRightItem.Checked = _settings.RotationMode == RotationOverrideMode.Right;
    }

    private void TryLoadBootRomSilent()
    {
        _settings.TryAutoDiscoverBootRom();
        if (!string.IsNullOrEmpty(_settings.BootRomPath) && File.Exists(_settings.BootRomPath))
        {
            try
            {
                lock (_emuLock)
                {
                    _system.LoadBootRomFromFile(_settings.BootRomPath);
                }
            }
            catch
            {
                // User can manually select a valid 512-byte boot ROM
            }
        }
    }

    private void SelectBootRomDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select Atari Lynx Boot ROM (512-byte lynxboot.img)",
            Filter = "Lynx Boot ROM (*.img;*.rom;*.bin)|*.img;*.rom;*.bin|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                lock (_emuLock)
                {
                    _system.LoadBootRomFromFile(dlg.FileName);
                    _system.Reset();
                }
                _settings.BootRomPath = dlg.FileName;
                _settings.Save();
                _audio.Reset();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Boot ROM Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void OpenCartridgeDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open Atari Lynx Cartridge",
            Filter = "Atari Lynx ROMs (*.lnx;*.lyx;*.o)|*.lnx;*.lyx;*.o|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            LoadCartridgePath(dlg.FileName);
        }
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevBorderStyle = FormBorderStyle;
            _prevWindowState = WindowState;
            _prevBounds = Bounds;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            if (MainMenuStrip != null) MainMenuStrip.Visible = false;
            _statusStrip.Visible = false;
            _isFullscreen = true;
        }
        else
        {
            FormBorderStyle = _prevBorderStyle;
            WindowState = _prevWindowState;
            if (_prevWindowState == FormWindowState.Normal) Bounds = _prevBounds;
            if (MainMenuStrip != null) MainMenuStrip.Visible = true;
            _statusStrip.Visible = true;
            _isFullscreen = false;
        }
        _viewport.Invalidate();
    }

    private void LoadCartridgePath(string path)
    {
        try
        {
            var cart = LynxCartridge.LoadFromFile(path);
            lock (_emuLock)
            {
                _system.LoadCartridge(cart);
                if (_system.BootRomLoaded)
                    _system.Reset();
            }
            _audio.Reset();
            _settings.AddRecentFile(path);
            RebuildRecentMenu();

            if (!_system.BootRomLoaded)
            {
                MessageBox.Show(this,
                    "Cartridge loaded, but no 512-byte Atari Lynx Boot ROM is configured.\n" +
                    "Use File -> Select Boot ROM... to point to your legally supplied lynxboot.img.",
                    "Boot ROM Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cartridge Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RebuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            var empty = new ToolStripMenuItem("(None)") { Enabled = false };
            _recentMenu.DropDownItems.Add(empty);
            return;
        }
        foreach (string path in _settings.RecentFiles)
        {
            string p = path;
            var item = new ToolStripMenuItem(Path.GetFileName(p))
            {
                ToolTipText = p
            };
            item.Click += (_, _) => LoadCartridgePath(p);
            _recentMenu.DropDownItems.Add(item);
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseMenuItem.Text = _paused ? "&Resume" : "&Pause";
        if (_paused) _audio.Reset();
        else _wakeEvent.Set();
    }

    private void ResetConsole()
    {
        lock (_emuLock)
        {
            _system.Reset();
            CopyFramebufferLocked();
        }
        _audio.Reset();
        _viewport.Invalidate();
    }

    private void ToggleMute()
    {
        _settings.Muted = !_settings.Muted;
        _audio.Muted = _settings.Muted;
        _muteMenuItem.Checked = _settings.Muted;
        _settings.Save();
    }

    private void AdjustVolume(float delta)
    {
        _settings.Volume = Math.Clamp(_settings.Volume + delta, 0f, 1f);
        _audio.Volume = _settings.Volume;
        _settings.Save();
    }

    private void OpenControlsDialog()
    {
        using var dlg = new ControlsConfigForm(_settings);
        dlg.ShowDialog(this);
    }

    private void OpenDebugger()
    {
        if (_debuggerForm == null || _debuggerForm.IsDisposed)
        {
            _debuggerForm = new DebuggerForm(
                _system,
                _emuLock,
                onStep: () =>
                {
                    _paused = true;
                    _pauseMenuItem.Text = "&Resume";
                    lock (_emuLock)
                    {
                        _system.StepInstruction();
                        CopyFramebufferLocked();
                    }
                    _viewport.Invalidate();
                },
                onTogglePause: TogglePause,
                isPaused: () => _paused);
            _debuggerForm.Show(this);
        }
        else
        {
            _debuggerForm.Focus();
        }
    }

    private void SaveScreenshotDialog()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save Screenshot",
            Filter = "PNG Image (*.png)|*.png|BMP Image (*.bmp)|*.bmp",
            FileName = $"realynx_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        uint[] snapshot;
        lock (_fbLock)
        {
            snapshot = (uint[])_presentPixels.Clone();
        }

        var rot = GetEffectiveRotation();
        var (pixels, w, h) = BmpWriter.GetRotatedFramebuffer(snapshot, rot);

        if (dlg.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            BmpWriter.SaveBgra32Bmp(dlg.FileName, pixels, w, h);
        }
        else
        {
            using var bmp = CreateBitmapFromPixels(pixels, w, h);
            bmp.Save(dlg.FileName, ImageFormat.Png);
        }
    }

    private LynxButtons PollInputs()
    {
        LynxButtons buttons = XInputController.Poll(0);

        lock (_pressedKeys)
        {
            bool IsDown(string action) =>
                _settings.KeyBindings.TryGetValue(action, out Keys k) && _pressedKeys.Contains(k);

            if (IsDown("Up")) buttons |= LynxButtons.Up;
            if (IsDown("Down")) buttons |= LynxButtons.Down;
            if (IsDown("Left")) buttons |= LynxButtons.Left;
            if (IsDown("Right")) buttons |= LynxButtons.Right;
            if (IsDown("A")) buttons |= LynxButtons.A;
            if (IsDown("B")) buttons |= LynxButtons.B;
            if (IsDown("Option1")) buttons |= LynxButtons.Option1;
            if (IsDown("Option2")) buttons |= LynxButtons.Option2;
            if (IsDown("Pause")) buttons |= LynxButtons.Pause;
        }

        // Remap D-pad directions to match screen rotation so Up/Down/Left/Right feel natural
        return RotateDpadForScreen(buttons, GetEffectiveRotation());
    }

    private static LynxButtons RotateDpadForScreen(LynxButtons input, CartRotation rotation)
    {
        if (rotation == CartRotation.None) return input;

        LynxButtons nonDpad = input & ~(LynxButtons.Up | LynxButtons.Down | LynxButtons.Left | LynxButtons.Right);
        LynxButtons rotated = nonDpad;

        if (rotation == CartRotation.Left) // 90 deg CCW: physical Up -> Lynx Right
        {
            if ((input & LynxButtons.Up) != 0)    rotated |= LynxButtons.Right;
            if ((input & LynxButtons.Right) != 0) rotated |= LynxButtons.Down;
            if ((input & LynxButtons.Down) != 0)  rotated |= LynxButtons.Left;
            if ((input & LynxButtons.Left) != 0)  rotated |= LynxButtons.Up;
        }
        else // CartRotation.Right: 90 deg CW: physical Up -> Lynx Left
        {
            if ((input & LynxButtons.Up) != 0)    rotated |= LynxButtons.Left;
            if ((input & LynxButtons.Left) != 0)  rotated |= LynxButtons.Down;
            if ((input & LynxButtons.Down) != 0)  rotated |= LynxButtons.Right;
            if ((input & LynxButtons.Right) != 0) rotated |= LynxButtons.Up;
        }

        return rotated;
    }

    private void EmulationLoop()
    {
        var sw = Stopwatch.StartNew();
        double lastWallSec = sw.Elapsed.TotalSeconds;
        double tickBudget = 0;

        double statsWindowStart = lastWallSec;
        long statsTicks = 0;
        long statsFrames = 0;
        short[] audioDrainBuf = new short[4096];

        while (_running)
        {
            if (_paused || !_system.BootRomLoaded)
            {
                _emulationSpeedPct = 0;
                _emulatedFps = 0;
                _wakeEvent.Wait(25);
                _wakeEvent.Reset();
                lastWallSec = sw.Elapsed.TotalSeconds;
                tickBudget = 0;
                statsWindowStart = lastWallSec;
                statsTicks = 0;
                statsFrames = 0;
                continue;
            }

            double now = sw.Elapsed.TotalSeconds;
            double dt = now - lastWallSec;
            lastWallSec = now;

            // Clamp catch-up to avoid spiral-of-death after system sleep or breakpoint
            if (dt > 0.05) dt = 0.05;

            // Slight audio-buffer-aware pacing adjustment (keeps audio queue smoothly around 2-4 buffers)
            bool ff = _fastForward;
            int queued = _audio.QueuedBuffers;
            double paceScale = ff ? 4.0 : (queued >= 6 ? 0.96 : (queued <= 1 && _audio.IsAvailable ? 1.02 : 1.0));
            tickBudget += dt * MikeyChip.MasterClockHz * paceScale;

            const long sliceTicks = LynxSystem.DefaultMasterTicksPerFrame / 2; // ~8.3ms slices for low input latency
            if (tickBudget >= sliceTicks)
            {
                long ranTicks = 0;
                int drainedAudio = 0;
                bool hitBp = false;
                long beforeFrames;
                long afterFrames;

                LynxButtons inputs = PollInputs();

                try
                {
                    lock (_emuLock)
                    {
                        _system.SetButtons(inputs);
                        beforeFrames = _system.TotalFrames;
                        ranTicks = _system.RunMasterTicks(sliceTicks);
                        afterFrames = _system.TotalFrames;
                        hitBp = _system.HitBreakpoint;

                        if (afterFrames > beforeFrames)
                        {
                            CopyFramebufferLocked();
                        }

                        drainedAudio = _system.Mikey.DrainAudioSamples(audioDrainBuf, audioDrainBuf.Length);
                    }
                }
                catch (Exception ex)
                {
                    _paused = true;
                    BeginInvoke(() =>
                    {
                        _pauseMenuItem.Text = "&Resume";
                        MessageBox.Show(this, $"Emulation error: {ex.Message}", "ReaLynx Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                    continue;
                }

                tickBudget -= ranTicks;
                if (tickBudget < -sliceTicks) tickBudget = 0;

                if (drainedAudio > 0 && !ff)
                {
                    _audio.SubmitSamples(audioDrainBuf, drainedAudio);
                }

                statsTicks += ranTicks;
                statsFrames += (afterFrames - beforeFrames);

                if (hitBp)
                {
                    _paused = true;
                    BeginInvoke(() =>
                    {
                        _pauseMenuItem.Text = "&Resume";
                        _debuggerForm?.RefreshAll();
                    });
                }

                double statsElapsed = now - statsWindowStart;
                if (statsElapsed >= 0.5)
                {
                    _emulationSpeedPct = (statsTicks / (statsElapsed * MikeyChip.MasterClockHz)) * 100.0;
                    _emulatedFps = statsFrames / statsElapsed;
                    statsWindowStart = now;
                    statsTicks = 0;
                    statsFrames = 0;
                }
            }
            else
            {
                double missingSec = (sliceTicks - tickBudget) / MikeyChip.MasterClockHz;
                int waitMs = Math.Clamp((int)(missingSec * 1000.0), 1, 8);
                _wakeEvent.Wait(waitMs);
                _wakeEvent.Reset();
            }
        }
    }

    private void CopyFramebufferLocked()
    {
        lock (_fbLock)
        {
            Array.Copy(_system.Mikey.Framebuffer, _presentPixels, _presentPixels.Length);
        }
    }

    private void Viewport_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        if (!_system.BootRomLoaded)
        {
            using var font = new Font("Segoe UI", 12f);
            using var brush = new SolidBrush(Color.LightGray);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(
                "ReaLynx — Atari Lynx Emulator\n\nSelect a legally supplied 512-byte Boot ROM (File -> Select Boot ROM...)\nand open a .lnx cartridge (File -> Open Cartridge...) to begin.",
                font, brush, _viewport.ClientRectangle, sf);
            return;
        }

        uint[] snapshot = new uint[_presentPixels.Length];
        lock (_fbLock)
        {
            Array.Copy(_presentPixels, snapshot, snapshot.Length);
        }

        var rot = GetEffectiveRotation();
        var (pixels, srcW, srcH) = BmpWriter.GetRotatedFramebuffer(snapshot, rot);

        if (_displayBitmap == null || _bitmapWidth != srcW || _bitmapHeight != srcH)
        {
            _displayBitmap?.Dispose();
            _displayBitmap = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
            _bitmapWidth = srcW;
            _bitmapHeight = srcH;
        }

        var bmpData = _displayBitmap.LockBits(new Rectangle(0, 0, srcW, srcH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] signed = Array.ConvertAll(pixels, u => unchecked((int)u));
            Marshal.Copy(signed, 0, bmpData.Scan0, signed.Length);
        }
        finally
        {
            _displayBitmap.UnlockBits(bmpData);
        }

        int cw = _viewport.ClientSize.Width;
        int ch = _viewport.ClientSize.Height;
        if (cw <= 0 || ch <= 0) return;

        int drawW, drawH;
        if (_settings.IntegerScaling)
        {
            int scale = Math.Max(1, Math.Min(cw / srcW, ch / srcH));
            drawW = srcW * scale;
            drawH = srcH * scale;
        }
        else
        {
            double scale = Math.Min((double)cw / srcW, (double)ch / srcH);
            drawW = Math.Max(1, (int)Math.Round(srcW * scale));
            drawH = Math.Max(1, (int)Math.Round(srcH * scale));
        }

        int drawX = (cw - drawW) / 2;
        int drawY = (ch - drawH) / 2;

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_displayBitmap, new Rectangle(drawX, drawY, drawW, drawH));
    }

    private static Bitmap CreateBitmapFromPixels(uint[] pixels, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] signed = Array.ConvertAll(pixels, u => unchecked((int)u));
            Marshal.Copy(signed, 0, data.Scan0, signed.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private void UpdateStatusBar()
    {
        _statusState.Text = !_system.BootRomLoaded ? "No Boot ROM" : (_paused ? "Paused" : "Running");
        _statusSpeed.Text = $"{_emulationSpeedPct:0.0}% ({_emulatedFps:0.0} FPS)";

        var cart = _system.Cartridge;
        _statusCart.Text = cart != null
            ? $"{cart.Info.Name} [{cart.Info.Format}, Rot={GetEffectiveRotation()}]"
            : $"No Cartridge [Rot={GetEffectiveRotation()}]";

        string volStr = _settings.Muted ? "Muted" : $"{(int)(_settings.Volume * 100)}%";
        _statusDiag.Text = $"Vol: {volStr} | AudioQ: {_audio.QueuedBuffers}/8";
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Tab)
        {
            _fastForward = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            return;
        }
        lock (_pressedKeys) { _pressedKeys.Add(e.KeyCode); }
    }

    private void MainForm_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Tab)
        {
            _fastForward = false;
            return;
        }
        lock (_pressedKeys) { _pressedKeys.Remove(e.KeyCode); }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _uiTimer.Stop();
        _running = false;
        _wakeEvent.Set();
        _emuThread?.Join(500);
        _audio.Dispose();
        _displayBitmap?.Dispose();
        _wakeEvent.Dispose();
    }
}
