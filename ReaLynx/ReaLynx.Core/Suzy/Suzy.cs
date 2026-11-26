using ReaLynx.Core.Cartridge;

namespace ReaLynx.Core.Suzy;

[Flags]
public enum LynxButtons : ushort
{
    None    = 0,
    A       = 0x0001, // Outside (JOYSTICK bit 0)
    B       = 0x0002, // Inside  (JOYSTICK bit 1)
    Option2 = 0x0004, // JOYSTICK bit 2
    Option1 = 0x0008, // JOYSTICK bit 3
    Right   = 0x0010, // JOYSTICK bit 4
    Left    = 0x0020, // JOYSTICK bit 5
    Down    = 0x0040, // JOYSTICK bit 6
    Up      = 0x0080, // JOYSTICK bit 7
    Pause   = 0x0100, // SWITCHES bit 0
}

public readonly record struct SuzySnapshot(
    ushort VidBas,
    ushort CollBas,
    ushort ScbNext,
    short HOff,
    short VOff,
    byte SprCtl0,
    byte SprCtl1,
    byte SprColl,
    byte SprSys,
    byte SuzyBusEn,
    byte SprGo,
    byte Joystick,
    byte Switches,
    uint MathProductOrDividend,
    uint MathAccumOrRemainder,
    int LastSpriteCount);

/// <summary>
/// Atari Lynx Suzy custom ASIC ($FC00-$FCFF):
/// - Hardware 16x16 signed/unsigned multiply + 32-bit accumulate and 32/16 unsigned divide
/// - Full Sprite Control Block (SCB) graphics processor (1/2/3/4bpp, packed & literal, scaling,
///   stretching, tilting, H/V flip, 4 quadrants, all 8 sprite types, pen palette, collision buffer)
/// - Joystick, switches, and cartridge ROM data ports (RCART0/RCART1)
/// </summary>
public sealed class SuzyChip
{
    private readonly byte[] _ram;
    private LynxCartridge? _cartridge;

    // Address / geometry registers
    private ushort _tmpAdr;
    private ushort _tiltAcum;
    private short _hOff;
    private short _vOff;
    private ushort _vidBas;
    private ushort _collBas;
    private ushort _vidAdr;
    private ushort _collAdr;
    private ushort _scbNext;
    private ushort _sprDLine;
    private short _hPosStrt;
    private short _vPosStrt;
    private ushort _sprHSiz;
    private ushort _sprVSiz;
    private short _stretch;
    private short _tilt;
    private ushort _sprDOff;
    private ushort _sprVPos;
    private ushort _collOff;
    private ushort _vSizAcum;
    private ushort _hSizOff = 0x007F;
    private ushort _vSizOff = 0x007F;
    private ushort _scbAdr;
    private ushort _procAdr;

    // Hardware math registers
    private byte _mathA, _mathB, _mathC, _mathD;
    private byte _mathE, _mathF, _mathG, _mathH;
    private byte _mathJ, _mathK, _mathL, _mathM;
    private byte _mathN, _mathP;
    private bool _mathWarning;
    private bool _mathLastCarry;

    // Sprite control registers
    private byte _sprCtl0;
    private byte _sprCtl1;
    private byte _sprColl;
    private byte _sprInit;
    private byte _suzyBusEn;
    private byte _sprGo;
    private byte _sprSysWrite;
    private bool _spriteWorking;
    private bool _spriteStopped;
    private readonly byte[] _penIndex = new byte[16];

    // Controls
    private byte _joystick;
    private byte _switches;

    public int LastSpriteCount { get; private set; }
    public int LastSpriteCycles { get; private set; }
    public bool SpriteWorking => _spriteWorking;

    public SuzyChip(byte[] ram)
    {
        _ram = ram;
        Reset();
    }

    public void AttachCartridge(LynxCartridge? cartridge) => _cartridge = cartridge;

    public void SetButtons(LynxButtons buttons)
    {
        _joystick = (byte)((ushort)buttons & 0xFF);
        _switches = (byte)(((ushort)buttons >> 8) & 0x01);
    }

