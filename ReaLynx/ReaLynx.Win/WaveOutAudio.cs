using System.Runtime.InteropServices;

namespace ReaLynx.Win;

/// <summary>
/// Low-latency Windows WinMM waveOut stereo 16-bit PCM audio player.
/// Uses a fixed ring of pinned audio buffers to prevent clicks, underruns, and runaway buffering.
/// </summary>
public sealed class WaveOutAudio : IDisposable
{
    private const int BufferCount = 8;
    private const int BufferSamples = 1470; // ~16.6ms stereo (735 frames * 2 channels at 44100 Hz)
    private const int WHDR_DONE = 0x00000001;
    private const int WHDR_PREPARED = 0x00000002;
    private const int WAVE_FORMAT_PCM = 1;
    private const uint WAVE_MAPPER = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);
    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);

    private IntPtr _handle;
    private readonly object _lock = new();
    private readonly short[][] _buffers = new short[BufferCount][];
    private readonly GCHandle[] _dataHandles = new GCHandle[BufferCount];
    private readonly IntPtr[] _headerPtrs = new IntPtr[BufferCount];
    private readonly List<short> _staging = new(BufferSamples * 2);
    private int _nextBuffer;
    private bool _disposed;

    public bool IsAvailable => _handle != IntPtr.Zero;
    public float Volume { get; set; } = 0.8f;
    public bool Muted { get; set; }

    public WaveOutAudio(int sampleRate = 44100)
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = 2,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 4,
            nAvgBytesPerSec = (uint)(sampleRate * 4),
            cbSize = 0
        };

        int res = waveOutOpen(out _handle, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (res != 0)
        {
            _handle = IntPtr.Zero;
            return;
        }

        int hdrSize = Marshal.SizeOf<WAVEHDR>();
        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = new short[BufferSamples];
            _dataHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);
            _headerPtrs[i] = Marshal.AllocHGlobal(hdrSize);

            var hdr = new WAVEHDR
            {
                lpData = _dataHandles[i].AddrOfPinnedObject(),
                dwBufferLength = (uint)(BufferSamples * sizeof(short)),
                dwFlags = WHDR_DONE
            };
            Marshal.StructureToPtr(hdr, _headerPtrs[i], false);
        }
    }

    public int QueuedBuffers
    {
        get
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero || _disposed) return 0;
                int queued = 0;
                for (int i = 0; i < BufferCount; i++)
                {
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(_headerPtrs[i]);
                    if ((hdr.dwFlags & WHDR_DONE) == 0 && (hdr.dwFlags & WHDR_PREPARED) != 0)
                        queued++;
                }
                return queued;
            }
        }
    }

    public void SubmitSamples(short[] samples, int count)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero || _disposed || count <= 0) return;

            float gain = Muted ? 0f : Math.Clamp(Volume, 0f, 1f);
            for (int i = 0; i < count; i++)
            {
                int scaled = (int)(samples[i] * gain);
                _staging.Add((short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
            }

            // Prevent runaway staging buffer growth if audio device stalls
            if (_staging.Count > BufferSamples * 4)
            {
                _staging.RemoveRange(0, _staging.Count - BufferSamples * 2);
            }

            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            while (_staging.Count >= BufferSamples)
            {
                IntPtr hdrPtr = _headerPtrs[_nextBuffer];
                var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtr);

                // If all buffers are currently playing, drop oldest staging block instead of blocking UI/emulation
                if ((hdr.dwFlags & WHDR_DONE) == 0 && (hdr.dwFlags & WHDR_PREPARED) != 0)
                {
                    _staging.RemoveRange(0, BufferSamples);
                    break;
                }

                if ((hdr.dwFlags & WHDR_PREPARED) != 0)
                {
                    waveOutUnprepareHeader(_handle, hdrPtr, hdrSize);
                }

                _staging.CopyTo(0, _buffers[_nextBuffer], 0, BufferSamples);
                _staging.RemoveRange(0, BufferSamples);

                hdr.lpData = _dataHandles[_nextBuffer].AddrOfPinnedObject();
                hdr.dwBufferLength = (uint)(BufferSamples * sizeof(short));
                hdr.dwFlags = 0;
                Marshal.StructureToPtr(hdr, hdrPtr, false);

                waveOutPrepareHeader(_handle, hdrPtr, hdrSize);
                waveOutWrite(_handle, hdrPtr, hdrSize);

                _nextBuffer = (_nextBuffer + 1) % BufferCount;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero || _disposed) return;
            waveOutReset(_handle);
            _staging.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero)
            {
                waveOutReset(_handle);
                int hdrSize = Marshal.SizeOf<WAVEHDR>();
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_headerPtrs[i] != IntPtr.Zero)
                    {
                        waveOutUnprepareHeader(_handle, _headerPtrs[i], hdrSize);
                        Marshal.FreeHGlobal(_headerPtrs[i]);
                        _headerPtrs[i] = IntPtr.Zero;
                    }
                    if (_dataHandles[i].IsAllocated)
                        _dataHandles[i].Free();
                }
                waveOutClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
