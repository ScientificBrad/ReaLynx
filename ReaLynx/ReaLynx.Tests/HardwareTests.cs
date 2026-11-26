using ReaLynx.Core;
using ReaLynx.Core.Cartridge;
using ReaLynx.Core.Mikey;
using ReaLynx.Core.Suzy;

namespace ReaLynx.Tests;

public sealed class HardwareTests
{
    [Fact]
    public void MapCtl_TogglesSuzyMikeyRomAndVectors()
    {
        var sys = new LynxSystem();
        byte[] fakeRom = new byte[512];
        fakeRom[0] = 0xAB;       // $FE00
        fakeRom[0x1FC] = 0x00;   // $FFFC
        fakeRom[0x1FD] = 0xFE;   // $FFFD -> $FE00
        sys.LoadBootRom(fakeRom);
        sys.Reset();

        // Default MAPCTL = 0x00: ROM & hardware mapped
        Assert.Equal(0x00, sys.Read(0xFFF9));
        Assert.Equal(0xAB, sys.Read(0xFE00));
        Assert.Equal(0x01, sys.Read(0xFC88)); // SUZYHREV
        Assert.Equal(0x01, sys.Read(0xFD88)); // MIKEYHREV

        // Write to $FE00 goes to underlying RAM
        sys.Write(0xFE00, 0x55);
        Assert.Equal(0xAB, sys.Read(0xFE00)); // Still sees ROM

        // Disable ROM ($FE00-$FFF7) via MAPCTL bit 2
        sys.Write(0xFFF9, 0x04);
        Assert.Equal(0x55, sys.Read(0xFE00)); // Now sees RAM!
        Assert.Equal(0xFE, sys.Read(0xFFFD)); // Vectors still ROM (bit 3 == 0)

        // Disable Suzy (bit 0), Mikey (bit 1), ROM (bit 2), Vectors (bit 3)
        sys.Write(0xFFF9, 0x0F);
        sys.Write(0xFC88, 0x77);
        sys.Write(0xFD88, 0x88);
        sys.Write(0xFFFD, 0x99);
        Assert.Equal(0x77, sys.Read(0xFC88));
        Assert.Equal(0x88, sys.Read(0xFD88));
        Assert.Equal(0x99, sys.Read(0xFFFD));
    }

    [Fact]
    public void Mikey_Timers_LinkAndRaiseInterrupts()
    {
        var sys = new LynxSystem();
        sys.Reset();

        // Timer 0: 1us clock (0), Reload=1, Enable=1, Backup=2
        sys.Write(0xFD00, 2);
        sys.Write(0xFD02, 2);
        sys.Write(0xFD01, 0x18); // Reload + Enable, clock=0 (1us = 16 master ticks)

        // Timer 2: Linked to Timer 0 (clock=7), IntEnable=1, Reload=1, Enable=1, Backup=1
        sys.Write(0xFD08, 1);
        sys.Write(0xFD0A, 1);
        sys.Write(0xFD09, 0x9F); // IntEn(80) | Reload(10) | Enable(08) | Link(07)

        // Timer 0 underflows every 3 ticks (2->1->0->underflow) = 48 master ticks.
        // Timer 2 underflows after 2 borrows from Timer 0 (1->0->underflow) = 96 master ticks.
        sys.Mikey.Advance(96);

        Assert.True(sys.Mikey.IrqPending);
        Assert.Equal(0x04, sys.Read(0xFD80)); // Bit 2 = Timer 2 IRQ

        // Acknowledge via INTRST ($FD80)
        sys.Write(0xFD80, 0x04);
        Assert.False(sys.Mikey.IrqPending);
    }

    [Fact]
    public void Mikey_OneShotTimer_FiresOnlyOnceUntilReset()
    {
        var sys = new LynxSystem();
        sys.Reset();

        // Timer 1: One-shot (EnableReload=0), IntEnable=1, EnableCount=1, Clock=0 (1us=16 ticks)
        sys.Write(0xFD04, 0x00); // Backup = 0
        sys.Write(0xFD06, 0x01); // Count = 1
        sys.Write(0xFD05, 0x88); // IntEn(80) | Enable(08) | Reload=0

        // After 2 ticks (32 master ticks), fires once
        sys.Mikey.Advance(32);
        Assert.Equal(0x02, sys.Read(0xFD80));

        // Acknowledge IRQ; advancing further MUST NOT re-trigger the stopped one-shot timer!
        sys.Write(0xFD80, 0x02);
        sys.Mikey.Advance(1000);
        Assert.Equal(0x00, sys.Read(0xFD80));
        Assert.False(sys.Mikey.IrqPending);
    }

