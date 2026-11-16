// Writes the TPK pixel records out as standalone .dds files.
//
// The pixel blob in a texture pack is exactly the body of a DDS file: our byte check
// against the AssetDumper oracle showed the stored payload is byte-identical to the
// oracle .dds body (12/12 on STREAML1RX.BUN). So we only have to synthesise the
// 128-byte DDS header. Unity 6 imports DXT1/DXT3/DXT5/BGRA8 .dds natively, so no BC
// codec is needed here.
//
// Mip chains in NFSU are truncated: a 256x256 DXT1 record is 43 680 bytes, i.e. six
// levels down to 8x8, not down to 1x1. The level count is therefore derived by
// matching the record size instead of using log2(size).
using System;
using System.Globalization;
using System.IO;

namespace NFSU
{
    public static class NfsuDdsWriter
    {
        const int DDSD_CAPS = 0x1;
        const int DDSD_HEIGHT = 0x2;
        const int DDSD_WIDTH = 0x4;
        const int DDSD_PITCH = 0x8;
        const int DDSD_PIXELFORMAT = 0x1000;
        const int DDSD_MIPMAPCOUNT = 0x20000;
        const int DDSD_LINEARSIZE = 0x80000;

        // DDS pixel-format flags (Microsoft DDS reference): FOURCC is 0x4, RGB is 0x40.
        const int DDPF_ALPHAPIXELS = 0x1;
        const int DDPF_FOURCC = 0x4;
        const int DDPF_RGB = 0x40;

        const int DDSCAPS_COMPLEX = 0x8;
        const int DDSCAPS_TEXTURE = 0x1000;
        const int DDSCAPS_MIPMAP = 0x400000;

        // 0x08 is NOT palette-indexed: dword @60 is 1024 for every such record whatever its size
        // (16x16 as well as 512x512), no 1024-byte palette exists next to the pixels, and reading the
        // bytes as a plain byte map draws the right picture - HEADLIGHTGLOW is a radial glow blob,
        // HEADLIGHTFLAREINNER/OUTER are lens flares, ARC_AIRPORTINN is lettering. So it is an
        // A8/L8 single channel, stored with the same 8x8-truncated mip chain, 1 byte per texel.
        public const int FmtA8 = 0x08;
        public const int FmtBGRA8 = 0x20;
        public const int FmtDXT1 = 0x22;
        public const int FmtDXT3 = 0x24;
        public const int FmtDXT5 = 0x26;

        public static bool IsSupportedFormat(int fmt)
        {
            return fmt == FmtA8 || fmt == FmtBGRA8 || fmt == FmtDXT1 || fmt == FmtDXT3 || fmt == FmtDXT5;
        }

        public static string FormatName(int fmt)
        {
            switch (fmt)
            {
                case FmtA8: return "A8";
                case FmtBGRA8: return "A8R8G8B8";
                case FmtDXT1: return "DXT1";
                case FmtDXT3: return "DXT3";
                case FmtDXT5: return "DXT5";
                default: return "0x" + fmt.ToString("X2", CultureInfo.InvariantCulture);
            }
        }

        static int FourCC(string s)
        {
            return (s[0] & 0xFF) | ((s[1] & 0xFF) << 8) | ((s[2] & 0xFF) << 16) | ((s[3] & 0xFF) << 24);
        }

        static int BlockBytes(int fmt) { return fmt == FmtDXT1 ? 8 : 16; }

        /// <summary>Bytes one mip level of <paramref name="fmt"/> occupies for a w*h surface.</summary>
        public static int LevelBytes(int w, int h, int fmt)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (fmt == FmtBGRA8 || fmt == FmtA8)
                return w * h * (fmt == FmtA8 ? 1 : 4);
            return ((w + 3) / 4) * ((h + 3) / 4) * BlockBytes(fmt);
        }