    public void Reset()
    {
        _tmpAdr = 0; _tiltAcum = 0; _hOff = 0; _vOff = 0;
        _vidBas = 0; _collBas = 0; _vidAdr = 0; _collAdr = 0;
        _scbNext = 0; _sprDLine = 0; _hPosStrt = 0; _vPosStrt = 0;
        _sprHSiz = 0; _sprVSiz = 0; _stretch = 0; _tilt = 0;
        _sprDOff = 0; _sprVPos = 0; _collOff = 0; _vSizAcum = 0;
        _hSizOff = 0x007F; _vSizOff = 0x007F;
        _scbAdr = 0; _procAdr = 0;

        _mathA = _mathB = _mathC = _mathD = 0;
        _mathE = _mathF = _mathG = _mathH = 0;
        _mathJ = _mathK = _mathL = _mathM = 0;
        _mathN = _mathP = 0;
        _mathWarning = false;
        _mathLastCarry = false;

        _sprCtl0 = 0; _sprCtl1 = 0; _sprColl = 0; _sprInit = 0;
        _suzyBusEn = 0; _sprGo = 0; _sprSysWrite = 0;
        _spriteWorking = false; _spriteStopped = false;
        for (int i = 0; i < 16; i++) _penIndex[i] = (byte)i;

        _joystick = 0;
        _switches = 0;
        LastSpriteCount = 0;
        LastSpriteCycles = 0;
    }

    public void AcknowledgeDone()
    {
        _spriteWorking = false;
    }

    public byte Read(ushort address) => ReadInternal(address, sideEffects: true);
    public byte Peek(ushort address) => ReadInternal(address, sideEffects: false);

    private byte ReadInternal(ushort address, bool sideEffects)
    {
        int reg = address & 0xFF;
        return reg switch
        {
            0x00 => (byte)(_tmpAdr & 0xFF),
            0x01 => (byte)(_tmpAdr >> 8),
            0x02 => (byte)(_tiltAcum & 0xFF),
            0x03 => (byte)(_tiltAcum >> 8),
            0x04 => (byte)(_hOff & 0xFF),
            0x05 => (byte)((_hOff >> 8) & 0xFF),
            0x06 => (byte)(_vOff & 0xFF),
            0x07 => (byte)((_vOff >> 8) & 0xFF),
            0x08 => (byte)(_vidBas & 0xFF),
            0x09 => (byte)(_vidBas >> 8),
            0x0A => (byte)(_collBas & 0xFF),
            0x0B => (byte)(_collBas >> 8),
            0x0C => (byte)(_vidAdr & 0xFF),
            0x0D => (byte)(_vidAdr >> 8),
            0x0E => (byte)(_collAdr & 0xFF),
            0x0F => (byte)(_collAdr >> 8),
            0x10 => (byte)(_scbNext & 0xFF),
            0x11 => (byte)(_scbNext >> 8),
            0x12 => (byte)(_sprDLine & 0xFF),
            0x13 => (byte)(_sprDLine >> 8),
            0x14 => (byte)(_hPosStrt & 0xFF),
            0x15 => (byte)((_hPosStrt >> 8) & 0xFF),
            0x16 => (byte)(_vPosStrt & 0xFF),
            0x17 => (byte)((_vPosStrt >> 8) & 0xFF),
            0x18 => (byte)(_sprHSiz & 0xFF),
            0x19 => (byte)(_sprHSiz >> 8),
            0x1A => (byte)(_sprVSiz & 0xFF),
            0x1B => (byte)(_sprVSiz >> 8),
            0x1C => (byte)(_stretch & 0xFF),
            0x1D => (byte)((_stretch >> 8) & 0xFF),
            0x1E => (byte)(_tilt & 0xFF),
            0x1F => (byte)((_tilt >> 8) & 0xFF),
            0x20 => (byte)(_sprDOff & 0xFF),
            0x21 => (byte)(_sprDOff >> 8),
            0x22 => (byte)(_sprVPos & 0xFF),
            0x23 => (byte)(_sprVPos >> 8),
            0x24 => (byte)(_collOff & 0xFF),
            0x25 => (byte)(_collOff >> 8),
            0x26 => (byte)(_vSizAcum & 0xFF),
            0x27 => (byte)(_vSizAcum >> 8),
            0x28 => (byte)(_hSizOff & 0xFF),
            0x29 => (byte)(_hSizOff >> 8),
            0x2A => (byte)(_vSizOff & 0xFF),
            0x2B => (byte)(_vSizOff >> 8),
            0x2C => (byte)(_scbAdr & 0xFF),
            0x2D => (byte)(_scbAdr >> 8),
            0x2E => (byte)(_procAdr & 0xFF),
            0x2F => (byte)(_procAdr >> 8),

            // Math registers
            0x52 => _mathD,
            0x53 => _mathC,
            0x54 => _mathB,
            0x55 => _mathA,
            0x56 => _mathP,
            0x57 => _mathN,
            0x60 => _mathH,
            0x61 => _mathG,
            0x62 => _mathF,
            0x63 => _mathE,
            0x6C => _mathM,
            0x6D => _mathL,
            0x6E => _mathK,
            0x6F => _mathJ,

            // Control & Revision
            0x80 => _sprCtl0,
            0x81 => _sprCtl1,
            0x82 => _sprColl,
            0x83 => _sprInit,
            0x88 => 0x01, // SUZYHREV
            0x89 => 0x00, // SUZYSREV
            0x90 => _suzyBusEn,
            0x91 => _sprGo,
            0x92 => GetSprSysRead(),

            // Controls & Cartridge (unpopulated cart bus reads 0x00 so Boot ROM detects no cart via CMP #$FB)
            0xB0 => GetJoystickRead(),
            0xB1 => _switches,
            0xB2 => sideEffects ? (_cartridge?.ReadBank0() ?? 0x00) : (byte)0x00,
            0xB3 => sideEffects ? (_cartridge?.ReadBank1() ?? 0x00) : (byte)0x00,

            _ => 0xFF
        };
    }