    [Fact]
    public void Mikey_AudioLfsr_GeneratesNonSilentStereoPcm()
    {
        var sys = new LynxSystem();
        sys.Reset();

        // Configure Audio Channel 0 for audible square-like tone (~1 kHz)
        sys.Write(0xFD20, 0x40); // Volume = +64
        sys.Write(0xFD21, 0x01); // Feedback tap 0
        sys.Write(0xFD24, 60);   // Backup = 60
        sys.Write(0xFD26, 60);   // Count = 60
        sys.Write(0xFD25, 0x1B); // Reload | Enable | Clock=3 (8us)

        sys.Mikey.Advance(160_000); // 10ms of 16 MHz ticks (~441 stereo samples = 882 shorts)
        short[] samples = sys.Mikey.DrainAllAudioSamples();

        Assert.True(samples.Length > 800);
        bool hasPositive = samples.Any(s => s > 500);
        bool hasNegative = samples.Any(s => s < -500);
        Assert.True(hasPositive && hasNegative, "Audio LFSR should oscillate between positive and negative PCM values.");
    }

    [Fact]
    public void Suzy_Math_UnsignedSignedMultiplyAccumulateAndDivide()
    {
        var sys = new LynxSystem();
        sys.Reset();

        // 1. Unsigned 16x16 multiply: 1000 * 2000 = 2,000,000 (0x001E8480)
        sys.Write(0xFC92, 0x00); // SPRSYS: unsigned, no accumulate
        sys.Write(0xFC52, 0xE8); // MATHD (lo of 1000) -> clears MATHC
        sys.Write(0xFC53, 0x03); // MATHC (hi of 1000)
        sys.Write(0xFC54, 0xD0); // MATHB (lo of 2000) -> clears MATHA
        sys.Write(0xFC55, 0x07); // MATHA (hi of 2000) -> triggers multiply

        uint prod = (uint)(sys.Read(0xFC60) | (sys.Read(0xFC61) << 8) | (sys.Read(0xFC62) << 16) | (sys.Read(0xFC63) << 24));
        Assert.Equal(2_000_000u, prod);

        // 2. Signed multiply + accumulate: (-10) * 25 = -250
        sys.Write(0xFC6C, 0x00); sys.Write(0xFC6E, 0x00); // Clear accumulator
        sys.Write(0xFC92, 0xC0); // SignMath (0x80) | Accumulate (0x40)
        sys.Write(0xFC52, 0xF6); // -10 lo
        sys.Write(0xFC53, 0xFF); // -10 hi
        sys.Write(0xFC54, 25);   // 25 lo (clears MATHA)
        sys.Write(0xFC55, 0x00); // 25 hi -> triggers multiply

        int signedProd = (int)(sys.Read(0xFC60) | (sys.Read(0xFC61) << 8) | (sys.Read(0xFC62) << 16) | (sys.Read(0xFC63) << 24));
        int acc = (int)(sys.Read(0xFC6C) | (sys.Read(0xFC6D) << 8) | (sys.Read(0xFC6E) << 16) | (sys.Read(0xFC6F) << 24));
        Assert.Equal(-250, signedProd);
        Assert.Equal(-250, acc);

        // 3. Unsigned 32/16 divide: 100000 / 300 = 333 remainder 100
        sys.Write(0xFC56, 0x2C); // MATHP = 300 lo
        sys.Write(0xFC57, 0x01); // MATHN = 300 hi
        sys.Write(0xFC60, 0xA0); // 100000 = 0x000186A0
        sys.Write(0xFC61, 0x86);
        sys.Write(0xFC62, 0x01);
        sys.Write(0xFC63, 0x00); // Writing MATHE triggers divide

        uint quot = (uint)(sys.Read(0xFC52) | (sys.Read(0xFC53) << 8) | (sys.Read(0xFC54) << 16) | (sys.Read(0xFC55) << 24));
        uint rem = (uint)(sys.Read(0xFC6C) | (sys.Read(0xFC6D) << 8) | (sys.Read(0xFC6E) << 16) | (sys.Read(0xFC6F) << 24));
        Assert.Equal(333u, quot);
        Assert.Equal(100u, rem);

        // 4. Divide by zero sets MathWarning (bit 6) and returns 0xFFFFFFFF
        sys.Write(0xFC56, 0x00);
        sys.Write(0xFC63, 0x00);
        Assert.True((sys.Read(0xFC92) & 0x40) != 0);
        Assert.Equal(0xFF, sys.Read(0xFC52));
        Assert.Equal(0xFF, sys.Read(0xFC55));
    }

