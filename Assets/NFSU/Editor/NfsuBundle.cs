// NFS Underground (2003) bundle reader.
//
// Byte-level layout verified on Tracks\STREAML1RX.BUN against the AssetDumper Collada
// oracle: 109/109 geometries matched on vertex and triangle counts, 101/101 placement
// records matched on position and rotation (0.000000 max error), 12/12 texture payloads
// byte-identical.
//
// Container:  u32 magic; u32 size; payload[size]
//   magic == 0          -> padding entry, skip 8 + size
//   magic & 0x80000000  -> container, payload holds nested chunks
//   otherwise           -> leaf
// Inside leaf payloads the meaningful body is aligned to the END of the payload; the
// leading bytes are slack or 0x11 fill runs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NFSU
{
    public sealed class NfsuChunk
    {
        public uint Magic;
        public long Payload;
        public int Size;
        public readonly List<NfsuChunk> Children = new List<NfsuChunk>();

        public List<NfsuChunk> Kids(uint magic)
        {
            var list = new List<NfsuChunk>();
            foreach (var c in Children)
                if (c.Magic == magic)
                    list.Add(c);
            return list;
        }
    }

    public sealed class NfsuSubmesh
    {
        public float[] BBoxMin = new float[3];
        public float[] BBoxMax = new float[3];
        public int IndexCount;
        public int Slot;          // index into NfsuDrawable.Slots (texture BinKeys)
        public int MatId;         // 0xFF = none
        public int IndexStart;
    }

    public sealed class NfsuDrawable
    {
        public uint BinKey;
        public string Name = "";
        public float[] BBox = new float[6];
        public float[] Rot = new float[9];       // row-major, game space
        public float[] Trans = new float[4];     // x,y,z,1  (not applied to scenery geometry by the game)
        public uint[] Slots = Array.Empty<uint>();
        public uint[] MatHashes = Array.Empty<uint>();
        public int SubmeshCount;
        public int TriCount;
        public int VertCount;
        public int Stride;
        public bool Unsupported;                 // skinned / multi-stream vertex format
        public byte[] Verts = Array.Empty<byte>();
        public byte[] Indices = Array.Empty<byte>();
        public readonly List<NfsuSubmesh> Submeshes = new List<NfsuSubmesh>();
    }

    public sealed class NfsuSectionType
    {
        public uint BinKey;
        public int Raw24;
    }

    public sealed class NfsuInstance
    {
        public short[] BBox = new short[6];      // whole metres: minx,miny,minz,maxx,maxy,maxz
        public int TypeIndex;
        public int Flags;
        public float[] Pos = new float[3];
        public short[] Rot = new short[9];       // /8192; the three consecutive triples are the
                                                 // COLUMNS of the game-space matrix (x' = m0*x + m3*y + m6*z ...)
    }

    public sealed class NfsuSection
    {
        public int Id;
        public readonly List<NfsuSectionType> Types = new List<NfsuSectionType>();
        public readonly List<NfsuInstance> Instances = new List<NfsuInstance>();
    }

    public sealed class NfsuTextureRecord
    {
        public string Name = "";
        public uint BinKey;
        // Dword @40 is NOT a name hash: only 8 distinct values over RA/RX/Global, each shared by a
        // whole family of records (see NfsuAlphaClassifier), so it is a texture/material group id.
        public uint GroupId;
        public long Offset;
        public int Size;
        public int Width;
        public int Height;
        public int Format;      // 0x08 A8/L8, 0x20 BGRA8, 0x22 DXT1, 0x24 DXT3, 0x26 DXT5
        public int LogW;
        public int LogH;
        public int Byte4E;
        public int Byte4F;
    }

    public sealed class NfsuTexturePack
    {
        public readonly List<NfsuTextureRecord> Records = new List<NfsuTextureRecord>();
        public byte[] Pixels = Array.Empty<byte>();
        // Pixel-block accounting (see ReadPixelBlob): -1 = the body is raw texels, >= 0 = the
        // number of JDLZ blocks decoded for it. ShortBlocks counts records whose stored size
        // disagrees with the block that was decoded for them.
        public int PixelBlocks = -1;
        public bool PixelsCompressed;
        public int ShortBlocks;
    }

    public sealed class NfsuBundle
    {
        public const uint ChunkSection = 0x80034100;
        public const uint ChunkSectionHeader = 0x00034101;
        public const uint ChunkSectionInstances = 0x00034103;
        public const uint ChunkSectionTypes = 0x00034102;
        public const uint ChunkObjectCollection = 0x80134000;
        public const uint ChunkDrawable = 0x80134010;
        public const uint ChunkDrawableInfo = 0x00134011;
        public const uint ChunkTextureSlots = 0x00134012;
        public const uint ChunkMaterialHashes = 0x00134013;
        public const uint ChunkGeometry = 0x80134100;
        public const uint ChunkGeometryHeader = 0x00134900;
        public const uint ChunkVertexBuffer = 0x00134B01;
        public const uint ChunkSubmeshes = 0x00134B02;
        public const uint ChunkIndices = 0x00134B03;
        public const uint ChunkTexturePack = 0xB3300000;

        public readonly string Path;
        public readonly byte[] Data;
        public readonly List<NfsuChunk> Top = new List<NfsuChunk>();

        public NfsuBundle(string path)
            : this(path, File.ReadAllBytes(path))
        {
        }

        /// <summary>
        /// Wraps a buffer that is already a chunk stream - used for a .lzc after NfsuJdlz
        /// (Tracks\TRACKB####.lzc) and for any other stream that is not a file of its own.
        /// </summary>
        public NfsuBundle(string path, byte[] data)
        {
            Path = path;
            Data = data;
            Parse(0, Data.LongLength, Top);
        }

        void Parse(long lo, long hi, List<NfsuChunk> into)
        {
            long pos = lo;
            while (pos + 8 <= hi)
            {
                uint magic = ReadU32(pos);
                uint size = ReadU32(pos + 4);
                if (magic == 0)
                {
                    pos += 8 + (long)size;      // padding entry
                    continue;
                }
                long payload = pos + 8;
                long end = payload + (long)size;
                if (end > hi)
                    throw new InvalidDataException(string.Format(
                        "truncated chunk 0x{0:X8} at {1}", magic, pos));
                var c = new NfsuChunk { Magic = magic, Payload = payload, Size = (int)size };
                into.Add(c);
                if ((magic & 0x80000000u) != 0)
                    Parse(payload, end, c.Children);
                pos = end;
            }
        }

        public IEnumerable<NfsuChunk> Walk()
        {
            var stack = new Stack<NfsuChunk>();
            for (int i = Top.Count - 1; i >= 0; i--)
                stack.Push(Top[i]);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                yield return c;
                for (int i = c.Children.Count - 1; i >= 0; i--)
                    stack.Push(c.Children[i]);
            }
        }

        // ---------------------------------------------------------------- primitives
        uint ReadU32(long at) { return BitConverter.ToUInt32(Data, (int)at); }
        int ReadU16(long at) { return BitConverter.ToUInt16(Data, (int)at); }
        short ReadI16(long at) { return BitConverter.ToInt16(Data, (int)at); }
        float ReadF32(long at) { return BitConverter.ToSingle(Data, (int)at); }

        byte[] RawFull(NfsuChunk c)
        {
            var b = new byte[c.Size];
            Array.Copy(Data, c.Payload, b, 0, c.Size);
            return b;
        }

        /// <summary>
        /// Payload body at the END of the payload, but respecting the format's 4-byte body padding:
        /// a body whose length is not a multiple of 4 (u16 index buffers: 6 * triangleCount ends at
        /// 2 mod 4 whenever the triangle count is odd) is followed by pad bytes, so the body ends
        /// that many bytes before the payload end. Reading it flush against the end shifts every
        /// index by one u16 and turns the mesh into shards.
        /// </summary>
        byte[] RawBody(NfsuChunk c, int length)
        {
            int pad = BodyPad(length);
            if (c.Size < length + pad)
                throw new InvalidDataException(string.Format("chunk 0x{0:X8} size {1} < body {2}", c.Magic, c.Size, length));
            var b = new byte[length];
            Array.Copy(Data, c.Payload + c.Size - pad - length, b, 0, length);
            return b;
        }

        /// <summary>Trailing pad bytes that follow a body of <paramref name="length"/> bytes.</summary>
        static int BodyPad(int length) { return (4 - (length & 3)) & 3; }

        static uint U32(byte[] b, int i) { return BitConverter.ToUInt32(b, i); }
        static ushort U16(byte[] b, int i) { return BitConverter.ToUInt16(b, i); }
        static short I16(byte[] b, int i) { return BitConverter.ToInt16(b, i); }
        static float F32(byte[] b, int i) { return BitConverter.ToSingle(b, i); }

        static string CStr(byte[] b, int i, int max)
        {
            int end = Math.Min(b.Length, i + max);
            int z = i;
            while (z < end && b[z] != 0)
                z++;
            return Encoding.ASCII.GetString(b, i, z - i);
        }

        static bool IsFill(byte[] b)
        {
            foreach (var x in b)
                if (x != 0x11 && x != 0x00)
                    return false;
            return true;
        }

        static int PickStride(int chunkSize, int vertCount, byte[] raw)
        {
            foreach (int cand in new[] { 24, 36, 32, 28, 20, 40, 44, 48, 16 })
            {
                int pad = chunkSize - cand * vertCount;
                if (pad < 0 || pad > 8192)
                    continue;
                bool ok = true;
                for (int i = 0; i < pad; i++)
                    if (raw[i] != 0x11 && raw[i] != 0x00) { ok = false; break; }
                if (ok)
                    return cand;
            }
            return 0;
        }

        // ---------------------------------------------------------------- drawables
        public List<NfsuDrawable> Drawables()
        {
            var list = new List<NfsuDrawable>();
            foreach (var c in Walk())
                if (c.Magic == ChunkDrawable)
                    list.Add(ReadDrawable(c));
            return list;
        }

        /// <summary>
        /// Reads one 0x80134010 drawable (info block, texture slots, geometry). Public because the
        /// car reader walks the chunk tree itself: it needs the drawable's own chunk to reach the
        /// 0x0013401A socket table, which <see cref="Drawables"/> throws away.
        /// </summary>
        public NfsuDrawable ReadDrawable(NfsuChunk c)
        {
            var info = c.Kids(ChunkDrawableInfo);
            if (info.Count == 0)
                throw new InvalidDataException("drawable at " + c.Payload + " has no 0x134011");
            var b = RawBody(info[0], 176);
            var d = new NfsuDrawable
            {
                BinKey = U32(b, 16),
                Name = CStr(b, 148, 28),
            };
            // min.xyzw @32 and max.xyzw @48: max.z sits at @56, so a plain six-float read at @32
            // would put min.w (always 0) where max.z belongs.
            for (int k = 0; k < 3; k++) d.BBox[k] = F32(b, 32 + 4 * k);
            for (int k = 0; k < 3; k++) d.BBox[3 + k] = F32(b, 48 + 4 * k);
            for (int i = 0; i < 9; i++) d.Rot[i] = F32(b, 64 + 4 * i);
            for (int i = 0; i < 4; i++) d.Trans[i] = F32(b, 112 + 4 * i);

            var slots = c.Kids(ChunkTextureSlots);
            if (slots.Count > 0)
            {
                var s = RawFull(slots[0]);
                var keys = new List<uint>();
                for (int i = 0; i + 4 <= s.Length; i += 8)
                    keys.Add(U32(s, i));
                d.Slots = keys.ToArray();
            }
            var mh = c.Kids(ChunkMaterialHashes);
            if (mh.Count > 0)
            {
                var s = RawFull(mh[0]);
                var keys = new List<uint>();
                for (int i = 0; i + 4 <= s.Length; i += 8)
                    keys.Add(U32(s, i));
                d.MatHashes = keys.ToArray();
            }

            var geo = c.Kids(ChunkGeometry);
            if (geo.Count > 0)
                ReadGeometry(geo[0], d);
            return d;
        }

        void ReadGeometry(NfsuChunk g, NfsuDrawable d)
        {
            var hl = g.Kids(ChunkGeometryHeader);
            var vbl = g.Kids(ChunkVertexBuffer);
            var ibl = g.Kids(ChunkIndices);
            var sml = g.Kids(ChunkSubmeshes);
            if (hl.Count == 0 || vbl.Count == 0 || ibl.Count == 0)
            {
                d.Unsupported = true;
                return;
            }
            var h = RawFull(hl[0]);
            var vraw = RawFull(vbl[0]);

            if ((sml.Count == 0 || IsFill(RawFull(sml[0]))) && IsFill(RawFull(ibl[0])))
                return;                                  // degenerate / empty geometry

            int sub = 0, tris = 0, verts = 0, stride = 0;
            // The header body sits at a variable position inside the payload, but its fields have
            // fixed relative offsets: submeshCount @i, triangleCount @i+20, vertexCount @i+36.
            for (int i = 0; i + 40 <= h.Length; i += 4)
            {
                uint scv = U32(h, i);
                uint tcv = U32(h, i + 20);
                uint vcv = U32(h, i + 36);
                // vertexCount is a 16-bit field that is sometimes sign-extended (0xFFFFB004 = 45060)
                if (vcv > 0x00FFFFFFu)
                    vcv &= 0xFFFFu;
                if (scv < 1 || scv > 256 || tcv == 0 || vcv == 0)
                    continue;
                int sc = (int)scv, tc = (int)tcv, vc = (int)vcv;
                if (sml.Count > 0 && sc * 60 > sml[0].Size)
                    continue;
                if (tc * 6 > ibl[0].Size || ibl[0].Size - tc * 6 > 8192)
                    continue;
                int st = PickStride(vbl[0].Size, vc, vraw);
                if (st == 0)
                    continue;
                sub = sc; tris = tc; verts = vc; stride = st;
                break;
            }
            if (verts == 0)
            {
                d.Unsupported = true;                    // not the plain 24-byte scenery layout
                return;
            }
            d.SubmeshCount = sub;
            d.TriCount = tris;
            d.VertCount = verts;
            d.Stride = stride;
            d.Verts = new byte[stride * verts];
            Array.Copy(vraw, vbl[0].Size - BodyPad(stride * verts) - stride * verts, d.Verts, 0, stride * verts);

            var iraw = RawFull(ibl[0]);
            d.Indices = new byte[tris * 6];
            // 6 * triangleCount is 2 mod 4 for an odd triangle count, and the format pads bodies to
            // a 4-byte boundary; reading flush against the payload end shifts every index by one
            // u16 (shards) - 590 of RA's 4863 drawables.
            Array.Copy(iraw, iraw.Length - BodyPad(tris * 6) - tris * 6, d.Indices, 0, tris * 6);

            if (sml.Count > 0)
            {
                var s = RawBody(sml[0], sub * 60);
                for (int r = 0; r < sub; r++)
                {
                    int o = r * 60;
                    var sm = new NfsuSubmesh
                    {
                        IndexCount = (int)U32(s, o + 12),
                        Slot = (int)U32(s, o + 28),
                        MatId = (int)U32(s, o + 32),
                        IndexStart = (int)U32(s, o + 52),
                    };
                    for (int k = 0; k < 3; k++)
                    {
                        sm.BBoxMin[k] = F32(s, o + 4 * k);
                        sm.BBoxMax[k] = F32(s, o + 16 + 4 * k);
                    }
                    d.Submeshes.Add(sm);
                }
            }
        }

        // ---------------------------------------------------------------- sections
        public List<NfsuSection> Sections()
        {
            var list = new List<NfsuSection>();
            foreach (var c in Walk())
            {
                if (c.Magic != ChunkSection)
                    continue;
                var hdr = c.Kids(ChunkSectionHeader);
                var sec = new NfsuSection();
                if (hdr.Count > 0)
                    sec.Id = (int)U32(RawBody(hdr[0], 60), 12);

                foreach (var t in c.Kids(ChunkSectionTypes))
                {
                    int n = t.Size / 72;
                    if (n <= 0)
                        continue;
                    var tb = RawBody(t, n * 72);
                    for (int i = 0; i < n; i++)
                        sec.Types.Add(new NfsuSectionType
                        {
                            BinKey = U32(tb, i * 72),
                            Raw24 = (int)U32(tb, i * 72 + 24),
                        });
                }

                foreach (var ic in c.Kids(ChunkSectionInstances))
                {
                    int n = ic.Size / 48;
                    var ib = RawFull(ic);
                    int lead = ic.Size - n * 48;
                    for (int r = 0; r < n; r++)
                    {
                        int o = lead + r * 48;
                        var ins = new NfsuInstance();
                        for (int k = 0; k < 6; k++) ins.BBox[k] = I16(ib, o + 2 * k);
                        ins.TypeIndex = U16(ib, o + 12);
                        ins.Flags = U16(ib, o + 14);
                        for (int k = 0; k < 3; k++) ins.Pos[k] = F32(ib, o + 16 + 4 * k);
                        for (int k = 0; k < 9; k++) ins.Rot[k] = I16(ib, o + 28 + 2 * k);
                        sec.Instances.Add(ins);
                    }
                }
                list.Add(sec);
            }
            return list;
        }

        // ---------------------------------------------------------------- texture packs
        public List<NfsuTexturePack> TexturePacks()
        {
            var list = new List<NfsuTexturePack>();
            foreach (var c in Walk())
            {
                if (c.Magic != ChunkTexturePack)
                    continue;
                var pack = new NfsuTexturePack();
                foreach (var sub in c.Kids(0xB3310000))
                {
                    foreach (var rc in sub.Children)
                    {
                        if (rc.Magic != 0x33310004)
                            continue;
                        var rb = RawFull(rc);
                        int n = rb.Length / 124;
                        for (int i = 0; i < n; i++)
                        {
                            int o = i * 124;
                            uint fmtWord = U32(rb, o + 72);
                            pack.Records.Add(new NfsuTextureRecord
                            {
                                Name = CStr(rb, o + 12, 32),
                                BinKey = U32(rb, o + 36),
                                GroupId = U32(rb, o + 40),
                                Offset = U32(rb, o + 48),
                                Size = (int)U32(rb, o + 56),
                                Width = U16(rb, o + 68),
                                Height = U16(rb, o + 70),
                                LogW = (int)(fmtWord & 0xFF),
                                LogH = (int)((fmtWord >> 8) & 0xFF),
                                Format = (int)((fmtWord >> 16) & 0xFF),
                                Byte4E = (int)((U32(rb, o + 76) >> 16) & 0xFF),
                                Byte4F = (int)((U32(rb, o + 76) >> 24) & 0xFF),
                            });
                        }
                    }
                }
                foreach (var sub in c.Kids(0xB3320000))
                {
                    foreach (var pc in sub.Children)
                    {
                        if (pc.Magic != 0x33320002 || pack.Records.Count == 0)
                            continue;
                        long end = 0;
                        foreach (var r in pack.Records)
                            end = Math.Max(end, r.Offset + r.Size);
                        int blocks, shortBlocks;
                        pack.Pixels = ReadPixelBlob(pc, pack.Records, end, out blocks,
                                                    out shortBlocks);
                        pack.PixelBlocks = blocks;
                        pack.PixelsCompressed = blocks >= 0;
                        pack.ShortBlocks = shortBlocks;
                    }
                }
                list.Add(pack);
            }
            return list;
        }

        /// <summary>
        /// A pack's 0x33320002 body is either raw texels (every map and Global pack here -
        /// the record offsets index the body directly, and our .dds output is already
        /// byte-identical to the AssetDumper oracle there) or a CHAIN OF JDLZ BLOCKS, one per
        /// record in table order. All Cars\...\TEXTURES.BIN are the second kind:
        /// Cars\TEXTURES.BIN carries exactly 353 blocks for its 353 records, they decode to
        /// 9,319,328 B against the 9,326,592 B the record table addresses, and every record's
        /// bytes then match the oracle's .dds (353/353, byte for byte). A block's compSize
        /// INCLUDES its own 16-byte header, so the next block starts at +compSize.
        /// Chained packs are scattered back into a blob at each record's own Offset, so the
        /// (blob, offset, size) pair the rest of the pipeline works with stays valid.
        /// </summary>
        byte[] ReadPixelBlob(NfsuChunk pc, List<NfsuTextureRecord> records, long end,
                             out int blocks, out int shortBlocks)
        {
            blocks = -1;
            shortBlocks = 0;
            var payload = RawFull(pc);
            int fill = 0;
            while (fill < payload.Length && payload[fill] == 0x11)
                fill++;
            uint magic = fill + 4 <= payload.Length ? U32(payload, fill) : 0u;
            if (magic != NfsuJdlz.JdlzMagic && magic != NfsuJdlz.RawwMagic)
                return RawBody(pc, (int)Math.Min(end, pc.Size));   // raw texels

            var blob = new byte[Math.Max(0, (int)end)];
            int pos = fill, i = 0;
            blocks = 0;
            while (pos + 16 <= payload.Length && i < records.Count)
            {
                uint m = U32(payload, pos);
                if (m != NfsuJdlz.JdlzMagic && m != NfsuJdlz.RawwMagic)
                    break;
                int csize = (int)U32(payload, pos + 12);
                if (csize < 16 || pos + csize > payload.Length)
                    break;
                int consumed;
                byte[] pix;
                try
                {
                    pix = NfsuJdlz.Decompress(payload, pos, csize, out consumed);
                }
                catch (Exception)
                {
                    break;
                }
                var rec = records[i];
                int n = Math.Min(pix.Length, rec.Size);
                if (n != pix.Length || n != rec.Size)
                    shortBlocks++;
                if (rec.Offset >= 0 && rec.Offset + n <= blob.Length)
                    Array.Copy(pix, 0, blob, rec.Offset, n);
                pos += csize;
                i++;
                blocks++;
            }
            return blob;
        }
    }
}
