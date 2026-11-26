using ReaLynx.Core;
using ReaLynx.Core.Cartridge;
using ReaLynx.Core.Cpu;
using ReaLynx.Core.Suzy;

namespace ReaLynx.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? bootRomPath = null;
        string? cartPath = null;
        string? screenshotPath = null;
        int frames = 60;
        long? maxCycles = null;
        bool trace = false;
        int traceLimit = 64;
        CartRotation? rotationOverride = null;
        var inputEvents = new List<(int Start, int End, LynxButtons Buttons)>();
        var breakpoints = new List<ushort>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--boot":
                    if (i + 1 < args.Length) bootRomPath = args[++i];
                    break;
                case "--cart":
                    if (i + 1 < args.Length) cartPath = args[++i];
                    break;
                case "--frames":
                    if (i + 1 < args.Length) frames = int.Parse(args[++i]);
                    break;
                case "--cycles":
                    if (i + 1 < args.Length) maxCycles = long.Parse(args[++i]);
                    break;
                case "--screenshot":
                    if (i + 1 < args.Length) screenshotPath = args[++i];
                    break;
                case "--trace":
                    trace = true;
                    break;
                case "--trace-limit":
                    if (i + 1 < args.Length) traceLimit = int.Parse(args[++i]);
                    break;
                case "--rotate":
                    if (i + 1 < args.Length)
                    {
                        rotationOverride = args[++i].ToLowerInvariant() switch
                        {
                            "left" => CartRotation.Left,
                            "right" => CartRotation.Right,
                            _ => CartRotation.None
                        };
                    }
                    break;
                case "--input":
                    // Format: button:startFrame-endFrame (e.g. a:900-910 or right:950-1000)
                    if (i + 1 < args.Length)
                    {
                        string[] parts = args[++i].Split(':');
                        if (parts.Length == 2)
                        {
                            string[] range = parts[1].Split('-');
                            int s = int.Parse(range[0]);
                            int e = range.Length > 1 ? int.Parse(range[1]) : s + 10;
                            LynxButtons btn = ParseButtons(parts[0]);
                            inputEvents.Add((s, e, btn));
                        }
                    }
                    break;
                case "--break":
                    if (i + 1 < args.Length)
                        breakpoints.Add(Convert.ToUInt16(args[++i].TrimStart('$'), 16));
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
            }
        }

        if (bootRomPath == null)
        {
            PrintUsage();
            return 1;
        }

        var sys = new LynxSystem
        {
            TraceEnabled = trace,
            MaxTraceEntries = traceLimit
        };

        try
        {
            sys.LoadBootRomFromFile(bootRomPath);
            if (cartPath != null)
            {
                var cart = LynxCartridge.LoadFromFile(cartPath);
                sys.LoadCartridge(cart);
                Console.WriteLine($"Loaded cartridge: {cart.Info.Name} ({cart.Info.Format})");
                Console.WriteLine($"  SHA-256:  {cart.Info.Sha256}");
                Console.WriteLine($"  Bank0:    {cart.Info.Bank0Bytes} bytes (page={cart.Info.Bank0PageSize})");
                Console.WriteLine($"  Bank1:    {cart.Info.Bank1Bytes} bytes (page={cart.Info.Bank1PageSize})");
                Console.WriteLine($"  Rotation: {cart.Info.Rotation}");
            }

            sys.Reset();
            foreach (ushort bp in breakpoints) sys.AddBreakpoint(bp);

            if (maxCycles.HasValue)
            {
                sys.RunMasterTicks(maxCycles.Value * LynxSystem.MasterTicksPerCpuCycle);
            }
            else
            {
                for (int f = 0; f < frames; f++)
                {
                    LynxButtons active = LynxButtons.None;
                    foreach (var ev in inputEvents)
                    {
                        if (f >= ev.Start && f <= ev.End)
                            active |= ev.Buttons;
                    }
                    sys.SetButtons(active);
                    sys.RunFrame();
                    if (sys.HitBreakpoint)
                    {
                        Console.WriteLine($"*** HIT BREAKPOINT ${sys.LastBreakpointAddress:X4} at frame {f} ***");
                        break;
                    }
                }
            }

            var cpu = sys.Cpu.GetState();
            var mikey = sys.Mikey.GetSnapshot();
            var suzy = sys.Suzy.GetSnapshot();
            int nonBlack = sys.Mikey.Framebuffer.Count(px => (px & 0x00FFFFFF) != 0);
            short[] audio = sys.Mikey.DrainAllAudioSamples();
            bool hasAudio = audio.Any(s => s != 0);

            Console.WriteLine("=== ReaLynx Headless Diagnostic Summary ===");
            Console.WriteLine($"Master Ticks: {sys.TotalMasterTicks:N0} | Frames: {sys.TotalFrames}");
            Console.WriteLine($"CPU: PC=${cpu.PC:X4} A=${cpu.A:X2} X=${cpu.X:X2} Y=${cpu.Y:X2} SP=${cpu.SP:X2} P=${cpu.P:X2} Sleep={cpu.IsSleeping} IRQ={cpu.IrqLine}");
            Console.WriteLine($"MAPCTL=${sys.MapCtl:X2} | DISPCTL=${mikey.DispCtl:X2} DISPADR=${mikey.DispAdr:X4} INT=${mikey.InterruptFlags:X2}");
            Console.WriteLine($"TIM0: BKUP={mikey.Timers[0].Backup} CTLA=${mikey.Timers[0].ControlA:X2} | TIM1: BKUP={mikey.Timers[1].Backup} CTLA=${mikey.Timers[1].ControlA:X2} | TIM2: BKUP={mikey.Timers[2].Backup} CTLA=${mikey.Timers[2].ControlA:X2}");
            Console.WriteLine($"SUZY: VIDBAS=${suzy.VidBas:X4} COLLBAS=${suzy.CollBas:X4} SCBNEXT=${suzy.ScbNext:X4} SPRSYS=${suzy.SprSys:X2} Sprites={suzy.LastSpriteCount}");
            Console.WriteLine($"Display: {nonBlack}/{sys.Mikey.Framebuffer.Length} non-black pixels | Audio samples={audio.Length} (active={hasAudio})");

            if (trace)
            {
                Console.WriteLine("=== Instruction Trace ===");
                foreach (var entry in sys.TraceLog)
                {
                    Console.WriteLine($"[{entry.MasterTicks,9}] ${entry.PC:X4}: {entry.BytesHex,-8} {entry.Mnemonic,-14} A={entry.A:X2} X={entry.X:X2} Y={entry.Y:X2} SP={entry.SP:X2} P={entry.P:X2}");
                }
            }

            if (screenshotPath != null)
            {
                var rot = rotationOverride ?? sys.Cartridge?.Info.Rotation ?? CartRotation.None;
                var (pixels, w, h) = BmpWriter.GetRotatedFramebuffer(sys.Mikey.Framebuffer, rot);
                BmpWriter.SaveBgra32Bmp(screenshotPath, pixels, w, h);
                Console.WriteLine($"Saved screenshot ({w}x{h}, rot={rot}) to: {screenshotPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static LynxButtons ParseButtons(string spec)
    {
        LynxButtons b = LynxButtons.None;
        foreach (string tok in spec.ToLowerInvariant().Split('+', ','))
        {
            b |= tok switch
            {
                "a" => LynxButtons.A,
                "b" => LynxButtons.B,
                "up" => LynxButtons.Up,
                "down" => LynxButtons.Down,
                "left" => LynxButtons.Left,
                "right" => LynxButtons.Right,
                "opt1" or "option1" => LynxButtons.Option1,
                "opt2" or "option2" => LynxButtons.Option2,
                "pause" or "start" => LynxButtons.Pause,
                _ => LynxButtons.None
            };
        }
        return b;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ReaLynx.Cli - Headless Atari Lynx Runner & Diagnostic Tool");
        Console.WriteLine("Usage:");
        Console.WriteLine("  ReaLynx.Cli --boot <lynxboot.img> [--cart <game.lnx>] [--frames N | --cycles N]");
        Console.WriteLine("              [--screenshot <out.bmp>] [--rotate none|left|right] [--trace] [--trace-limit N]");
    }
}