        /// <summary>Mip level count whose summed sizes equal <paramref name="payloadBytes"/>.</summary>
        public static int MipCount(int w, int h, int fmt, int payloadBytes)
        {
            long total = 0;
            int levels = 0;
            int ww = Math.Max(1, w), hh = Math.Max(1, h);
            while (true)
            {
                total += LevelBytes(ww, hh, fmt);
                levels++;
                if (total == payloadBytes)
                    return levels;
                if (total > payloadBytes || (ww == 1 && hh == 1))
                    break;
                ww = Math.Max(1, ww / 2);
                hh = Math.Max(1, hh / 2);
            }
            return 1;
        }

        /// <summary>
        /// Builds a complete .dds file (header + payload) for one texture pack record.
        /// Returns null when the format cannot be written.
        /// </summary>
        public static byte[] BuildDds(NfsuTextureRecord rec, byte[] blob)
        {
            if (rec == null || blob == null || rec.Size <= 0 || rec.Width <= 0 || rec.Height <= 0)
                return null;
            if (!IsSupportedFormat(rec.Format))
                return null;
            long available = blob.Length - rec.Offset;
            int take = (int)Math.Min(rec.Size, available);
            if (take <= 0)
                return null;

            int stored = MipCount(rec.Width, rec.Height, rec.Format, take);
            int full = FullLevelCount(rec.Width, rec.Height);
            int mips = Math.Max(stored, 1);
            byte[] payload = new byte[take];
            Array.Copy(blob, (int)rec.Offset, payload, 0, take);
            if (stored > 1 && stored < full)
            {
                // NFSU chains stop at 8x8, Unity requires the chain down to 1x1, so the
                // missing tail levels are filled with copies of the smallest stored level.
                payload = PadChain(payload, rec.Width, rec.Height, rec.Format, stored, full);
                mips = full;
            }
            // A8 has no 8-bit DDS form Unity accepts; expanded to A8R8G8B8 with RGB = the channel
            // and A = 255, an additive material then adds exactly channel * vertex colour, which is
            // what a glow decal needs.
            bool expandedA8 = rec.Format == FmtA8;
            if (expandedA8)
                payload = ExpandA8(payload);
            bool compressed = !expandedA8 && rec.Format != FmtBGRA8;
            int linear = LevelBytes(rec.Width, rec.Height, rec.Format);

            using (var ms = new MemoryStream(128 + payload.Length))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(FourCC("DDS "));
                w.Write(124);                                     // dwSize
                int flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT
                            | (compressed ? DDSD_LINEARSIZE : DDSD_PITCH);
                if (mips > 1)
                    flags |= DDSD_MIPMAPCOUNT;
                w.Write(flags);
                w.Write(rec.Height);
                w.Write(rec.Width);
                w.Write(compressed ? linear : rec.Width * 4);     // pitch or linear size
                w.Write(1);                                       // depth
                w.Write(mips);
                for (int i = 0; i < 11; i++)                      // reserved1
                    w.Write(0);
                w.Write(32);                                      // pixelFormat dwSize
                if (compressed)
                {
                    string cc = rec.Format == FmtDXT1 ? "DXT1" : rec.Format == FmtDXT3 ? "DXT3" : "DXT5";
                    w.Write(DDPF_FOURCC);
                    w.Write(FourCC(cc));
                    w.Write(0);                                   // bitCount
                    for (int i = 0; i < 4; i++)                   // masks stay zero for FOURCC
                        w.Write(0);
                }
                else
                {
                    w.Write(DDPF_RGB | DDPF_ALPHAPIXELS);
                    w.Write(0);                                   // fourCC
                    w.Write(32);                                  // bitCount
                    w.Write(0x00FF0000);                          // RMASK
                    w.Write(0x0000FF00);                          // GMASK
                    w.Write(0x000000FF);                          // BMASK
                    w.Write(0xFF000000);                          // AMASK
                }
                int caps = DDSCAPS_TEXTURE | (mips > 1 ? DDSCAPS_COMPLEX | DDSCAPS_MIPMAP : 0);
                w.Write(caps);
                w.Write(0);                                       // caps2
                w.Write(0);                                       // caps3
                w.Write(0);                                       // caps4
                w.Write(0);                                       // reserved5
                w.Write(payload);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Levels a complete chain has for w*h (down to 1x1), which is what Unity insists on.</summary>
        static int FullLevelCount(int w, int h)
        {
            int levels = 1;
            int ww = Math.Max(1, w), hh = Math.Max(1, h);
            while (ww > 1 || hh > 1)
            {
                ww = Math.Max(1, ww / 2);
                hh = Math.Max(1, hh / 2);
                levels++;
            }
            return levels;
        }

        static int DimAt(int d, int level)
        {
            return Math.Max(1, d >> level);
        }

        /// <summary>
        /// Expands an A8/L8 chain into A8R8G8B8 (BGR = the channel, alpha = opaque). Only the level
        /// layout matters: 4 bytes per texel everywhere, so the padded chain stays a valid chain.
        /// </summary>
        static byte[] ExpandA8(byte[] src)
        {
            var dst = new byte[src.Length * 4];
            for (int i = 0, o = 0; i < src.Length; i++, o += 4)
            {
                byte v = src[i];
                dst[o] = v;
                dst[o + 1] = v;
                dst[o + 2] = v;
                dst[o + 3] = 0xFF;
            }
            return dst;
        }

        /// <summary>
        /// Copies the stored chain and synthesises the levels the game never stored, tiling the
        /// smallest stored level's blocks. The result is a complete, Unity-acceptable chain.
        /// </summary>
        static byte[] PadChain(byte[] payload, int w, int h, int fmt, int stored, int full)
        {
            var levelBytes = new int[full];
            var levelStart = new int[full + 1];
            for (int i = 0; i < full; i++)
            {
                levelBytes[i] = LevelBytes(DimAt(w, i), DimAt(h, i), fmt);
                levelStart[i + 1] = levelStart[i] + levelBytes[i];
            }
            int copyLen = Math.Min(payload.Length, levelStart[stored]);
            var result = new byte[levelStart[full]];
            Array.Copy(payload, 0, result, 0, copyLen);

            int unit = fmt == FmtBGRA8 || fmt == FmtA8 ? (fmt == FmtA8 ? 1 : 4) : BlockBytes(fmt);
            int srcStart = levelStart[stored - 1];
            int srcLen = levelBytes[stored - 1];
            if (srcLen <= 0 || srcStart + srcLen > payload.Length)
                return result;
            for (int i = stored; i < full; i++)
            {
                int at = levelStart[i];
                for (int done = 0; done < levelBytes[i]; done += unit)
                {
                    int n = Math.Min(unit, levelBytes[i] - done);
                    Array.Copy(payload, srcStart + (done % srcLen), result, at + done, n);
                }
            }
            return result;
        }

        /// <summary>
        /// DXT1 keyed-alpha probe: index 3 of the 3-colour mode is transparent black, and its use is
        /// bimodal per texture. Measured over RA's 1508 DXT1 records: 945 never use the 3-colour mode
        /// at all (opaque by construction); of the 563 that do, 140 use index 3 in >=50% of those
        /// blocks (ORG_FERN24, ARC_LINKFENCE, ARC_FENCE_GRATE, OBJ_EXHAUSTFAN - genuinely
        /// alpha-tested geometry) and the other 423 use it in none, so a majority test separates the
        /// two classes with a very wide margin. That is why only DXT1 gets a keyed-alpha rule here:
        /// for DXT3/DXT5 the alpha channel is a secondary mask for the game's own passes, and
        /// <see cref="MeasureAlpha"/> plus the TPK group id is what decides those.
        /// </summary>
        public static bool Dxt1UsesKeyedAlpha(NfsuTextureRecord rec, byte[] blob)
        {
            if (rec == null || blob == null || rec.Format != FmtDXT1)
                return false;
            long available = blob.Length - rec.Offset;
            int take = (int)Math.Max(0, Math.Min(rec.Size, available));
            if (take <= 0)
                return false;

            int blocks = take / 8;
            if (blocks == 0)
                return false;
            int bstep = Math.Max(1, blocks / 4096);            // cap the work for big chains
            int sampledBlocks = 0, flaggedBlocks = 0;
            for (int b = 0; b < blocks; b += bstep)
            {
                int o = (int)rec.Offset + b * 8;
                if (o + 8 > blob.Length)
                    break;
                // DXT1 block: c0 c1 then 4 bytes holding the 16 two-bit texel indices.
                ushort c0 = BitConverter.ToUInt16(blob, o);
                ushort c1 = BitConverter.ToUInt16(blob, o + 2);
                if (c0 > c1)
                    continue;                               // 4 opaque colours, never transparent
                sampledBlocks++;
                for (int k = 4; k < 8; k++)
                {
                    byte row = blob[o + k];
                    if ((row & 3) == 3 || ((row >> 2) & 3) == 3 ||
                        ((row >> 4) & 3) == 3 || ((row >> 6) & 3) == 3)
                    {
                        flaggedBlocks++;
                        break;
                    }
                }
            }
            return sampledBlocks > 0 && (float)flaggedBlocks / sampledBlocks >= 0.5f;
        }

        /// <summary>
        /// Measures how the alpha channel of a record actually behaves, which is what decides the
        /// material mode for DXT3/DXT5/A8R8G8B8 art (see NfsuAlphaClassifier). Only mip level 0 is
        /// decoded, and at most MaxMeasuredBlocks compressed blocks of it.
        ///
        /// The numbers mirror the Python model in nfs_probe/nfsu_alpha.py feature for feature, so the
        /// two implementations classify identically: alpha is normalised to 0..1, luminance to 0..1,
        /// Frac0/Frac15 count the endpoints of the format's own alpha range, and Spread is the
        /// 95th-minus-5th percentile of the sampled alpha (numpy's linear interpolation).
        ///
        /// Returns false for DXT1 (no alpha channel - <see cref="Dxt1UsesKeyedAlpha"/> decides) and
        /// for A8 (a single channel: its bytes are the value, so these features would be meaningless).
        /// </summary>
        public const int MaxMeasuredBlocks = 6000;

        public static bool MeasureAlpha(NfsuTextureRecord rec, byte[] blob, out NfsuAlphaFeatures feat)
        {
            feat = new NfsuAlphaFeatures();
            if (rec == null || blob == null || rec.Width <= 0 || rec.Height <= 0)
                return false;
            long available = blob.Length - rec.Offset;
            int take = (int)Math.Max(0, Math.Min((long)rec.Size, available));
            if (take <= 0)
                return false;

            var acc = new AlphaSamples();
            float zeroAt, oneAt;

            if (rec.Format == FmtBGRA8)
            {
                zeroAt = 0.02f;
                oneAt = 0.98f;
                int texels = take / 4;
                int cap = MaxMeasuredBlocks * 16;
                for (int t = 0; t < texels && t < cap; t++)
                {
                    int o = (int)rec.Offset + t * 4;          // D3DCOLOR order: B,G,R,A
                    if (o + 4 > blob.Length)
                        break;
                    acc.Add(blob[o + 3] * (1f / 255f),
                            Luma(blob[o + 2] * (1f / 255f), blob[o + 1] * (1f / 255f),
                                 blob[o] * (1f / 255f)));
                }
            }
            else if (rec.Format == FmtDXT3 || rec.Format == FmtDXT5)
            {
                bool dxt3 = rec.Format == FmtDXT3;
                zeroAt = dxt3 ? 0.5f / 15f : 0.02f;
                oneAt = dxt3 ? 14.5f / 15f : 0.98f;

                int w = rec.Width, h = rec.Height;
                int bw = (w + 3) / 4, bh = (h + 3) / 4;
                int blocks = 0;
                var alpha = new float[16];
                var cr = new float[16];
                var cg = new float[16];
                var cb = new float[16];
                bool stop = false;
                for (int by = 0; by < bh && !stop; by++)
                {
                    for (int bx = 0; bx < bw; bx++)
                    {
                        int rel = (by * bw + bx) * 16;
                        if (rel + 16 > take)
                            continue;                         // stored chain is short of this block
                        if (blocks >= MaxMeasuredBlocks)
                        {
                            stop = true;
                            break;
                        }
                        blocks++;

                        int off = (int)rec.Offset + rel;      // reads are blob-absolute
                        if (off + 16 > blob.Length)
                            break;
                        if (dxt3)
                            Dxt3Alpha(blob, off, alpha);
                        else
                            Dxt5Alpha(blob, off, alpha);
                        ColourBlock(blob, off + 8, cr, cg, cb);
                        for (int j = 0; j < 16; j++)
                        {
                            int gy = by * 4 + j / 4;
                            int gx = bx * 4 + j % 4;
                            if (gy >= h || gx >= w)
                                continue;
                            acc.Add(alpha[j], Luma(cr[j], cg[j], cb[j]));
                        }
                    }
                }
            }
            else
            {
                return false;
            }

            if (acc.Count == 0)
                return false;
            feat = acc.Finish(zeroAt, oneAt);
            return true;
        }

        static float Luma(float r, float g, float b)
        {
            return 0.299f * r + 0.587f * g + 0.114f * b;
        }

        /// <summary>565 red/blue (5 bits) to 0..1, low bits filled from the high ones.</summary>
        static float Unpack5(int v)
        {
            return ((v << 3) | (v >> 2)) * (1f / 255f);
        }

        /// <summary>
        /// 565 green (6 bits) to 0..1. It has its own shift: reusing the 5-bit one inflates green
        /// (the 5-bit fill of 63 is 511/255 = 2.004), which pushed measured luminance above 1.0.
        /// </summary>
        static float Unpack6(int v)
        {
            return ((v << 2) | (v >> 4)) * (1f / 255f);
        }

        /// <summary>Decodes one DXT1 colour sub-block into 16 RGB triples.</summary>
        static void ColourBlock(byte[] blob, int off, float[] r, float[] g, float[] b)
        {
            ushort c0 = (ushort)(blob[off] | (blob[off + 1] << 8));
            ushort c1 = (ushort)(blob[off + 2] | (blob[off + 3] << 8));
            float r0 = Unpack5((c0 >> 11) & 0x1F), g0 = Unpack6((c0 >> 5) & 0x3F), b0 = Unpack5(c0 & 0x1F);
            float r1 = Unpack5((c1 >> 11) & 0x1F), g1 = Unpack6((c1 >> 5) & 0x3F), b1 = Unpack5(c1 & 0x1F);
            for (int k = 0; k < 4; k++)
            {
                byte row = blob[off + 4 + k];
                for (int j = 0; j < 4; j++)
                {
                    int sel = (row >> (2 * j)) & 3;
                    int idx = k * 4 + j;
                    if (c0 > c1)
                    {
                        // four colours: two endpoints plus two thirds/one thirds blends
                        r[idx] = sel == 0 ? r0 : sel == 1 ? r1 : sel == 2 ? (2 * r0 + r1) / 3 : (r0 + 2 * r1) / 3;
                        g[idx] = sel == 0 ? g0 : sel == 1 ? g1 : sel == 2 ? (2 * g0 + g1) / 3 : (g0 + 2 * g1) / 3;
                        b[idx] = sel == 0 ? b0 : sel == 1 ? b1 : sel == 2 ? (2 * b0 + b1) / 3 : (b0 + 2 * b1) / 3;
                    }
                    else
                    {
                        // three colours plus transparent black
                        r[idx] = sel < 2 ? (sel == 0 ? r0 : r1) : sel == 2 ? (r0 + r1) / 2 : 0f;
                        g[idx] = sel < 2 ? (sel == 0 ? g0 : g1) : sel == 2 ? (g0 + g1) / 2 : 0f;
                        b[idx] = sel < 2 ? (sel == 0 ? b0 : b1) : sel == 2 ? (b0 + b1) / 2 : 0f;
                    }
                }
            }
        }

        /// <summary>DXT3 explicit 4-bit alpha of one block: 16 values in 0..1.</summary>
        static void Dxt3Alpha(byte[] blob, int off, float[] a)
        {
            for (int j = 0; j < 16; j += 2)
            {
                byte v = blob[off + j / 2];
                a[j] = (v & 0x0F) * (1f / 15f);
                a[j + 1] = (v >> 4) * (1f / 15f);
            }
        }

        /// <summary>
        /// DXT5 interpolated alpha of one block: 16 values in 0..1. (No DXT5 record exists in RA, RX
        /// or the Global packs - they are DXT1/DXT3/A8/A8R8G8B8 - but the codec is complete anyway.)
        /// </summary>
        static void Dxt5Alpha(byte[] blob, int off, float[] a)
        {
            float a0 = blob[off] * (1f / 255f);
            float a1 = blob[off + 1] * (1f / 255f);
            var lut = new float[8];
            if (a0 > a1)
            {
                for (int i = 0; i < 8; i++)
                    lut[i] = ((7 - i) * a0 + i * a1) / 7f;
            }
            else
            {
                for (int i = 0; i < 6; i++)
                    lut[i] = ((5 - i) * a0 + i * a1) / 5f;
                lut[6] = 0f;
                lut[7] = 1f;
            }
            long bits = 0;
            for (int i = 5; i >= 0; i--)
                bits = (bits << 8) | blob[off + 2 + i];
            for (int j = 0; j < 16; j++)
                a[j] = lut[(int)((bits >> (3 * j)) & 7)];
        }

        /// <summary>Sampled (alpha, luminance) pairs of one mip level, kept in insertion order.</summary>
        sealed class AlphaSamples
        {
            float[] alpha = new float[4096];
            float[] luma = new float[4096];

            public int Count { get; private set; }

            public void Add(float a, float l)
            {
                if (Count == alpha.Length)
                {
                    Array.Resize(ref alpha, alpha.Length * 2);
                    Array.Resize(ref luma, luma.Length * 2);
                }
                alpha[Count] = a;
                luma[Count] = l;
                Count++;
            }

            public NfsuAlphaFeatures Finish(float zeroAt, float oneAt)
            {
                var f = new NfsuAlphaFeatures { Sampled = Count };
                double sum = 0, sumSq = 0, sumL = 0, sumLSq = 0, sumAL = 0;
                int atZero = 0, atOne = 0, dark = 0, lowN = 0, highN = 0;
                double lowSum = 0, highSum = 0;
                for (int i = 0; i < Count; i++)
                {
                    double a = alpha[i], l = luma[i];
                    sum += a; sumSq += a * a; sumL += l; sumLSq += l * l; sumAL += a * l;
                    if (a <= zeroAt) atZero++;
                    if (a >= oneAt) atOne++;
                    if (l < 0.06) dark++;
                    if (a < 0.25) { lowSum += l; lowN++; }
                    if (a > 0.75) { highSum += l; highN++; }
                }
                double mean = sum / Count, meanL = sumL / Count;
                double varA = Math.Max(0, sumSq / Count - mean * mean);
                double varL = Math.Max(0, sumLSq / Count - meanL * meanL);
                f.MeanA = (float)mean;
                f.Frac0 = atZero / (float)Count;
                f.Frac15 = atOne / (float)Count;
                f.Black = dark / (float)Count;
                f.Corr = varA > 1e-8 && varL > 1e-8 ? (float)((sumAL / Count - mean * meanL) / Math.Sqrt(varA * varL)) : 0f;
                f.LLow = lowN > 0 ? (float)(lowSum / lowN) : -1f;
                f.LHigh = highN > 0 ? (float)(highSum / highN) : -1f;
                f.Spread = Percentile(alpha, Count, 0.95f) - Percentile(alpha, Count, 0.05f);
                return f;
            }

            static float Percentile(float[] src, int n, float q)
            {
                var c = new float[n];
                Array.Copy(src, c, n);
                Array.Sort(c);
                double h = (n - 1) * (double)q;
                int lo = (int)Math.Floor(h);
                int hi = Math.Min(n - 1, lo + 1);
                float t = (float)(h - lo);
                return c[lo] * (1f - t) + c[hi] * t;
            }
        }

        /// <summary>File name for a record, safe for AssetDatabase paths.</summary>
        public static string RecordFileName(NfsuTextureRecord rec)
        {
            var name = Sanitize(rec.Name);
            if (string.IsNullOrEmpty(name))
                name = "texture";
            return string.Format(CultureInfo.InvariantCulture, "0x{0:X8}_{1}", rec.BinKey, name);
        }

        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
            }
            return sb.ToString().Trim('_');
        }
    }
}