    private byte GetJoystickRead()
    {
        // SPRSYS bit 3 (LEFTHAND = 0x08): Suzy hardware swaps Up<->Down (bits 7<->6)
        // and Left<->Right (bits 5<->4) on $FCB0 reads.
        if ((_sprSysWrite & 0x08) == 0)
            return _joystick;

        byte low = (byte)(_joystick & 0x0F);
        byte swapped = (byte)(
            ((_joystick & 0x80) >> 1) | // Up -> Down
            ((_joystick & 0x40) << 1) | // Down -> Up
            ((_joystick & 0x20) >> 1) | // Left -> Right
            ((_joystick & 0x10) << 1)); // Right -> Left
        return (byte)(low | swapped);
    }

    private byte GetSprSysRead()
    {
        byte r = 0;
        // bit 7: MathWorking (0 since math completes immediately)
        if (_mathWarning) r |= 0x40;
        if (_mathLastCarry) r |= 0x20;
        if ((_sprSysWrite & 0x10) != 0) r |= 0x10; // VStretch
        if ((_sprSysWrite & 0x08) != 0) r |= 0x08; // LeftHand
        if ((_sprSysWrite & 0x04) != 0) r |= 0x04; // UnsafeAccess
        if (_spriteStopped) r |= 0x02;
        if (_spriteWorking) r |= 0x01;
        return r;
    }

