namespace ReaLynx.Core.Cpu;

public readonly record struct DisassembledInstruction(
    ushort Address,
    byte Length,
    string BytesHex,
    string Mnemonic);

/// <summary>
/// Disassembler for the Atari Lynx 65SC02 instruction set.
/// </summary>
public static class Disassembler65SC02
{
    private enum AddrMode
    {
        Imp, Acc, Imm, Zp, ZpX, ZpY, Abs, AbsX, AbsY, Ind, IndX, IndY, ZpInd, AbsIndX, Rel,
        Nop1, Nop2, Nop3
    }

    private static readonly (string Mnemonic, AddrMode Mode)[] Table = BuildTable();

    public static DisassembledInstruction Disassemble(IMemoryBus bus, ushort address)
    {
        byte op = bus.Peek(address);
        var (mnem, mode) = Table[op];
        byte len = GetLength(mode);

        byte b1 = len >= 2 ? bus.Peek((ushort)(address + 1)) : (byte)0;
        byte b2 = len >= 3 ? bus.Peek((ushort)(address + 2)) : (byte)0;
        ushort word = (ushort)(b1 | (b2 << 8));

        string bytesHex = len switch
        {
            1 => $"{op:X2}",
            2 => $"{op:X2} {b1:X2}",
            _ => $"{op:X2} {b1:X2} {b2:X2}"
        };

        string operand = mode switch
        {
            AddrMode.Imp or AddrMode.Nop1 => "",
            AddrMode.Acc => " A",
            AddrMode.Imm => $" #${b1:X2}",
            AddrMode.Zp => $" ${b1:X2}",
            AddrMode.ZpX => $" ${b1:X2},X",
            AddrMode.ZpY => $" ${b1:X2},Y",
            AddrMode.Abs => $" ${word:X4}",
            AddrMode.AbsX => $" ${word:X4},X",
            AddrMode.AbsY => $" ${word:X4},Y",
            AddrMode.Ind => $" (${word:X4})",
            AddrMode.IndX => $" (${b1:X2},X)",
            AddrMode.IndY => $" (${b1:X2}),Y",
            AddrMode.ZpInd => $" (${b1:X2})",
            AddrMode.AbsIndX => $" (${word:X4},X)",
            AddrMode.Rel => $" ${(ushort)(address + 2 + (sbyte)b1):X4}",
            AddrMode.Nop2 => $" #${b1:X2}",
            AddrMode.Nop3 => $" ${word:X4}",
            _ => ""
        };

        return new DisassembledInstruction(address, len, bytesHex, $"{mnem}{operand}");
    }

    private static byte GetLength(AddrMode mode) => mode switch
    {
        AddrMode.Imp or AddrMode.Acc or AddrMode.Nop1 => 1,
        AddrMode.Imm or AddrMode.Zp or AddrMode.ZpX or AddrMode.ZpY
            or AddrMode.IndX or AddrMode.IndY or AddrMode.ZpInd or AddrMode.Rel or AddrMode.Nop2 => 2,
        _ => 3
    };

