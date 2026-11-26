namespace ReaLynx.Core.Mikey;

public readonly record struct TimerSnapshot(
    int Index,
    byte Backup,
    byte ControlA,
    byte Count,
    byte ControlB,
    bool Enabled,
    bool Reload,
    bool InterruptEnabled,
    byte ClockSource);

public readonly record struct AudioChannelSnapshot(
    int Index,
    sbyte Volume,
    byte Feedback,
    sbyte Output,
    ushort ShiftRegister,
    byte Backup,
    byte ControlA,
    byte Count,
    byte ControlB);

public readonly record struct MikeySnapshot(
    byte InterruptFlags,
    byte SysCtl1,
    byte IoDir,
    byte IoDat,
    byte SerCtl,
    byte SerDat,
    byte DispCtl,
    ushort DispAdr,
    byte MStereo,
    byte MPan,
    TimerSnapshot[] Timers,
    AudioChannelSnapshot[] Audio);

/// <summary>
/// Atari Lynx Mikey custom ASIC ($FD00-$FDFF):
/// - 8 hardware timers with clock dividers (1us..64us) and borrow linking
/// - Interrupt controller (INTRST/INTSET)
/// - 4-channel 12-bit LFSR stereo audio synthesizer with integrate mode
/// - 160x102 4bpp display scanner & 12-bit RGB palette
/// - Cartridge address strobe I/O (SYSCTL1, IODIR, IODAT)
/// - ComLynx UART registers (SERCTL, SERDAT)
/// </summary>
public sealed class MikeyChip
{
    public const int MasterClockHz = 16_000_000;
    public const int ScreenWidth = 160;
    public const int ScreenHeight = 102;
    public const int SampleRate = 44_100;

    // Clock divider periods in 16 MHz master ticks: 1us, 2us, 4us, 8us, 16us, 32us, 64us
    private static readonly int[] ClockPeriods = [16, 32, 64, 128, 256, 512, 1024];

    private sealed class TimerChannel
    {
        public byte Backup;
        public byte ControlA;
        public byte Count;
        public byte ControlB;
        public int TickAccum;

        public bool IntEnable => (ControlA & 0x80) != 0;
        public bool EnableReload => (ControlA & 0x10) != 0;
        public bool EnableCount => (ControlA & 0x08) != 0;
        public int ClockSource => ControlA & 0x07;
        public bool TimerDone
        {
            get => (ControlB & 0x08) != 0;
            set { if (value) ControlB |= 0x08; else ControlB &= unchecked((byte)~0x08); }
        }
        public bool BorrowOut
        {
            get => (ControlB & 0x01) != 0;
            set { if (value) ControlB |= 0x01; else ControlB &= unchecked((byte)~0x01); }
        }
        public bool BorrowIn
        {
            get => (ControlB & 0x02) != 0;
            set { if (value) ControlB |= 0x02; else ControlB &= unchecked((byte)~0x02); }
        }

        public void Reset()
        {
            Backup = 0;
            ControlA = 0;
            Count = 0;
            ControlB = 0;
            TickAccum = 0;
        }
    }

    private sealed class AudioChannel
    {
        public sbyte Volume;
        public byte Feedback;
        public sbyte Output;
        public ushort ShiftRegister; // 12-bit LFSR
        public byte Backup;
        public byte ControlA;
        public byte Count;
        public byte ControlB;
        public int TickAccum;

        public bool IntegrateMode => (ControlA & 0x20) != 0;
        public bool EnableReload => (ControlA & 0x10) != 0;
        public bool EnableCount => (ControlA & 0x08) != 0;
        public int ClockSource => ControlA & 0x07;

        public ushort FeedbackMask
        {
            get
            {
                // AUDxFEED bits 7,6,5,4,3,2,1,0 -> LFSR taps 11,10,5,4,3,2,1,0
                // AUDxCTLA bit 7 -> LFSR tap 7
                int mask = (Feedback & 0x3F)
                         | ((Feedback & 0x40) << 4) // bit 6 -> tap 10
                         | ((Feedback & 0x80) << 4) // bit 7 -> tap 11
                         | (ControlA & 0x80);       // bit 7 -> tap 7
                return (ushort)mask;
            }
        }

