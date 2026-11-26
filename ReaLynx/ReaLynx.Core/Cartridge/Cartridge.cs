using System.Security.Cryptography;
using System.Text;

namespace ReaLynx.Core.Cartridge;

public enum CartRotation : byte
{
    None  = 0,
    Left  = 1, // 90 degrees counter-clockwise (vertical, buttons down)
    Right = 2, // 90 degrees clockwise (vertical, buttons up)
}

public sealed class CartridgeLoadException : Exception
{
    public CartridgeLoadException(string message) : base(message) { }
}

public readonly record struct CartridgeInfo(
    string Name,
    string Manufacturer,
    ushort Bank0PageSize,
    ushort Bank1PageSize,
    int Bank0Bytes,
    int Bank1Bytes,
    CartRotation Rotation,
    ushort Version,
    string Format,
    string Sha256);

/// <summary>
/// Atari Lynx cartridge image loader (.lnx and validated raw .lyx) and hardware address/strobe interface.
/// </summary>
public sealed class LynxCartridge
{
    public const int LnxHeaderSize = 64;
    private static readonly byte[] LnxMagic = [(byte)'L', (byte)'Y', (byte)'N', (byte)'X'];

    private readonly byte[] _bank0;
    private readonly byte[] _bank1;
    private readonly int _bank0Mask;
    private readonly int _bank1Mask;
    private readonly int _bank0Shift;
    private readonly int _bank1Shift;

    // Hardware cartridge address shift register (8-bit block) and intra-block counter
    private byte _shiftRegister;
    private int _counter;
    private bool _lastStrobe;
    private bool _audinState;

    public CartridgeInfo Info { get; }
    public bool IsHomebrewRamImage { get; }
    public ushort HomebrewLoadAddress { get; }
    public byte[] HomebrewPayload { get; }
    public byte ShiftRegister => _shiftRegister;
    public int Counter => _counter;
    public int CurrentBank0Address => GetAddress(_bank0Shift, _bank0Mask, _bank0.Length);
    public int CurrentBank1Address => GetAddress(_bank1Shift, _bank1Mask, _bank1.Length);

    private LynxCartridge(
        byte[] bank0,
        byte[] bank1,
        ushort bank0PageSize,
        ushort bank1PageSize,
        CartridgeInfo info,
        bool isHomebrew = false,
        ushort homebrewLoadAddr = 0,
        byte[]? homebrewPayload = null)
    {
        _bank0 = bank0;
        _bank1 = bank1;
        _bank0Mask = bank0PageSize > 0 ? bank0PageSize - 1 : 0;
        _bank1Mask = bank1PageSize > 0 ? bank1PageSize - 1 : 0;
        _bank0Shift = PageSizeToShift(bank0PageSize);
        _bank1Shift = PageSizeToShift(bank1PageSize);
        Info = info;
        IsHomebrewRamImage = isHomebrew;
        HomebrewLoadAddress = homebrewLoadAddr;
        HomebrewPayload = homebrewPayload ?? [];
    }

    public void Reset()
    {
        _shiftRegister = 0;
        _counter = 0;
        _lastStrobe = false;
        _audinState = false;
    }

    /// <summary>
    /// Updates the cartridge address shift register from Mikey SYSCTL1 bit 0 (strobe)
    /// and IODAT bit 1 (data bit), plus IODAT bit 4 (AUDIN).
    /// </summary>
    public void UpdateAddressStrobe(bool strobe, bool addressDataBit, bool audinBit)
    {
        _audinState = audinBit;
        if (strobe && !_lastStrobe)
        {
            _shiftRegister = (byte)((_shiftRegister << 1) | (addressDataBit ? 1 : 0));
        }
        if (strobe)
        {
            _counter = 0;
        }
        _lastStrobe = strobe;
    }

