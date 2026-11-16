// JDLZ decompression for NFS Underground .lzc files.
//
// The .lzc container is a 16-byte header plus an LZ77 stream; it is the ONLY way to read
// Tracks\TRACKB####.lzc (the per-route road/traffic data), because those route files are not
// plain chunk bundles. AssetDumper reads 0 resources from a .lzc, so this decoder is not
// optional for that data.
//
// Header (all little-endian):
//   +0x00 char[4] magic            'JDLZ' = compressed, 'RAWW' = stored, other = no header
//   +0x04 u8   version             0x02
//   +0x05 u8   headerSize          0x10, where the stream starts
//   +0x06 u16  reserved
//   +0x08 u32  decompressedSize
//   +0x0C u32  compressedSize
//
// The stream has TWO independent 1-bit flag queues, each refilled one byte at a time with the
// 0x100 sentinel marking "queue empty"; the refill test is `flags == 1`, checked every step.
// flag1 bit 0 = literal byte; flag1 bit 1 = back-reference from two bytes b0 b1, whose form is
// chosen by flag2's current bit (flag2 is shifted only on reference steps):
//   near (flag2 bit 1): len = (((b0 & 0xF0) << 4) | b1) + 3   dist = (b0 & 0x0F) + 1
//   far  (flag2 bit 0): len =  (b0 & 0x1F) + 3                dist = (((b0 & 0xE0) << 3) | b1) + 17
// Copies are byte-by-byte, so a copy may overlap the write cursor - that is how runs are stored.
//
// Measured on this install: all 31 real TRACKB*.lzc decode to exactly decompressedSize, consume
// the whole stream (1-2 trailing bytes), and the result parses as the same chunk container the
// .BUN files use. Two more facts that bite: Global\GlobalB.lzc carries NO header (it is byte
// identical to GlobalB.BUN), and Tracks\TRACKB1410.lzc is a 164-byte text stub starting "Scen".
using System;
using System.IO;

namespace NFSU
{
    public static class NfsuJdlz
    {
        public const uint JdlzMagic = 0x5A4C444A;   // bytes 'J','D','L','Z'
        public const uint RawwMagic = 0x57574152;   // bytes 'R','A','W','W'

        public struct Header
        {
            public bool Present;        // false = the file already holds decompressed data
            public bool Stored;         // 'RAWW': stored verbatim after the header
            public int Version;
            public int HeaderSize;
            public int DecompressedSize;
            public int CompressedSize;
        }

        public static Header ReadHeader(byte[] data)
        {
            return data == null ? new Header() : ReadHeaderAt(data, 0, data.Length);
        }

        static Header ReadHeaderAt(byte[] data, int at, int length)
        {
            var h = new Header();
            if (data == null || length < 16 || at < 0 || at + length > data.Length)
                return h;
            uint magic = (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) |
                                (data[at + 3] << 24));
            if (magic != JdlzMagic && magic != RawwMagic)
                return h;
            h.Present = true;
            h.Stored = magic == RawwMagic;
            h.Version = data[at + 4];
            h.HeaderSize = data[at + 5];
            h.DecompressedSize = (int)BitConverter.ToUInt32(data, at + 8);
            h.CompressedSize = (int)BitConverter.ToUInt32(data, at + 12);
            return h;
        }

        /// <summary>
        /// Decodes a .lzc buffer. <paramref name="consumed"/> reports how much of the input the
        /// stream needed: a short or overflowing stream means the codec is wrong, so the caller
        /// can report it instead of silently walking a truncated chunk tree.
        /// </summary>
        public static byte[] Decompress(byte[] data, out int consumed)
        {
            if (data == null)
                throw new InvalidDataException("JDLZ: null buffer");
            return Decompress(data, 0, data.Length, out consumed);
        }

        /// <summary>
        /// Same stream, decoded from an arbitrary offset of a bigger buffer - a texture pack's
        /// pixel chunk is a chain of these blocks (one per TPK record, each block's compSize
        /// INCLUDES its own 16-byte header, so the next block starts at +compSize).
        /// </summary>
        public static byte[] Decompress(byte[] data, int at, int length, out int consumed)
        {
            if (data == null || at < 0 || length < 16 || at + length > data.Length)
                throw new InvalidDataException("JDLZ: bad buffer window " + at + "+" + length);
            var h = ReadHeaderAt(data, at, length);
            if (!h.Present)
            {
                consumed = 0;
                var whole = new byte[length];
                Array.Copy(data, at, whole, 0, length);
                return whole;
            }
            if (h.HeaderSize < 16 || h.HeaderSize > length)
                throw new InvalidDataException("JDLZ bad headerSize " + h.HeaderSize);
            if (h.DecompressedSize < 0)
                throw new InvalidDataException("JDLZ bad decompressedSize " + h.DecompressedSize);
            if (h.Stored)
            {
                int n = Math.Min(h.DecompressedSize, length - h.HeaderSize);
                var stored = new byte[n];
                Array.Copy(data, at + h.HeaderSize, stored, 0, n);
                consumed = h.HeaderSize + n;
                return stored;
            }

            int stop = at + length;
            var outBuf = new byte[h.DecompressedSize];
            int pos = at + h.HeaderSize;
            int o = 0;
            int f1 = 1, f2 = 1;
            while (o < outBuf.Length)
            {
                if (f1 == 1)
                {
                    if (pos >= stop)
                        throw new InvalidDataException("JDLZ stream ended at " + pos);
                    f1 = data[pos++] | 0x100;
                }
                if (f2 == 1)
                {
                    if (pos >= stop)
                        throw new InvalidDataException("JDLZ stream ended at " + pos);
                    f2 = data[pos++] | 0x100;
                }
                if ((f1 & 1) != 0)
                {
                    if (pos + 1 >= stop)
                        throw new InvalidDataException("JDLZ stream ended at " + pos);
                    byte b0 = data[pos], b1 = data[pos + 1];
                    pos += 2;
                    int len, dist;
                    if ((f2 & 1) != 0)
                    {
                        len = (((b0 & 0xF0) << 4) | b1) + 3;
                        dist = (b0 & 0x0F) + 1;
                    }
                    else
                    {
                        len = (b0 & 0x1F) + 3;
                        dist = (((b0 & 0xE0) << 3) | b1) + 17;
                    }
                    if (dist > o)
                        throw new InvalidDataException(string.Format(
                            "JDLZ distance {0} past output end {1}", dist, o));
                    int start = o - dist;
                    for (int k = 0; k < len && o < outBuf.Length; k++)
                        outBuf[o++] = outBuf[start + k];
                    f2 >>= 1;
                }
                else
                {
                    if (pos >= data.Length)
                        throw new InvalidDataException("JDLZ stream ended at " + pos);
                    outBuf[o++] = data[pos++];
                }
                f1 >>= 1;
            }
            consumed = pos;
            return outBuf;
        }

        /// <summary>Reads a .lzc (or a plain bundle) from disk and returns the chunk stream.</summary>
        public static byte[] DecompressFile(string path, out int consumed)
        {
            return Decompress(File.ReadAllBytes(path), out consumed);
        }
    }
}
