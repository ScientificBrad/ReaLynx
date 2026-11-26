using ReaLynx.Core;
using ReaLynx.Core.Cartridge;
using ReaLynx.Core.Mikey;

namespace ReaLynx.Tests;

public sealed class RegressionAndDebuggerTests
{
    [Fact]
    public void DispCtl_Bit1FlipsDisplay180Degrees()
    {
        var sys = new LynxSystem();
        sys.Reset();

        // Palette 1 = white
        sys.Write(0xFDA1, 0x0F);
        sys.Write(0xFDB1, 0xFF);
        sys.Write(0xFD94, 0x00); sys.Write(0xFD95, 0x20); // DISPADR = $2000

        // Put pixel pen 1 at (x=0, y=0) -> high nibble of $2000
        sys.Ram[0x2000] = 0x10;

        // Normal (DISPCTL = $0D, bit 1 = 0)
        sys.Write(0xFD92, 0x0D);
        sys.Mikey.RenderFullFramebufferFromRam();
        Assert.Equal(0xFFFFFFFFu, sys.Mikey.Framebuffer[0]);
        Assert.Equal(0xFF000000u, sys.Mikey.Framebuffer[101 * 160 + 159]);

        // Flipped 180 (DISPCTL = $0F, bit 1 = 1)
        sys.Write(0xFD92, 0x0F);
        sys.Mikey.RenderFullFramebufferFromRam();
        Assert.Equal(0xFF000000u, sys.Mikey.Framebuffer[0]);
        Assert.Equal(0xFFFFFFFFu, sys.Mikey.Framebuffer[101 * 160 + 159]);
    }

    [Fact]
    public void Suzy_CollisionBuffer_DetectsOverlapAndWritesDepository()
    {
        var sys = new LynxSystem();
        sys.Reset();

        sys.Write(0xFC08, 0x00); sys.Write(0xFC09, 0x20); // VIDBAS = $2000
        sys.Write(0xFC0A, 0x00); sys.Write(0xFC0B, 0x40); // COLLBAS = $4000
        sys.Write(0xFC24, 0x18); sys.Write(0xFC25, 0x00); // COLLOFF = 24 bytes after SCB
        sys.Write(0xFC90, 0x01); // SUZYBUSEN = 1

        // 2x1 literal 4bpp sprite at $1200 (pen 3)
        sys.Ram[0x1200] = 0x02; sys.Ram[0x1201] = 0x33; sys.Ram[0x1202] = 0x00;

        void WriteScb(ushort addr, ushort next, byte collNum, short x, short y)
        {
            sys.Ram[addr + 0] = 0xC4; // 4bpp Normal
            sys.Ram[addr + 1] = 0x90; // Literal, reload HV, reload palette
            sys.Ram[addr + 2] = collNum;
            sys.Ram[addr + 3] = (byte)(next & 0xFF); sys.Ram[addr + 4] = (byte)(next >> 8);
            sys.Ram[addr + 5] = 0x00; sys.Ram[addr + 6] = 0x12;
            sys.Ram[addr + 7] = (byte)(x & 0xFF); sys.Ram[addr + 8] = (byte)(x >> 8);
            sys.Ram[addr + 9] = (byte)(y & 0xFF); sys.Ram[addr + 10] = (byte)(y >> 8);
            sys.Ram[addr + 11] = 0x00; sys.Ram[addr + 12] = 0x01;
            sys.Ram[addr + 13] = 0x00; sys.Ram[addr + 14] = 0x01;
            for (int i = 0; i < 8; i++)
                sys.Ram[addr + 15 + i] = (byte)(((i * 2) << 4) | (i * 2 + 1));
            sys.Ram[addr + 24] = 0x00; // Depository
        }

        // Sprite 1 (collNum=2) at (10,10), chains to Sprite 2 (collNum=5) at (10,10)
        WriteScb(0x1000, 0x1040, 2, 10, 10);
        WriteScb(0x1040, 0x0000, 5, 10, 10);

        sys.Write(0xFC10, 0x00); sys.Write(0xFC11, 0x10);
        sys.Write(0xFC91, 0x01); // SPRGO

        Assert.Equal(2, sys.Suzy.LastSpriteCount);
        Assert.Equal(0, sys.Ram[0x1000 + 24]); // Sprite 1 saw empty collision buffer
        Assert.Equal(2, sys.Ram[0x1040 + 24]); // Sprite 2 collided with Sprite 1 (collNum=2)!
    }

