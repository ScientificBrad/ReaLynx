using ReaLynx.Core;
using ReaLynx.Core.Cpu;

namespace ReaLynx.Tests;

internal sealed class FlatBus : IMemoryBus
{
    public readonly byte[] Mem = new byte[65536];
    public byte Read(ushort address) => Mem[address];
    public void Write(ushort address, byte value) => Mem[address] = value;
    public byte Peek(ushort address) => Mem[address];

    public void Load(ushort addr, params byte[] bytes)
    {
        Buffer.BlockCopy(bytes, 0, Mem, addr, bytes.Length);
    }

    public void SetResetVector(ushort addr)
    {
        Mem[0xFFFC] = (byte)(addr & 0xFF);
        Mem[0xFFFD] = (byte)(addr >> 8);
    }

    public void SetIrqVector(ushort addr)
    {
        Mem[0xFFFE] = (byte)(addr & 0xFF);
        Mem[0xFFFF] = (byte)(addr >> 8);
    }

    public void SetNmiVector(ushort addr)
    {
        Mem[0xFFFA] = (byte)(addr & 0xFF);
        Mem[0xFFFB] = (byte)(addr >> 8);
    }
}

public sealed class Cpu65SC02Tests
{
    [Fact]
    public void Bra_PhxPlx_PhyPly_Stz_IncDecA_WorkCorrectly()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.Load(0x0200,
            0xA9, 0x41,       // LDA #$41
            0x1A,             // INC A -> $42
            0xAA,             // TAX
            0xDA,             // PHX
            0x3A,             // DEC A -> $41
            0xA8,             // TAY
            0x5A,             // PHY
            0xA2, 0x00,       // LDX #$00
            0xA0, 0x00,       // LDY #$00
            0x7A,             // PLY -> $41
            0xFA,             // PLX -> $42
            0x80, 0x02,       // BRA +2 (skips 2 bytes)
            0xA9, 0xFF,       // (skipped)
            0x64, 0x10        // STZ $10
        );
        bus.Mem[0x10] = 0xAA;

        var cpu = new Cpu65SC02(bus);
        for (int i = 0; i < 13; i++) cpu.Step();

