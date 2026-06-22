using System;
using System.Collections.Generic;
using System.IO;
using BitMiracle.LibTiff.Classic;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Remaps overlay PNG pixels between FFXIV body UV spaces (bibo, gen3, gen2/vanilla)
/// using pre-baked 16-bit RGBA TIFF transfer maps from LooseTextureCompilerCore (MIT).
/// Maps are loaded lazily on first use and cached for the plugin lifetime.
/// </summary>
public class UVRemapService
{
    private readonly IPluginLog log;
    private readonly string mapsDir;

    private readonly Dictionary<(string From, string To), TransferMap?> cache = new();
    private readonly object cacheLock = new();

    private sealed class TransferMap(ushort[] x, ushort[] y, bool[] valid, int w, int h)
    {
        public readonly ushort[] X = x;
        public readonly ushort[] Y = y;
        public readonly bool[]   Valid = valid;
        public readonly int      W = w, H = h;
    }

    public UVRemapService(IPluginLog log, string pluginDir)
    {
        this.log = log;
        mapsDir = Path.Combine(pluginDir, "uvmaps");
    }

    /// <summary>
    /// Infers the body type ("bibo", "gen3", "gen2") from a material game path suffix.
    /// Returns null for equipment/accessory materials that don't use a body-UV layout.
    /// </summary>
    public static string? InferBodyType(string mtrlGamePath)
    {
        if (mtrlGamePath.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)) return "bibo";
        if (mtrlGamePath.EndsWith("_eve.mtrl",  StringComparison.OrdinalIgnoreCase)) return "gen3";
        // _a/_b only mean body UV types when under /obj/body/ — equipment paths also end in _a/_b.
        // _a = vanilla (gen2 UV), _b = gen3 UV (used by body mods like AB Body, SPS gen3, etc.)
        if (mtrlGamePath.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase))
        {
            if (mtrlGamePath.EndsWith("_b.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen3";
            if (mtrlGamePath.EndsWith("_a.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen2";
        }
        return null;
    }

    /// <summary>
    /// Remaps <paramref name="srcRgba"/> from <paramref name="from"/> UV space to
    /// <paramref name="to"/> UV space. Returns the input unchanged on failure.
    /// </summary>
    public byte[] Remap(byte[] srcRgba, int srcW, int srcH, string from, string to)
    {
        var map = GetMap(from, to);
        if (map == null) return srcRgba;
        return ApplyRemap(srcRgba, srcW, srcH, map);
    }

    /// <summary>
    /// Returns the right half (x = w/2 .. w-1) of a 4-channel RGBA image as a (w/2 × h) buffer.
    /// Used for bibo→gen2: the right half of a 4096×4096 bibo overlay is in vanilla UV space.
    /// </summary>
    public static byte[] CropRightHalf(byte[] src, int srcW, int srcH)
    {
        int halfW = srcW / 2;
        var dst = new byte[halfW * srcH * 4];
        for (int y = 0; y < srcH; y++)
            Array.Copy(src, (y * srcW + halfW) * 4, dst, y * halfW * 4, halfW * 4);
        return dst;
    }

    // ── Map cache ────────────────────────────────────────────────────────────

    private TransferMap? GetMap(string from, string to)
    {
        var key = (from.ToLowerInvariant(), to.ToLowerInvariant());
        lock (cacheLock)
        {
            if (cache.TryGetValue(key, out var hit)) return hit;
            var map = LoadMap(from, to);
            if (map != null) cache[key] = map;
            return map;
        }
    }

    private TransferMap? LoadMap(string from, string to)
    {
        var filename = $"{from.ToLowerInvariant()}_to_{to.ToLowerInvariant()}_transfer.tif";
        var path = Path.Combine(mapsDir, filename);
        if (!File.Exists(path))
        {
            log.Warning("[Proteus] UV transfer map not found: {0}", path);
            return null;
        }

        log.Information("[Proteus] Loading UV transfer map {0} ...", filename);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var tiff = Tiff.Open(path, "r");
            if (tiff == null)
            {
                log.Error("[Proteus] Failed to open TIFF: {0}", path);
                return null;
            }

            int w = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            int n = w * h;

            var mapX  = new ushort[n];
            var mapY  = new ushort[n];
            var valid = new bool[n];

            int scanlineBytes = tiff.ScanlineSize();
            var scanline = new byte[scanlineBytes];

            for (int row = 0; row < h; row++)
            {
                tiff.ReadScanline(scanline, row);
                int rowOff = row * w;
                for (int x = 0; x < w; x++)
                {
                    int si = x * 8; // 8 bytes per RGBA16 pixel
                    mapX [rowOff + x] = BitConverter.ToUInt16(scanline, si);
                    mapY [rowOff + x] = BitConverter.ToUInt16(scanline, si + 2);
                    valid[rowOff + x] = BitConverter.ToUInt16(scanline, si + 6) > 0;
                }
            }

            sw.Stop();
            log.Information("[Proteus] UV map loaded {0}×{1} in {2:F1}s", w, h, sw.Elapsed.TotalSeconds);
            return new TransferMap(mapX, mapY, valid, w, h);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Failed to load UV transfer map: {0}", path);
            return null;
        }
    }

    // ── Remap ────────────────────────────────────────────────────────────────

    private static byte[] ApplyRemap(byte[] src, int srcW, int srcH, TransferMap map)
    {
        int dstW = map.W, dstH = map.H;
        var dst = new byte[dstW * dstH * 4];

        for (int dy = 0; dy < dstH; dy++)
        {
            for (int dx = 0; dx < dstW; dx++)
            {
                int idx = dy * dstW + dx;
                if (!map.Valid[idx]) continue; // leave transparent (zero-initialised)

                float srcXf = (float)map.X[idx] / 65535f * (srcW - 1);
                float srcYf = (float)map.Y[idx] / 65535f * (srcH - 1);

                int x1 = (int)srcXf;
                int y1 = (int)srcYf;
                int x2 = Math.Min(x1 + 1, srcW - 1);
                int y2 = Math.Min(y1 + 1, srcH - 1);
                float xf = srcXf - x1;
                float yf = srcYf - y1;

                int dstOff = idx * 4;
                for (int c = 0; c < 4; c++)
                {
                    float topLeft     = src[(y1 * srcW + x1) * 4 + c];
                    float topRight    = src[(y1 * srcW + x2) * 4 + c];
                    float bottomLeft  = src[(y2 * srcW + x1) * 4 + c];
                    float bottomRight = src[(y2 * srcW + x2) * 4 + c];
                    dst[dstOff + c] = (byte)(topLeft     * (1 - xf) * (1 - yf)
                                           + topRight    * xf       * (1 - yf)
                                           + bottomLeft  * (1 - xf) * yf
                                           + bottomRight * xf       * yf
                                           + 0.5f);
                }
            }
        }

        return dst;
    }

    public static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return src;
        var dst = new byte[dstW * dstH * 4];
        float xScale = (float)srcW / dstW;
        float yScale = (float)srcH / dstH;
        for (int dy = 0; dy < dstH; dy++)
        {
            for (int dx = 0; dx < dstW; dx++)
            {
                float srcXf = Math.Clamp((dx + 0.5f) * xScale - 0.5f, 0, srcW - 1);
                float srcYf = Math.Clamp((dy + 0.5f) * yScale - 0.5f, 0, srcH - 1);
                int x1 = (int)srcXf, y1 = (int)srcYf;
                int x2 = Math.Min(x1 + 1, srcW - 1), y2 = Math.Min(y1 + 1, srcH - 1);
                float xf = srcXf - x1, yf = srcYf - y1;
                int dstOff = (dy * dstW + dx) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float topLeft     = src[(y1 * srcW + x1) * 4 + c];
                    float topRight    = src[(y1 * srcW + x2) * 4 + c];
                    float bottomLeft  = src[(y2 * srcW + x1) * 4 + c];
                    float bottomRight = src[(y2 * srcW + x2) * 4 + c];
                    dst[dstOff + c] = (byte)(topLeft     * (1 - xf) * (1 - yf)
                                           + topRight    * xf       * (1 - yf)
                                           + bottomLeft  * (1 - xf) * yf
                                           + bottomRight * xf       * yf
                                           + 0.5f);
                }
            }
        }
        return dst;
    }
}
