using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NFSU
{
    /// <summary>
    /// Reads NFSU car bundles: <c>Cars\&lt;NAME&gt;\GEOMETRY.BIN</c>.
    /// <para>
    /// A car bundle is the same flat chunk container as a track bundle, but its drawables use a
    /// 36-byte vertex record (position, normal, D3DCOLOR, uv) instead of the scenery 24-byte one,
    /// and they carry two extra tables the map reader never sees:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>0x0013401A</c> - the socket/frame table: 80-byte records (<c>u32 key</c>,
    /// three filler dwords, then a DirectX row-major 4x4 whose translation sits in the last row).
    /// The same four keys (0x31A66786, 0xBF700A79, 0x9DB90133, 0xD09091C6) recur in most car
    /// bundles, and they also show up on headlight/brakelight parts at those parts' own local
    /// positions, so the key carries no wheel semantics - the positions decide. On a traffic body
    /// they sit at the four corners, and a corner usually has TWO of them with the same x and z but
    /// different lateral offsets (4DR_SEDAN 0.544 / 0.649 m, BUS 0.850 / 1.100, FIRETRUCK 0.850 /
    /// 1.100, SUV 0.600 / 0.800): the tyre mesh occupies y from its own origin out to y + width, and
    /// only the OUTER candidate puts the tyre's outer face at or beyond the body skin in the wheel
    /// region. Measured over all 56 corners of the 14 traffic bundles that have a frame table,
    /// against the body's own skin inside the footprint the tyre covers (nfs_probe/
    /// car_sidewall_fit.py): the outer choice leaves the sidewall flush with or outside the skin in
    /// 43 corners and within 2 cm of it in 48, never buried deeper than 0.076 m (median +0.087 m
    /// proud); re-centring the tyre mesh on its own mid-plane gives a median of -0.072 m, and
    /// picking the inner candidate moves the outer face inboard by 0.10-0.25 m on every paired
    /// corner. Rows also come out duplicated (identical key and position), hence <see cref="DedupeFrames"/>.
    /// The matrices themselves are pure rotations (front determinant +1, rear -0.926 with the y
    /// column negated), so the side is carried by the position sign plus a mirrored copy of the
    /// mesh, not by the matrix.</description></item>
    /// <item><description><c>0x80034020</c> - the physics block: <c>0x00039200</c> (88 B, the physics
    /// name, e.g. "4DR_SEDAN_CV"), <c>0x00039201</c> (8 x 48 B corner records, the corner f32[3] at
    /// +16) and <c>0x00039202</c> (a u16 table, meaning open). The corner box is the car's collision
    /// envelope: slightly smaller than the body mesh (mirrors stick out of the mesh, not of the box).
    /// Its bottom plane is where the shipped game rests the car, but it is NOT a precise ground
    /// plane: measured per car, the lowest wheel bottom there (socket z - tyre radius) differs from
    /// the box floor by -0.32 .. +0.21 m (4DR_SEDAN -0.004, COUPE +0.002, FIRETRUCK -0.009,
    /// AMBULANCE 0.000, 4DR_SEDAN02 -0.022, BUS -0.061, TAXI02 +0.028, TAXI +0.040, HATCHBACK02
    /// +0.055, MINIVAN +0.094, SUV +0.170, PICKUP +0.182, PANELVAN +0.219, HATCHBACK +0.210,
    /// PARCELVAN -0.318). Four cars store a box floor of exactly 0.0. The body mesh tells a steadier
    /// story: its lowest vertex is within +/-6 cm of the model's z = 0 plane on all 15 cars.
    /// Wheel height therefore comes from the socket itself; <c>NfsuTrafficCarRig.GroundOffset</c>
    /// (the box bottom) is the placement a driver uses, <c>ContactOffset</c> (the wheel bottom) the
    /// number the wheel rule is judged by.</description></item>
    /// </list>
    /// The 4x4 in <c>0x00134011</c> (the same field the scenery drawables have) is NOT applied to
    /// the geometry. Read as 9 floats it does not even look like a rotation for cars - the field is
    /// a full 4x4 with 4-float columns at @64/@80/@96/@112 - and it carries an export-time scale:
    /// 1.618 on 4DR_SEDAN's body, 1.602 on its tyre, 1.000 on the SUV, 1.048 on the NEON wheel.
    /// Applying it would make the visual size disagree with the physics box, which matches the raw
    /// vertex extents, and the raw tyre diameters (0.58-1.17 m) are already correct metres. So the
    /// vertices are used as-is, exactly like the map builder does with scenery geometry.
    /// </summary>
    public sealed class NfsuCarBundle
    {
        public const uint ChunkModelPack = 0x80134001;
        public const uint ChunkModelSource = 0x00134002;      // source path, e.g. "..\PC\CD\CARS\4DR_SEDAN\GEOMETRY.BIN"
        public const uint ChunkModelLodList = 0x00134003;     // u32 key + u32 0 per drawable
        public const uint ChunkModelLookup = 0x00134004;      // (BinKey, file offset, chunk size, 0, 0)
        public const uint ChunkFrameTable = 0x0013401A;       // 80-byte socket records
        public const uint ChunkPhysics = 0x80034020;
        public const uint ChunkPhysicsName = 0x00039200;      // 88 B, holds e.g. "4DR_SEDAN_CV"
        public const uint ChunkPhysicsCorners = 0x00039201;   // 8 x 48 B, corner f32[3] at +16
        public const uint ChunkPhysicsLinks = 0x00039202;     // u16 table, meaning open

        public const int FrameRecord = 80;
        public const int CornerRecord = 48;

        /// <summary>One 80-byte socket record. <see cref="M"/> is row-major with the translation in row 3.</summary>
        public sealed class NfsuCarFrame
        {
            public uint Key;
            public readonly float[] M = new float[16];

            public float X { get { return M[12]; } }
            public float Y { get { return M[13]; } }
            public float Z { get { return M[14]; } }

            /// <summary>3x3 part only, row-major (its determinant is -1 on mirrored sockets).</summary>
            public float[] Rot3()
            {
                return new[] { M[0], M[1], M[2], M[4], M[5], M[6], M[8], M[9], M[10] };
            }
        }

        /// <summary>One drawable plus the socket table attached to it and its LOD name split.</summary>
        public sealed class NfsuCarPart
        {
            public NfsuDrawable Drawable;
            public readonly List<NfsuCarFrame> Frames = new List<NfsuCarFrame>();

            /// <summary>Drawable name without the trailing <c>_A</c>/<c>_B</c>/<c>_C</c> LOD suffix.</summary>
            public string Group = "";
            public char Lod = '?';
            public bool IsWheel;
            public bool IsBody;
        }

        public readonly string FilePath;
        public readonly string CarName;
        public string SourcePath = "";
        public string PhysicsName = "";

        public readonly List<NfsuCarPart> Parts = new List<NfsuCarPart>();
        public readonly List<float[]> PhysicsCorners = new List<float[]>();

        /// <summary>min.xyz, max.xyz of the physics corner box, in the same Z-up metres as the mesh.</summary>
        public readonly float[] PhysicsBox = new float[6];
        public bool HasPhysicsBox;

        public int VertexCount;
        public int TriangleCount;
        public int UnsupportedParts;

        public NfsuCarBundle(string path)
        {
            FilePath = path;
            CarName = Path.GetFileName(Path.GetDirectoryName(path) ?? path);
            var bundle = new NfsuBundle(path);

            foreach (var c in bundle.Walk())
            {
                if (c.Magic == ChunkModelSource)
                    SourcePath = ReadText(Raw(bundle, c));
            }

            foreach (var c in bundle.Walk())
            {
                if (c.Magic != NfsuBundle.ChunkDrawable)
                    continue;
                var d = bundle.ReadDrawable(c);
                var p = new NfsuCarPart { Drawable = d };
                SplitLod(d.Name, out p.Group, out p.Lod);
                p.IsWheel = d.Name.IndexOf("TIRE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            d.Name.IndexOf("WHEEL", StringComparison.OrdinalIgnoreCase) >= 0;
                p.IsBody = d.Name.IndexOf("BODY", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (var f in c.Kids(ChunkFrameTable))
                    p.Frames.AddRange(ReadFrames(Raw(bundle, f)));
                DedupeFrames(p.Frames);
                if (d.VertCount == 0 || d.Unsupported)
                {
                    UnsupportedParts++;
                    continue;
                }
                if (d.Stride != 36)
                {
                    // Cars are stride 36 (position, normal, colour, uv). 9386 of the 9390 drawables
                    // in the install read that way; the three exceptions are SUPRA headlight LOD-C
                    // parts, which the stride scan mis-reads as 24 because their payload is tiny.
                    UnsupportedParts++;
                    continue;
                }
                Parts.Add(p);
                VertexCount += d.VertCount;
                TriangleCount += d.TriCount;
            }

            foreach (var c in bundle.Walk())
            {
                if (c.Magic == ChunkPhysicsName)
                    PhysicsName = ReadText(Raw(bundle, c));
                if (c.Magic != ChunkPhysicsCorners)
                    continue;
                foreach (var corner in ReadCorners(Raw(bundle, c)))
                {
                    PhysicsCorners.Add(corner);
                    for (int k = 0; k < 3; k++)
                    {
                        if (!HasPhysicsBox)
                        {
                            PhysicsBox[k] = corner[k];
                            PhysicsBox[3 + k] = corner[k];
                        }
                        else
                        {
                            PhysicsBox[k] = Math.Min(PhysicsBox[k], corner[k]);
                            PhysicsBox[3 + k] = Math.Max(PhysicsBox[3 + k], corner[k]);
                        }
                    }
                    // Set the flag only after the whole corner went through, otherwise the first
                    // corner seeds the box from axis 0 alone and axes 1-2 stay at 0 - which made
                    // the box floor read 0 for every car whose underside sits above the ground.
                    HasPhysicsBox = true;
                }
            }
        }

        /// <summary>
        /// A traffic-car bundle has only body and tyre parts (4-5 drawables, 1.2-1.7 k verts).
        /// Player-car bundles (NEON, 240SX, ...) hold hundreds of KIT/STYLE parts and are not
        /// what the ambient-traffic importer wants.
        /// </summary>
        public bool LooksLikeTraffic()
        {
            if (Parts.Count == 0 || Parts.Count > 12)
                return false;
            bool body = false, tyre = false;
            foreach (var p in Parts)
            {
                if (!p.IsWheel && !p.IsBody)
                    return false;
                if (p.IsWheel) tyre = true;
                if (p.IsBody) body = true;
            }
            return body && tyre;
        }

        // ------------------------------------------------------------------ tables

        /// <summary>
        /// Frame table: right-aligned body of 80-byte records, the leading slack is 0x11/0x00 fill.
        /// A record is accepted only when its 4x4 reads as a transform (m[3] == m[7] == m[11] == 0,
        /// m[15] == 1) and its key dword is non-zero, which is what makes the record count
        /// unambiguous instead of trusting the leading run of 0x11.
        /// </summary>
        static List<NfsuCarFrame> ReadFrames(byte[] raw)
        {
            var outList = new List<NfsuCarFrame>();
            for (int n = raw.Length / FrameRecord; n > 0; n--)
            {
                int lead = raw.Length - n * FrameRecord;
                if (!IsFill(raw, 0, lead))
                    continue;
                var recs = new List<NfsuCarFrame>(n);
                bool ok = true;
                for (int r = 0; r < n; r++)
                {
                    int o = lead + r * FrameRecord;
                    uint key = BitConverter.ToUInt32(raw, o);
                    var f = new NfsuCarFrame { Key = key };
                    for (int k = 0; k < 16; k++)
                        f.M[k] = BitConverter.ToSingle(raw, o + 16 + 4 * k);
                    if (key == 0 || f.M[3] != 0f || f.M[7] != 0f || f.M[11] != 0f || f.M[15] != 1f)
                    {
                        ok = false;
                        break;
                    }
                    recs.Add(f);
                }
                if (ok)
                    return recs;
            }
            return outList;
        }

        /// <summary>
        /// The frame tables repeat some sockets verbatim (4DR_SEDAN lists all 6 of them twice).
        /// Duplicates say nothing, and they would double the candidate set the wheel rule has to
        /// choose from, so they are dropped right after reading.
        /// </summary>
        static void DedupeFrames(List<NfsuCarFrame> frames)
        {
            for (int i = frames.Count - 1; i > 0; i--)
            {
                for (int j = 0; j < i; j++)
                {
                    if (!SameSocket(frames[i], frames[j]))
                        continue;
                    frames.RemoveAt(i);
                    break;
                }
            }
        }

        static bool SameSocket(NfsuCarFrame a, NfsuCarFrame b)
        {
            for (int k = 0; k < 16; k++)
                if (Math.Abs(a.M[k] - b.M[k]) > 1e-4f)
                    return false;
            return true;
        }

        /// <summary>
        /// Physics corner records: 8 x 48 B with the f32[3] corner at +16 of each record (the first
        /// four dwords are a small header). Right-aligned body, fill lead, and each corner must be a
        /// plausible car coordinate.
        /// </summary>
        static List<float[]> ReadCorners(byte[] raw)
        {
            var outList = new List<float[]>();
            for (int n = raw.Length / CornerRecord; n > 0; n--)
            {
                int lead = raw.Length - n * CornerRecord;
                if (!IsFill(raw, 0, lead))
                    continue;
                var pts = new List<float[]>(n);
                bool ok = true;
                for (int r = 0; r < n; r++)
                {
                    int o = lead + r * CornerRecord + 16;
                    var p = new[]
                    {
                        BitConverter.ToSingle(raw, o),
                        BitConverter.ToSingle(raw, o + 4),
                        BitConverter.ToSingle(raw, o + 8),
                    };
                    if (!Finite(p[0]) || !Finite(p[1]) || !Finite(p[2]) ||
                        Math.Abs(p[0]) > 30f || Math.Abs(p[1]) > 30f || Math.Abs(p[2]) > 30f ||
                        (Math.Abs(p[0]) < 1e-3f && Math.Abs(p[1]) < 1e-3f && Math.Abs(p[2]) < 1e-3f))
                    {
                        ok = false;
                        break;
                    }
                    pts.Add(p);
                }
                if (ok)
                    return pts;
            }
            return outList;
        }

        // ------------------------------------------------------------------ helpers

        static byte[] Raw(NfsuBundle b, NfsuChunk c)
        {
            var buf = new byte[c.Size];
            Array.Copy(b.Data, c.Payload, buf, 0, c.Size);
            return buf;
        }

        static bool IsFill(byte[] b, int i, int count)
        {
            for (int k = 0; k < count; k++)
                if (b[i + k] != 0x11 && b[i + k] != 0x00)
                    return false;
            return true;
        }

        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        static string ReadText(byte[] raw)
        {
            int start = 0;
            while (start < raw.Length && (raw[start] == 0x11 || raw[start] == 0x00))
                start++;
            int end = start;
            while (end < raw.Length && raw[end] != 0)
                end++;
            return Encoding.ASCII.GetString(raw, start, end - start);
        }

        static void SplitLod(string name, out string group, out char lod)
        {
            group = name;
            lod = '?';
            if (name.Length >= 3 && name[name.Length - 2] == '_')
            {
                char c = name[name.Length - 1];
                if (c >= 'A' && c <= 'Z')
                {
                    group = name.Substring(0, name.Length - 2);
                    lod = c;
                }
            }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0}: {1} parts, {2} verts, {3} tris, {4} unsupported",
                CarName, Parts.Count, VertexCount, TriangleCount, UnsupportedParts);
            if (HasPhysicsBox)
                sb.AppendFormat(CultureInfo.InvariantCulture, ", box x[{0:0.00},{1:0.00}] y[{2:0.00},{3:0.00}] z[{4:0.00},{5:0.00}]",
                    PhysicsBox[0], PhysicsBox[3], PhysicsBox[1], PhysicsBox[4], PhysicsBox[2], PhysicsBox[5]);
            sb.AppendFormat(CultureInfo.InvariantCulture, ", physics \"{0}\", source \"{1}\"", PhysicsName, SourcePath);
            return sb.ToString();
        }
    }
}
