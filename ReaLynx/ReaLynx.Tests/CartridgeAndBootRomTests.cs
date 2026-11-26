using ReaLynx.Core;
using ReaLynx.Core.Cartridge;
using ReaLynx.Core.Suzy;

namespace ReaLynx.Tests;

public sealed class CartridgeAndBootRomTests
{
    private const string WorkspaceBootRomPath = @"D:\Emulation\AtariLynx\BOOTROM\lynxboot.img";

    [Fact]
    public void LnxHeaderValidation_AcceptsValid_RejectsInvalid()
    {
        byte[] bank0 = new byte[65536];
        bank0[0] = 0x42;
        byte[] lnx = LynxCartridge.BuildLnxImage(bank0, 256, "TestCart", "ReaLynx", CartRotation.Left);

        var cart = LynxCartridge.LoadLnx(lnx);
        Assert.Equal("TestCart", cart.Info.Name);
        Assert.Equal("ReaLynx", cart.Info.Manufacturer);
        Assert.Equal((ushort)256, cart.Info.Bank0PageSize);
        Assert.Equal(CartRotation.Left, cart.Info.Rotation);
        Assert.Equal(0x42, cart.ReadBank0());

        // Bad magic
        byte[] badMagic = (byte[])lnx.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<CartridgeLoadException>(() => LynxCartridge.LoadLnx(badMagic));

        // Bad version
        byte[] badVer = (byte[])lnx.Clone();
        badVer[8] = 2;
        Assert.Throws<CartridgeLoadException>(() => LynxCartridge.LoadLnx(badVer));

        // Bad rotation
        byte[] badRot = (byte[])lnx.Clone();
        badRot[58] = 9;
        Assert.Throws<CartridgeLoadException>(() => LynxCartridge.LoadLnx(badRot));
    }

    [Fact]
    public void CartridgeStrobe_ShiftsBlockAddressAndReadsCorrectBank0Offset()
    {
        byte[] bank0 = new byte[65536]; // 256 pages * 256 bytes
        bank0[0x05 * 256 + 0] = 0xDE;
        bank0[0x05 * 256 + 1] = 0xAD;
        var cart = LynxCartridge.LoadRaw(bank0, 256);

        // Shift in block 0x05 (binary 00000101, MSB first)
        for (int bit = 7; bit >= 0; bit--)
        {
            bool dataBit = ((0x05 >> bit) & 1) != 0;
            cart.UpdateAddressStrobe(false, dataBit, false);
            cart.UpdateAddressStrobe(true, dataBit, false);
        }
        cart.UpdateAddressStrobe(false, false, false);

        Assert.Equal(0x05, cart.ShiftRegister);
        Assert.Equal(0xDE, cart.ReadBank0());
        Assert.Equal(0xAD, cart.ReadBank0());
    }

    [Fact]
    public void RealAtariLynxBootRom_ExecutesAndDisplaysInsertGameWithoutCartridge()
    {
        if (!File.Exists(WorkspaceBootRomPath))
            return; // Skip only if user's boot ROM is absent

        var sys = new LynxSystem();
        sys.LoadBootRomFromFile(WorkspaceBootRomPath);
        sys.Reset();

        // Real Lynx Boot ROM reset vector is in $FE00-$FFF7
        Assert.InRange(sys.Cpu.PC, (ushort)0xFE00, (ushort)0xFFF7);

        // Run 30 frames of the real boot ROM with no cartridge inserted.
        // The real Atari Lynx Boot ROM zeroes 64KB RAM (~11 frames), initializes Mikey,
        // checks the cartridge, and draws the scaled "INSERT GAME" sprite via Suzy!
        for (int f = 0; f < 30; f++)
            sys.RunFrame();

        // Verify the Boot ROM actually ran millions of ticks, enabled display, and drew non-black pixels!
        Assert.True(sys.TotalMasterTicks > 2_000_000);
        Assert.True((sys.Mikey.DispCtl & 0x01) != 0, "Real Boot ROM should enable Video DMA (bit 0) in DISPCTL.");

        int nonBlackPixels = sys.Mikey.Framebuffer.Count(px => (px & 0x00FFFFFF) != 0);
        Assert.True(nonBlackPixels > 1000, $"Real Boot ROM should render 'INSERT GAME' pixels (got {nonBlackPixels}).");
    }

