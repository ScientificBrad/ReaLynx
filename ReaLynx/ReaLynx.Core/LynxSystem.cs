using ReaLynx.Core.Cartridge;
using ReaLynx.Core.Cpu;
using ReaLynx.Core.Mikey;
using ReaLynx.Core.Suzy;

namespace ReaLynx.Core;

/// <summary>16-bit address bus interface used by the 65SC02 CPU and debugger.</summary>
public interface IMemoryBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
    byte Peek(ushort address);
}

public readonly record struct TraceEntry(
    long MasterTicks,
    ushort PC,
    string BytesHex,
    string Mnemonic,
    byte A,
    byte X,
    byte Y,
    byte SP,
    byte P);

/// <summary>
/// Complete Atari Lynx console combining 64KB RAM, 512-byte Boot ROM, MAPCTL ($FFF9),
/// 65SC02 CPU, Mikey, Suzy, Cartridge interface, and integrated debugger.
/// </summary>
public sealed class LynxSystem : IMemoryBus
{
    public const int RamSize = 65536;
    public const int BootRomSize = 512;
    public const int MasterTicksPerCpuCycle = 4; // 16 MHz / 4 = 4 MHz CPU
    public const int DefaultMasterTicksPerFrame = 266_667; // ~60 Hz (16,000,000 / 60)

    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _bootRom = new byte[BootRomSize];
    private bool _bootRomLoaded;

    // MAPCTL ($FFF9): bit 0 = disable Suzy, bit 1 = disable Mikey, bit 2 = disable ROM ($FE00-$FFF7),
    // bit 3 = disable ROM vectors ($FFFA-$FFFF), bit 7 = sequential mode
    private byte _mapCtl;

    // Debugger state
    private readonly HashSet<ushort> _breakpoints = [];
    private readonly Queue<TraceEntry> _traceBuffer = new();
    public bool TraceEnabled { get; set; }
    public int MaxTraceEntries { get; set; } = 4096;
    public bool HitBreakpoint { get; private set; }
    public ushort LastBreakpointAddress { get; private set; }

    public Cpu65SC02 Cpu { get; }
    public MikeyChip Mikey { get; }
    public SuzyChip Suzy { get; }
    public LynxCartridge? Cartridge { get; private set; }

    public byte MapCtl => _mapCtl;
    public bool BootRomLoaded => _bootRomLoaded;
    public string? BootRomPath { get; private set; }
    public long TotalMasterTicks { get; private set; }
    public long TotalFrames { get; private set; }
    public byte[] Ram => _ram;

    public LynxSystem()
    {
        Suzy = new SuzyChip(_ram);
        Mikey = new MikeyChip(
            _ram,
            onCartStrobe: (strobe, dataBit, audinBit) => Cartridge?.UpdateAddressStrobe(strobe, dataBit, audinBit),
            onCpuSleep: OnCpuSleepRequested,
            onSuzyDoneAck: () => Suzy.AcknowledgeDone());
        Cpu = new Cpu65SC02(this);
    }

    private void OnCpuSleepRequested()
    {
        // In our synchronous sprite engine, Suzy already finished painting on SPRGO write,
        // so CPUSLEEP only sleeps if Suzy is still marked working.
        if (Suzy.SpriteWorking)
        {
            Cpu.IsSleeping = true;
        }
    }

    public void LoadBootRomFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Boot ROM file not found: {path}", path);