    public void Write(ushort address, byte value)
    {
        int reg = address & 0xFF;
        switch (reg)
        {
            case 0x00: _tmpAdr = (ushort)((_tmpAdr & 0xFF00) | value); break;
            case 0x01: _tmpAdr = (ushort)((_tmpAdr & 0x00FF) | (value << 8)); break;
            case 0x02: _tiltAcum = (ushort)((_tiltAcum & 0xFF00) | value); break;
            case 0x03: _tiltAcum = (ushort)((_tiltAcum & 0x00FF) | (value << 8)); break;
            case 0x04: _hOff = (short)((_hOff & 0xFF00) | value); break;
            case 0x05: _hOff = (short)((_hOff & 0x00FF) | (value << 8)); break;
            case 0x06: _vOff = (short)((_vOff & 0xFF00) | value); break;
            case 0x07: _vOff = (short)((_vOff & 0x00FF) | (value << 8)); break;
            case 0x08: _vidBas = (ushort)((_vidBas & 0xFF00) | value); break;
            case 0x09: _vidBas = (ushort)((_vidBas & 0x00FF) | (value << 8)); break;
            case 0x0A: _collBas = (ushort)((_collBas & 0xFF00) | value); break;
            case 0x0B: _collBas = (ushort)((_collBas & 0x00FF) | (value << 8)); break;
            case 0x0C: _vidAdr = (ushort)((_vidAdr & 0xFF00) | value); break;
            case 0x0D: _vidAdr = (ushort)((_vidAdr & 0x00FF) | (value << 8)); break;
            case 0x0E: _collAdr = (ushort)((_collAdr & 0xFF00) | value); break;
            case 0x0F: _collAdr = (ushort)((_collAdr & 0x00FF) | (value << 8)); break;
            case 0x10: _scbNext = (ushort)((_scbNext & 0xFF00) | value); break;
            case 0x11: _scbNext = (ushort)((_scbNext & 0x00FF) | (value << 8)); break;
            case 0x12: _sprDLine = (ushort)((_sprDLine & 0xFF00) | value); break;
            case 0x13: _sprDLine = (ushort)((_sprDLine & 0x00FF) | (value << 8)); break;
            case 0x14: _hPosStrt = (short)((_hPosStrt & 0xFF00) | value); break;
            case 0x15: _hPosStrt = (short)((_hPosStrt & 0x00FF) | (value << 8)); break;
            case 0x16: _vPosStrt = (short)((_vPosStrt & 0xFF00) | value); break;
            case 0x17: _vPosStrt = (short)((_vPosStrt & 0x00FF) | (value << 8)); break;
            case 0x18: _sprHSiz = (ushort)((_sprHSiz & 0xFF00) | value); break;
            case 0x19: _sprHSiz = (ushort)((_sprHSiz & 0x00FF) | (value << 8)); break;
            case 0x1A: _sprVSiz = (ushort)((_sprVSiz & 0xFF00) | value); break;
            case 0x1B: _sprVSiz = (ushort)((_sprVSiz & 0x00FF) | (value << 8)); break;
            case 0x1C: _stretch = (short)((_stretch & 0xFF00) | value); break;
            case 0x1D: _stretch = (short)((_stretch & 0x00FF) | (value << 8)); break;
            case 0x1E: _tilt = (short)((_tilt & 0xFF00) | value); break;
            case 0x1F: _tilt = (short)((_tilt & 0x00FF) | (value << 8)); break;
            case 0x20: _sprDOff = (ushort)((_sprDOff & 0xFF00) | value); break;
            case 0x21: _sprDOff = (ushort)((_sprDOff & 0x00FF) | (value << 8)); break;
            case 0x22: _sprVPos = (ushort)((_sprVPos & 0xFF00) | value); break;
            case 0x23: _sprVPos = (ushort)((_sprVPos & 0x00FF) | (value << 8)); break;
            case 0x24: _collOff = (ushort)((_collOff & 0xFF00) | value); break;
            case 0x25: _collOff = (ushort)((_collOff & 0x00FF) | (value << 8)); break;
            case 0x26: _vSizAcum = (ushort)((_vSizAcum & 0xFF00) | value); break;
            case 0x27: _vSizAcum = (ushort)((_vSizAcum & 0x00FF) | (value << 8)); break;
            case 0x28: _hSizOff = (ushort)((_hSizOff & 0xFF00) | value); break;
            case 0x29: _hSizOff = (ushort)((_hSizOff & 0x00FF) | (value << 8)); break;
            case 0x2A: _vSizOff = (ushort)((_vSizOff & 0xFF00) | value); break;
            case 0x2B: _vSizOff = (ushort)((_vSizOff & 0x00FF) | (value << 8)); break;
            case 0x2C: _scbAdr = (ushort)((_scbAdr & 0xFF00) | value); break;
            case 0x2D: _scbAdr = (ushort)((_scbAdr & 0x00FF) | (value << 8)); break;
            case 0x2E: _procAdr = (ushort)((_procAdr & 0xFF00) | value); break;
            case 0x2F: _procAdr = (ushort)((_procAdr & 0x00FF) | (value << 8)); break;

            // Hardware Math — writing low byte clears high byte per Lynx hardware spec
            case 0x52: _mathD = value; _mathC = 0; break;
            case 0x53: _mathC = value; break;
            case 0x54: _mathB = value; _mathA = 0; break;
            case 0x55: _mathA = value; ExecuteMultiply(); break;
            case 0x56: _mathP = value; _mathN = 0; break;
            case 0x57: _mathN = value; break;

            case 0x60: _mathH = value; _mathG = 0; break;
            case 0x61: _mathG = value; break;
            case 0x62: _mathF = value; _mathE = 0; break;
            case 0x63: _mathE = value; ExecuteDivide(); break;

            case 0x6C: _mathM = value; _mathL = 0; break;
            case 0x6D: _mathL = value; break;
            case 0x6E: _mathK = value; _mathJ = 0; break;
            case 0x6F: _mathJ = value; break;

            // Control
            case 0x80: _sprCtl0 = value; break;
            case 0x81: _sprCtl1 = value; break;
            case 0x82: _sprColl = value; break;
            case 0x83: _sprInit = value; break;
            case 0x90: _suzyBusEn = value; break;
            case 0x91:
                _sprGo = value;
                if ((_sprGo & 0x01) != 0 && (_suzyBusEn & 0x01) != 0)
                {
                    PaintSprites();
                }
                break;
            case 0x92:
                _sprSysWrite = value;
                if ((value & 0x02) == 0) _spriteStopped = false;
                break;

            case 0xB2: _cartridge?.WriteBank0(value); break;
            case 0xB3: _cartridge?.WriteBank1(value); break;
        }
    }

