using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

/// <summary>Renders the tray "cleaning" spinner frames: an amber comet arc
/// rotating around a faint track circle, plus a solid amber status dot in the
/// bottom-right corner, blended over the base app icon.
/// <para>
/// The tray shows the 256px icon scaled down to 16-32px, so every mark is sized
/// for that: the ring is ~1.5px wide at 16px (the original 10px band shrank to
/// well under a pixel and was barely visible), it runs outside the laptop and
/// brush artwork, and both the ring and the dot carry a dark halo so they hold
/// their contrast against the green tile. The dot is the same in every frame:
/// a steady "busy" signal that does not depend on catching the motion.
/// </para>
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

    // Geometry in the 256px design space. The ring (radius 110) runs between
    // the laptop/brush artwork and the tile edge; ring plus halo reaches 126,
    // inside the 128 half-width of the tile.
    private const double DesignSize = 256;
    internal const double DesignRadius = 110;
    private const double DesignHalfWidth = 10;
    private const double DesignHaloWidth = 6;  // dark rim around the ring, each side
    private const double HaloAlpha = 0.85;
    private const double HeadSpan = 1.3;      // fully bright head, radians
    private const double TailSpan = 2.8;      // comet tail length, radians
    private const double TrackAlpha = 0.25;   // faint full-circle track under the comet
    private const double EdgeSoftness = 1.5;  // antialias width of every edge, px
    // Status dot: centred in the bottom-right quadrant, fully inside the tile's
    // rounded corner (outer edge at 240 along the diagonal).
    internal const double DesignDotCenter = 192;
    internal const double DesignDotRadius = 38;
    private const double DesignDotHalo = 10;
    private const byte DarkR = 0x0F;
    private const byte DarkG = 0x2B;
    private const byte DarkB = 0x24;  // #0F2B24 - the tile's own darkest green
    private const byte AmberR = 243;
    private const byte AmberG = 183;
    private const byte AmberB = 78; // #F3B74E - the brush handle's amber

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
    /// bottom-up BGRA pixel buffer of the icon (straight alpha, as icon bitmaps
    /// store it). Internal (rather than private) so tests can verify the
    /// rotation math and the status dot on a synthetic tile.
    /// </summary>
    internal static void BlendSpinner(byte[] pixels, int width, int height, int step)
    {
        step = ((step % FrameCount) + FrameCount) % FrameCount;
        var scale = width / DesignSize;
        var cx = width / 2.0;
        var cy = height / 2.0;
        var radius = DesignRadius * scale;
        var half = Math.Max(1.2, DesignHalfWidth * scale);
        var halo = DesignHaloWidth * scale;
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
                if (band > half + halo + EdgeSoftness) continue;

                var i = row + x * 4;
                if (pixels[i + 3] == 0) continue; // transparent pixel; nothing to blend onto

                var ang = Math.Atan2(dy, dx);
                var behind = head - ang;
                behind -= Math.Floor(behind / (2 * Math.PI)) * (2 * Math.PI); // into [0, 2pi)
                var strength = Math.Max(TrackAlpha, CometAlpha(behind));

                // Dark halo first, so the amber band reads against the green tile
                // even once the icon is scaled down to a few pixels.
                Over(pixels, i, DarkR, DarkG, DarkB, Coverage(half + halo, band) * strength * HaloAlpha);
                Over(pixels, i, AmberR, AmberG, AmberB, Coverage(half, band) * strength);
            }
        }

        BlendStatusDot(pixels, width, height, scale);
    }

    /// <summary>The solid amber status dot with its dark rim, in the
    /// bottom-right quadrant. Identical in every frame.</summary>
    private static void BlendStatusDot(byte[] pixels, int width, int height, double scale)
    {
        var dotX = DesignDotCenter * scale;
        var dotY = DesignDotCenter * scale; // measured from the top, like the ring
        var dotRadius = DesignDotRadius * scale;
        var rim = DesignDotHalo * scale;
        var reach = dotRadius + rim + EdgeSoftness;
        var top = Math.Max(0, (int)(dotY - reach));
        var bottom = Math.Min(height - 1, (int)Math.Ceiling(dotY + reach));
        var left = Math.Max(0, (int)(dotX - reach));
        var right = Math.Min(width - 1, (int)Math.Ceiling(dotX + reach));
        for (var visualY = top; visualY <= bottom; visualY++)
        {
            var dy = visualY + 0.5 - dotY;
            var row = (height - 1 - visualY) * width * 4; // buffer is bottom-up
            for (var x = left; x <= right; x++)
            {
                var dx = x + 0.5 - dotX;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var i = row + x * 4;
                if (pixels[i + 3] == 0) continue;
                Over(pixels, i, DarkR, DarkG, DarkB, Coverage(dotRadius + rim, distance));
                Over(pixels, i, AmberR, AmberG, AmberB, Coverage(dotRadius, distance));
            }
        }
    }

    /// <summary>Antialiased coverage of a shape extending <paramref name="extent"/>
    /// from its centre line (or centre point) at <paramref name="distance"/>.</summary>
    private static double Coverage(double extent, double distance)
        => Math.Clamp((extent + EdgeSoftness / 2 - distance) / EdgeSoftness, 0, 1);

    /// <summary>Source-over of a solid colour at <paramref name="alpha"/> onto one
    /// straight-alpha BGRA pixel. Over an opaque pixel this is a plain lerp.</summary>
    private static void Over(byte[] pixels, int i, byte r, byte g, byte b, double alpha)
    {
        if (alpha <= 0) return;
        var dstAlpha = pixels[i + 3] / 255.0;
        var outAlpha = alpha + dstAlpha * (1 - alpha);
        var keep = dstAlpha * (1 - alpha);
        pixels[i] = Saturate((b * alpha + pixels[i] * keep) / outAlpha);
        pixels[i + 1] = Saturate((g * alpha + pixels[i + 1] * keep) / outAlpha);
        pixels[i + 2] = Saturate((r * alpha + pixels[i + 2] * keep) / outAlpha);
        pixels[i + 3] = Saturate(255 * outAlpha);
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
        => (byte)(value >= 255 ? 255 : value <= 0 ? 0 : Math.Round(value));

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