    [Fact]
    public void Suzy_SpriteEngine_RendersLiteralSpriteAndPaletteToFramebuffer()
    {
        var sys = new LynxSystem();
        sys.Reset();

        // Set up framebuffer at $2000 and palette color 5 = bright red (R=15, G=0, B=0)
        sys.Write(0xFC08, 0x00); sys.Write(0xFC09, 0x20); // VIDBAS = $2000
        sys.Write(0xFD94, 0x00); sys.Write(0xFD95, 0x20); // DISPADR = $2000
        sys.Write(0xFD92, 0x0D);                          // DISPCTL: DMA + Color
        sys.Write(0xFDA5, 0x00);                          // GREEN[5] = 0
        sys.Write(0xFDB5, 0x0F);                          // BLUERED[5] = B:0, R:15

        // Build a 2x2 literal 4bpp sprite at $1100 (all pixels pen 5)
        // Line 0: offset=2, data=0x55
        // Line 1: offset=2, data=0x55
        // End: 0x00
        sys.Ram[0x1100] = 0x02; sys.Ram[0x1101] = 0x55;
        sys.Ram[0x1102] = 0x02; sys.Ram[0x1103] = 0x55;
        sys.Ram[0x1104] = 0x00;

        // Build SCB at $1000:
        // SPRCTL0 = 0xC4 (4bpp | Normal)
        // SPRCTL1 = 0x90 (Literal | Reload HV size | Reload palette)
        // SPRCOLL = 0x20 (No collide)
        ushort scb = 0x1000;
        sys.Ram[scb + 0] = 0xC4;
        sys.Ram[scb + 1] = 0x90;
        sys.Ram[scb + 2] = 0x20;
        sys.Ram[scb + 3] = 0x00; sys.Ram[scb + 4] = 0x00; // SCBNEXT = 0
        sys.Ram[scb + 5] = 0x00; sys.Ram[scb + 6] = 0x11; // SPRDLINE = $1100
        sys.Ram[scb + 7] = 10;   sys.Ram[scb + 8] = 0;    // HPOS = 10
        sys.Ram[scb + 9] = 20;   sys.Ram[scb + 10] = 0;   // VPOS = 20
        sys.Ram[scb + 11] = 0x00; sys.Ram[scb + 12] = 0x01; // HSIZ = 1.0 ($0100)
        sys.Ram[scb + 13] = 0x00; sys.Ram[scb + 14] = 0x01; // VSIZ = 1.0 ($0100)
        // Identity palette (8 bytes)
        for (int i = 0; i < 8; i++)
            sys.Ram[scb + 15 + i] = (byte)(((i * 2) << 4) | (i * 2 + 1));

        // Trigger Suzy
        sys.Write(0xFC10, 0x00); sys.Write(0xFC11, 0x10); // SCBNEXT = $1000
        sys.Write(0xFC90, 0x01); // SUZYBUSEN = 1
        sys.Write(0xFC91, 0x01); // SPRGO = 1

        Assert.Equal(1, sys.Suzy.LastSpriteCount);
        // Verify RAM at (x=10, y=20) => offset 20*80 + 5 = 1605
        Assert.Equal(0x55, sys.Ram[0x2000 + 20 * 80 + 5]);
        Assert.Equal(0x55, sys.Ram[0x2000 + 21 * 80 + 5]);

        // Render through Mikey and verify red BGRA pixel (0xFFFF0000)
        sys.Mikey.RenderFullFramebufferFromRam();
        Assert.Equal(0xFFFF0000u, sys.Mikey.Framebuffer[20 * 160 + 10]);
        Assert.Equal(0xFFFF0000u, sys.Mikey.Framebuffer[21 * 160 + 11]);
    }

    [Fact]
    public void Suzy_Joystick_InvertsDirectionsWhenSprSysLeftHandBitIsSet()
    {
        var sys = new LynxSystem();
        sys.Reset();
        sys.SetButtons(LynxButtons.Up | LynxButtons.Left | LynxButtons.A);

        // SPRSYS bit 3 = 0: raw bits (Up=0x80 | Left=0x20 | A=0x01 = 0xA1)
        sys.Write(0xFC92, 0x00);
        Assert.Equal(0xA1, sys.Read(0xFCB0));

        // SPRSYS bit 3 = 1 (LeftHand): Up<->Down, Left<->Right (Down=0x40 | Right=0x10 | A=0x01 = 0x51)
        sys.Write(0xFC92, 0x08);
        Assert.Equal(0x51, sys.Read(0xFCB0));
    }
}
