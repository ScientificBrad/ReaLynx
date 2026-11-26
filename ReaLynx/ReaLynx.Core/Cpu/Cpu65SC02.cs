using System.Runtime.CompilerServices;

namespace ReaLynx.Core.Cpu;

[Flags]
public enum StatusFlags : byte
{
    Carry     = 0x01, // C
    Zero      = 0x02, // Z
    Interrupt = 0x04, // I (IRQ disable)
    Decimal   = 0x08, // D (BCD mode)
    Break     = 0x10, // B (only pushed on stack by BRK/PHP)
    Unused    = 0x20, // Always 1 when pushed
    Overflow  = 0x40, // V
    Negative  = 0x80, // N
}

public readonly record struct CpuState(
    byte A,
    byte X,
    byte Y,
    byte SP,
    ushort PC,
    byte P,
    long TotalCycles,
    bool IsSleeping,
    bool IrqLine,
    bool NmiPending);

/// <summary>
/// Atari Lynx 65SC02 CPU core (integrated inside Mikey, running at 4 MHz derived from 16 MHz master clock).
///
/// Documented Lynx 65SC02 specifics:
/// - Implements all official NMOS 6502 instructions plus CMOS 65SC02 additions:
///   BRA, PHX, PHY, PLX, PLY, STZ, TRB, TSB, BIT #imm/zp,X/abs,X, INC A, DEC A, JMP (abs,X), (zp) indirect.
/// - Excludes Rockwell/WDC extensions (RMB0-7, SMB0-7, BBR0-7, BBS0-7, WAI, STP); those opcodes act as
///   G65SC02 NOPs with documented byte lengths and cycle counts.
/// - Fixes NMOS JMP (abs) page-boundary bug (6 cycles).
/// - Clears Decimal (D) flag on RESET, BRK, IRQ, and NMI.
/// - In BCD mode (D=1), ADC and SBC compute valid N, V, Z flags from the decimal result and take +1 cycle.
/// - BIT #imm ($89) only modifies Z (leaves N and V untouched).
/// - RMW abs,X instructions (ASL/LSR/ROL/ROR) take 6 cycles without page cross, 7 cycles with page cross.
///   (INC/DEC abs,X take 7 cycles on 65C02/65SC02).
/// </summary>
public sealed class Cpu65SC02
{
    public const ushort VectorNmi   = 0xFFFA;
    public const ushort VectorReset = 0xFFFC;
    public const ushort VectorIrq   = 0xFFFE;

    private readonly IMemoryBus _bus;