        byte[] data = File.ReadAllBytes(path);
        LoadBootRom(data);
        BootRomPath = path;
    }

    public void LoadBootRom(byte[] romData)
    {
        if (romData.Length != BootRomSize)
            throw new ArgumentException($"Atari Lynx boot ROM must be exactly {BootRomSize} bytes (got {romData.Length} bytes).");

        Buffer.BlockCopy(romData, 0, _bootRom, 0, BootRomSize);
        _bootRomLoaded = true;
    }

    public void LoadCartridge(LynxCartridge cartridge)
    {
        Cartridge = cartridge;
        Suzy.AttachCartridge(cartridge);
    }

    public void UnloadCartridge()
    {
        Cartridge = null;
        Suzy.AttachCartridge(null);
    }

    public void Reset()
    {
        Array.Clear(_ram);
        _mapCtl = 0x00; // All hardware & Boot ROM mapped on reset
        Mikey.Reset();
        Suzy.Reset();
        Cartridge?.Reset();
        Cpu.Reset();

        // Standard BS93/BLL homebrew RAM executables load directly into RAM (no cartridge ROM to decrypt)
        if (Cartridge is { IsHomebrewRamImage: true } hb)
        {
            Buffer.BlockCopy(hb.HomebrewPayload, 0, _ram, hb.HomebrewLoadAddress, hb.HomebrewPayload.Length);
            // Set up standard Lynx display timers & Suzy bus as BLL loader leaves them
            Mikey.Write(0xFD00, 0x9E); Mikey.Write(0xFD01, 0x18); // Timer 0 HBL (159us)
            Mikey.Write(0xFD08, 0x68); Mikey.Write(0xFD09, 0x1F); // Timer 2 VBL (105 lines)
            Suzy.Write(0xFC90, 0x01);                             // SUZYBUSEN = 1
            Cpu.PC = hb.HomebrewLoadAddress;
            Cpu.SP = 0xFF;
        }

        TotalMasterTicks = 0;
        TotalFrames = 0;
        HitBreakpoint = false;
        _traceBuffer.Clear();
    }

    public void SetButtons(LynxButtons buttons) => Suzy.SetButtons(buttons);

    // ===== Debugger API =====

    public void AddBreakpoint(ushort address) => _breakpoints.Add(address);
    public void RemoveBreakpoint(ushort address) => _breakpoints.Remove(address);
    public void ClearBreakpoints() => _breakpoints.Clear();
    public IReadOnlyCollection<ushort> Breakpoints => _breakpoints;
    public IReadOnlyCollection<TraceEntry> TraceLog => _traceBuffer;
    public void ClearTrace() => _traceBuffer.Clear();

    /// <summary>
    /// Executes a single 65SC02 instruction and advances Mikey by the corresponding master clock ticks.
    /// Returns the number of 16 MHz master ticks elapsed.
    /// </summary>
    public int StepInstruction()
    {
        HitBreakpoint = false;

        if (TraceEnabled)
        {
            var dis = Disassembler65SC02.Disassemble(this, Cpu.PC);
            if (_traceBuffer.Count >= MaxTraceEntries)
                _traceBuffer.Dequeue();
            _traceBuffer.Enqueue(new TraceEntry(
                TotalMasterTicks, Cpu.PC, dis.BytesHex, dis.Mnemonic,
                Cpu.A, Cpu.X, Cpu.Y, Cpu.SP, Cpu.P));
        }

        Cpu.IrqLine = Mikey.IrqPending;
        int cpuCycles = Cpu.Step();
        int masterTicks = cpuCycles * MasterTicksPerCpuCycle;

        Mikey.Advance(masterTicks);
        TotalMasterTicks += masterTicks;
        Cpu.IrqLine = Mikey.IrqPending;

        if (Mikey.FrameCompleted)
        {
            Mikey.FrameCompleted = false;
            TotalFrames++;
        }

        if (_breakpoints.Count > 0 && _breakpoints.Contains(Cpu.PC))
        {
            HitBreakpoint = true;
            LastBreakpointAddress = Cpu.PC;
        }

        return masterTicks;
    }

    /// <summary>
    /// Deterministically runs the emulator for at least <paramref name="masterTicks"/> 16 MHz ticks,
    /// stopping early if a debugger breakpoint is hit.
    /// </summary>
    public long RunMasterTicks(long masterTicks)
    {
        long elapsed = 0;
        while (elapsed < masterTicks)
        {
            elapsed += StepInstruction();
            if (HitBreakpoint) break;
        }
        return elapsed;
    }

    /// <summary>
    /// Runs until Mikey completes one vertical frame (or falls back to ~60 Hz master tick budget
    /// if the software has not yet configured Timer 0/Timer 2).
    /// </summary>
    public long RunFrame()
    {
        long startFrames = TotalFrames;
        long elapsed = 0;
        const long maxTicksPerFrame = DefaultMasterTicksPerFrame;

        while (TotalFrames == startFrames && elapsed < maxTicksPerFrame)
        {
            elapsed += StepInstruction();
            if (HitBreakpoint) break;
        }

        // If software didn't trigger VBL DMA yet but has a valid DISPADR, refresh framebuffer
        if (TotalFrames == startFrames)
        {
            if ((Mikey.DispCtl & 0x01) != 0)
                Mikey.RenderFullFramebufferFromRam();
            TotalFrames++;
        }

        return elapsed;
    }

    // ===== IMemoryBus (Atari Lynx Memory Map & MAPCTL $FFF9) =====

    public byte Read(ushort address)
    {
        if (address >= 0xFC00)
        {
            if (address < 0xFD00)
            {
                if ((_mapCtl & 0x01) == 0)
                    return Suzy.Read(address);
                return _ram[address];
            }
            if (address < 0xFE00)
            {
                if ((_mapCtl & 0x02) == 0)
                    return Mikey.Read(address);
                return _ram[address];
            }
            if (address == 0xFFF8)
            {
                return _ram[0xFFF8];
            }
            if (address == 0xFFF9)
            {
                return _mapCtl;
            }
            if (address >= 0xFFFA)
            {
                if ((_mapCtl & 0x08) == 0 && _bootRomLoaded)
                    return _bootRom[address - 0xFE00];
                return _ram[address];
            }
            // $FE00..$FFF7
            if ((_mapCtl & 0x04) == 0 && _bootRomLoaded)
                return _bootRom[address - 0xFE00];
            return _ram[address];
        }

        return _ram[address];
    }

    public void Write(ushort address, byte value)
    {
        if (address >= 0xFC00)
        {
            if (address < 0xFD00)
            {
                if ((_mapCtl & 0x01) == 0)
                {
                    Suzy.Write(address, value);
                    return;
                }
                _ram[address] = value;
                return;
            }
            if (address < 0xFE00)
            {
                if ((_mapCtl & 0x02) == 0)
                {
                    Mikey.Write(address, value);
                    return;
                }
                _ram[address] = value;
                return;
            }
            if (address == 0xFFF9)
            {
                _mapCtl = value;
                return;
            }
            // Writes to $FE00..$FFF8 and $FFFA..$FFFF always go to underlying RAM
            _ram[address] = value;
            return;
        }

        _ram[address] = value;
    }

    public byte Peek(ushort address)
    {
        if (address >= 0xFC00)
        {
            if (address < 0xFD00)
                return (_mapCtl & 0x01) == 0 ? Suzy.Peek(address) : _ram[address];
            if (address < 0xFE00)
                return (_mapCtl & 0x02) == 0 ? Mikey.Peek(address) : _ram[address];
            if (address == 0xFFF8) return _ram[0xFFF8];
            if (address == 0xFFF9) return _mapCtl;
            if (address >= 0xFFFA)
                return ((_mapCtl & 0x08) == 0 && _bootRomLoaded) ? _bootRom[address - 0xFE00] : _ram[address];
            return ((_mapCtl & 0x04) == 0 && _bootRomLoaded) ? _bootRom[address - 0xFE00] : _ram[address];
        }
        return _ram[address];
    }
}