        Assert.Equal(0x41, cpu.A);
        Assert.Equal(0x42, cpu.X);
        Assert.Equal(0x41, cpu.Y);
        Assert.Equal(0x00, bus.Mem[0x10]);
    }

    [Fact]
    public void BitImmediate_LeavesNAndVUnchanged_OnlySetsZ()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.Load(0x0200,
            0xA9, 0x01, // LDA #$01 (N=0, Z=0)
            0xB8,       // CLV (V=0)
            0x89, 0xC0  // BIT #$C0 -> A & $C0 == 0 -> Z=1, N and V MUST remain 0!
        );

        var cpu = new Cpu65SC02(bus);
        cpu.Step();
        cpu.Step();
        int cycles = cpu.Step();

        Assert.Equal(2, cycles);
        Assert.True((cpu.P & (byte)StatusFlags.Zero) != 0);
        Assert.True((cpu.P & (byte)StatusFlags.Negative) == 0);
        Assert.True((cpu.P & (byte)StatusFlags.Overflow) == 0);
    }

    [Fact]
    public void JmpIndirect_FixesNmosPageBoundaryBug_AndTakes6Cycles()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.Load(0x0200, 0x6C, 0xFF, 0x10); // JMP ($10FF)
        bus.Mem[0x10FF] = 0x34;
        bus.Mem[0x1100] = 0x12; // 65SC02 reads $1100, NOT $1000!
        bus.Mem[0x1000] = 0x99;

        var cpu = new Cpu65SC02(bus);
        int cycles = cpu.Step();

        Assert.Equal(6, cycles);
        Assert.Equal(0x1234, cpu.PC);
    }

    [Fact]
    public void JmpAbsIndexedX_AndZpIndirect_Work()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.Mem[0x20] = 0x00;
        bus.Mem[0x21] = 0x05;
        bus.Mem[0x0500] = 0x77;

        bus.Mem[0x0402] = 0x00;
        bus.Mem[0x0403] = 0x06;

        bus.Load(0x0200,
            0xB2, 0x20,       // LDA ($20) -> $77
            0xA2, 0x02,       // LDX #$02
            0x7C, 0x00, 0x04  // JMP ($0400,X) -> $0600
        );

        var cpu = new Cpu65SC02(bus);
        int c1 = cpu.Step();
        cpu.Step();
        int c3 = cpu.Step();

        Assert.Equal(5, c1);
        Assert.Equal(6, c3);
        Assert.Equal(0x77, cpu.A);
        Assert.Equal(0x0600, cpu.PC);
    }

    [Fact]
    public void TsbAndTrb_ModifyBitsAndSetZ()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.Mem[0x30] = 0x0F;
        bus.Load(0x0200,
            0xA9, 0xF0, // LDA #$F0
            0x04, 0x30, // TSB $30 -> Z=1 (0xF0 & 0x0F == 0), mem = 0xFF
            0xA9, 0x03, // LDA #$03
            0x14, 0x30  // TRB $30 -> Z=0 (0x03 & 0xFF != 0), mem = 0xFC
        );

        var cpu = new Cpu65SC02(bus);
        cpu.Step();
        int cTsb = cpu.Step();
        Assert.Equal(5, cTsb);
        Assert.Equal(0xFF, bus.Mem[0x30]);
        Assert.True((cpu.P & (byte)StatusFlags.Zero) != 0);

        cpu.Step();
        int cTrb = cpu.Step();
        Assert.Equal(5, cTrb);
        Assert.Equal(0xFC, bus.Mem[0x30]);
        Assert.True((cpu.P & (byte)StatusFlags.Zero) == 0);
    }

    [Fact]
    public void DecimalMode_AdcAndSbc_ComputeValidFlagsAndTakeExtraCycle()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.Load(0x0200,
            0xF8,       // SED
            0x18,       // CLC
            0xA9, 0x45, // LDA #$45
            0x69, 0x55, // ADC #$55 -> $00, C=1, Z=1, N=0 (3 cycles in BCD)
            0x38,       // SEC
            0xA9, 0x50, // LDA #$50
            0xE9, 0x25  // SBC #$25 -> $25, C=1, Z=0, N=0 (3 cycles in BCD)
        );

        var cpu = new Cpu65SC02(bus);
        cpu.Step(); cpu.Step(); cpu.Step();
        int adcCycles = cpu.Step();
        Assert.Equal(3, adcCycles);
        Assert.Equal(0x00, cpu.A);
        Assert.True((cpu.P & (byte)StatusFlags.Carry) != 0);
        Assert.True((cpu.P & (byte)StatusFlags.Zero) != 0);
        Assert.True((cpu.P & (byte)StatusFlags.Negative) == 0);

        cpu.Step(); cpu.Step();
        int sbcCycles = cpu.Step();
        Assert.Equal(3, sbcCycles);
        Assert.Equal(0x25, cpu.A);
        Assert.True((cpu.P & (byte)StatusFlags.Carry) != 0);
        Assert.True((cpu.P & (byte)StatusFlags.Zero) == 0);
    }

    [Fact]
    public void BrkAndIrq_ClearDecimalFlag_AndReturnViaRti()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.SetIrqVector(0x0300);
        bus.Load(0x0200,
            0xF8,       // SED
            0x00, 0x00, // BRK #$00
            0xEA        // NOP
        );
        bus.Load(0x0300,
            0xA9, 0x99, // LDA #$99
            0x40        // RTI
        );

        var cpu = new Cpu65SC02(bus);
        cpu.Step(); // SED
        Assert.True((cpu.P & (byte)StatusFlags.Decimal) != 0);

        int brkCycles = cpu.Step(); // BRK
        Assert.Equal(7, brkCycles);
        Assert.Equal(0x0300, cpu.PC);
        Assert.True((cpu.P & (byte)StatusFlags.Decimal) == 0); // 65SC02 clears D on BRK!
        Assert.True((cpu.P & (byte)StatusFlags.Interrupt) != 0);

        cpu.Step(); // LDA #$99
        cpu.Step(); // RTI
        Assert.Equal(0x0203, cpu.PC);
        Assert.Equal(0x99, cpu.A);
        Assert.True((cpu.P & (byte)StatusFlags.Decimal) != 0); // Restored by RTI
    }

    [Fact]
    public void Lynx65SC02_OmitsRockwellAndWdcExtensions_TreatsAsNop()
    {
        var bus = new FlatBus();
        bus.SetResetVector(0x0200);
        bus.Mem[0x10] = 0xFF;
        // 0x07 (RMB0 on Rockwell), 0x87 (SMB0), 0xCB (WAI on WDC), 0xDB (STP on WDC)
        bus.Load(0x0200, 0x07, 0x87, 0xCB, 0xDB, 0xA9, 0x55);

        var cpu = new Cpu65SC02(bus);
        Assert.Equal(1, cpu.Step());
        Assert.Equal(1, cpu.Step());
        Assert.Equal(1, cpu.Step());
        Assert.Equal(1, cpu.Step());
        cpu.Step();

        Assert.Equal(0x55, cpu.A);
        Assert.Equal(0xFF, bus.Mem[0x10]); // Untouched
        Assert.False(cpu.IsSleeping);
    }
}