    [Fact]
    public void Debugger_BreakpointsAndBoundedTraceLog_WorkDeterministically()
    {
        var sys = new LynxSystem
        {
            TraceEnabled = true,
            MaxTraceEntries = 3
        };
        sys.Reset();
        sys.Write(0xFFF9, 0x0C);

        // 5 NOPs at $0200..$0204
        for (int i = 0; i < 5; i++) sys.Ram[0x0200 + i] = 0xEA;
        sys.Cpu.PC = 0x0200;
        sys.AddBreakpoint(0x0204);

        sys.RunMasterTicks(1000);

        Assert.True(sys.HitBreakpoint);
        Assert.Equal(0x0204, sys.LastBreakpointAddress);
        Assert.Equal(0x0204, sys.Cpu.PC);
        Assert.Equal(3, sys.TraceLog.Count); // Bounded to MaxTraceEntries = 3
    }

    [Fact]
    public void FramebufferRotation_TransformsDimensionsAndCoordinates()
    {
        uint[] fb = new uint[MikeyChip.ScreenWidth * MikeyChip.ScreenHeight];
        fb[0] = 0xFF112233u; // Top-left (0, 0)

        var (leftPx, lw, lh) = BmpWriter.GetRotatedFramebuffer(fb, CartRotation.Left);
        Assert.Equal(102, lw);
        Assert.Equal(160, lh);
        Assert.Equal(0xFF112233u, leftPx[(159) * 102 + 0]); // (0,0) -> (0, 159)

        var (rightPx, rw, rh) = BmpWriter.GetRotatedFramebuffer(fb, CartRotation.Right);
        Assert.Equal(102, rw);
        Assert.Equal(160, rh);
        Assert.Equal(0xFF112233u, rightPx[0 * 102 + 101]); // (0,0) -> (101, 0)
    }

    [Fact]
    public void ComLynxUart_LoopbackModeSetsRxReadyAndIrq()
    {
        var sys = new LynxSystem();
        sys.Reset();
        sys.Mikey.ComLynxLoopback = true;

        sys.Write(0xFD8C, 0x40); // Enable RX interrupt
        sys.Write(0xFD8D, 0x5A); // Send byte

        Assert.True((sys.Read(0xFD8C) & 0x40) != 0); // RXRDY set
        Assert.Equal(0x10, sys.Read(0xFD80));        // Timer 4 / UART IRQ flag
        Assert.Equal(0x5A, sys.Read(0xFD8D));        // Read echoed byte
        Assert.True((sys.Read(0xFD8C) & 0x40) == 0); // RXRDY cleared after read
    }