        public void Reset()
        {
            Volume = 0;
            Feedback = 0;
            Output = 0;
            ShiftRegister = 0;
            Backup = 0;
            ControlA = 0;
            Count = 0;
            ControlB = 0;
            TickAccum = 0;
        }
    }

    private readonly TimerChannel[] _timers = new TimerChannel[8];
    private readonly AudioChannel[] _audio = new AudioChannel[4];

    private readonly byte[] _ram;
    private readonly Action<bool, bool, bool> _onCartStrobe;
    private readonly Action _onCpuSleep;
    private readonly Action _onSuzyDoneAck;

    // Palette: 16 entries, 4-bit G ($FDA0..$FDAF), 4-bit B & 4-bit R ($FDB0..$FDBF)
    private readonly byte[] _green = new byte[16];
    private readonly byte[] _blueRed = new byte[16];
    private readonly uint[] _paletteBgra = new uint[16];

    // Display framebuffer (160 x 102 BGRA32):
    // _workingFramebuffer is updated scanline-by-scanline during active display;
    // Framebuffer holds the last 100% completed frame (published at VBL) to eliminate mid-frame tearing.
    private readonly uint[] _workingFramebuffer = new uint[ScreenWidth * ScreenHeight];
    public uint[] Framebuffer { get; } = new uint[ScreenWidth * ScreenHeight];
    private int _currentScanline;
    private ushort _dmaLineAddress;
    public bool FrameCompleted { get; set; }

    // Interrupt & System registers
    private byte _interruptFlags;
    private byte _sysCtl1;
    private byte _ioDir;
    private byte _ioDat;
    private byte _serCtl;
    private byte _serDat;
    private byte _dispCtl;
    private byte _pBkup;
    private ushort _dispAdr;

    // Lynx II stereo registers
    private readonly byte[] _atten = [0xFF, 0xFF, 0xFF, 0xFF];
    private byte _mPan;
    private byte _mStereo;

    // ComLynx UART state (local loopback optional; external link cable disconnected by default)
    public bool ComLynxLoopback { get; set; }
    private bool _uartRxReady;

    // Audio sample output buffer (interleaved stereo 16-bit PCM)
    private readonly List<short> _audioSampleBuffer = new(4096);
    private double _audioSampleAccum;
    private long _audioLeftAccum;
    private long _audioRightAccum;
    private int _audioAccumCount;
    private const double MasterTicksPerSample = (double)MasterClockHz / SampleRate;

    public bool IrqPending => _interruptFlags != 0;
    public byte DispCtl => _dispCtl;
    public ushort DispAdr => _dispAdr;

    public MikeyChip(
        byte[] ram,
        Action<bool, bool, bool> onCartStrobe,
        Action onCpuSleep,
        Action onSuzyDoneAck)
    {
        _ram = ram;
        _onCartStrobe = onCartStrobe;
        _onCpuSleep = onCpuSleep;
        _onSuzyDoneAck = onSuzyDoneAck;

        for (int i = 0; i < 8; i++) _timers[i] = new TimerChannel();
        for (int i = 0; i < 4; i++) _audio[i] = new AudioChannel();
        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i < 8; i++) _timers[i].Reset();
        for (int i = 0; i < 4; i++) _audio[i].Reset();

        Array.Clear(_green);
        Array.Clear(_blueRed);
        Array.Fill(_paletteBgra, 0xFF000000u);
        Array.Fill(_workingFramebuffer, 0xFF000000u);
        Array.Fill(Framebuffer, 0xFF000000u);

        _interruptFlags = 0;
        _sysCtl1 = 0;
        _ioDir = 0;
        _ioDat = 0x04; // Bit 2 (NOEXP) = 1 (no ComLynx cable inserted)
        _serCtl = 0;
        _serDat = 0;
        _dispCtl = 0;
        _pBkup = 0;
        _dispAdr = 0;
        _currentScanline = 0;
        _dmaLineAddress = 0;
        FrameCompleted = false;