    private void ExecuteMultiply()
    {
        bool signMath = (_sprSysWrite & 0x80) != 0;
        bool accumulate = (_sprSysWrite & 0x40) != 0;

        ushort ab = (ushort)((_mathA << 8) | _mathB);
        ushort cd = (ushort)((_mathC << 8) | _mathD);

        uint product;
        if (signMath)
        {
            // Documented Lynx signed multiply: negate negative operands, multiply, apply sign
            int sa = (short)ab;
            int sc = (short)cd;
            long prod = (long)sa * sc;
            product = unchecked((uint)prod);
        }
        else
        {
            product = (uint)ab * cd;
        }

        _mathH = (byte)(product & 0xFF);
        _mathG = (byte)((product >> 8) & 0xFF);
        _mathF = (byte)((product >> 16) & 0xFF);
        _mathE = (byte)((product >> 24) & 0xFF);

        _mathWarning = false;
        _mathLastCarry = false;

        if (accumulate)
        {
            uint prevAcc = (uint)(_mathM | (_mathL << 8) | (_mathK << 16) | (_mathJ << 24));
            ulong sum = (ulong)prevAcc + product;
            uint newAcc = (uint)sum;

            _mathM = (byte)(newAcc & 0xFF);
            _mathL = (byte)((newAcc >> 8) & 0xFF);
            _mathK = (byte)((newAcc >> 16) & 0xFF);
            _mathJ = (byte)((newAcc >> 24) & 0xFF);

            _mathLastCarry = sum > 0xFFFFFFFFUL;
            if (signMath)
            {
                bool overflow = (~(prevAcc ^ product) & (prevAcc ^ newAcc) & 0x80000000u) != 0;
                _mathWarning = overflow;
            }
            else
            {
                _mathWarning = _mathLastCarry;
            }
        }
    }

    private void ExecuteDivide()
    {
        uint dividend = (uint)(_mathH | (_mathG << 8) | (_mathF << 16) | (_mathE << 24));
        ushort divisor = (ushort)((_mathN << 8) | _mathP);

        if (divisor == 0)
        {
            _mathD = 0xFF; _mathC = 0xFF; _mathB = 0xFF; _mathA = 0xFF;
            _mathM = 0; _mathL = 0; _mathK = 0; _mathJ = 0;
            _mathWarning = true;
            _mathLastCarry = true;
            return;
        }

        uint quotient = dividend / divisor;
        uint remainder = dividend % divisor;

        _mathD = (byte)(quotient & 0xFF);
        _mathC = (byte)((quotient >> 8) & 0xFF);
        _mathB = (byte)((quotient >> 16) & 0xFF);
        _mathA = (byte)((quotient >> 24) & 0xFF);

        _mathM = (byte)(remainder & 0xFF);
        _mathL = (byte)((remainder >> 8) & 0xFF);
        _mathK = (byte)((remainder >> 16) & 0xFF);
        _mathJ = (byte)((remainder >> 24) & 0xFF);

        _mathWarning = false;
        _mathLastCarry = remainder != 0;
    }

    // ==================== SPRITE ENGINE ====================

    private ushort ReadRamWord(ushort addr) =>
        (ushort)(_ram[addr] | (_ram[(ushort)(addr + 1)] << 8));