    [Fact]
    public void Suzy_FourQuadrantSprite_DrawsInCounterClockwiseOrder()
    {
        var sys = new LynxSystem();
        sys.Reset();

        sys.Write(0xFC08, 0x00); sys.Write(0xFC09, 0x20); // VIDBAS = $2000
        sys.Write(0xFC90, 0x01); // SUZYBUSEN = 1

        // 4-quadrant literal 4bpp sprite centered at (10, 10):
        // Quad 0 (DR: +X,+Y) = pen 1 at (10,10)
        // Quad 1 (UR: +X,-Y) = pen 2 at (10,9)
        // Quad 2 (UL: -X,-Y) = pen 3 at (9,9)
        // Quad 3 (DL: -X,+Y) = pen 4 at (9,10)
        ushort d = 0x1100;
        sys.Ram[d++] = 0x02; sys.Ram[d++] = 0x10; // Q0: 1 pixel pen 1, 1 pixel pen 0
        sys.Ram[d++] = 0x01;                      // Next quad (Q1: UR)
        sys.Ram[d++] = 0x02; sys.Ram[d++] = 0x20; // Q1: pen 2
        sys.Ram[d++] = 0x01;                      // Next quad (Q2: UL)
        sys.Ram[d++] = 0x02; sys.Ram[d++] = 0x30; // Q2: pen 3
        sys.Ram[d++] = 0x01;                      // Next quad (Q3: DL)
        sys.Ram[d++] = 0x02; sys.Ram[d++] = 0x40; // Q3: pen 4
        sys.Ram[d++] = 0x00;                      // End

        ushort scb = 0x1000;
        sys.Ram[scb + 0] = 0xC4; // 4bpp Normal
        sys.Ram[scb + 1] = 0x90; // Literal, reload HV, reload palette, startQuad=0
        sys.Ram[scb + 2] = 0x20; // No collide
        sys.Ram[scb + 5] = 0x00; sys.Ram[scb + 6] = 0x11; // SPRDLINE = $1100
        sys.Ram[scb + 7] = 10;   sys.Ram[scb + 9] = 10;   // HPOS=10, VPOS=10
        sys.Ram[scb + 12] = 0x01; sys.Ram[scb + 14] = 0x01; // HSIZ=1.0, VSIZ=1.0
        for (int i = 0; i < 8; i++)
            sys.Ram[scb + 15 + i] = (byte)(((i * 2) << 4) | (i * 2 + 1));

        sys.Write(0xFC10, 0x00); sys.Write(0xFC11, 0x10);
        sys.Write(0xFC91, 0x01);

        byte GetPen(int x, int y)
        {
            byte b = sys.Ram[0x2000 + y * 80 + (x >> 1)];
            return (x & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
        }

        Assert.Equal(1, GetPen(10, 10)); // Q0 Down-Right
        Assert.Equal(2, GetPen(10, 9));  // Q1 Up-Right
        Assert.Equal(3, GetPen(9, 9));   // Q2 Up-Left
        Assert.Equal(4, GetPen(9, 10));  // Q3 Down-Left
    }

    [Fact]
    public void Suzy_BackgroundType0Sprite_DoesNotOverwriteCollisionDepository()
    {
        var sys = new LynxSystem();
        sys.Reset();

        sys.Write(0xFC08, 0x00); sys.Write(0xFC09, 0x20); // VIDBAS = $2000
        sys.Write(0xFC0A, 0x00); sys.Write(0xFC0B, 0x40); // COLLBAS = $4000
        sys.Write(0xFC24, 0x00); sys.Write(0xFC25, 0x00); // COLLOFF = 0 (points at SCB[0]!)
        sys.Write(0xFC90, 0x01); // SUZYBUSEN = 1

        sys.Ram[0x1200] = 0x02; sys.Ram[0x1201] = 0x11; sys.Ram[0x1202] = 0x00;
        ushort scb = 0x1000;
        sys.Ram[scb + 0] = 0xC0; // 4bpp Background (type 0)
        sys.Ram[scb + 1] = 0x90; // Literal, reload HV, reload palette
        sys.Ram[scb + 2] = 0x00; // Collide enabled (bit 5 = 0), collNum = 0
        sys.Ram[scb + 5] = 0x00; sys.Ram[scb + 6] = 0x12;
        sys.Ram[scb + 7] = 10;   sys.Ram[scb + 9] = 10;
        sys.Ram[scb + 12] = 0x01; sys.Ram[scb + 14] = 0x01;
        for (int i = 0; i < 8; i++)
            sys.Ram[scb + 15 + i] = (byte)(((i * 2) << 4) | (i * 2 + 1));

        sys.Write(0xFC10, 0x00); sys.Write(0xFC11, 0x10);
        sys.Write(0xFC91, 0x01);

        // Background (type 0) MUST NOT overwrite SCB[0] via collision depository!
        Assert.Equal(0xC0, sys.Ram[scb + 0]);
    }
}