    private static (string, AddrMode)[] BuildTable()
    {
        var t = new (string, AddrMode)[256];
        for (int i = 0; i < 256; i++)
            t[i] = ("NOP?", AddrMode.Nop1);

        void Set(int op, string m, AddrMode mode) => t[op] = (m, mode);

        Set(0x00, "BRK", AddrMode.Imp);
        Set(0x01, "ORA", AddrMode.IndX);
        Set(0x04, "TSB", AddrMode.Zp);
        Set(0x05, "ORA", AddrMode.Zp);
        Set(0x06, "ASL", AddrMode.Zp);
        Set(0x08, "PHP", AddrMode.Imp);
        Set(0x09, "ORA", AddrMode.Imm);
        Set(0x0A, "ASL", AddrMode.Acc);
        Set(0x0C, "TSB", AddrMode.Abs);
        Set(0x0D, "ORA", AddrMode.Abs);
        Set(0x0E, "ASL", AddrMode.Abs);

        Set(0x10, "BPL", AddrMode.Rel);
        Set(0x11, "ORA", AddrMode.IndY);
        Set(0x12, "ORA", AddrMode.ZpInd);
        Set(0x14, "TRB", AddrMode.Zp);
        Set(0x15, "ORA", AddrMode.ZpX);
        Set(0x16, "ASL", AddrMode.ZpX);
        Set(0x18, "CLC", AddrMode.Imp);
        Set(0x19, "ORA", AddrMode.AbsY);
        Set(0x1A, "INC", AddrMode.Acc);
        Set(0x1C, "TRB", AddrMode.Abs);
        Set(0x1D, "ORA", AddrMode.AbsX);
        Set(0x1E, "ASL", AddrMode.AbsX);

        Set(0x20, "JSR", AddrMode.Abs);
        Set(0x21, "AND", AddrMode.IndX);
        Set(0x24, "BIT", AddrMode.Zp);
        Set(0x25, "AND", AddrMode.Zp);
        Set(0x26, "ROL", AddrMode.Zp);
        Set(0x28, "PLP", AddrMode.Imp);
        Set(0x29, "AND", AddrMode.Imm);
        Set(0x2A, "ROL", AddrMode.Acc);
        Set(0x2C, "BIT", AddrMode.Abs);
        Set(0x2D, "AND", AddrMode.Abs);
        Set(0x2E, "ROL", AddrMode.Abs);

        Set(0x30, "BMI", AddrMode.Rel);
        Set(0x31, "AND", AddrMode.IndY);
        Set(0x32, "AND", AddrMode.ZpInd);
        Set(0x34, "BIT", AddrMode.ZpX);
        Set(0x35, "AND", AddrMode.ZpX);
        Set(0x36, "ROL", AddrMode.ZpX);
        Set(0x38, "SEC", AddrMode.Imp);
        Set(0x39, "AND", AddrMode.AbsY);
        Set(0x3A, "DEC", AddrMode.Acc);
        Set(0x3C, "BIT", AddrMode.AbsX);
        Set(0x3D, "AND", AddrMode.AbsX);
        Set(0x3E, "ROL", AddrMode.AbsX);

        Set(0x40, "RTI", AddrMode.Imp);
        Set(0x41, "EOR", AddrMode.IndX);
        Set(0x45, "EOR", AddrMode.Zp);
        Set(0x46, "LSR", AddrMode.Zp);
        Set(0x48, "PHA", AddrMode.Imp);
        Set(0x49, "EOR", AddrMode.Imm);
        Set(0x4A, "LSR", AddrMode.Acc);
        Set(0x4C, "JMP", AddrMode.Abs);
        Set(0x4D, "EOR", AddrMode.Abs);
        Set(0x4E, "LSR", AddrMode.Abs);

        Set(0x50, "BVC", AddrMode.Rel);
        Set(0x51, "EOR", AddrMode.IndY);
        Set(0x52, "EOR", AddrMode.ZpInd);
        Set(0x55, "EOR", AddrMode.ZpX);
        Set(0x56, "LSR", AddrMode.ZpX);
        Set(0x58, "CLI", AddrMode.Imp);
        Set(0x59, "EOR", AddrMode.AbsY);
        Set(0x5A, "PHY", AddrMode.Imp);
        Set(0x5D, "EOR", AddrMode.AbsX);
        Set(0x5E, "LSR", AddrMode.AbsX);

        Set(0x60, "RTS", AddrMode.Imp);
        Set(0x61, "ADC", AddrMode.IndX);
        Set(0x64, "STZ", AddrMode.Zp);
        Set(0x65, "ADC", AddrMode.Zp);
        Set(0x66, "ROR", AddrMode.Zp);
        Set(0x68, "PLA", AddrMode.Imp);
        Set(0x69, "ADC", AddrMode.Imm);
        Set(0x6A, "ROR", AddrMode.Acc);
        Set(0x6C, "JMP", AddrMode.Ind);
        Set(0x6D, "ADC", AddrMode.Abs);
        Set(0x6E, "ROR", AddrMode.Abs);

        Set(0x70, "BVS", AddrMode.Rel);
        Set(0x71, "ADC", AddrMode.IndY);
        Set(0x72, "ADC", AddrMode.ZpInd);
        Set(0x74, "STZ", AddrMode.ZpX);
        Set(0x75, "ADC", AddrMode.ZpX);
        Set(0x76, "ROR", AddrMode.ZpX);
        Set(0x78, "SEI", AddrMode.Imp);
        Set(0x79, "ADC", AddrMode.AbsY);
        Set(0x7A, "PLY", AddrMode.Imp);
        Set(0x7C, "JMP", AddrMode.AbsIndX);
        Set(0x7D, "ADC", AddrMode.AbsX);
        Set(0x7E, "ROR", AddrMode.AbsX);

        Set(0x80, "BRA", AddrMode.Rel);
        Set(0x81, "STA", AddrMode.IndX);
        Set(0x84, "STY", AddrMode.Zp);
        Set(0x85, "STA", AddrMode.Zp);
        Set(0x86, "STX", AddrMode.Zp);
        Set(0x88, "DEY", AddrMode.Imp);
        Set(0x89, "BIT", AddrMode.Imm);
        Set(0x8A, "TXA", AddrMode.Imp);
        Set(0x8C, "STY", AddrMode.Abs);
        Set(0x8D, "STA", AddrMode.Abs);
        Set(0x8E, "STX", AddrMode.Abs);

        Set(0x90, "BCC", AddrMode.Rel);
        Set(0x91, "STA", AddrMode.IndY);
        Set(0x92, "STA", AddrMode.ZpInd);
        Set(0x94, "STY", AddrMode.ZpX);
        Set(0x95, "STA", AddrMode.ZpX);
        Set(0x96, "STX", AddrMode.ZpY);
        Set(0x98, "TYA", AddrMode.Imp);
        Set(0x99, "STA", AddrMode.AbsY);
        Set(0x9A, "TXS", AddrMode.Imp);
        Set(0x9C, "STZ", AddrMode.Abs);
        Set(0x9D, "STA", AddrMode.AbsX);
        Set(0x9E, "STZ", AddrMode.AbsX);

        Set(0xA0, "LDY", AddrMode.Imm);
        Set(0xA1, "LDA", AddrMode.IndX);
        Set(0xA2, "LDX", AddrMode.Imm);
        Set(0xA4, "LDY", AddrMode.Zp);
        Set(0xA5, "LDA", AddrMode.Zp);
        Set(0xA6, "LDX", AddrMode.Zp);
        Set(0xA8, "TAY", AddrMode.Imp);
        Set(0xA9, "LDA", AddrMode.Imm);
        Set(0xAA, "TAX", AddrMode.Imp);
        Set(0xAC, "LDY", AddrMode.Abs);
        Set(0xAD, "LDA", AddrMode.Abs);
        Set(0xAE, "LDX", AddrMode.Abs);

        Set(0xB0, "BCS", AddrMode.Rel);
        Set(0xB1, "LDA", AddrMode.IndY);
        Set(0xB2, "LDA", AddrMode.ZpInd);
        Set(0xB4, "LDY", AddrMode.ZpX);
        Set(0xB5, "LDA", AddrMode.ZpX);
        Set(0xB6, "LDX", AddrMode.ZpY);
        Set(0xB8, "CLV", AddrMode.Imp);
        Set(0xB9, "LDA", AddrMode.AbsY);
        Set(0xBA, "TSX", AddrMode.Imp);
        Set(0xBC, "LDY", AddrMode.AbsX);
        Set(0xBD, "LDA", AddrMode.AbsX);
        Set(0xBE, "LDX", AddrMode.AbsY);

        Set(0xC0, "CPY", AddrMode.Imm);
        Set(0xC1, "CMP", AddrMode.IndX);
        Set(0xC4, "CPY", AddrMode.Zp);
        Set(0xC5, "CMP", AddrMode.Zp);
        Set(0xC6, "DEC", AddrMode.Zp);
        Set(0xC8, "INY", AddrMode.Imp);
        Set(0xC9, "CMP", AddrMode.Imm);
        Set(0xCA, "DEX", AddrMode.Imp);
        Set(0xCC, "CPY", AddrMode.Abs);
        Set(0xCD, "CMP", AddrMode.Abs);
        Set(0xCE, "DEC", AddrMode.Abs);

        Set(0xD0, "BNE", AddrMode.Rel);
        Set(0xD1, "CMP", AddrMode.IndY);
        Set(0xD2, "CMP", AddrMode.ZpInd);
        Set(0xD5, "CMP", AddrMode.ZpX);
        Set(0xD6, "DEC", AddrMode.ZpX);
        Set(0xD8, "CLD", AddrMode.Imp);
        Set(0xD9, "CMP", AddrMode.AbsY);
        Set(0xDA, "PHX", AddrMode.Imp);
        Set(0xDD, "CMP", AddrMode.AbsX);
        Set(0xDE, "DEC", AddrMode.AbsX);

        Set(0xE0, "CPX", AddrMode.Imm);
        Set(0xE1, "SBC", AddrMode.IndX);
        Set(0xE4, "CPX", AddrMode.Zp);
        Set(0xE5, "SBC", AddrMode.Zp);
        Set(0xE6, "INC", AddrMode.Zp);
        Set(0xE8, "INX", AddrMode.Imp);
        Set(0xE9, "SBC", AddrMode.Imm);
        Set(0xEA, "NOP", AddrMode.Imp);
        Set(0xEC, "CPX", AddrMode.Abs);
        Set(0xED, "SBC", AddrMode.Abs);
        Set(0xEE, "INC", AddrMode.Abs);

        Set(0xF0, "BEQ", AddrMode.Rel);
        Set(0xF1, "SBC", AddrMode.IndY);
        Set(0xF2, "SBC", AddrMode.ZpInd);
        Set(0xF5, "SBC", AddrMode.ZpX);
        Set(0xF6, "INC", AddrMode.ZpX);
        Set(0xF8, "SED", AddrMode.Imp);
        Set(0xF9, "SBC", AddrMode.AbsY);
        Set(0xFA, "PLX", AddrMode.Imp);
        Set(0xFD, "SBC", AddrMode.AbsX);
        Set(0xFE, "INC", AddrMode.AbsX);

        foreach (int op in new[] { 0x02, 0x22, 0x42, 0x44, 0x54, 0x62, 0x82, 0xC2, 0xD4, 0xE2, 0xF4 })
            Set(op, "NOP?", AddrMode.Nop2);
        foreach (int op in new[] { 0x5C, 0xDC, 0xFC })
            Set(op, "NOP?", AddrMode.Nop3);

        return t;
    }
}