    private void PaintSprites()
    {
        _spriteWorking = true;
        LastSpriteCount = 0;
        LastSpriteCycles = 0;

        bool noCollideGlobal = (_sprSysWrite & 0x20) != 0;
        bool vStretch = (_sprSysWrite & 0x10) != 0;
        bool stopOnCurrent = (_sprSysWrite & 0x02) != 0;
        bool everonDetect = (_sprGo & 0x04) != 0;

        int safetyLimit = 4096; // Guard against corrupt cyclic SCB chains

        while ((_scbNext & 0xFF00) != 0 && safetyLimit-- > 0)
        {
            _scbAdr = _scbNext;
            ushort ptr = _scbAdr;

            _sprCtl0 = _ram[ptr++];
            _sprCtl1 = _ram[ptr++];
            _sprColl = _ram[ptr++];
            _scbNext = ReadRamWord(ptr); ptr += 2;

            // Bit 2 of SPRCTL1 = Skip sprite
            if ((_sprCtl1 & 0x04) != 0)
            {
                if (stopOnCurrent) { _spriteStopped = true; break; }
                continue;
            }

            _sprDLine = ReadRamWord(ptr); ptr += 2;
            _hPosStrt = (short)ReadRamWord(ptr); ptr += 2;
            _vPosStrt = (short)ReadRamWord(ptr); ptr += 2;

            int reloadDepth = (_sprCtl1 >> 4) & 0x03;
            _stretch = 0;
            _tilt = 0;
            if (reloadDepth >= 1)
            {
                _sprHSiz = ReadRamWord(ptr); ptr += 2;
                _sprVSiz = ReadRamWord(ptr); ptr += 2;
            }
            if (reloadDepth >= 2)
            {
                _stretch = (short)ReadRamWord(ptr); ptr += 2;
            }
            if (reloadDepth >= 3)
            {
                _tilt = (short)ReadRamWord(ptr); ptr += 2;
            }

            // Palette reload (when ReusePalette bit 3 == 0)
            if ((_sprCtl1 & 0x08) == 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    byte b = _ram[ptr++];
                    _penIndex[i * 2] = (byte)(b >> 4);
                    _penIndex[i * 2 + 1] = (byte)(b & 0x0F);
                }
            }

            bool everonHit = RenderSingleSprite(noCollideGlobal, vStretch);
            LastSpriteCount++;

            // Write Everon result back into SCB SPRCOLL bit 7 if enabled
            if (everonDetect)
            {
                ushort collDepAddr = (ushort)(_scbAdr + _collOff);
                if (!everonHit)
                    _ram[collDepAddr] |= 0x80;
                else
                    _ram[collDepAddr] &= 0x7F;
            }

            if (stopOnCurrent)
            {
                _spriteStopped = true;
                break;
            }
        }