        Array.Fill(_atten, (byte)0xFF);
        _mPan = 0;
        _mStereo = 0;
        _uartRxReady = false;

        _audioSampleBuffer.Clear();
        _audioSampleAccum = 0;
        _audioLeftAccum = 0;
        _audioRightAccum = 0;
        _audioAccumCount = 0;
    }

    public int DrainAudioSamples(short[] destination, int maxShorts)
    {
        int count = Math.Min(_audioSampleBuffer.Count, maxShorts);
        if (count == 0) return 0;
        _audioSampleBuffer.CopyTo(0, destination, 0, count);
        _audioSampleBuffer.RemoveRange(0, count);
        return count;
    }

    public short[] DrainAllAudioSamples()
    {
        if (_audioSampleBuffer.Count == 0) return [];
        short[] result = _audioSampleBuffer.ToArray();
        _audioSampleBuffer.Clear();
        return result;
    }

    /// <summary>
    /// Advances Mikey by the given number of 16 MHz master clock ticks.
    /// Chunks large advances so audio sampling stays accurate even when called with large intervals.
    /// </summary>
    public void Advance(int masterTicks)
    {
        const int maxChunk = 128; // ~8us (< 1 audio sample at 44.1 kHz)
        int remaining = masterTicks;
        while (remaining > 0)
        {
            int chunk = remaining > maxChunk ? maxChunk : remaining;
            AdvanceChunk(chunk);
            remaining -= chunk;
        }
    }

    private void AdvanceChunk(int masterTicks)
    {
        // Tick all unlinked timers (clock sources 0..6)
        for (int i = 0; i < 8; i++)
        {
            var t = _timers[i];
            if (!t.EnableCount || t.ClockSource == 7) continue;

            int period = ClockPeriods[t.ClockSource];
            t.TickAccum += masterTicks;
            while (t.TickAccum >= period)
            {
                t.TickAccum -= period;
                ClockTimer(i);
            }
        }

        // Tick all unlinked audio channels (clock sources 0..6)
        for (int ch = 0; ch < 4; ch++)
        {
            var a = _audio[ch];
            if (!a.EnableCount || a.ClockSource == 7) continue;

            int period = ClockPeriods[a.ClockSource];
            a.TickAccum += masterTicks;
            while (a.TickAccum >= period)
            {
                a.TickAccum -= period;
                ClockAudio(ch);
            }
        }

        // Sample audio into stereo PCM buffer
        SampleAudio(masterTicks);
    }

    private void ClockTimer(int index)
    {
        var t = _timers[index];
        if (!t.EnableCount) return;

        // One-shot timer (EnableReload == false) stops once TimerDone is set
        if (!t.EnableReload && t.TimerDone) return;

        if (t.Count == 0)
        {
            t.BorrowOut = true;
            t.TimerDone = true;

            if (t.IntEnable)
            {
                _interruptFlags |= (byte)(1 << index);
            }

            // Hardware display hooks on Timer 0 (HBL) and Timer 2 (VBL)
            if (index == 0)
            {
                OnHorizontalBlank();
            }
            else if (index == 2)
            {
                OnVerticalBlank();
            }

            // Propagate borrow-out to linked timer / audio channel
            PropagateTimerBorrow(index);

            if (t.EnableReload)
            {
                t.Count = t.Backup;
            }
        }
        else
        {
            t.Count--;
            t.BorrowOut = false;
        }
    }

    private void PropagateTimerBorrow(int timerIndex)
    {
        // Documented Mikey timer link chain:
        // 0->2->4, 1->3->5->7->Audio0->Audio1->Audio2->Audio3->1
        switch (timerIndex)
        {
            case 0: ClockLinkedTimer(2); break;
            case 2: ClockLinkedTimer(4); break;
            case 1: ClockLinkedTimer(3); break;
            case 3: ClockLinkedTimer(5); break;
            case 5: ClockLinkedTimer(7); break;
            case 7: ClockLinkedAudio(0); break;
        }
    }

    private void ClockLinkedTimer(int targetIndex)
    {
        var t = _timers[targetIndex];
        t.BorrowIn = true;
        if (t.EnableCount && t.ClockSource == 7)
        {
            ClockTimer(targetIndex);
        }
    }

    private void ClockLinkedAudio(int ch)
    {
        var a = _audio[ch];
        a.ControlB |= 0x02; // BorrowIn
        if (a.EnableCount && a.ClockSource == 7)
        {
            ClockAudio(ch);
        }
    }

    private void ClockAudio(int ch)
    {
        var a = _audio[ch];
        if (!a.EnableCount) return;

        // One-shot audio timer stops once TimerDone (bit 3) is set
        if (!a.EnableReload && (a.ControlB & 0x08) != 0) return;

        if (a.Count == 0)
        {
            a.ControlB |= 0x09; // TimerDone (bit 3) | BorrowOut (bit 0)

            // Clock LFSR if Backup != 0 or linked
            if (a.Backup != 0 || a.ClockSource == 7)
            {
                int tapped = a.ShiftRegister & a.FeedbackMask;
                // Parity of tapped bits, inverted (XNOR)
                int parity = System.Numerics.BitOperations.PopCount((uint)tapped) & 1;
                int newBit = parity ^ 1;
                a.ShiftRegister = (ushort)(((a.ShiftRegister << 1) | newBit) & 0x0FFF);

                if (a.IntegrateMode)
                {
                    int delta = (a.ShiftRegister & 1) != 0 ? a.Volume : -a.Volume;
                    a.Output = (sbyte)Math.Clamp(a.Output + delta, -128, 127);
                }
                else
                {
                    int val = (a.ShiftRegister & 1) != 0 ? a.Volume : -a.Volume;
                    a.Output = (sbyte)Math.Clamp(val, -128, 127);
                }
            }

            // Link chain: Audio0->Audio1->Audio2->Audio3->Timer1
            if (ch < 3)
                ClockLinkedAudio(ch + 1);
            else
                ClockLinkedTimer(1);

            if (a.EnableReload)
            {
                a.Count = a.Backup;
            }
        }
        else
        {
            a.Count--;
            a.ControlB &= unchecked((byte)~0x01); // Clear BorrowOut
        }
    }

    private void OnHorizontalBlank()
    {
        // Lines 0, 1, 2 are vertical blanking (giving the VBL ISR time to update DISPADR and palette).
        // Latch DISPADR at line 3 (start of first visible scanline 0) so double-buffered games never
        // scan out the stale buffer being overwritten by Suzy.
        if (_currentScanline == 3)
        {
            _dmaLineAddress = (ushort)(_dispAdr & 0xFFFC);
        }

        if ((_dispCtl & 0x01) != 0) // Bit 0 = Video DMA enabled
        {
            int line = _currentScanline - 3;
            if (line >= 0 && line < ScreenHeight)
            {
                RenderScanline(line, _dmaLineAddress, _workingFramebuffer);
                _dmaLineAddress = (ushort)(_dmaLineAddress + 80);
            }
        }
        _currentScanline++;
        if (!_timers[2].EnableCount && _currentScanline >= 105)
        {
            // Safety wrap only if Timer 2 (VCOUNT) isn't running
            PublishCompletedFrame();
            _currentScanline = 0;
        }
    }

    private void OnVerticalBlank()
    {
        PublishCompletedFrame();
        _currentScanline = 0;
    }

    private void PublishCompletedFrame()
    {
        Array.Copy(_workingFramebuffer, Framebuffer, Framebuffer.Length);
        FrameCompleted = true;
    }

    private void RenderScanline(int y, ushort lineAddr, uint[] targetBuffer)
    {
        bool flip = (_dispCtl & 0x02) != 0; // Bit 1 = 180-degree hardware flip
        int destY = flip ? (ScreenHeight - 1 - y) : y;
        int rowBase = destY * ScreenWidth;

        for (int b = 0; b < 80; b++)
        {
            byte packed = _ram[(ushort)(lineAddr + b)];
            int hiPen = (packed >> 4) & 0x0F;
            int loPen = packed & 0x0F;

            int x0 = b * 2;
            int x1 = x0 + 1;
            if (flip)
            {
                targetBuffer[rowBase + (ScreenWidth - 1 - x0)] = _paletteBgra[hiPen];
                targetBuffer[rowBase + (ScreenWidth - 1 - x1)] = _paletteBgra[loPen];
            }
            else
            {
                targetBuffer[rowBase + x0] = _paletteBgra[hiPen];
                targetBuffer[rowBase + x1] = _paletteBgra[loPen];
            }
        }
    }

    /// <summary>
    /// Renders the entire 160x102 screen directly from DISPADR in RAM using current palette.
    /// Useful for headless snapshots or games that update DISPADR without Timer 0/2 DMA.
    /// </summary>
    public void RenderFullFramebufferFromRam()
    {
        ushort addr = (ushort)(_dispAdr & 0xFFFC);
        for (int y = 0; y < ScreenHeight; y++)
        {
            RenderScanline(y, addr, Framebuffer);
            addr = (ushort)(addr + 80);
        }
    }

    private void SampleAudio(int masterTicks)
    {
        int left = 0;
        int right = 0;

        for (int ch = 0; ch < 4; ch++)
        {
            int sample = _audio[ch].Output; // -128..127

            // MSTEREO: bit (4+ch) disables left, bit ch disables right
            bool leftEnabled = (_mStereo & (1 << (4 + ch))) == 0;
            bool rightEnabled = (_mStereo & (1 << ch)) == 0;

            if (leftEnabled)
            {
                int att = (_mPan & (1 << (4 + ch))) != 0 ? ((_atten[ch] >> 4) & 0x0F) : 15;
                left += (sample * att) / 15;
            }
            if (rightEnabled)
            {
                int att = (_mPan & (1 << ch)) != 0 ? (_atten[ch] & 0x0F) : 15;
                right += (sample * att) / 15;
            }
        }

        _audioLeftAccum += (long)left * masterTicks;
        _audioRightAccum += (long)right * masterTicks;
        _audioAccumCount += masterTicks;
        _audioSampleAccum += masterTicks;

        while (_audioSampleAccum >= MasterTicksPerSample)
        {
            _audioSampleAccum -= MasterTicksPerSample;
            if (_audioAccumCount > 0)
            {
                // Scale 4 channels (-512..508) to 16-bit PCM (-32768..32767)
                int l = (int)((_audioLeftAccum * 48) / _audioAccumCount);
                int r = (int)((_audioRightAccum * 48) / _audioAccumCount);
                _audioSampleBuffer.Add((short)Math.Clamp(l, short.MinValue, short.MaxValue));
                _audioSampleBuffer.Add((short)Math.Clamp(r, short.MinValue, short.MaxValue));
            }
            else
            {
                _audioSampleBuffer.Add(0);
                _audioSampleBuffer.Add(0);
            }
            _audioLeftAccum = 0;
            _audioRightAccum = 0;
            _audioAccumCount = 0;
        }
    }

    private void RebuildPaletteEntry(int index)
    {
        int g = (_green[index] & 0x0F) * 17;
        int b = ((_blueRed[index] >> 4) & 0x0F) * 17;
        int r = (_blueRed[index] & 0x0F) * 17;
        _paletteBgra[index] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private void NotifyCartStrobe()
    {
        bool strobe = (_sysCtl1 & 0x01) != 0;
        bool dataBit = (_ioDat & 0x02) != 0;
        bool audinBit = (_ioDat & 0x10) != 0;
        _onCartStrobe(strobe, dataBit, audinBit);
    }

    public byte Read(ushort address) => ReadInternal(address, sideEffects: true);
    public byte Peek(ushort address) => ReadInternal(address, sideEffects: false);

    private byte ReadInternal(ushort address, bool sideEffects)
    {
        int reg = address & 0xFF;

        // Timers 0..7 ($FD00..$FD1F)
        if (reg <= 0x1F)
        {
            int idx = reg >> 2;
            return (reg & 3) switch
            {
                0 => _timers[idx].Backup,
                1 => _timers[idx].ControlA,
                2 => _timers[idx].Count,
                3 => _timers[idx].ControlB,
                _ => 0xFF
            };
        }

        // Audio 0..3 ($FD20..$FD3F)
        if (reg <= 0x3F)
        {
            int ch = (reg - 0x20) >> 3;
            var a = _audio[ch];
            return (reg & 7) switch
            {
                0 => unchecked((byte)a.Volume),
                1 => a.Feedback,
                2 => unchecked((byte)a.Output),
                3 => (byte)(a.ShiftRegister & 0xFF),
                4 => a.Backup,
                5 => a.ControlA,
                6 => a.Count,
                7 => (byte)(((a.ShiftRegister >> 4) & 0xF0) | (a.ControlB & 0x0F)),
                _ => 0xFF
            };
        }

        // Lynx II Stereo Attenuation ($FD40..$FD44, $FD50)
        if (reg >= 0x40 && reg <= 0x43) return _atten[reg - 0x40];
        if (reg == 0x44) return _mPan;
        if (reg == 0x50) return _mStereo;

        // Palette ($FDA0..$FDBF)
        if (reg >= 0xA0 && reg <= 0xAF) return _green[reg - 0xA0];
        if (reg >= 0xB0 && reg <= 0xBF) return _blueRed[reg - 0xB0];

        return reg switch
        {
            0x80 => _interruptFlags, // INTRST
            0x81 => _interruptFlags, // INTSET
            0x84 => 0x00,            // MAGRDY0
            0x85 => 0x00,            // MAGRDY1
            0x86 => 0x80,            // AUDIN
            0x87 => _sysCtl1,        // SYSCTL1
            0x88 => 0x01,            // MIKEYHREV
            0x89 => 0x00,            // MIKEYSREV
            0x8A => _ioDir,          // IODIR
            0x8B => (byte)(_ioDat | 0x04), // IODAT (NOEXP=1 when no cable)
            0x8C => GetSerCtlRead(), // SERCTL
            0x8D => ReadSerDat(sideEffects), // SERDAT
            0x90 => 0x00,            // SDONEACK
            0x91 => 0x00,            // CPUSLEEP
            0x92 => _dispCtl,        // DISPCTL
            0x93 => _pBkup,          // PBKUP
            0x94 => (byte)(_dispAdr & 0xFF),
            0x95 => (byte)(_dispAdr >> 8),
            _    => 0xFF
        };
    }

    private byte GetSerCtlRead()
    {
        // Bit 7: TXRDY (always ready), Bit 6: RXRDY, Bit 5: TXEMPTY (always empty)
        byte status = 0xA0; // TXRDY | TXEMPTY
        if (_uartRxReady) status |= 0x40;
        return status;
    }

    private byte ReadSerDat(bool sideEffects)
    {
        if (sideEffects) _uartRxReady = false;
        return _serDat;
    }

    public void Write(ushort address, byte value)
    {
        int reg = address & 0xFF;

        // Timers 0..7 ($FD00..$FD1F)
        if (reg <= 0x1F)
        {
            int idx = reg >> 2;
            var t = _timers[idx];
            switch (reg & 3)
            {
                case 0: t.Backup = value; break;
                case 1:
                    t.ControlA = (byte)(value & 0xBF); // Bit 6 is strobe-only ResetTimerDone
                    if ((value & 0x40) != 0) t.TimerDone = false;
                    break;
                case 2: t.Count = value; break;
                case 3: t.ControlB = (byte)(value & 0x0F); break;
            }
            return;
        }

        // Audio 0..3 ($FD20..$FD3F)
        if (reg <= 0x3F)
        {
            int ch = (reg - 0x20) >> 3;
            var a = _audio[ch];
            switch (reg & 7)
            {
                case 0: a.Volume = unchecked((sbyte)value); break;
                case 1: a.Feedback = value; break;
                case 2: a.Output = unchecked((sbyte)value); break;
                case 3: a.ShiftRegister = (ushort)((a.ShiftRegister & 0x0F00) | value); break;
                case 4: a.Backup = value; break;
                case 5:
                    a.ControlA = (byte)(value & 0xBF);
                    if ((value & 0x40) != 0) a.ControlB &= unchecked((byte)~0x08);
                    break;
                case 6: a.Count = value; break;
                case 7:
                    a.ShiftRegister = (ushort)((a.ShiftRegister & 0x00FF) | ((value & 0xF0) << 4));
                    a.ControlB = (byte)(value & 0x0F);
                    break;
            }
            return;
        }

        // Lynx II Stereo ($FD40..$FD44, $FD50)
        if (reg >= 0x40 && reg <= 0x43) { _atten[reg - 0x40] = value; return; }
        if (reg == 0x44) { _mPan = value; return; }
        if (reg == 0x50) { _mStereo = value; return; }

        // Palette ($FDA0..$FDBF)
        if (reg >= 0xA0 && reg <= 0xAF)
        {
            int idx = reg - 0xA0;
            _green[idx] = (byte)(value & 0x0F);
            RebuildPaletteEntry(idx);
            return;
        }
        if (reg >= 0xB0 && reg <= 0xBF)
        {
            int idx = reg - 0xB0;
            _blueRed[idx] = value;
            RebuildPaletteEntry(idx);
            return;
        }

        switch (reg)
        {
            case 0x80: // INTRST: write 1 bits to clear interrupt flags
                _interruptFlags &= (byte)~value;
                break;
            case 0x81: // INTSET: write 1 bits to set interrupt flags
                _interruptFlags |= value;
                break;
            case 0x87: // SYSCTL1
                _sysCtl1 = value;
                NotifyCartStrobe();
                break;
            case 0x8A: // IODIR
                _ioDir = value;
                break;
            case 0x8B: // IODAT
                _ioDat = value;
                NotifyCartStrobe();
                break;
            case 0x8C: // SERCTL
                _serCtl = value;
                if ((value & 0x08) != 0) _uartRxReady = false; // Reset errors
                break;
            case 0x8D: // SERDAT
                _serDat = value;
                if (ComLynxLoopback)
                {
                    _uartRxReady = true;
                    if ((_serCtl & 0x40) != 0) // RxIntEn
                        _interruptFlags |= 0x10; // Timer 4 / UART IRQ
                }
                break;
            case 0x90: // SDONEACK
                _onSuzyDoneAck();
                break;
            case 0x91: // CPUSLEEP
                _onCpuSleep();
                break;
            case 0x92: // DISPCTL
                _dispCtl = value;
                break;
            case 0x93: // PBKUP
                _pBkup = value;
                break;
            case 0x94: // DISPADRL
                _dispAdr = (ushort)((_dispAdr & 0xFF00) | value);
                break;
            case 0x95: // DISPADRH
                _dispAdr = (ushort)((_dispAdr & 0x00FF) | (value << 8));
                break;
        }
    }

    public MikeySnapshot GetSnapshot()
    {
        var timers = new TimerSnapshot[8];
        for (int i = 0; i < 8; i++)
        {
            var t = _timers[i];
            timers[i] = new TimerSnapshot(i, t.Backup, t.ControlA, t.Count, t.ControlB,
                t.EnableCount, t.EnableReload, t.IntEnable, (byte)t.ClockSource);
        }
        var audio = new AudioChannelSnapshot[4];
        for (int i = 0; i < 4; i++)
        {
            var a = _audio[i];
            audio[i] = new AudioChannelSnapshot(i, a.Volume, a.Feedback, a.Output,
                a.ShiftRegister, a.Backup, a.ControlA, a.Count, a.ControlB);
        }
        return new MikeySnapshot(_interruptFlags, _sysCtl1, _ioDir, _ioDat,
            _serCtl, _serDat, _dispCtl, _dispAdr, _mStereo, _mPan, timers, audio);
    }
}