    public byte A { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public byte SP { get; set; }
    public ushort PC { get; set; }
    public byte P { get; set; }

    public long TotalCycles { get; private set; }
    public bool IsSleeping { get; set; }

    /// <summary>Level-sensitive IRQ line (true when Mikey has unacknowledged enabled timer/UART interrupts).</summary>
    public bool IrqLine { get; set; }

    private bool _nmiLine;
    private bool _nmiPending;

    public Cpu65SC02(IMemoryBus bus)
    {
        _bus = bus;
        Reset();
    }

    public void SetNmiLine(bool asserted)
    {
        if (asserted && !_nmiLine)
        {
            _nmiPending = true;
        }
        _nmiLine = asserted;
    }

    public void Reset()
    {
        A = 0;
        X = 0;
        Y = 0;
        SP = 0xFD;
        P = (byte)(StatusFlags.Unused | StatusFlags.Interrupt); // D=0, I=1
        IsSleeping = false;
        _nmiPending = false;
        _nmiLine = false;
        PC = ReadWord(VectorReset);
        TotalCycles = 0;
    }

    public CpuState GetState() => new(A, X, Y, SP, PC, P, TotalCycles, IsSleeping, IrqLine, _nmiPending);

    /// <summary>
    /// Executes one instruction (or services a pending interrupt, or idles 1 cycle if sleeping).
    /// Returns the number of CPU cycles consumed.
    /// </summary>
    public int Step()
    {
        if (IsSleeping)
        {
            // While sleeping on CPUSLEEP ($FD91), Suzy owns the bus.
            // Interrupts can also wake the CPU if Mikey asserts IRQ/NMI.
            if (_nmiPending || (IrqLine && (P & (byte)StatusFlags.Interrupt) == 0))
            {
                IsSleeping = false;
            }
            else
            {
                TotalCycles += 1;
                return 1;
            }
        }

        if (_nmiPending)
        {
            _nmiPending = false;
            int cycles = ServiceInterrupt(VectorNmi, isBrk: false);
            TotalCycles += cycles;
            return cycles;
        }

        if (IrqLine && (P & (byte)StatusFlags.Interrupt) == 0)
        {
            int cycles = ServiceInterrupt(VectorIrq, isBrk: false);
            TotalCycles += cycles;
            return cycles;
        }

        byte opcode = FetchByte();
        int used = ExecuteOpcode(opcode);
        TotalCycles += used;
        return used;
    }

    private int ServiceInterrupt(ushort vector, bool isBrk)
    {
        PushWord(PC);
        byte pushedP = (byte)(P | (byte)StatusFlags.Unused);
        if (isBrk)
            pushedP |= (byte)StatusFlags.Break;
        else
            pushedP &= unchecked((byte)~StatusFlags.Break);
        PushByte(pushedP);

        // 65SC02 clears Decimal flag and sets Interrupt disable on all interrupts
        P = (byte)((P | (byte)StatusFlags.Interrupt) & ~(byte)StatusFlags.Decimal);
        PC = ReadWord(vector);
        return 7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchByte() => _bus.Read(PC++);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort FetchWord()
    {
        byte lo = _bus.Read(PC++);
        byte hi = _bus.Read(PC++);
        return (ushort)(lo | (hi << 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadWord(ushort addr)
    {
        byte lo = _bus.Read(addr);
        byte hi = _bus.Read((ushort)(addr + 1));
        return (ushort)(lo | (hi << 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadWordZeroPage(byte zp)
    {
        byte lo = _bus.Read(zp);
        byte hi = _bus.Read((byte)(zp + 1));
        return (ushort)(lo | (hi << 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PushByte(byte v) => _bus.Write((ushort)(0x0100 | SP--), v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte PopByte() => _bus.Read((ushort)(0x0100 | ++SP));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PushWord(ushort v)
    {
        PushByte((byte)(v >> 8));
        PushByte((byte)(v & 0xFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort PopWord()
    {
        byte lo = PopByte();
        byte hi = PopByte();
        return (ushort)(lo | (hi << 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetFlag(StatusFlags flag, bool value)
    {
        if (value) P |= (byte)flag;
        else P &= unchecked((byte)~flag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetFlag(StatusFlags flag) => (P & (byte)flag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetNZ(byte v)
    {
        P = (byte)((P & ~((byte)StatusFlags.Negative | (byte)StatusFlags.Zero))
            | (v & 0x80)
            | (v == 0 ? (byte)StatusFlags.Zero : 0));
    }

    // Addressing mode helpers
    private ushort AddrZp() => FetchByte();
    private ushort AddrZpX() => (byte)(FetchByte() + X);
    private ushort AddrZpY() => (byte)(FetchByte() + Y);
    private ushort AddrAbs() => FetchWord();

    private ushort AddrAbsX(out bool pageCross)
    {
        ushort baseAddr = FetchWord();
        ushort result = (ushort)(baseAddr + X);
        pageCross = (baseAddr & 0xFF00) != (result & 0xFF00);
        return result;
    }

    private ushort AddrAbsY(out bool pageCross)
    {
        ushort baseAddr = FetchWord();
        ushort result = (ushort)(baseAddr + Y);
        pageCross = (baseAddr & 0xFF00) != (result & 0xFF00);
        return result;
    }

    private ushort AddrIndX()
    {
        byte zp = (byte)(FetchByte() + X);
        return ReadWordZeroPage(zp);
    }

    private ushort AddrIndY(out bool pageCross)
    {
        byte zp = FetchByte();
        ushort baseAddr = ReadWordZeroPage(zp);
        ushort result = (ushort)(baseAddr + Y);
        pageCross = (baseAddr & 0xFF00) != (result & 0xFF00);
        return result;
    }

    private ushort AddrZpInd()
    {
        byte zp = FetchByte();
        return ReadWordZeroPage(zp);
    }

    // ALU helpers
    private int Adc(byte value)
    {
        int carry = GetFlag(StatusFlags.Carry) ? 1 : 0;
        if (!GetFlag(StatusFlags.Decimal))
        {
            int sum = A + value + carry;
            SetFlag(StatusFlags.Carry, sum > 0xFF);
            SetFlag(StatusFlags.Overflow, (~(A ^ value) & (A ^ sum) & 0x80) != 0);
            A = (byte)sum;
            SetNZ(A);
            return 0;
        }
        else
        {
            // 65SC02 BCD ADC: valid N, V, Z flags and +1 cycle
            int lo = (A & 0x0F) + (value & 0x0F) + carry;
            int hi = (A & 0xF0) + (value & 0xF0);
            if (lo > 0x09)
            {
                lo += 0x06;
                hi += 0x10;
            }
            int binResult = (hi & 0xFF) | (lo & 0x0F);
            SetFlag(StatusFlags.Overflow, (~(A ^ value) & (A ^ binResult) & 0x80) != 0);
            if (hi > 0x90)
            {
                hi += 0x60;
            }
            SetFlag(StatusFlags.Carry, hi > 0xFF);
            A = (byte)((hi & 0xF0) | (lo & 0x0F));
            SetNZ(A);
            return 1;
        }
    }

    private int Sbc(byte value)
    {
        int borrow = GetFlag(StatusFlags.Carry) ? 0 : 1;
        if (!GetFlag(StatusFlags.Decimal))
        {
            int diff = A - value - borrow;
            SetFlag(StatusFlags.Carry, diff >= 0);
            SetFlag(StatusFlags.Overflow, ((A ^ value) & (A ^ diff) & 0x80) != 0);
            A = (byte)diff;
            SetNZ(A);
            return 0;
        }
        else
        {
            // 65SC02 BCD SBC: valid N, V, Z flags and +1 cycle
            int binDiff = A - value - borrow;
            SetFlag(StatusFlags.Overflow, ((A ^ value) & (A ^ binDiff) & 0x80) != 0);

            int lo = (A & 0x0F) - (value & 0x0F) - borrow;
            int hi = (A & 0xF0) - (value & 0xF0);
            if (lo < 0)
            {
                lo -= 0x06;
                hi -= 0x10;
            }
            if (hi < 0)
            {
                hi -= 0x60;
            }
            SetFlag(StatusFlags.Carry, binDiff >= 0);
            A = (byte)((hi & 0xF0) | (lo & 0x0F));
            SetNZ(A);
            return 1;
        }
    }

    private void Compare(byte reg, byte value)
    {
        int diff = reg - value;
        SetFlag(StatusFlags.Carry, reg >= value);
        SetNZ((byte)diff);
    }

    private byte Asl(byte v)
    {
        SetFlag(StatusFlags.Carry, (v & 0x80) != 0);
        v = (byte)(v << 1);
        SetNZ(v);
        return v;
    }

    private byte Lsr(byte v)
    {
        SetFlag(StatusFlags.Carry, (v & 0x01) != 0);
        v = (byte)(v >> 1);
        SetNZ(v);
        return v;
    }

    private byte Rol(byte v)
    {
        int oldC = GetFlag(StatusFlags.Carry) ? 1 : 0;
        SetFlag(StatusFlags.Carry, (v & 0x80) != 0);
        v = (byte)((v << 1) | oldC);
        SetNZ(v);
        return v;
    }

    private byte Ror(byte v)
    {
        int oldC = GetFlag(StatusFlags.Carry) ? 0x80 : 0;
        SetFlag(StatusFlags.Carry, (v & 0x01) != 0);
        v = (byte)((v >> 1) | oldC);
        SetNZ(v);
        return v;
    }

    private void Bit(byte v)
    {
        SetFlag(StatusFlags.Zero, (A & v) == 0);
        SetFlag(StatusFlags.Negative, (v & 0x80) != 0);
        SetFlag(StatusFlags.Overflow, (v & 0x40) != 0);
    }

    private int Branch(bool condition)
    {
        sbyte offset = (sbyte)FetchByte();
        if (!condition) return 2;
        ushort oldPc = PC;
        PC = (ushort)(PC + offset);
        return (oldPc & 0xFF00) != (PC & 0xFF00) ? 4 : 3;
    }

    private int ExecuteOpcode(byte op)
    {
        bool pc;
        ushort addr;
        byte v;

        switch (op)
        {
            // ===== BRK / RTI / RTS / JSR / JMP =====
            case 0x00: // BRK (2 bytes: skips signature byte)
                PC++;
                return ServiceInterrupt(VectorIrq, isBrk: true);

            case 0x20: // JSR abs
                addr = FetchWord();
                PushWord((ushort)(PC - 1));
                PC = addr;
                return 6;

            case 0x40: // RTI
                P = (byte)((PopByte() & ~(byte)StatusFlags.Break) | (byte)StatusFlags.Unused);
                PC = PopWord();
                return 6;

            case 0x60: // RTS
                PC = (ushort)(PopWord() + 1);
                return 6;

            case 0x4C: // JMP abs
                PC = FetchWord();
                return 3;

            case 0x6C: // JMP (abs) — 65SC02 fixes page-boundary bug, takes 6 cycles
                addr = FetchWord();
                PC = ReadWord(addr);
                return 6;

            case 0x7C: // JMP (abs,X) — 65SC02
                addr = (ushort)(FetchWord() + X);
                PC = ReadWord(addr);
                return 6;

            // ===== Branches =====
            case 0x10: return Branch(!GetFlag(StatusFlags.Negative)); // BPL
            case 0x30: return Branch(GetFlag(StatusFlags.Negative));  // BMI
            case 0x50: return Branch(!GetFlag(StatusFlags.Overflow)); // BVC
            case 0x70: return Branch(GetFlag(StatusFlags.Overflow));  // BVS
            case 0x80: return Branch(true);                           // BRA (65SC02)
            case 0x90: return Branch(!GetFlag(StatusFlags.Carry));    // BCC
            case 0xB0: return Branch(GetFlag(StatusFlags.Carry));     // BCS
            case 0xD0: return Branch(!GetFlag(StatusFlags.Zero));     // BNE
            case 0xF0: return Branch(GetFlag(StatusFlags.Zero));      // BEQ

            // ===== Flags =====
            case 0x18: SetFlag(StatusFlags.Carry, false); return 2;     // CLC
            case 0x38: SetFlag(StatusFlags.Carry, true); return 2;      // SEC
            case 0x58: SetFlag(StatusFlags.Interrupt, false); return 2; // CLI
            case 0x78: SetFlag(StatusFlags.Interrupt, true); return 2;  // SEI
            case 0xB8: SetFlag(StatusFlags.Overflow, false); return 2;  // CLV
            case 0xD8: SetFlag(StatusFlags.Decimal, false); return 2;   // CLD
            case 0xF8: SetFlag(StatusFlags.Decimal, true); return 2;    // SED

            // ===== Stack =====
            case 0x08: PushByte((byte)(P | (byte)StatusFlags.Break | (byte)StatusFlags.Unused)); return 3; // PHP
            case 0x28: P = (byte)((PopByte() & ~(byte)StatusFlags.Break) | (byte)StatusFlags.Unused); return 4; // PLP
            case 0x48: PushByte(A); return 3;               // PHA
            case 0x68: A = PopByte(); SetNZ(A); return 4;   // PLA
            case 0xDA: PushByte(X); return 3;               // PHX (65SC02)
            case 0xFA: X = PopByte(); SetNZ(X); return 4;   // PLX (65SC02)
            case 0x5A: PushByte(Y); return 3;               // PHY (65SC02)
            case 0x7A: Y = PopByte(); SetNZ(Y); return 4;   // PLY (65SC02)

            // ===== Transfers =====
            case 0xAA: X = A; SetNZ(X); return 2;  // TAX
            case 0x8A: A = X; SetNZ(A); return 2;  // TXA
            case 0xA8: Y = A; SetNZ(Y); return 2;  // TAY
            case 0x98: A = Y; SetNZ(A); return 2;  // TYA
            case 0xBA: X = SP; SetNZ(X); return 2; // TSX
            case 0x9A: SP = X; return 2;           // TXS

            // ===== LDA =====
            case 0xA9: A = FetchByte(); SetNZ(A); return 2;
            case 0xA5: A = _bus.Read(AddrZp()); SetNZ(A); return 3;
            case 0xB5: A = _bus.Read(AddrZpX()); SetNZ(A); return 4;
            case 0xAD: A = _bus.Read(AddrAbs()); SetNZ(A); return 4;
            case 0xBD: A = _bus.Read(AddrAbsX(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0xB9: A = _bus.Read(AddrAbsY(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0xA1: A = _bus.Read(AddrIndX()); SetNZ(A); return 6;
            case 0xB1: A = _bus.Read(AddrIndY(out pc)); SetNZ(A); return pc ? 6 : 5;
            case 0xB2: A = _bus.Read(AddrZpInd()); SetNZ(A); return 5; // 65SC02

            // ===== LDX =====
            case 0xA2: X = FetchByte(); SetNZ(X); return 2;
            case 0xA6: X = _bus.Read(AddrZp()); SetNZ(X); return 3;
            case 0xB6: X = _bus.Read(AddrZpY()); SetNZ(X); return 4;
            case 0xAE: X = _bus.Read(AddrAbs()); SetNZ(X); return 4;
            case 0xBE: X = _bus.Read(AddrAbsY(out pc)); SetNZ(X); return pc ? 5 : 4;

            // ===== LDY =====
            case 0xA0: Y = FetchByte(); SetNZ(Y); return 2;
            case 0xA4: Y = _bus.Read(AddrZp()); SetNZ(Y); return 3;
            case 0xB4: Y = _bus.Read(AddrZpX()); SetNZ(Y); return 4;
            case 0xAC: Y = _bus.Read(AddrAbs()); SetNZ(Y); return 4;
            case 0xBC: Y = _bus.Read(AddrAbsX(out pc)); SetNZ(Y); return pc ? 5 : 4;

            // ===== STA =====
            case 0x85: _bus.Write(AddrZp(), A); return 3;
            case 0x95: _bus.Write(AddrZpX(), A); return 4;
            case 0x8D: _bus.Write(AddrAbs(), A); return 4;
            case 0x9D: _bus.Write(AddrAbsX(out _), A); return 5;
            case 0x99: _bus.Write(AddrAbsY(out _), A); return 5;
            case 0x81: _bus.Write(AddrIndX(), A); return 6;
            case 0x91: _bus.Write(AddrIndY(out _), A); return 6;
            case 0x92: _bus.Write(AddrZpInd(), A); return 5; // 65SC02

            // ===== STX =====
            case 0x86: _bus.Write(AddrZp(), X); return 3;
            case 0x96: _bus.Write(AddrZpY(), X); return 4;
            case 0x8E: _bus.Write(AddrAbs(), X); return 4;

            // ===== STY =====
            case 0x84: _bus.Write(AddrZp(), Y); return 3;
            case 0x94: _bus.Write(AddrZpX(), Y); return 4;
            case 0x8C: _bus.Write(AddrAbs(), Y); return 4;

            // ===== STZ (65SC02) =====
            case 0x64: _bus.Write(AddrZp(), 0); return 3;
            case 0x74: _bus.Write(AddrZpX(), 0); return 4;
            case 0x9C: _bus.Write(AddrAbs(), 0); return 4;
            case 0x9E: _bus.Write(AddrAbsX(out _), 0); return 5;

            // ===== ORA =====
            case 0x09: A |= FetchByte(); SetNZ(A); return 2;
            case 0x05: A |= _bus.Read(AddrZp()); SetNZ(A); return 3;
            case 0x15: A |= _bus.Read(AddrZpX()); SetNZ(A); return 4;
            case 0x0D: A |= _bus.Read(AddrAbs()); SetNZ(A); return 4;
            case 0x1D: A |= _bus.Read(AddrAbsX(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0x19: A |= _bus.Read(AddrAbsY(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0x01: A |= _bus.Read(AddrIndX()); SetNZ(A); return 6;
            case 0x11: A |= _bus.Read(AddrIndY(out pc)); SetNZ(A); return pc ? 6 : 5;
            case 0x12: A |= _bus.Read(AddrZpInd()); SetNZ(A); return 5; // 65SC02

            // ===== AND =====
            case 0x29: A &= FetchByte(); SetNZ(A); return 2;
            case 0x25: A &= _bus.Read(AddrZp()); SetNZ(A); return 3;
            case 0x35: A &= _bus.Read(AddrZpX()); SetNZ(A); return 4;
            case 0x2D: A &= _bus.Read(AddrAbs()); SetNZ(A); return 4;
            case 0x3D: A &= _bus.Read(AddrAbsX(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0x39: A &= _bus.Read(AddrAbsY(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0x21: A &= _bus.Read(AddrIndX()); SetNZ(A); return 6;
            case 0x31: A &= _bus.Read(AddrIndY(out pc)); SetNZ(A); return pc ? 6 : 5;
            case 0x32: A &= _bus.Read(AddrZpInd()); SetNZ(A); return 5; // 65SC02

            // ===== EOR =====
            case 0x49: A ^= FetchByte(); SetNZ(A); return 2;
            case 0x45: A ^= _bus.Read(AddrZp()); SetNZ(A); return 3;
            case 0x55: A ^= _bus.Read(AddrZpX()); SetNZ(A); return 4;
            case 0x4D: A ^= _bus.Read(AddrAbs()); SetNZ(A); return 4;
            case 0x5D: A ^= _bus.Read(AddrAbsX(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0x59: A ^= _bus.Read(AddrAbsY(out pc)); SetNZ(A); return pc ? 5 : 4;
            case 0x41: A ^= _bus.Read(AddrIndX()); SetNZ(A); return 6;
            case 0x51: A ^= _bus.Read(AddrIndY(out pc)); SetNZ(A); return pc ? 6 : 5;
            case 0x52: A ^= _bus.Read(AddrZpInd()); SetNZ(A); return 5; // 65SC02

            // ===== ADC =====
            case 0x69: return 2 + Adc(FetchByte());
            case 0x65: return 3 + Adc(_bus.Read(AddrZp()));
            case 0x75: return 4 + Adc(_bus.Read(AddrZpX()));
            case 0x6D: return 4 + Adc(_bus.Read(AddrAbs()));
            case 0x7D: v = _bus.Read(AddrAbsX(out pc)); return (pc ? 5 : 4) + Adc(v);
            case 0x79: v = _bus.Read(AddrAbsY(out pc)); return (pc ? 5 : 4) + Adc(v);
            case 0x61: return 6 + Adc(_bus.Read(AddrIndX()));
            case 0x71: v = _bus.Read(AddrIndY(out pc)); return (pc ? 6 : 5) + Adc(v);
            case 0x72: return 5 + Adc(_bus.Read(AddrZpInd())); // 65SC02

            // ===== SBC =====
            case 0xE9: return 2 + Sbc(FetchByte());
            case 0xE5: return 3 + Sbc(_bus.Read(AddrZp()));
            case 0xF5: return 4 + Sbc(_bus.Read(AddrZpX()));
            case 0xED: return 4 + Sbc(_bus.Read(AddrAbs()));
            case 0xFD: v = _bus.Read(AddrAbsX(out pc)); return (pc ? 5 : 4) + Sbc(v);
            case 0xF9: v = _bus.Read(AddrAbsY(out pc)); return (pc ? 5 : 4) + Sbc(v);
            case 0xE1: return 6 + Sbc(_bus.Read(AddrIndX()));
            case 0xF1: v = _bus.Read(AddrIndY(out pc)); return (pc ? 6 : 5) + Sbc(v);
            case 0xF2: return 5 + Sbc(_bus.Read(AddrZpInd())); // 65SC02

            // ===== CMP =====
            case 0xC9: Compare(A, FetchByte()); return 2;
            case 0xC5: Compare(A, _bus.Read(AddrZp())); return 3;
            case 0xD5: Compare(A, _bus.Read(AddrZpX())); return 4;
            case 0xCD: Compare(A, _bus.Read(AddrAbs())); return 4;
            case 0xDD: Compare(A, _bus.Read(AddrAbsX(out pc))); return pc ? 5 : 4;
            case 0xD9: Compare(A, _bus.Read(AddrAbsY(out pc))); return pc ? 5 : 4;
            case 0xC1: Compare(A, _bus.Read(AddrIndX())); return 6;
            case 0xD1: Compare(A, _bus.Read(AddrIndY(out pc))); return pc ? 6 : 5;
            case 0xD2: Compare(A, _bus.Read(AddrZpInd())); return 5; // 65SC02

            // ===== CPX =====
            case 0xE0: Compare(X, FetchByte()); return 2;
            case 0xE4: Compare(X, _bus.Read(AddrZp())); return 3;
            case 0xEC: Compare(X, _bus.Read(AddrAbs())); return 4;

            // ===== CPY =====
            case 0xC0: Compare(Y, FetchByte()); return 2;
            case 0xC4: Compare(Y, _bus.Read(AddrZp())); return 3;
            case 0xCC: Compare(Y, _bus.Read(AddrAbs())); return 4;

            // ===== BIT =====
            case 0x89: // BIT #imm (65SC02): only affects Z, leaves N and V unchanged
                SetFlag(StatusFlags.Zero, (A & FetchByte()) == 0);
                return 2;
            case 0x24: Bit(_bus.Read(AddrZp())); return 3;
            case 0x34: Bit(_bus.Read(AddrZpX())); return 4; // 65SC02
            case 0x2C: Bit(_bus.Read(AddrAbs())); return 4;
            case 0x3C: Bit(_bus.Read(AddrAbsX(out pc))); return pc ? 5 : 4; // 65SC02

            // ===== TSB / TRB (65SC02) =====
            case 0x04: // TSB zp
                addr = AddrZp(); v = _bus.Read(addr);
                SetFlag(StatusFlags.Zero, (A & v) == 0);
                _bus.Write(addr, (byte)(v | A));
                return 5;
            case 0x0C: // TSB abs
                addr = AddrAbs(); v = _bus.Read(addr);
                SetFlag(StatusFlags.Zero, (A & v) == 0);
                _bus.Write(addr, (byte)(v | A));
                return 6;
            case 0x14: // TRB zp
                addr = AddrZp(); v = _bus.Read(addr);
                SetFlag(StatusFlags.Zero, (A & v) == 0);
                _bus.Write(addr, (byte)(v & ~A));
                return 5;
            case 0x1C: // TRB abs
                addr = AddrAbs(); v = _bus.Read(addr);
                SetFlag(StatusFlags.Zero, (A & v) == 0);
                _bus.Write(addr, (byte)(v & ~A));
                return 6;

            // ===== ASL =====
            case 0x0A: A = Asl(A); return 2;
            case 0x06: addr = AddrZp(); _bus.Write(addr, Asl(_bus.Read(addr))); return 5;
            case 0x16: addr = AddrZpX(); _bus.Write(addr, Asl(_bus.Read(addr))); return 6;
            case 0x0E: addr = AddrAbs(); _bus.Write(addr, Asl(_bus.Read(addr))); return 6;
            case 0x1E: addr = AddrAbsX(out pc); _bus.Write(addr, Asl(_bus.Read(addr))); return pc ? 7 : 6;

            // ===== LSR =====
            case 0x4A: A = Lsr(A); return 2;
            case 0x46: addr = AddrZp(); _bus.Write(addr, Lsr(_bus.Read(addr))); return 5;
            case 0x56: addr = AddrZpX(); _bus.Write(addr, Lsr(_bus.Read(addr))); return 6;
            case 0x4E: addr = AddrAbs(); _bus.Write(addr, Lsr(_bus.Read(addr))); return 6;
            case 0x5E: addr = AddrAbsX(out pc); _bus.Write(addr, Lsr(_bus.Read(addr))); return pc ? 7 : 6;

            // ===== ROL =====
            case 0x2A: A = Rol(A); return 2;
            case 0x26: addr = AddrZp(); _bus.Write(addr, Rol(_bus.Read(addr))); return 5;
            case 0x36: addr = AddrZpX(); _bus.Write(addr, Rol(_bus.Read(addr))); return 6;
            case 0x2E: addr = AddrAbs(); _bus.Write(addr, Rol(_bus.Read(addr))); return 6;
            case 0x3E: addr = AddrAbsX(out pc); _bus.Write(addr, Rol(_bus.Read(addr))); return pc ? 7 : 6;

            // ===== ROR =====
            case 0x6A: A = Ror(A); return 2;
            case 0x66: addr = AddrZp(); _bus.Write(addr, Ror(_bus.Read(addr))); return 5;
            case 0x76: addr = AddrZpX(); _bus.Write(addr, Ror(_bus.Read(addr))); return 6;
            case 0x6E: addr = AddrAbs(); _bus.Write(addr, Ror(_bus.Read(addr))); return 6;
            case 0x7E: addr = AddrAbsX(out pc); _bus.Write(addr, Ror(_bus.Read(addr))); return pc ? 7 : 6;

            // ===== INC / DEC =====
            case 0x1A: A++; SetNZ(A); return 2; // INC A (65SC02)
            case 0x3A: A--; SetNZ(A); return 2; // DEC A (65SC02)
            case 0xE8: X++; SetNZ(X); return 2; // INX
            case 0xCA: X--; SetNZ(X); return 2; // DEX
            case 0xC8: Y++; SetNZ(Y); return 2; // INY
            case 0x88: Y--; SetNZ(Y); return 2; // DEY

            case 0xE6: addr = AddrZp(); v = (byte)(_bus.Read(addr) + 1); _bus.Write(addr, v); SetNZ(v); return 5;
            case 0xF6: addr = AddrZpX(); v = (byte)(_bus.Read(addr) + 1); _bus.Write(addr, v); SetNZ(v); return 6;
            case 0xEE: addr = AddrAbs(); v = (byte)(_bus.Read(addr) + 1); _bus.Write(addr, v); SetNZ(v); return 6;
            case 0xFE: addr = AddrAbsX(out _); v = (byte)(_bus.Read(addr) + 1); _bus.Write(addr, v); SetNZ(v); return 7;

            case 0xC6: addr = AddrZp(); v = (byte)(_bus.Read(addr) - 1); _bus.Write(addr, v); SetNZ(v); return 5;
            case 0xD6: addr = AddrZpX(); v = (byte)(_bus.Read(addr) - 1); _bus.Write(addr, v); SetNZ(v); return 6;
            case 0xCE: addr = AddrAbs(); v = (byte)(_bus.Read(addr) - 1); _bus.Write(addr, v); SetNZ(v); return 6;
            case 0xDE: addr = AddrAbsX(out _); v = (byte)(_bus.Read(addr) - 1); _bus.Write(addr, v); SetNZ(v); return 7;

            // ===== NOP =====
            case 0xEA: return 2;

            // ===== G65SC02 Undefined Opcodes (NOPs with documented byte lengths & cycle counts) =====
            // 2-byte, 2-cycle NOPs
            case 0x02: case 0x22: case 0x42: case 0x62:
            case 0x82: case 0xC2: case 0xE2:
                PC++;
                return 2;

            // 2-byte, 3-cycle NOP
            case 0x44:
                PC++;
                return 3;

            // 2-byte, 4-cycle NOPs
            case 0x54: case 0xD4: case 0xF4:
                PC++;
                return 4;

            // 3-byte, 8-cycle NOP
            case 0x5C:
                PC += 2;
                return 8;

            // 3-byte, 4-cycle NOPs
            case 0xDC: case 0xFC:
                PC += 2;
                return 4;

            // 1-byte, 1-cycle NOPs (all remaining undefined opcodes on G65SC02,
            // including x3, xB, x7, xF where Rockwell/WDC RMB/SMB/BBR/BBS/WAI/STP sit)
            default:
                return 1;
        }
    }
}
