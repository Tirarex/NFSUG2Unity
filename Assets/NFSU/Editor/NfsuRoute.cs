// One race route / district of NFS Underground: Tracks\TRACKB####.lzc.
//
// A .lzc is a JDLZ stream (see NfsuJdlz) whose payload is the same flat chunk container the .BUN
// bundles use, so NfsuBundle can walk it unchanged. What it holds is the stuff that is NOT in any
// .BUN: the route's road network, the ambient-traffic corridor list, crashable/zone tables, fog
// zones and the named street-light / effect lists (which is why AssetDumper's --export-lights
// yields nothing: it never opens a .lzc).
//
// Every stride and field offset below is the shipped loader's own, read out of Speed.exe v1.4
// (32-bit, image base 0x400000) - not fitted to the data:
//   0x441380  0x00034130 / 131 / 132 / 133 / 134: strides 8, 16, 32, 2 and the bbox record; the
//             bodies of 130 / 131 / 134 start at a 16-byte aligned address (the loader aligns
//             them) while 132 / 133 start flush at the payload. The loader also force-writes -1
//             into every 0x00034132 record's u16 at offset 6, i.e. that u16 is a link the game
//             recomputes, not data.
//   0x442b70  0x00034121 is a CHAIN, not an array: slot = i16 @+0x0A, list id = u32 @+0x0C,
//             delta = i16 @+0x10, next record = p + 0x27c + 0x88 * delta. Verified twice over:
//             the chain lands on offsets that independently look like records (16 of them in
//             TRACKB1001 by a content scan), the slots it yields increase monotonically
//             (1,3,4,5,6,7,8,11,12,...) and the walk ends just past the payload.
//   0x4442a0  0x00034107 = 56-byte records (magic divide 0x24924925 -> /56), 0x00034108 = 12 B.
//   0x42ded   0x00034122 / 23 / 24 = 32 / 368 / 264-byte records (magic divides 0xB21642C9,
//             0x3E0F83E1).
//
// The road network (settled numerically, see GridIndexStats):
//   0x00034131 = a sparse grid of 32 m cells: f32 x, f32 y (cell CENTRE, always a multiple of 32
//                offset by 16), u32 start, u32 count; 999999.0 marks an unused cell.
//   0x00034133 = the u16 array the cells index into; its values are 0x00034132 record indices.
//   0x00034132 = one ROAD PATCH. Its four corners are i16 x at +0x08,+0x0A,+0x0C,+0x0E and i16 y at
//                +0x10,+0x12,+0x14,+0x16, in 1/8 metre units, ring order A-B-C-D. A and D are
//                adjacent along one side edge, B = A + w and C = D + w: the deviation from that
//                parallelogram is 0.06 m at the median and the angle at A is 89.6 deg at the median,
//                with mean |w| = 3.5 m (a lane) and mean patch length |D-A| = 5.5 m. 9336/12253
//                patches have BOTH their far corners equal to another patch's A corner, so the quads
//                tile a continuous surface - they are drawn as stored, no assumed width.
//   Proof of the scale and the offsets: every patch's first corner, and in fact all four corners,
//   lie within half a cell of some cell that lists that patch - 12253/12253 in TRACKB1001, also
//   21591/21591 (1104), 3199/3199 (1099), 582/582 (1301). Nothing else in the record is a coordinate.
//   A cell lists a patch once per cell it touches, so "the first point must be in the listing cell"
//   is the WRONG test (it reports 12302/17158 pairs and a 71 m worst error on correct data).
//   Each route file carries its OWN patches (31 distinct blobs for 31 files): it is the road surface
//   of that circuit, not the whole city repeated.
//   The four u16 fields at +0x18..+0x1E are not connectivity: tested as link indices (raw, low byte,
//   high byte) they score 0.0% endpoint agreement. Their meaning, like +0x00 (always a multiple of
//   0x80), +0x02 (28 distinct) and +0x04 (0..94), is still open.
//
// Coordinates are in the game's Z-up metre frame, the same one the scenery bundles use: the
// route bbox union over all 31 files is x -1384..2446, y -1930..898 against STREAML1RA.BUN's
// 21 461 instance positions at x -741..2475, y -3070..2133. Traffic corridor midpoints sit a
// median 14 m from the nearest RA scenery instance, so they really do run along the streets.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NFSU
{
    /// <summary>One ambient-traffic corridor: a straight A-B link plus the vehicle set that uses it.</summary>
    public sealed class NfsuTrafficSegment
    {
        public int Offset;          // offset of the record inside the 0x00034121 payload
        public int Slot;            // i16 @+0x0A, index into the game's fixed table
        public int ListId;          // u32 @+0x0C, which of the game's lists it is linked into
        public int Delta;           // i16 @+0x10, chain step (next = p + 0x27c + 0x88 * delta)
        public float Ax, Ay;        // game-space XY of the first end
        public float Bx, By;        // game-space XY of the second end
        public float Value;         // f32 @+0x38, 3.1..29.6 over RA - a speed class, not a length
        public string Name = "";    // char[40] @+0x3C, traffic-vehicle set id
    }

    /// <summary>One cell of the route's spatial index (0x00034131). Coordinates are the cell centre.</summary>
    public sealed class NfsuGridCell
    {
        public float X, Y;          // 999999.0 = unused cell sentinel
        public int Start;
        public int Count;
    }

    /// <summary>Measured quality of one route file's spatial index (NfsuRoute.GridIndexStats).</summary>
    public sealed class NfsuGridIndexStats
    {
        public int Links, Cells, Pairs;
        public int FirstCornerInside;       // patches whose first corner is in a listing cell
        public int AllCornersInside;        // patches whose four corners all are
        public int PairsBeyondCell;         // (cell, patch) pairs further than half a cell from the patch
        public float WorstPair, MedianPair, P90Pair;
    }

    /// <summary>
    /// One road patch (0x00034132): four corners in 1/8 metre units plus the raw u16 fields.
    /// A and D are neighbours along one side edge of the patch (D of one record is A of the next),
    /// B = A + w and C = D + w are the other side, so the corners ring A-B-C-D and the mean |w| is a
    /// lane (3.5 m measured over TRACKB1001, mean patch length 5.5 m).
    /// </summary>
    public sealed class NfsuRoadLink
    {
        public int Index;
        public readonly int[] Units = new int[16];   // the record verbatim, as unsigned u16
        public readonly float[] X = new float[4];    // i16 @+0x08..+0x0E divided by 8
        public readonly float[] Y = new float[4];    // i16 @+0x10..+0x16 divided by 8

        public float Length
        {
            get
            {
                float sum = 0;
                for (int k = 0; k < 3; k++)
                {
                    float dx = X[k + 1] - X[k], dy = Y[k + 1] - Y[k];
                    sum += (float)Math.Sqrt(dx * dx + dy * dy);
                }
                return sum;
            }
        }
    }

    /// <summary>One row of the per-file chunk inventory (what the parity diff compares).</summary>
    public sealed class NfsuRouteChunkRow
    {
        public int Depth;
        public uint Magic;
        public int Size;
        public int Lead;            // leading 0x11 run inside the payload
        public int Count = -1;      // record count when the stride is known, else -1
    }

    public sealed class NfsuRoute
    {
        public const uint ChunkZones = 0x00034107;
        public const uint ChunkZoneHeader = 0x00034108;
        public const uint ChunkTraffic = 0x00034121;
        public const uint ChunkLinks = 0x00034122;
        public const uint ChunkLinkGroups = 0x00034123;
        public const uint ChunkLinkZones = 0x00034124;
        public const uint ChunkSectors = 0x00034130;      // 8 B: four u16 grid-cell ids
        public const uint ChunkGrid = 0x00034131;         // 16 B: cell centre + range into ChunkLinkIndex
        public const uint ChunkRoadLinks = 0x00034132;    // 32 B: four points at 1/8 m
        public const uint ChunkLinkIndex = 0x00034133;    // u16 array of ChunkRoadLinks indices
        public const uint ChunkBBox = 0x00034134;
        public const uint ChunkSpecial = 0x00034146;
        public const uint ChunkAreaTable = 0x0003B300;
        public const uint ChunkLights = 0x00135003;
        public const uint ChunkEffects = 0x00135102;

        /// <summary>Coordinate scale of a road link: the record's i16 units are 1/8 metre.</summary>
        public const float LinkUnit = 1f / 8f;

        /// <summary>Record size per chunk id, straight from the loader's size->count math.</summary>
        static readonly Dictionary<uint, int> Strides = new Dictionary<uint, int>
        {
            { ChunkSectors, 8 },
            { ChunkGrid, 16 },
            { ChunkRoadLinks, 32 },
            { ChunkLinkIndex, 2 },
            { ChunkZones, 56 },
            { ChunkLinks, 32 },
            { ChunkLinkGroups, 368 },
            { ChunkLinkZones, 264 },
            { ChunkLights, 96 },
            { ChunkEffects, 80 },
        };

        public readonly string Path;
        public readonly int RouteId;
        public readonly byte[] Data;
        public readonly NfsuBundle Bundle;
        public readonly int Consumed;         // bytes of the file the JDLZ stream needed

        public NfsuRoute(string path)
        {
            Path = path;
            string name = System.IO.Path.GetFileName(path);
            int id = 0;
            foreach (char c in name)
                if (c >= '0' && c <= '9')
                    id = id * 10 + (c - '0');
            RouteId = id;
            int consumed;
            Data = NfsuJdlz.DecompressFile(path, out consumed);
            Consumed = consumed;
            Bundle = new NfsuBundle(path, Data);
        }

        /// <summary>Every TRACKB####.lzc in the game install that really carries a route.</summary>
        public static List<string> FindRouteFiles()
        {
            var outFiles = new List<string>();
            string tracks = FindTracksDir();
            if (tracks == null)
                return outFiles;
            foreach (var f in Directory.GetFiles(tracks, "TRACKB*.lzc"))
            {
                byte[] head = new byte[4];
                using (var fs = File.OpenRead(f))
                    if (fs.Read(head, 0, 4) != 4)
                        continue;
                uint magic = (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24));
                if (magic == NfsuJdlz.JdlzMagic || magic == NfsuJdlz.RawwMagic)
                    outFiles.Add(f.Replace("\\", "/"));
            }
            outFiles.Sort(StringComparer.OrdinalIgnoreCase);
            return outFiles;
        }

        static string FindTracksDir()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string candidate = System.IO.Path.Combine(
                    System.IO.Path.Combine(dir.FullName, "Need for Speed Underground"), "Tracks");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        // ------------------------------------------------------------------ chunks
        public List<NfsuRouteChunkRow> Inventory()
        {
            var rows = new List<NfsuRouteChunkRow>();
            foreach (var c in Bundle.Top)
                Walk(c, 0, rows);
            return rows;
        }

        void Walk(NfsuChunk c, int depth, List<NfsuRouteChunkRow> rows)
        {
            bool container = (c.Magic & 0x80000000u) != 0 || c.Children.Count > 0;
            var row = new NfsuRouteChunkRow { Depth = depth, Magic = c.Magic, Size = c.Size };
            if (container)
            {
                rows.Add(row);
            }
            else
            {
                row.Lead = LeadFill(c.Payload, c.Size);
                int stride;
                if (Strides.TryGetValue(c.Magic, out stride) && stride > 0)
                    row.Count = (c.Size - row.Lead) / stride;
                rows.Add(row);
            }
            foreach (var k in c.Children)
                Walk(k, depth + 1, rows);
        }

        /// <summary>First chunk with this id: (payload offset, size) or false.</summary>
        public bool First(uint magic, out long payload, out int size)
        {
            foreach (var c in Bundle.Walk())
                if (c.Magic == magic)
                {
                    payload = c.Payload;
                    size = c.Size;
                    return true;
                }
            payload = 0;
            size = 0;
            return false;
        }

        /// <summary>
        /// Length of the leading 0x11 run of a payload. The bundle format keeps that slack at the
        /// front (same reason as the .BUN body padding); for the route chunks it is what makes the
        /// body land on the 16-byte address the loader demands.
        /// </summary>
        int LeadFill(long payload, int size)
        {
            int i = 0;
            while (i < size && Bundle.Data[(int)payload + i] == 0x11)
                i++;
            return i;
        }

        // ------------------------------------------------------------------ bbox
        /// <summary>
        /// Route bounding box from 0x00034134: f32[3] min at the body start, then a gap, then
        /// f32[3] max at body+16 (the loader copies those six floats to its route descriptor at
        /// +0x30 and +0x40). Z reads as exactly -127/+127 in every file, so Z is a default, not
        /// measured elevation.
        /// </summary>
        public bool TryBBox(out float[] mn, out float[] mx)
        {
            long payload;
            int size;
            mn = mx = null;
            if (!First(ChunkBBox, out payload, out size))
                return false;
            int lead = LeadFill(payload, size);
            if (size < lead + 28)
                return false;
            mn = new[] { F32(payload + lead), F32(payload + lead + 4), F32(payload + lead + 8) };
            mx = new[] { F32(payload + lead + 16), F32(payload + lead + 20), F32(payload + lead + 24) };
            return true;
        }

        // ------------------------------------------------------------------ traffic corridors
        /// <summary>
        /// The traffic corridor chain (0x00034121), walked exactly the way the game links it.
        /// Records whose name and points are all zero are unused slots of the fixed table; they are
        /// returned too, so the count matches the loader's view.
        /// </summary>
        public List<NfsuTrafficSegment> TrafficSegments()
        {
            var list = new List<NfsuTrafficSegment>();
            long payload;
            int size;
            if (!First(ChunkTraffic, out payload, out size))
                return list;
            long end = payload + size;
            long p = payload;
            while (p + 0x7C <= end)
            {
                var s = new NfsuTrafficSegment
                {
                    Offset = (int)(p - payload),
                    Slot = BitConverter.ToInt16(Bundle.Data, (int)(p + 0x0A)),
                    ListId = (int)BitConverter.ToUInt32(Bundle.Data, (int)(p + 0x0C)),
                    Delta = BitConverter.ToInt16(Bundle.Data, (int)(p + 0x10)),
                    Ax = F32(p + 0x20),
                    Ay = F32(p + 0x24),
                    Bx = F32(p + 0x28),
                    By = F32(p + 0x2C),
                    Value = F32(p + 0x38),
                };
                s.Name = Name40(p + 0x3C);
                list.Add(s);
                p = p + 0x27c + 0x88 * (long)s.Delta;
            }
            return list;
        }

        // ------------------------------------------------------------------ road network
        public List<NfsuGridCell> Grid()
        {
            var list = new List<NfsuGridCell>();
            long payload;
            int size;
            if (!First(ChunkGrid, out payload, out size))
                return list;
            int lead = LeadFill(payload, size);
            long body = payload + lead;
            for (int i = 0; i < (size - lead) / 16; i++)
            {
                long at = body + i * 16;
                list.Add(new NfsuGridCell
                {
                    X = F32(at),
                    Y = F32(at + 4),
                    Start = (int)BitConverter.ToUInt32(Bundle.Data, (int)(at + 8)),
                    Count = (int)BitConverter.ToUInt32(Bundle.Data, (int)(at + 12)),
                });
            }
            return list;
        }

        public int RoadLinkCount()
        {
            long payload;
            int size;
            return First(ChunkRoadLinks, out payload, out size) ? size / 32 : 0;
        }

        public int LinkIndexCount()
        {
            long payload;
            int size;
            return First(ChunkLinkIndex, out payload, out size) ? size / 2 : 0;
        }

        /// <summary>
        /// The route's road links (0x00034132): four points each, i16 x at +0x08..+0x0E and i16 y at
        /// +0x10..+0x16, in 1/8 metre units. Body sits flush at the payload (the loader does not align
        /// this one) and count = size / 32.
        /// </summary>
        public List<NfsuRoadLink> RoadLinks()
        {
            var list = new List<NfsuRoadLink>();
            long payload;
            int size;
            if (!First(ChunkRoadLinks, out payload, out size))
                return list;
            for (int i = 0; i < size / 32; i++)
            {
                int at = (int)(payload + i * 32);
                var l = new NfsuRoadLink { Index = i };
                for (int k = 0; k < 16; k++)
                    l.Units[k] = BitConverter.ToUInt16(Bundle.Data, at + k * 2);
                for (int k = 0; k < 4; k++)
                {
                    l.X[k] = (short)l.Units[4 + k] * LinkUnit;
                    l.Y[k] = (short)l.Units[8 + k] * LinkUnit;
                }
                list.Add(l);
            }
            return list;
        }

        /// <summary>Link index list (0x00034133) the grid cells point into.</summary>
        public ushort[] LinkIndexList()
        {
            long payload;
            int size;
            if (!First(ChunkLinkIndex, out payload, out size))
                return Array.Empty<ushort>();
            var list = new ushort[size / 2];
            for (int i = 0; i < list.Length; i++)
                list[i] = BitConverter.ToUInt16(Bundle.Data, (int)(payload + i * 2));
            return list;
        }

        /// <summary>
        /// Honest metrics for the file's own spatial index. A cell lists a patch once per cell it
        /// touches, so "the first point must be in the cell that lists it" is the WRONG test. What is
        /// checked here: (a) every patch has its first corner, and in fact all four corners, inside at
        /// least one of the cells that list it; (b) the distance from each listing cell's centre to the
        /// patch ring A-B-C-D. (a) is what proves the 1/8-metre scale and the +0x08/+0x10 offsets.
        /// </summary>
        public NfsuGridIndexStats GridIndexStats()
        {
            const float half = 16f;         // half of a 32 m cell
            var cells = new List<NfsuGridCell>();
            foreach (var c in Grid())
                if (c.X < 99999f)
                    cells.Add(c);
            var index = LinkIndexList();
            var links = RoadLinks();
            var st = new NfsuGridIndexStats { Links = links.Count, Cells = cells.Count };

            var listed = new Dictionary<int, List<int>>();
            for (int ci = 0; ci < cells.Count; ci++)
            {
                var c = cells[ci];
                if (c.Start + c.Count > index.Length)
                    continue;
                for (int k = 0; k < c.Count; k++)
                {
                    int li = index[c.Start + k];
                    if (li >= links.Count)
                        continue;
                    List<int> l;
                    if (!listed.TryGetValue(li, out l))
                    {
                        l = new List<int>();
                        listed[li] = l;
                    }
                    l.Add(ci);
                }
            }
            foreach (var kv in listed)
            {
                var l = links[kv.Key];
                var cl = kv.Value;
                if (CornerInCell(l.X[0], l.Y[0], cl, cells, half))
                    st.FirstCornerInside++;
                bool all = true;
                for (int k = 0; k < 4; k++)
                    if (!CornerInCell(l.X[k], l.Y[k], cl, cells, half))
                    {
                        all = false;
                        break;
                    }
                if (all)
                    st.AllCornersInside++;
            }

            var dists = new List<float>();
            for (int ci = 0; ci < cells.Count; ci++)
            {
                var c = cells[ci];
                if (c.Start + c.Count > index.Length)
                    continue;
                for (int k = 0; k < c.Count; k++)
                {
                    int li = index[c.Start + k];
                    if (li >= links.Count)
                        continue;
                    st.Pairs++;
                    var l = links[li];
                    float d = float.MaxValue;
                    for (int s = 0; s < 3; s++)
                        d = Mathf.Min(d, SegDist(c.X, c.Y, l.X[s], l.Y[s], l.X[s + 1], l.Y[s + 1]));
                    dists.Add(d);
                    if (d > st.WorstPair)
                        st.WorstPair = d;
                    if (d > half)
                        st.PairsBeyondCell++;
                }
            }
            dists.Sort();
            if (dists.Count > 0)
            {
                st.MedianPair = dists[dists.Count / 2];
                st.P90Pair = dists[(int)(0.9f * dists.Count)];
            }
            return st;
        }

        static bool CornerInCell(float x, float y, List<int> cellIds, List<NfsuGridCell> cells, float half)
        {
            foreach (int ci in cellIds)
                if (Mathf.Abs(x - cells[ci].X) <= half && Mathf.Abs(y - cells[ci].Y) <= half)
                    return true;
            return false;
        }

        static float SegDist(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float l2 = dx * dx + dy * dy;
            if (l2 <= 1e-12f)
                return Mathf.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / l2);
            float qx = ax + t * dx, qy = ay + t * dy;
            return Mathf.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }

        // ------------------------------------------------------------------ helpers
        float F32(long at) { return BitConverter.ToSingle(Bundle.Data, (int)at); }

        string Name40(long at)
        {
            // char[40]: space-padded, and a record that is not used at all is 40 NULs. Trailing
            // padding is trimmed but a LEADING space is kept - two shipped records really do
            // store " GUHLER2KW207023", and treating that as an empty name would drop them.
            var sb = new StringBuilder(40);
            int baseIndex = (int)at;
            for (int i = 0; i < 40; i++)
            {
                byte b = Bundle.Data[baseIndex + i];
                sb.Append(b == 0 ? '\0' : (char)b);
            }
            return sb.ToString().TrimEnd('\0', ' ');
        }

        public static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    }
}
