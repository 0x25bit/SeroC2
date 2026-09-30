using System.Runtime.InteropServices;

namespace SeroStub;

// Windows Media Foundation H264 encoder — NativeAOT-compatible COM vtable P/Invoke.
// Converts BGRA frames to H264 Annex-B NAL units using CLSID_CMSH264EncoderMFT.
// Returns null from Create() when MF is unavailable; callers fall back to JPEG.
internal sealed class H264Encoder : IDisposable
{
    // ── CLSIDs & IIDs ──────────────────────────────────────────────────────────

    static readonly Guid CLSID_CMSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");
    static readonly Guid IID_IMFTransform         = new("BF94C121-5B05-4E6F-9E5F-26E6028A4BB7");

    static readonly Guid MFMediaType_Video             = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_H264            = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_NV12            = new("3231564E-0000-0010-8000-00AA00389B71");
    static readonly Guid MF_MT_MAJOR_TYPE              = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MF_MT_SUBTYPE                 = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MF_MT_AVG_BITRATE             = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid MF_MT_INTERLACE_MODE          = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MF_MT_FRAME_SIZE              = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_FRAME_RATE              = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MF_MT_MPEG2_PROFILE           = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    static readonly Guid MF_LOW_LATENCY                = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");

    // MFT_MESSAGE_TYPE constants
    const uint MFT_NOTIFY_BEGIN_STREAMING = 0x10000000;
    const uint MFT_NOTIFY_START_OF_STREAM = 0x10000001;
    const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    const int  S_OK = 0;
    const int  MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    // ── P/Invoke ───────────────────────────────────────────────────────────────

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsCtx,
                                       ref Guid riid, out nint ppv);
    [DllImport("mfplat.dll")] static extern int MFStartup(uint Version, uint dwFlags);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out nint ppMFType);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out nint ppIMFSample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint cbMaxLength, out nint ppBuffer);

    // ── Delegates ─────────────────────────────────────────────────────────────

    // IMFAttributes: SetUINT32=[21] SetUINT64=[22] SetGUID=[24]
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetUINT32_Del(nint p, ref Guid key, uint v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetUINT64_Del(nint p, ref Guid key, ulong v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetGUID_Del(nint p, ref Guid key, ref Guid v);

    // IMFTransform: GetOutputStreamInfo=[7] GetAttributes=[8]
    //   SetInputType=[15] SetOutputType=[16]
    //   ProcessMessage=[23] ProcessInput=[24] ProcessOutput=[25]
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetOutputStreamInfo_Del(nint p, uint streamId, out MftOutputStreamInfo info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetAttributes_Del(nint p, out nint ppAttribs);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetInputType_Del(nint p, uint streamId, nint pType, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetOutputType_Del(nint p, uint streamId, nint pType, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ProcessMessage_Del(nint p, uint msg, nint param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ProcessInput_Del(nint p, uint streamId, nint pSample, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ProcessOutput_Del(nint p, uint flags, uint count, ref MftOutputDataBuffer buf, out uint pdwStatus);

    // IMFSample (inherits IMFAttributes=33 entries):
    //   SetSampleTime=[36] SetSampleDuration=[38] ConvertToContiguousBuffer=[41] AddBuffer=[42]
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetSampleTime_Del(nint p, long hns);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetSampleDuration_Del(nint p, long hns);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ConvertToContiguousBuffer_Del(nint p, out nint ppBuf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int AddBuffer_Del(nint p, nint pBuf);

    // IMFMediaBuffer: Lock=[3] Unlock=[4] GetCurrentLength=[5] SetCurrentLength=[6]
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    unsafe delegate int Lock_Del(nint p, out byte* ppb, out uint maxLen, out uint curLen);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int Unlock_Del(nint p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetCurrentLength_Del(nint p, uint len);

    // IUnknown: AddRef=[1], Release=[2]
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint AddRef_Del(nint p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint Release_Del(nint p);

    // IMFSample: RemoveAllBuffers=[44]
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int RemoveAllBuffers_Del(nint p);

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct MftOutputStreamInfo { public uint dwFlags, cbSize, cbAlignment; }

    [StructLayout(LayoutKind.Sequential)]
    struct MftOutputDataBuffer
    {
        public uint  dwStreamID;
        public nint  pSample;  // IMFSample* (8 bytes on x64, 4-byte padding before on x64)
        public uint  dwStatus;
        public nint  pEvents;  // IMFCollection*
    }

    // ── Instance state ─────────────────────────────────────────────────────────

    nint _pTransform;
    int  _width, _height;
    long _sampleTime, _sampleDuration;
    bool _mftProvidesOutputSamples;

    // Cached hot-path delegates for _pTransform (same instance throughout encoder lifetime)
    ProcessInput_Del?  _fnProcessInput;
    ProcessOutput_Del? _fnProcessOutput;

    // Cached vtable delegates for IMFMediaBuffer / IMFSample — vtable is per-class, not per-instance,
    // so caching statically on first Encode() is safe across all encoder instances.
    static Lock_Del?                      _sLock;
    static Unlock_Del?                    _sUnlock;
    static SetCurrentLength_Del?          _sSetLen;
    static AddBuffer_Del?                 _sAddBuf;
    static SetSampleTime_Del?             _sSetTime;
    static SetSampleDuration_Del?         _sSetDur;
    static ConvertToContiguousBuffer_Del? _sConvertBuf;
    static Release_Del?           _sBufRel;      // IMFMediaBuffer::Release
    static Release_Del?           _sSmpRel;      // IMFSample::Release
    static AddRef_Del?            _sBufAddRef;   // IMFMediaBuffer::AddRef
    static AddRef_Del?            _sSmpAddRef;   // IMFSample::AddRef
    static RemoveAllBuffers_Del?  _sRemAllBufs;  // IMFSample::RemoveAllBuffers

    // Preallocated input resources — created once in Init, reused every frame
    nint _inBuf;     // IMFMediaBuffer holding one NV12 frame
    nint _inSample;  // IMFSample wrapping _inBuf (AddBuffer called once in Init)
    nint _outSample; // preallocated output IMFSample (!_mftProvidesOutputSamples only)

    byte[] _nalBuf = new byte[128 * 1024]; // grow-only NAL output; returned to caller without copy

    H264Encoder() { }

    // Creates a new encoder for the given frame dimensions.
    // Returns null when MF or the H264 encoder MFT is unavailable.
    public static H264Encoder? Create(int width, int height, int fps, int bitrateBps = 0)
    {
        try
        {
            var enc = new H264Encoder();
            if (enc.Init(width, height, fps, bitrateBps)) return enc;
            enc.Dispose(); // release _pTransform if CoCreateInstance succeeded but Init failed later
            return null;
        }
        catch { return null; }
    }

    bool Init(int width, int height, int fps, int bitrateBps)
    {
        if (MFStartup(0x10070 /*MF_VERSION*/, 0) != S_OK) return false;

        var clsid = CLSID_CMSH264EncoderMFT;
        var iid   = IID_IMFTransform;
        if (CoCreateInstance(ref clsid, 0, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out _pTransform) != S_OK
            || _pTransform == 0)
            return false;

        _width         = width;
        _height        = height;
        _sampleDuration = fps > 0 ? 10_000_000L / fps : 333_333; // 100 ns units

        // Enable low-latency BEFORE setting media types so the MFT configures accordingly
        var getAttr = Fn<GetAttributes_Del>(_pTransform, 8);
        if (getAttr(_pTransform, out nint pAttribs) == S_OK && pAttribs != 0)
        {
            Set32(pAttribs, MF_LOW_LATENCY, 1);
            Rel(pAttribs);
        }

        // Output type: H264
        if (MFCreateMediaType(out nint pOut) != S_OK) return false;
        try
        {
            if (!SetG(pOut, MF_MT_MAJOR_TYPE, MFMediaType_Video)) return false;
            if (!SetG(pOut, MF_MT_SUBTYPE,     MFVideoFormat_H264)) return false;
            if (bitrateBps <= 0) bitrateBps = Math.Max(500_000, width * height * fps / 30 / 4);
            if (!Set32(pOut, MF_MT_AVG_BITRATE,    (uint)bitrateBps)) return false;
            if (!Set32(pOut, MF_MT_INTERLACE_MODE, 2 /*Progressive*/)) return false;
            if (!Set64(pOut, MF_MT_FRAME_SIZE, PackWH(width, height))) return false;
            if (!Set64(pOut, MF_MT_FRAME_RATE, PackWH(fps > 0 ? fps : 30, 1))) return false;
            if (!Set32(pOut, MF_MT_MPEG2_PROFILE, 66 /*Baseline — widest hw compatibility*/)) return false;
            var fn = Fn<SetOutputType_Del>(_pTransform, 16);
            if (fn(_pTransform, 0, pOut, 0) != S_OK) return false;
        }
        finally { Rel(pOut); }

        // Input type: NV12
        if (MFCreateMediaType(out nint pIn) != S_OK) return false;
        try
        {
            if (!SetG(pIn, MF_MT_MAJOR_TYPE, MFMediaType_Video)) return false;
            if (!SetG(pIn, MF_MT_SUBTYPE,    MFVideoFormat_NV12)) return false;
            if (!Set32(pIn, MF_MT_INTERLACE_MODE, 2)) return false;
            if (!Set64(pIn, MF_MT_FRAME_SIZE, PackWH(width, height))) return false;
            if (!Set64(pIn, MF_MT_FRAME_RATE, PackWH(fps > 0 ? fps : 30, 1))) return false;
            if (!Set32(pIn, MF_MT_ALL_SAMPLES_INDEPENDENT, 1)) return false;
            var fn = Fn<SetInputType_Del>(_pTransform, 15);
            if (fn(_pTransform, 0, pIn, 0) != S_OK) return false;
        }
        finally { Rel(pIn); }

        // Check if the MFT provides its own output samples
        var getInfo = Fn<GetOutputStreamInfo_Del>(_pTransform, 7);
        if (getInfo(_pTransform, 0, out var info) == S_OK)
            _mftProvidesOutputSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

        var msg = Fn<ProcessMessage_Del>(_pTransform, 23);
        msg(_pTransform, MFT_NOTIFY_BEGIN_STREAMING, 0);
        msg(_pTransform, MFT_NOTIFY_START_OF_STREAM, 0);

        _fnProcessInput  = Fn<ProcessInput_Del>(_pTransform, 24);
        _fnProcessOutput = Fn<ProcessOutput_Del>(_pTransform, 25);

        // Preallocate input buffer + sample once to avoid 3 MB alloc per frame
        int nv12Sz = width * height * 3 / 2;
        if (MFCreateMemoryBuffer((uint)nv12Sz, out _inBuf) != S_OK) return false;
        if (MFCreateSample(out _inSample) != S_OK) return false;
        (_sAddBuf ??= Fn<AddBuffer_Del>(_inSample, 42))(_inSample, _inBuf);

        return true;
    }

    // Encodes one BGRA frame. bgraBits points to W×H BGRA pixels with the given stride.
    // Returns (reused grow-only buffer, valid byte count), or null on encoder error.
    // Caller must consume before the next Encode() call — buffer is overwritten each frame.
    public unsafe (byte[] buf, int len)? Encode(nint bgraBits, int bgraStride)
    {
        if (_pTransform == 0) return null;
        int w = _width, h = _height;
        int nv12Size = w * h * 3 / 2;

        // Reuse preallocated input buffer + sample — no 3 MB alloc per frame
        var lockFn   = _sLock   ??= Fn<Lock_Del>(_inBuf, 3);
        var unlockFn = _sUnlock ??= Fn<Unlock_Del>(_inBuf, 4);
        var setLenFn = _sSetLen ??= Fn<SetCurrentLength_Del>(_inBuf, 6);
        if (lockFn(_inBuf, out byte* pDst, out _, out _) != S_OK) return null;
        BgraToNv12((byte*)bgraBits, w, h, bgraStride, pDst);
        unlockFn(_inBuf);
        setLenFn(_inBuf, (uint)nv12Size);

        (_sSetTime ??= Fn<SetSampleTime_Del>(_inSample, 36))(_inSample, _sampleTime);
        (_sSetDur  ??= Fn<SetSampleDuration_Del>(_inSample, 38))(_inSample, _sampleDuration);
        _sampleTime += _sampleDuration;

        // Temporarily AddRef so the MFT cannot free our preallocated objects if it calls Release
        (_sSmpAddRef ??= Fn<AddRef_Del>(_inSample, 1))(_inSample);
        (_sBufAddRef ??= Fn<AddRef_Del>(_inBuf, 1))(_inBuf);
        try
        {
            if (_fnProcessInput!(_pTransform, 0, _inSample, 0) != S_OK) return null;
        }
        finally
        {
            (_sSmpRel ??= Fn<Release_Del>(_inSample, 2))(_inSample);
            (_sBufRel ??= Fn<Release_Del>(_inBuf, 2))(_inBuf);
        }

        // Drain output NAL units
        var outFn = _fnProcessOutput!;
        var outBuf = new MftOutputDataBuffer();

        if (!_mftProvidesOutputSamples)
        {
            if (_outSample == 0)
            {
                if (MFCreateSample(out _outSample) != S_OK || _outSample == 0) return null;
            }
            else
                (_sRemAllBufs ??= Fn<RemoveAllBuffers_Del>(_outSample, 44))(_outSample);
            outBuf.pSample = _outSample;
        }

        try
        {
            int hr = outFn(_pTransform, 0, 1, ref outBuf, out _);
            if (hr < 0 || outBuf.pSample == 0) return null;

            // ConvertToContiguousBuffer = vtable[41] on IMFSample
            if ((_sConvertBuf ??= Fn<ConvertToContiguousBuffer_Del>(outBuf.pSample, 41))(outBuf.pSample, out nint pOutBuf) != S_OK
                || pOutBuf == 0)
                return null;
            try
            {
                if (_sLock!(pOutBuf, out byte* pData, out _, out uint cbLen) != S_OK)
                    return null;
                try
                {
                    if (cbLen == 0) return null;
                    if (_nalBuf.Length < cbLen) _nalBuf = new byte[cbLen];
                    fixed (byte* pResult = _nalBuf)
                        Buffer.MemoryCopy(pData, pResult, cbLen, cbLen);
                    return (_nalBuf, (int)cbLen);
                }
                finally { _sUnlock!(pOutBuf); }
            }
            finally { (_sBufRel ??= Fn<Release_Del>(pOutBuf, 2))(pOutBuf); }
        }
        finally
        {
            if (_mftProvidesOutputSamples && outBuf.pSample != 0) (_sSmpRel ??= Fn<Release_Del>(outBuf.pSample, 2))(outBuf.pSample);
            // !_mftProvidesOutputSamples: _outSample kept alive for reuse — freed in Dispose
            if (outBuf.pEvents != 0) Rel(outBuf.pEvents);
        }
    }

    // ── BGRA → NV12 conversion (BT.601 limited range) ────────────────────────

    // Cached ParallelOptions — allocated once, reused every frame
    static System.Threading.Tasks.ParallelOptions? _nv12PO;

    // Frame params for BgraToNv12 static lambda — written before Parallel.For, read inside.
    // BgraToNv12 is called from the single capture thread only; no re-entrancy is possible.
    static nint _nv12BgraBase, _nv12YBase, _nv12UvBase;
    static int  _nv12W, _nv12Stride;

    static unsafe void BgraToNv12(byte* bgra, int w, int h, int bgraStride, byte* nv12)
    {
        _nv12BgraBase = (nint)bgra;
        _nv12YBase    = (nint)nv12;
        _nv12UvBase   = (nint)(nv12 + w * h);
        _nv12W        = w;
        _nv12Stride   = bgraStride;

        var po = _nv12PO ??= new System.Threading.Tasks.ParallelOptions
            { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };

        // Process row pairs {even, odd} in one iteration — both rows read adjacent BGRA memory
        // in the same thread, staying in L1/L2 cache. Halves iteration count vs per-row loop.
        // h is always even for H264 (CaptureAndEncodeH264 enforces even dimensions).
        // static lambda: no closure object allocated per call — delegate is cached by compiler.
        System.Threading.Tasks.Parallel.For(0, h >> 1, po, static pair =>
        {
            unsafe
            {
                int   y0    = pair << 1;
                byte* px0   = (byte*)(_nv12BgraBase + (long)y0 * _nv12Stride);
                byte* px1   = px0 + _nv12Stride;      // odd row — adjacent in memory
                byte* yRow0 = (byte*)(_nv12YBase + (long)y0 * _nv12W);
                byte* yRow1 = yRow0 + _nv12W;
                byte* uvRow = (byte*)(_nv12UvBase + (long)pair * _nv12W);

                // Even row: Y for every pixel + UV for even pixels
                // Read each pixel as a 32-bit word — 1 load + shifts instead of 3 byte loads.
                for (int x = 0; x < _nv12W; x += 2, px0 += 8)
                {
                    uint p0 = ((uint*)px0)[0];
                    int b0 = (int)(p0 & 0xFF), g0 = (int)(p0 >> 8 & 0xFF), r0 = (int)(p0 >> 16 & 0xFF);
                    yRow0[x]     = (byte)(((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16);
                    uvRow[x]     = (byte)(((-38 * r0 -  74 * g0 + 112 * b0 + 128) >> 8) + 128);
                    uvRow[x + 1] = (byte)(((112 * r0 -  94 * g0 -  18 * b0 + 128) >> 8) + 128);
                    uint p1 = ((uint*)px0)[1];
                    int b1 = (int)(p1 & 0xFF), g1 = (int)(p1 >> 8 & 0xFF), r1 = (int)(p1 >> 16 & 0xFF);
                    yRow0[x + 1] = (byte)(((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16);
                }
                // Odd row: Y only — 2 pixels per iteration, 32-bit reads
                for (int x = 0; x < _nv12W; x += 2, px1 += 8)
                {
                    uint p0 = ((uint*)px1)[0];
                    int b0 = (int)(p0 & 0xFF), g0 = (int)(p0 >> 8 & 0xFF), r0 = (int)(p0 >> 16 & 0xFF);
                    yRow1[x]     = (byte)(((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16);
                    uint p1 = ((uint*)px1)[1];
                    int b1 = (int)(p1 & 0xFF), g1 = (int)(p1 >> 8 & 0xFF), r1 = (int)(p1 >> 16 & 0xFF);
                    yRow1[x + 1] = (byte)(((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16);
                }
            }
        });
    }

    // ── Vtable helpers ─────────────────────────────────────────────────────────

    static T Fn<T>(nint comPtr, int vtblIndex) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(
               Marshal.ReadIntPtr(Marshal.ReadIntPtr(comPtr), vtblIndex * nint.Size));

    static void Rel(nint ptr)
    {
        if (ptr != 0)
            Marshal.GetDelegateForFunctionPointer<Release_Del>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(ptr), 2 * nint.Size))(ptr);
    }

    bool Set32(nint ptr, Guid key, uint v) => Fn<SetUINT32_Del>(ptr, 21)(ptr, ref key, v) == S_OK;
    bool Set64(nint ptr, Guid key, ulong v) => Fn<SetUINT64_Del>(ptr, 22)(ptr, ref key, v) == S_OK;
    bool SetG(nint ptr, Guid key, Guid val) => Fn<SetGUID_Del>(ptr, 24)(ptr, ref key, ref val) == S_OK;

    static ulong PackWH(int w, int h) => ((ulong)(uint)w << 32) | (uint)h;

    // ── IDisposable ────────────────────────────────────────────────────────────

    bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pTransform != 0) { Rel(_pTransform); _pTransform = 0; }
        if (_outSample  != 0) { Rel(_outSample);  _outSample  = 0; }
        if (_inSample   != 0) { Rel(_inSample);   _inSample   = 0; } // releases _inBuf via AddBuffer ref
        if (_inBuf      != 0) { Rel(_inBuf);      _inBuf      = 0; } // releases our owner ref
    }
}