        _spriteWorking = false;
        _sprGo &= unchecked((byte)~0x01);
    }

    private bool RenderSingleSprite(bool noCollideGlobal, bool vStretch)
    {
        int bpp = ((_sprCtl0 >> 6) & 0x03) + 1;
        bool hFlip = (_sprCtl0 & 0x20) != 0;
        bool vFlip = (_sprCtl0 & 0x10) != 0;
        int spriteType = _sprCtl0 & 0x07;
        bool literal = (_sprCtl1 & 0x80) != 0;
        int startQuad = _sprCtl1 & 0x03;

        bool spriteCollide = !noCollideGlobal && ((_sprColl & 0x20) == 0)
                             && spriteType != 1 && spriteType != 5;
        byte collNum = (byte)(_sprColl & 0x0F);
        byte maxColl = 0;
        bool everonHit = false;

        int startX = _hPosStrt - _hOff;
        int startY = _vPosStrt - _vOff;

        // Epyx Atari Lynx Suzy quadrant encoding (SPRCTL1 bits 1..0):
        //   bit 1 (0x02) = DRAWLEFT (0=+X, 1=-X)
        //   bit 0 (0x01) = DRAWUP   (0=+Y, 1=-Y)
        //   0 (00): Down-Right (+X, +Y)
        //   1 (01): Up-Right   (+X, -Y)
        //   3 (11): Up-Left    (-X, -Y)
        //   2 (10): Down-Left  (-X, +Y)
        // Counter-clockwise quadrant transition order on lineOffset==1: 0 -> 1 -> 3 -> 2 -> 0
        static int QuadHSign(int q, bool hf) => ((q & 0x02) != 0 ? -1 : 1) * (hf ? -1 : 1);
        static int QuadVSign(int q, bool vf) => ((q & 0x01) != 0 ? -1 : 1) * (vf ? -1 : 1);
        static int NextQuadCcw(int q) => q switch { 0 => 1, 1 => 3, 3 => 2, _ => 0 };

        int curQuad = startQuad;
        int initHSign = QuadHSign(curQuad, hFlip);
        int initVSign = QuadVSign(curQuad, vFlip);
        int hSign = initHSign;
        int vSign = initVSign;

        int curY = startY;
        int curXBase = startX;
        int hSiz = _sprHSiz;
        int vSiz = _sprVSiz;
        int vAcum = _vSizOff;
        int tiltAcum = 0;
        ushort lineAddr = _sprDLine;

        // Process quadrants (up to 4 transitions via offset==1)
        int quadGuard = 4;
        while (quadGuard-- > 0)
        {
            while (true)
            {
                byte lineOffset = _ram[lineAddr];
                if (lineOffset == 0)
                {
                    // End of sprite data
                    goto DoneSprite;
                }
                if (lineOffset == 1)
                {
                    // Quadrant change: advance lineAddr by 1 and move CCW to next quadrant
                    lineAddr = (ushort)(lineAddr + 1);
                    curQuad = NextQuadCcw(curQuad);
                    hSign = QuadHSign(curQuad, hFlip);
                    vSign = QuadVSign(curQuad, vFlip);

                    // Offset by 1 pixel when crossing to the opposite side of the reference point
                    curXBase = startX + (hSign != initHSign ? hSign : 0);
                    curY = startY + (vSign != initVSign ? vSign : 0);
                    hSiz = _sprHSiz;
                    vSiz = _sprVSiz;
                    vAcum = _vSizOff;
                    tiltAcum = 0;
                    break;
                }

                ushort nextLineAddr = (ushort)(lineAddr + lineOffset);
                vAcum += vSiz;
                int repeatLines = vAcum >> 8;
                vAcum &= 0xFF;

                for (int r = 0; r < repeatLines; r++)
                {
                    if (curY >= 0 && curY < 102)
                    {
                        bool lineHit = RenderSpriteScanline(
                            (ushort)(lineAddr + 1),
                            nextLineAddr,
                            curXBase,
                            curY,
                            hSign,
                            hSiz,
                            bpp,
                            literal,
                            spriteType,
                            spriteCollide,
                            collNum,
                            ref maxColl);
                        if (lineHit) everonHit = true;
                    }

                    curY += vSign;
                    hSiz = Math.Max(0, hSiz + _stretch);
                    if (vStretch) vSiz = Math.Max(0, vSiz + _stretch);
                    tiltAcum += _tilt;
                    curXBase += (tiltAcum >> 8);
                    tiltAcum &= 0xFF;
                    LastSpriteCycles += 16;
                }

                lineAddr = nextLineAddr;
            }
        }

    DoneSprite:
        // Background (type 0) sprites clear COLLBAS but do NOT write to the collision depository
        if (spriteCollide && spriteType != 0)
        {
            ushort collDepAddr = (ushort)(_scbAdr + _collOff);
            _ram[collDepAddr] = maxColl;
        }

        return everonHit;
    }

    private bool RenderSpriteScanline(
        ushort dataStart,
        ushort dataEnd,
        int startX,
        int y,
        int hSign,
        int hSiz,
        int bpp,
        bool literal,
        int spriteType,
        bool spriteCollide,
        byte collNum,
        ref byte maxColl)
    {
        int x = startX;
        int hAcum = _hSizOff;
        bool anyOnScreen = false;
        byte localMaxColl = maxColl;

        int bitPos = 0;
        int totalBytes = (ushort)(dataEnd - dataStart);
        int totalBits = totalBytes * 8;

        int ReadBits(int count)
        {
            if (bitPos + count > totalBits) return -1;
            int val = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIdx = bitPos >> 3;
                int bitIdx = 7 - (bitPos & 7);
                int bit = (_ram[(ushort)(dataStart + byteIdx)] >> bitIdx) & 1;
                val = (val << 1) | bit;
                bitPos++;
            }
            return val;
        }

        void PlotPen(int rawPen)
        {
            byte pen = _penIndex[rawPen & 0x0F];
            hAcum += hSiz;
            int repeat = hAcum >> 8;
            hAcum &= 0xFF;

            for (int i = 0; i < repeat; i++)
            {
                if (x >= 0 && x < 160)
                {
                    anyOnScreen = true;
                    PlotPixel(x, y, pen, spriteType, spriteCollide, collNum, ref localMaxColl);
                }
                x += hSign;
            }
        }

        if (literal)
        {
            while (bitPos + bpp <= totalBits)
            {
                int rawPen = ReadBits(bpp);
                if (rawPen < 0) break;
                PlotPen(rawPen);
            }
        }
        else
        {
            // Packed format: 1-bit packet type (1=literal, 0=RLE), 4-bit count
            while (bitPos + 5 <= totalBits)
            {
                int header = ReadBits(5);
                if (header <= 0) break; // 00000 = end of line

                bool isLiteralPacket = (header & 0x10) != 0;
                int count = (header & 0x0F) + 1;

                if (isLiteralPacket)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int rawPen = ReadBits(bpp);
                        if (rawPen < 0)
                        {
                            maxColl = localMaxColl;
                            return anyOnScreen;
                        }
                        PlotPen(rawPen);
                    }
                }
                else
                {
                    int rawPen = ReadBits(bpp);
                    if (rawPen < 0)
                    {
                        maxColl = localMaxColl;
                        return anyOnScreen;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        PlotPen(rawPen);
                    }
                }
            }
        }

        maxColl = localMaxColl;
        return anyOnScreen;
    }

    private void PlotPixel(
        int x, int y, byte pen, int spriteType,
        bool spriteCollide, byte collNum, ref byte maxColl)
    {
        // Transparency checks by sprite type
        switch (spriteType)
        {
            case 0: // Background
            case 1: // Background No-Collide
                break; // All pens including 0 are opaque
            case 2: // Boundary Shadow
            case 3: // Boundary
                if (pen == 0 || pen == 0x0F) return;
                break;
            default: // Normal (4), Non-Collide (5), XOR Shadow (6), Shadow (7)
                if (pen == 0) return;
                break;
        }

        int offset = y * 80 + (x >> 1);
        ushort vidAddr = (ushort)(_vidBas + offset);
        bool highNibble = (x & 1) == 0;

        byte curVid = _ram[vidAddr];
        byte outPen = pen;
        if (spriteType == 6) // XOR Shadow
        {
            byte existing = highNibble ? (byte)(curVid >> 4) : (byte)(curVid & 0x0F);
            outPen = (byte)(existing ^ pen);
        }

        if (highNibble)
            _ram[vidAddr] = (byte)((curVid & 0x0F) | (outPen << 4));
        else
            _ram[vidAddr] = (byte)((curVid & 0xF0) | (outPen & 0x0F));

        // Collision buffer handling
        if (spriteCollide)
        {
            // Pen 0x0E is non-collidable in Shadow/Normal/BoundaryShadow/XORShadow modes
            bool penCollides = !(pen == 0x0E && (spriteType is 2 or 4 or 6 or 7));
            if (penCollides)
            {
                ushort collAddr = (ushort)(_collBas + offset);
                byte curCollByte = _ram[collAddr];
                byte existingColl = highNibble ? (byte)(curCollByte >> 4) : (byte)(curCollByte & 0x0F);
                if (existingColl > maxColl) maxColl = existingColl;

                // Shadow (type 7) sprites read COLLBAS for collisions but do NOT write to COLLBAS
                if (spriteType != 7)
                {
                    if (highNibble)
                        _ram[collAddr] = (byte)((curCollByte & 0x0F) | (collNum << 4));
                    else
                        _ram[collAddr] = (byte)((curCollByte & 0xF0) | (collNum & 0x0F));
                }
            }
        }
    }

    public SuzySnapshot GetSnapshot()
    {
        uint prod = (uint)(_mathH | (_mathG << 8) | (_mathF << 16) | (_mathE << 24));
        uint acc = (uint)(_mathM | (_mathL << 8) | (_mathK << 16) | (_mathJ << 24));
        return new SuzySnapshot(
            _vidBas, _collBas, _scbNext, _hOff, _vOff,
            _sprCtl0, _sprCtl1, _sprColl, GetSprSysRead(),
            _suzyBusEn, _sprGo, _joystick, _switches,
            prod, acc, LastSpriteCount);
    }
}