    public byte ReadBank0()
    {
        byte val = 0xFF;
        if (_bank0.Length > 0)
        {
            int addr = GetAddress(_bank0Shift, _bank0Mask, _bank0.Length);
            val = _bank0[addr];
        }
        if (!_lastStrobe)
        {
            _counter = (_counter + 1) & 0x7FF;
        }
        return val;
    }

    public byte ReadBank1()
    {
        byte val = 0xFF;
        if (_bank1.Length > 0)
        {
            int addr = GetAddress(_bank1Shift, _bank1Mask, _bank1.Length);
            val = _bank1[addr];
        }
        if (!_lastStrobe)
        {
            _counter = (_counter + 1) & 0x7FF;
        }
        return val;
    }

    public void WriteBank0(byte value)
    {
        // Standard ROM cartridges ignore writes, but still clock the counter
        if (!_lastStrobe)
        {
            _counter = (_counter + 1) & 0x7FF;
        }
    }

    public void WriteBank1(byte value)
    {
        if (!_lastStrobe)
        {
            _counter = (_counter + 1) & 0x7FF;
        }
    }

    private int GetAddress(int shift, int mask, int bankLen)
    {
        if (bankLen == 0) return 0;
        int addr = (_shiftRegister << shift) | (_counter & mask);
        if (bankLen > (256 << shift) && _audinState)
        {
            addr |= (256 << shift);
        }
        return addr % bankLen;
    }

    public static LynxCartridge LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new CartridgeLoadException($"Cartridge file not found: {path}");

        byte[] data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (data.Length >= LnxHeaderSize &&
            data[0] == LnxMagic[0] && data[1] == LnxMagic[1] &&
            data[2] == LnxMagic[2] && data[3] == LnxMagic[3])
        {
            return LoadLnx(data);
        }

        // Standard Atari Lynx BS93 / BLL homebrew RAM executable (10-byte header: 80 08 <addrBE> <lenBE> "BS93")
        if (data.Length >= 10 &&
            data[0] == 0x80 && data[1] == 0x08 &&
            data[6] == (byte)'B' && data[7] == (byte)'S' &&
            data[8] == (byte)'9' && data[9] == (byte)'3')
        {
            return LoadBs93Homebrew(data, Path.GetFileNameWithoutExtension(path));
        }

        if (ext == ".lnx")
        {
            throw new CartridgeLoadException("File has .lnx extension but is missing a valid 64-byte 'LYNX' header (or BS93 homebrew header).");
        }

        if (ext == ".lyx")
        {
            // Infer standard validated geometry only for exact standard Lynx ROM sizes
            ushort pageSize = data.Length switch
            {
                65536  => 256,
                131072 => 512,
                262144 => 1024,
                524288 => 2048,
                _ => throw new CartridgeLoadException(
                    $"Raw .lyx file size {data.Length} bytes does not match a standard Lynx cartridge geometry (64KB, 128KB, 256KB, or 512KB). Provide explicit geometry or convert to .lnx.")
            };
            return LoadRaw(data, pageSize, 0, CartRotation.None, Path.GetFileNameWithoutExtension(path));
        }

