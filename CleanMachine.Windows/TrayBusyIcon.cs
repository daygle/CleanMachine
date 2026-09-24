using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

/// <summary>Renders the tray "cleaning" spinner frames: an amber comet arc
/// rotating around a faint track circle, blended over the base app icon.
/// Frames are composed in managed code (GetIconInfo/GetDIBits -&gt; blend -&gt;
/// CreateDIBSection -&gt; CreateIconIndirect) instead of being drawn with GDI,
/// because GDI writes a zero alpha byte into 32bpp icon DIBs, which would punch
/// the arc out as transparent. Geometry is defined for the icon's 256px design
/// size and scales to whatever size the base icon happens to be. Frames returned
/// by <see cref="RenderFrame"/> must be released with <see cref="Destroy"/>.</summary>
internal static class TrayBusyIcon
{
    /// <summary>Frames per rotation; the tray animates them at ~120ms each,
    /// i.e. roughly one turn per second.</summary>
    internal const int FrameCount = 8;

    // Geometry in the 256px design space: the ring (radius 96) sits on clean
    // green background, clear of the laptop, brush and the rounded corners.
    private const double DesignSize = 256;
    private const double DesignRadius = 96;
    private const double DesignHalfWidth = 5;
    private const double HeadSpan = 0.65;    // fully bright head, radians
    private const double TailSpan = 1.35;    // comet tail length, radians
    private const double TrackAlpha = 0.22;  // faint full-circle track under the comet
    private const double EdgeSoftness = 1.5; // antialias width of the band edge, px
    private static readonly byte AmberR = 243;
    private static readonly byte AmberG = 183;
    private static readonly byte AmberB = 78; // #F3B74E - the brush handle's amber

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public int bmPlanes;
        public int bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>Renders spinner frame <paramref name="step"/> (wrapped into
    /// [0, FrameCount)) over the base icon. Returns IntPtr.Zero on any GDI
    /// failure; the caller then keeps showing the plain icon.</summary>
    internal static IntPtr RenderFrame(IntPtr baseIcon, int step)
    {
        if (baseIcon == IntPtr.Zero || !GetIconInfo(baseIcon, out var info))
            return IntPtr.Zero;
        try
        {
            var bm = new BITMAP();
            if (GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), ref bm) == 0)
                return IntPtr.Zero;
            if (bm.bmWidth <= 0 || bm.bmHeight <= 0)
                return IntPtr.Zero;

            var width = bm.bmWidth;
            var height = bm.bmHeight;
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = height, // positive = bottom-up, matching the pixel buffer
                biPlanes = 1,
                biBitCount = 32
            };

            var pixels = new byte[width * height * 4];
            var screen = GetDC(IntPtr.Zero);
            var rows = GetDIBits(screen, info.hbmColor, 0, (uint)height, pixels, ref header, 0);
            ReleaseDC(IntPtr.Zero, screen);
            if (rows == 0) return IntPtr.Zero;

            BlendSpinner(pixels, width, height, step);

            var dibHeader = header;
            var hbm = CreateDIBSection(IntPtr.Zero, ref dibHeader, 0 /*DIB_RGB_COLORS*/,
                out var bits, IntPtr.Zero, 0);
            if (hbm == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                if (bits == IntPtr.Zero) return IntPtr.Zero;
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                var frame = new ICONINFO { fIcon = 1, hbmMask = info.hbmMask, hbmColor = hbm };
                return CreateIconIndirect(ref frame);
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    /// <summary>Releases a frame rendered by <see cref="RenderFrame"/>.</summary>
    internal static void Destroy(IntPtr frame)
    {
        if (frame != IntPtr.Zero) DestroyIcon(frame);
    }

    /// <summary>Blends the spinner for frame <paramref name="step"/> into a
    /// bottom-up premultiplied-BGRA pixel buffer of the icon. Internal (rather
    /// than private) so tests can verify the rotation math on a synthetic tile.
    /// </summary>
    internal static void BlendSpinner(byte[] pixels, int width, int height, int step)
    {
        step = ((step % FrameCount) + FrameCount) % FrameCount;
        var scale = width / DesignSize;
        var cx = width / 2.0;
        var cy = height / 2.0;
        var radius = DesignRadius * scale;
        var half = Math.Max(1.2, DesignHalfWidth * scale);
        // Head starts at 12 o'clock and sweeps clockwise as the frames advance.
        var head = -Math.PI / 2 + step * (2 * Math.PI / FrameCount);

        for (var y = 0; y < height; y++)
        {
            // The buffer is bottom-up; flip y so angles match on-screen orientation.
            var dy = (height - 1 - y + 0.5) - cy;
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5) - cx;
                var band = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius);
                if (band > half + EdgeSoftness) continue;

                var coverage = (half + EdgeSoftness / 2 - band) / EdgeSoftness;
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;

                var ang = Math.Atan2(dy, dx);
                var behind = head - ang;
                behind -= Math.Floor(behind / (2 * Math.PI)) * (2 * Math.PI); // into [0, 2pi)
                var alpha = Math.Max(TrackAlpha, CometAlpha(behind)) * coverage;

                var i = row + x * 4;
                if (pixels[i + 3] == 0) continue; // transparent pixel; nothing to blend onto

                var inv = 1 - alpha;
                pixels[i] = Saturate(AmberB * alpha + pixels[i] * inv);
                pixels[i + 1] = Saturate(AmberG * alpha + pixels[i + 1] * inv);
                pixels[i + 2] = Saturate(AmberR * alpha + pixels[i + 2] * inv);
                pixels[i + 3] = Saturate(255 * alpha + pixels[i + 3] * inv);
            }
        }
    }

    /// <summary>Brightness of the comet at <paramref name="behind"/> radians
    /// behind the head: solid for the head span, then eased to zero at the end
    /// of the tail (continuous with the faint track at both joins).</summary>
    private static double CometAlpha(double behind)
    {
        if (behind >= TailSpan) return 0;
        if (behind <= HeadSpan) return 1;
        var t = (TailSpan - behind) / (TailSpan - HeadSpan);
        return t * t;
    }

    private static byte Saturate(double value)
        => (byte)(value >= 255 ? 255 : value <= 0 ? 0 : value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr hObject, int c, ref BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint uStartScan,
        uint cScanLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
        uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
