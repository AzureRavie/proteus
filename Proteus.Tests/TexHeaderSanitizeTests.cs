using System;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Regression tests for <see cref="TextureLoader.SanitizeTexBytes"/> and
/// <see cref="TextureLoader.Mip0ByteSize"/>.
///
/// Bug: a Penumbra mod shipping TexTools-exported BC7 textures turned the whole body pink
/// when composited. The .tex headers declared MipCount=12 but an OffsetToSurface table whose
/// values were tiny yet monotonic and in-bounds (surf0=80, surf1=343 for a 2048×2048 BC7 whose
/// mip 0 alone is 4 MB). The old sanitizer only checked monotonicity/bounds, accepted the table,
/// and Lumina then decoded mip 0 from a 263-byte slice → solid magenta baked into the composite.
/// </summary>
public class TexHeaderSanitizeTests
{
    private const uint Bc7 = 0x6432;
    private const uint Bgra8 = 0x1450;

    private static byte[] MakeHeader(uint format, int w, int h, int mipCount, uint[] surfaceOffsets)
    {
        // Size the buffer to hold the data implied by the largest surface offset, so the
        // sanitizer's in-bounds check sees a realistic file (a real .tex carries its pixel data).
        uint maxOff = 80;
        foreach (var o in surfaceOffsets) if (o > maxOff) maxOff = o;
        var b = new byte[Math.Max(80u, maxOff + 64u)];
        BitConverter.TryWriteBytes(b.AsSpan(4), format);
        BitConverter.TryWriteBytes(b.AsSpan(8), (ushort)w);
        BitConverter.TryWriteBytes(b.AsSpan(10), (ushort)h);
        BitConverter.TryWriteBytes(b.AsSpan(12), (ushort)1);
        b[14] = (byte)(mipCount & 0x7F);
        for (int i = 0; i < surfaceOffsets.Length && i < 13; i++)
            BitConverter.TryWriteBytes(b.AsSpan(28 + i * 4), surfaceOffsets[i]);
        return b;
    }

    private static int MipCountOf(byte[] tex) => tex[14] & 0x7F;

    [Fact]
    public void Mip0ByteSize_ComputesPerFormat()
    {
        Assert.Equal(2048L * 2048, TextureLoader.Mip0ByteSize(Bc7, 2048, 2048));     // BC7: 1 byte/px
        Assert.Equal(2048L * 2048 * 4, TextureLoader.Mip0ByteSize(Bgra8, 2048, 2048)); // BGRA8: 4 byte/px
        Assert.Equal(1024L * 1024 / 2, TextureLoader.Mip0ByteSize(0x3420, 1024, 1024)); // BC1: 0.5 byte/px
        Assert.Equal(0, TextureLoader.Mip0ByteSize(0xDEAD, 64, 64));                 // unknown → 0
    }

    [Fact]
    public void BogusOffsetTable_CollapsesToSingleMip()
    {
        // The real-world defect: 12 mips claimed, but surf1 (343) is far smaller than where
        // mip 0 of a 2048×2048 BC7 must end (80 + 4 MB). Must clamp to mip 0.
        var tex = MakeHeader(Bc7, 2048, 2048, 12, new uint[] { 80, 343, 409, 425 });
        var result = TextureLoader.SanitizeTexBytes(tex);
        Assert.Equal(1, MipCountOf(result));
    }

    [Fact]
    public void ValidContiguousMipChain_IsPreserved()
    {
        // Correct BC7 chain: each surface starts exactly one mip after the previous.
        const uint m0 = 80;
        uint m1 = m0 + 2048u * 2048;          // + 4 MB
        uint m2 = m1 + 1024u * 1024;          // + 1 MB
        uint m3 = m2 + 512u * 512;            // + 256 KB
        var tex = MakeHeader(Bc7, 2048, 2048, 4, new uint[] { m0, m1, m2, m3 });
        var result = TextureLoader.SanitizeTexBytes(tex);
        Assert.Equal(4, MipCountOf(result));
    }

    [Fact]
    public void ZeroedSecondOffset_CollapsesToSingleMip()
    {
        var tex = MakeHeader(Bc7, 2048, 2048, 12, new uint[] { 80, 0, 0, 0 });
        var result = TextureLoader.SanitizeTexBytes(tex);
        Assert.Equal(1, MipCountOf(result));
    }

    [Fact]
    public void SingleMip_IsLeftUntouched()
    {
        var tex = MakeHeader(Bc7, 2048, 2048, 1, new uint[] { 80 });
        var result = TextureLoader.SanitizeTexBytes(tex);
        Assert.Equal(1, MipCountOf(result));
    }
}