        throw new CartridgeLoadException(
            $"Unsupported cartridge format '{ext}'. Expected a standard .lnx image (with 64-byte LYNX header) or a validated raw .lyx image.");
    }

    public static LynxCartridge LoadLnx(byte[] data)
    {
        if (data.Length < LnxHeaderSize)
            throw new CartridgeLoadException($"LNX image too small ({data.Length} bytes; minimum header is {LnxHeaderSize} bytes).");

        if (data[0] != 'L' || data[1] != 'Y' || data[2] != 'N' || data[3] != 'X')
            throw new CartridgeLoadException("Invalid LNX magic header (expected 'LYNX').");

        ushort bank0PageSize = (ushort)(data[4] | (data[5] << 8));
        ushort bank1PageSize = (ushort)(data[6] | (data[7] << 8));
        ushort version = (ushort)(data[8] | (data[9] << 8));

        if (version != 1)
            throw new CartridgeLoadException($"Unsupported LNX header version {version} (expected version 1).");

        if (bank0PageSize == 0 || !IsValidPageSize(bank0PageSize))
            throw new CartridgeLoadException($"Invalid Bank 0 page size {bank0PageSize} (expected 256, 512, 1024, or 2048).");

        if (bank1PageSize != 0 && !IsValidPageSize(bank1PageSize))
            throw new CartridgeLoadException($"Invalid Bank 1 page size {bank1PageSize} (expected 0, 256, 512, 1024, or 2048).");

        string name = ReadAsciiZ(data, 10, 32);
        string manufacturer = ReadAsciiZ(data, 42, 16);
        byte rotRaw = data[58];
        if (rotRaw > 2)
            throw new CartridgeLoadException($"Invalid rotation metadata byte {rotRaw} (expected 0=None, 1=Left, 2=Right).");

        var rotation = (CartRotation)rotRaw;

        int expectedBank0 = bank0PageSize * 256;
        int expectedBank1 = bank1PageSize * 256;
        int payloadLen = data.Length - LnxHeaderSize;

        if (payloadLen == 0)
            throw new CartridgeLoadException("LNX file contains a header but no ROM payload data.");

        byte[] bank0 = new byte[expectedBank0];
        Array.Fill(bank0, (byte)0xFF);
        int copy0 = Math.Min(expectedBank0, payloadLen);
        Buffer.BlockCopy(data, LnxHeaderSize, bank0, 0, copy0);

        byte[] bank1 = expectedBank1 > 0 ? new byte[expectedBank1] : [];
        if (expectedBank1 > 0)
        {
            Array.Fill(bank1, (byte)0xFF);
            int rem = payloadLen - copy0;
            if (rem > 0)
            {
                Buffer.BlockCopy(data, LnxHeaderSize + copy0, bank1, 0, Math.Min(expectedBank1, rem));
            }
        }

        string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var info = new CartridgeInfo(
            string.IsNullOrWhiteSpace(name) ? "Untitled" : name,
            manufacturer,
            bank0PageSize,
            bank1PageSize,
            expectedBank0,
            expectedBank1,
            rotation,
            version,
            "LNX",
            sha256);

        return new LynxCartridge(bank0, bank1, bank0PageSize, bank1PageSize, info);
    }

    public static LynxCartridge LoadRaw(
        byte[] rawRom,
        ushort bank0PageSize,
        ushort bank1PageSize = 0,
        CartRotation rotation = CartRotation.None,
        string name = "Raw Cartridge")
    {
        if (!IsValidPageSize(bank0PageSize))
            throw new CartridgeLoadException($"Invalid raw Bank 0 page size {bank0PageSize}.");
        if (bank1PageSize != 0 && !IsValidPageSize(bank1PageSize))
            throw new CartridgeLoadException($"Invalid raw Bank 1 page size {bank1PageSize}.");

        int expectedBank0 = bank0PageSize * 256;
        int expectedBank1 = bank1PageSize * 256;
        if (rawRom.Length != expectedBank0 + expectedBank1)
            throw new CartridgeLoadException(
                $"Raw cartridge length ({rawRom.Length} bytes) does not match specified geometry ({expectedBank0} + {expectedBank1} bytes).");

        byte[] bank0 = new byte[expectedBank0];
        Buffer.BlockCopy(rawRom, 0, bank0, 0, expectedBank0);

        byte[] bank1 = expectedBank1 > 0 ? new byte[expectedBank1] : [];
        if (expectedBank1 > 0)
            Buffer.BlockCopy(rawRom, expectedBank0, bank1, 0, expectedBank1);

        string sha256 = Convert.ToHexString(SHA256.HashData(rawRom)).ToLowerInvariant();
        var info = new CartridgeInfo(
            name,
            "",
            bank0PageSize,
            bank1PageSize,
            expectedBank0,
            expectedBank1,
            rotation,
            0,
            "LYX (Raw)",
            sha256);

        return new LynxCartridge(bank0, bank1, bank0PageSize, bank1PageSize, info);
    }

    public static LynxCartridge LoadBs93Homebrew(byte[] data, string name = "BS93 Homebrew")
    {
        if (data.Length < 10 || data[0] != 0x80 || data[1] != 0x08 ||
            data[6] != 'B' || data[7] != 'S' || data[8] != '9' || data[9] != '3')
        {
            throw new CartridgeLoadException("Invalid BS93/BLL homebrew header.");
        }

        ushort loadAddr = (ushort)((data[2] << 8) | data[3]);
        int payloadLen = data.Length - 10;
        if (payloadLen <= 0 || loadAddr + payloadLen > 0xFC00)
        {
            throw new CartridgeLoadException(
                $"BS93 homebrew payload ({payloadLen} bytes at ${loadAddr:X4}) exceeds valid Lynx RAM range.");
        }

        byte[] payload = new byte[payloadLen];
        Buffer.BlockCopy(data, 10, payload, 0, payloadLen);

        string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var info = new CartridgeInfo(
            name,
            "Homebrew (BS93/BLL)",
            0, 0, payloadLen, 0,
            CartRotation.None,
            0,
            "BS93",
            sha256);

        return new LynxCartridge([], [], 256, 0, info, isHomebrew: true, homebrewLoadAddr: loadAddr, homebrewPayload: payload);
    }

    /// <summary>
    /// Builds a valid .lnx byte array from raw Bank 0 (and optional Bank 1) ROM data.
    /// Useful for generating test cartridges.
    /// </summary>
    public static byte[] BuildLnxImage(
        byte[] bank0Data,
        ushort bank0PageSize,
        string name = "Test",
        string manufacturer = "ReaLynx",
        CartRotation rotation = CartRotation.None,
        byte[]? bank1Data = null,
        ushort bank1PageSize = 0)
    {
        int bank0Total = bank0PageSize * 256;
        int bank1Total = bank1PageSize * 256;
        byte[] img = new byte[LnxHeaderSize + bank0Total + bank1Total];
        Array.Fill(img, (byte)0xFF, LnxHeaderSize, bank0Total + bank1Total);

        img[0] = (byte)'L'; img[1] = (byte)'Y'; img[2] = (byte)'N'; img[3] = (byte)'X';
        img[4] = (byte)(bank0PageSize & 0xFF);
        img[5] = (byte)(bank0PageSize >> 8);
        img[6] = (byte)(bank1PageSize & 0xFF);
        img[7] = (byte)(bank1PageSize >> 8);
        img[8] = 1; img[9] = 0; // Version 1

        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        Buffer.BlockCopy(nameBytes, 0, img, 10, Math.Min(31, nameBytes.Length));
        byte[] mfgBytes = Encoding.ASCII.GetBytes(manufacturer);
        Buffer.BlockCopy(mfgBytes, 0, img, 42, Math.Min(15, mfgBytes.Length));
        img[58] = (byte)rotation;

        Buffer.BlockCopy(bank0Data, 0, img, LnxHeaderSize, Math.Min(bank0Data.Length, bank0Total));
        if (bank1Data != null && bank1Total > 0)
            Buffer.BlockCopy(bank1Data, 0, img, LnxHeaderSize + bank0Total, Math.Min(bank1Data.Length, bank1Total));

        return img;
    }

    private static bool IsValidPageSize(ushort pageSize) =>
        pageSize is 256 or 512 or 1024 or 2048;

    private static int PageSizeToShift(ushort pageSize) => pageSize switch
    {
        256  => 8,
        512  => 9,
        1024 => 10,
        2048 => 11,
        _    => 8
    };

    private static string ReadAsciiZ(byte[] data, int offset, int maxLen)
    {
        int len = 0;
        while (len < maxLen && data[offset + len] != 0) len++;
        return Encoding.ASCII.GetString(data, offset, len).Trim();
    }
}