    [Fact]
    public void InteractiveLynxProgram_MovesSpriteWithJoystickAndProducesAudio()
    {
        // End-to-end 65SC02 program running in RAM with Mikey timers, display DMA, Suzy sprite, joystick input, and audio
        var sys = new LynxSystem();
        sys.Reset();
        sys.Write(0xFFF9, 0x0C); // Disable Boot ROM & Vectors so RAM vectors at $FFFC are used

        // Program at $0200:
        // - Configure palette color 1 = bright green ($FDA1=$0F, $FDB1=$00)
        // - Configure DISPCTL=$0D, DISPADR=$2000, VIDBAS=$2000, SUZYBUSEN=$01
        // - Enable Audio 0 tone
        // - Loop: read JOYSTICK ($FCB0), if Right (bit 4) increment SCB HPOS ($1007), trigger Suzy SPRGO ($FC91)
        ushort pc = 0x0200;
        void Emit(params byte[] b) { foreach (byte x in b) sys.Ram[pc++] = x; }

        Emit(0xA9, 0x0F, 0x8D, 0xA1, 0xFD); // LDA #$0F; STA $FDA1 (Green[1]=15)
        Emit(0xA9, 0x00, 0x8D, 0xB1, 0xFD); // LDA #$00; STA $FDB1 (BlueRed[1]=0)
        Emit(0xA9, 0x00, 0x8D, 0x94, 0xFD); // DISPADR lo = $00
        Emit(0xA9, 0x20, 0x8D, 0x95, 0xFD); // DISPADR hi = $20
        Emit(0xA9, 0x0D, 0x8D, 0x92, 0xFD); // DISPCTL = $0D
        Emit(0xA9, 0x00, 0x8D, 0x08, 0xFC); // VIDBAS lo = $00
        Emit(0xA9, 0x20, 0x8D, 0x09, 0xFC); // VIDBAS hi = $20
        Emit(0xA9, 0x01, 0x8D, 0x90, 0xFC); // SUZYBUSEN = 1
        // Audio 0 on
        Emit(0xA9, 0x40, 0x8D, 0x20, 0xFD); // AUD0VOL = $40
        Emit(0xA9, 0x01, 0x8D, 0x21, 0xFD); // AUD0FEED = $01
        Emit(0xA9, 0x20, 0x8D, 0x24, 0xFD); // AUD0BKUP = $20
        Emit(0xA9, 0x19, 0x8D, 0x25, 0xFD); // AUD0CTLA = $19

        ushort loopAddr = pc;
        Emit(0xAD, 0xB0, 0xFC);             // LDA $FCB0 (JOYSTICK)
        Emit(0x29, 0x10);                   // AND #$10 (Right)
        Emit(0xF0, 0x03);                   // BEQ +3
        Emit(0xEE, 0x07, 0x10);             // INC $1007 (SCB HPOS lo)
        Emit(0xA9, 0x00, 0x8D, 0x10, 0xFC); // SCBNEXT lo = $00
        Emit(0xA9, 0x10, 0x8D, 0x11, 0xFC); // SCBNEXT hi = $10
        Emit(0xA9, 0x01, 0x8D, 0x91, 0xFC); // SPRGO = 1
        Emit(0x4C, (byte)(loopAddr & 0xFF), (byte)(loopAddr >> 8)); // JMP loop

        // Sprite data at $1100 (2x1 pen 1)
        sys.Ram[0x1100] = 0x02; sys.Ram[0x1101] = 0x11; sys.Ram[0x1102] = 0x00;
        // SCB at $1000
        sys.Ram[0x1000] = 0xC4; sys.Ram[0x1001] = 0x90; sys.Ram[0x1002] = 0x20;
        sys.Ram[0x1005] = 0x00; sys.Ram[0x1006] = 0x11;
        sys.Ram[0x1007] = 10;   sys.Ram[0x1009] = 10;
        sys.Ram[0x100C] = 0x01; sys.Ram[0x100E] = 0x01;
        for (int i = 0; i < 8; i++) sys.Ram[0x100F + i] = (byte)(((i * 2) << 4) | (i * 2 + 1));

        sys.Cpu.PC = 0x0200;

        // Run 20 instructions without input -> sprite at X=10
        for (int i = 0; i < 20; i++) sys.StepInstruction();
        Assert.Equal(10, sys.Ram[0x1007]);

        // Press Right and run 20 instructions -> sprite moves right!
        sys.SetButtons(LynxButtons.Right);
        for (int i = 0; i < 20; i++) sys.StepInstruction();
        Assert.True(sys.Ram[0x1007] > 10, "Pressing Right should move sprite X coordinate.");

        sys.Mikey.RenderFullFramebufferFromRam();
        int movedX = sys.Ram[0x1007];
        Assert.Equal(0xFF00FF00u, sys.Mikey.Framebuffer[10 * 160 + movedX]);

        // Verify audio samples generated
        sys.RunMasterTicks(50_000);
        var pcm = sys.Mikey.DrainAllAudioSamples();
        Assert.Contains(pcm, s => s != 0);
    }

    [Fact]
    public void Bs93HomebrewExecutable_LoadsDirectlyIntoRamAndExecutes()
    {
        // Minimal BS93 header (80 08 <0200 BE> <len BE> "BS93") + LDA #$42; STA $0050; BRA -2
        byte[] prog = [0xA9, 0x42, 0x85, 0x50, 0x80, 0xFE];
        int totalLen = 10 + prog.Length;
        byte[] bs93 = new byte[totalLen];
        bs93[0] = 0x80; bs93[1] = 0x08;
        bs93[2] = 0x02; bs93[3] = 0x00; // Load address $0200
        bs93[4] = (byte)(totalLen >> 8); bs93[5] = (byte)(totalLen & 0xFF);
        bs93[6] = (byte)'B'; bs93[7] = (byte)'S'; bs93[8] = (byte)'9'; bs93[9] = (byte)'3';
        Buffer.BlockCopy(prog, 0, bs93, 10, prog.Length);

        var cart = LynxCartridge.LoadBs93Homebrew(bs93, "MiniTest");
        Assert.True(cart.IsHomebrewRamImage);
        Assert.Equal((ushort)0x0200, cart.HomebrewLoadAddress);

        var sys = new LynxSystem();
        sys.LoadCartridge(cart);
        sys.Reset();
        Assert.Equal((ushort)0x0200, sys.Cpu.PC);

        sys.StepInstruction(); // LDA #$42
        sys.StepInstruction(); // STA $50
        Assert.Equal(0x42, sys.Ram[0x50]);
    }
}
