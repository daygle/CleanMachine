using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Tests for the tray cleaning-spinner pixel blend. The pixel buffer is
/// bottom-up (GDI DIB order), so these tests pin down the two things that are
/// easy to get wrong and invisible without running the app: the head starting at
/// 12 o'clock for frame 0, and the rotation direction being clockwise. Uses a
/// synthetic fully-opaque tile in the brand's dark green - no Win32 involved.</summary>
public sealed class TrayBusyIconTests
{
    private const int Size = 256;
    private const byte BaseRed = 0x0F;   // #0F2B24 tile, bottom gradient colour

    // Ring radius 96 around the (128,128) centre: 12 o'clock lands on visual
    // row 32 = buffer row 223 (bottom-up), 6 o'clock on visual row 224 = buffer
    // row 31. The comet's leading edge is sharp, so each head sample sits a hair
    // BEHIND the head along the clockwise sweep: toward -x at 12 (x=127) and
    // toward +x at 6 (x=128).
    private const int TopBufferY = 223;
    private const int TopX = 127;
    private const int BottomBufferY = 31;
    private const int BottomX = 128;
    private const int CenterBufferY = 127;

    [Fact]
    public void FrameZeroStartsAtTwelveAndFrameFourRotatesToSix()
    {
        // Frame 0: bright amber head at 12 o'clock, faint track only at 6.
        var first = OpaqueTile();
        TrayBusyIcon.BlendSpinner(first, Size, Size, 0);

        Assert.Equal(243, Red(first, TopBufferY, TopX));    // head: full amber
        Assert.Equal(255, Alpha(first, TopBufferY, TopX));  // opaque over the tile
        Assert.True(Red(first, BottomBufferY, BottomX) <= 100, // track: only faint amber
            $"bottom track too bright: {Red(first, BottomBufferY, BottomX)}");

        // Frame 4 is half a rotation later: the head must be at 6 o'clock now.
        // If the buffer's y-flip is wrong this swaps and both asserts fail.
        var half = OpaqueTile();
        TrayBusyIcon.BlendSpinner(half, Size, Size, 4);

        Assert.Equal(243, Red(half, BottomBufferY, BottomX));
        Assert.True(Red(half, TopBufferY, TopX) <= 100,
            $"top track too bright after half rotation: {Red(half, TopBufferY, TopX)}");
    }

    [Fact]
    public void OnlyTheRingBandIsTouched()
    {
        var pixels = OpaqueTile();
        TrayBusyIcon.BlendSpinner(pixels, Size, Size, 0);

        // The centre is far inside the ring: pixel must stay exactly the base tile.
        var i = (CenterBufferY * Size + 127) * 4;
        Assert.Equal(0x24, pixels[i]);       // B
        Assert.Equal(0x2B, pixels[i + 1]);   // G
        Assert.Equal(BaseRed, pixels[i + 2]); // R
        Assert.Equal(255, pixels[i + 3]);    // A
    }

    private static byte[] OpaqueTile()
    {
        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x24;      // B
            pixels[i + 1] = 0x2B;  // G
            pixels[i + 2] = BaseRed;
            pixels[i + 3] = 0xFF;
        }
        return pixels;
    }

    private static byte Red(byte[] pixels, int bufferY, int x)
        => pixels[(bufferY * Size + x) * 4 + 2];

    private static byte Alpha(byte[] pixels, int bufferY, int x)
        => pixels[(bufferY * Size + x) * 4 + 3];
}
