// Builds Unity content (textures, materials, meshes, GameObjects) out of NFSU bundles.
//
// Coordinate conversion: the game is a right-handed Y-up D3D space, Unity is left-handed
// Y-up, so every point maps (x, y, z)_game -> (x, z, -y)_Unity. The same change of basis
// is applied to the stored 3x3 instance matrix, which keeps rotations consistent.
//
// Placement matrices can have a negative determinant (mirrored scenery, 2 033 of them in
// STREAML1RA). Negative scale renders badly in Unity, so those instances get a baked mesh
// variant instead: positions negated on X and triangle winding reversed, and the stored
// matrix gets its first column negated so the two mirror operations cancel out.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NFSU
{
    public enum NfsuAlphaMode
    {
        Auto,       // choose per texture from its TPK group id and the alpha bits actually stored in it
        Opaque,
        Cutout,
        Blend,
        Additive,
    }

    public sealed class NfsuImportOptions
    {
        public readonly List<string> BundlePaths = new List<string>();
        public string SectionFilter = "";            // "2601,2604" - empty means every section
        public string SkipPrefixes = "SKYDOME";      // mesh-name prefixes to leave out
        public string ColliderPrefixes = "TRS,TER";  // these get a MeshCollider
        public string RootName = "NFSU_MAP";
        public string ProjectFolder = "Assets/NFSU";

        public NfsuAlphaMode AlphaMode = NfsuAlphaMode.Auto;
        public bool UvFlipV = true;

        // The bundle's u16 index order is already Unity-compatible: Unity draws the side its
        // right-hand-rule normal faces, and that is the side NFSU draws too (measured: every
        // SKYDOME triangle, 720/720, has its RH normal pointing inward at the camera that sits
        // inside the dome, and terrain faces point up; the AssetDumper Collada writes the raw
        // positions and the raw index order under up_axis=Z_UP, which renders right in Blender).
        public bool FlipWinding;
        public bool BakeMirrorMeshes = true;

        // Unity transforms can only express rotation times axis-aligned scale, so a placement matrix
        // with shear cannot be expressed exactly; for those the whole matrix is baked into their own
        // mesh variant instead. Measured over RA's 21461 placements with the real Unity transform
        // (rotation * diagonal scale from the matrix columns): only 203 lose more than 2 cm and 17
        // more than 2 m, so about 1% of the placements get a baked mesh (~165 distinct ones).
        public bool BakeShearedPlacements = true;
        public float ShearToleranceM = 0.02f;

        // Every scenery instance record carries the world AABB of that instance in whole metres, so
        // the transform we build can be checked against the game's own numbers. This is the check
        // that catches matrix-layout mistakes (a row/column mix-up rotates a third of the city).
        public bool VerifyPlacementAABB = true;
        public bool SaveMeshAssets = true;
        public bool WriteTextures = true;
        public bool AddMeshColliders = true;
        public bool SetLayers = true;
        public bool MarkStatic = true;
        public bool ReplaceExistingRoot = true;
        public bool TexturesForSelectedOnly = true;   // only write the .dds the selected sections use
        public bool ClearGeneratedFirst;             // delete old generated .mat/.dds/.mesh assets
        public int MaxSections;                      // 0 = unlimited
    }

    public sealed class NfsuImportStats
    {
        /// <summary>
        /// How far a placement's box may stick out of the AABB stored in its own scenery record before
        /// it counts as wrong. Those boxes are whole-metre and padded (a correct pipeline still
        /// overhangs ~4% of them in RA), so 2 m is the honest bound - see <see cref="VerifyPlacement"/>.
        /// </summary>
        public const float AabbSlackM = 2.0f;

        public int SectionsImported;
        public int SectionsSeen;
        public int InstancesCreated;
        public int InstancesSkipped;
        public int MirroredInstances;
        public int MeshesBuilt;
        public int MirroredMeshes;
        public int ShearedInstances;           // placed with the exact matrix baked into their mesh
        public int BakedMeshes;
        public int MaterialsCreated;
        public int MaterialsOpaque;
        public int MaterialsCutout;
        public int MaterialsBlend;
        public int MaterialsAdditive;
        public int TexturesWritten;
        public int CollidersCreated;
        public int Vertices;
        public int Triangles;
        public int PlacedTriangles;
        public int UnsupportedDrawables;
        public int UnresolvedTypes;
        public int TexturesUnsupportedFormat;
        public int MissingMeshTextures;
        public int PlacementAabbChecked;       // placements verified against their stored world AABB
        public int PlacementAabbBad;
        public int PlacementDecisive;          // of those, the ones where a wrong matrix cannot fit
        public int PlacementDecisiveBad;
        public float WorstPlacementAabbErr;
        public readonly List<string> Notes = new List<string>();

        public void Note(string message)
        {
            if (Notes.Count < 300)
                Notes.Add(message);
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("NFSU import result");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  sections {0}/{1}   instances {2} (skipped {3}, mirrored {4})   colliders {5}",
                SectionsImported, SectionsSeen, InstancesCreated, InstancesSkipped, MirroredInstances, CollidersCreated));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  meshes {0} (+{1} mirrored, +{2} exact-matrix baked)   materials {3}   textures {4}   verts {5:N0}   tris {6:N0}",
                MeshesBuilt, MirroredMeshes, BakedMeshes, MaterialsCreated, TexturesWritten, Vertices, Triangles));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  placements: {0} instances, {1} mirrored, {2} baked with their exact matrix",
                InstancesCreated, MirroredInstances, ShearedInstances));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  material alpha modes: opaque {0}   cutout {1}   blend {2}   additive {3}",
                MaterialsOpaque, MaterialsCutout, MaterialsBlend, MaterialsAdditive));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  placement AABB containment: {0} checked, {1} out by more than {2:N1} m (worst {3:N2} m)" +
                " | decisive subset {4}, {5} out",
                PlacementAabbChecked, PlacementAabbBad, AabbSlackM, WorstPlacementAabbErr,
                PlacementDecisive, PlacementDecisiveBad));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  unsupported drawables {0}   unresolved type refs {1}   unwritten textures {2}   mesh texture slots with no texture {3}",
                UnsupportedDrawables, UnresolvedTypes, TexturesUnsupportedFormat, MissingMeshTextures));
            if (Notes.Count > 0)
            {
                sb.AppendLine("  notes:");
                foreach (var n in Notes)
                    sb.AppendLine("    " + n);
                if (Notes.Count >= 300)
                    sb.AppendLine("    (note list truncated at 300)");
            }
            return sb.ToString();
        }
    }

    public sealed class NfsuMapBuilder : IDisposable
    {
        const string TexSub = "Textures";
        const string MatSub = "Materials";
        const string MeshSub = "Meshes";

        // C maps game (x,y,z) to Unity (x,z,-y); BasisInverse is its transpose.
        static readonly float[] BasisUnityOfGame = { 1, 0, 0, 0, 0, 1, 0, -1, 0 };
        static readonly float[] BasisGameOfUnity = { 1, 0, 0, 0, 0, -1, 0, 1, 0 };

        static readonly string[][] PrefixLayers =
        {
            new[] { "TRS", "NFSU_Terrain" }, new[] { "TER", "NFSU_Terrain" },
            new[] { "EAR", "NFSU_Terrain" }, new[] { "GRO", "NFSU_Terrain" },
            new[] { "XWU", "NFSU_Wall" }, new[] { "XW", "NFSU_Wall" },
            new[] { "XB", "NFSU_Building" }, new[] { "ARC", "NFSU_Building" },
            new[] { "BUI", "NFSU_Building" }, new[] { "WHS", "NFSU_Building" },
            new[] { "VEN", "NFSU_Building" }, new[] { "HOT", "NFSU_Building" },
            new[] { "PRI", "NFSU_Building" }, new[] { "BIG", "NFSU_Building" },
            new[] { "SGN", "NFSU_Sign" }, new[] { "ZPM", "NFSU_Sign" }, new[] { "XS", "NFSU_Sign" },
            new[] { "XT", "NFSU_Decor" }, new[] { "XO", "NFSU_Decor" }, new[] { "XV", "NFSU_Decor" },
            new[] { "OBJ", "NFSU_Decor" }, new[] { "OAB", "NFSU_Decor" }, new[] { "OSA", "NFSU_Decor" },
            new[] { "OSB", "NFSU_Decor" }, new[] { "OSC", "NFSU_Decor" }, new[] { "OSF", "NFSU_Decor" },
            new[] { "XOU", "NFSU_Decor" }, new[] { "XSU", "NFSU_Decor" }, new[] { "XBU", "NFSU_Decor" },
        };

        readonly NfsuImportOptions opt;
        readonly NfsuImportStats stats = new NfsuImportStats();

        readonly Dictionary<uint, NfsuDrawable> drawables = new Dictionary<uint, NfsuDrawable>();
        readonly Dictionary<uint, TextureSource> texSources = new Dictionary<uint, TextureSource>();
        readonly Dictionary<uint, MeshVariant> meshCache = new Dictionary<uint, MeshVariant>();
        readonly Dictionary<string, Material> matCache = new Dictionary<string, Material>();
        readonly Dictionary<string, int> layerCache = new Dictionary<string, int>();
        readonly HashSet<uint> wantedTexKeys = new HashSet<uint>();
        // Texture BinKeys used by a drawable that has more than one texture slot: base + overlay
        // art, the strongest signal that an overlay's alpha is a light pass (see NfsuAlphaClassifier).
        readonly HashSet<uint> multiSlotTexKeys = new HashSet<uint>();
        readonly Dictionary<uint, NfsuAlphaClass> classCache = new Dictionary<uint, NfsuAlphaClass>();

        sealed class TextureSource
        {
            public NfsuTextureRecord Record;
            public byte[] Blob;
            public string AssetPath;
            public Texture2D Texture;
        }

        sealed class MeshVariant
        {
            public Mesh Base;
            public Mesh Mirrored;
            public readonly Dictionary<string, Mesh> Baked = new Dictionary<string, Mesh>();
            public Material[] Materials = Array.Empty<Material>();
            public bool Failed;
        }

        string[] skipPrefixes = Array.Empty<string>();
        string[] colliderPrefixes = Array.Empty<string>();
        bool cancelled;
        bool texturesDone;

        public NfsuImportStats Stats { get { return stats; } }
        public bool Cancelled { get { return cancelled; } }
        public int DrawableCount { get { return drawables.Count; } }
        public int TextureRecordCount { get { return texSources.Count; } }

        public NfsuMapBuilder(NfsuImportOptions options)
        {
            opt = options;
            skipPrefixes = SplitList(opt.SkipPrefixes);
            colliderPrefixes = SplitList(opt.ColliderPrefixes);
        }

        public void Dispose()
        {
            // Meshes are either project assets or objects the scene still references, so
            // nothing is destroyed here - caches only.
            meshCache.Clear();
            matCache.Clear();
        }

        /// <summary>
        /// Deletes the generated .mat / .dds / .mesh assets (Unity removes their .meta files with
        /// them). Used by verification runs: a material's name encodes its alpha mode, so stale
        /// assets from an earlier probe would otherwise keep being reused and hide the change.
        /// </summary>
        public void ClearGeneratedAssets()
        {
            if (!AssetDatabase.IsValidFolder(opt.ProjectFolder))
                return;
            int deleted = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var pair in new[]
                {
                    new[] { MatSub, "t:Material" }, new[] { TexSub, "t:Texture2D" },
                    new[] { MeshSub, "t:Mesh" },
                })
                {
                    string folder = opt.ProjectFolder + "/" + pair[0];
                    if (!AssetDatabase.IsValidFolder(folder))
                        continue;
                    foreach (string guid in AssetDatabase.FindAssets(pair[1], new[] { folder }))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (AssetDatabase.DeleteAsset(path))
                            deleted++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            stats.Note("cleared " + deleted + " previously generated assets under " + opt.ProjectFolder);
        }

        static string[] SplitList(string s)
        {
            if (string.IsNullOrEmpty(s))
                return Array.Empty<string>();
            var parts = s.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim();
            return parts;
        }

        static string Key(uint v) { return string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", v); }

        static bool StartsWithAny(string s, string[] prefixes)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            foreach (var p in prefixes)
                if (p.Length > 0 && s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ loading
        public void LoadBundles(List<NfsuSection> intoSections)
        {
            foreach (var path in opt.BundlePaths)
            {
                if (string.IsNullOrEmpty(path))
                    continue;
                if (!File.Exists(path))
                {
                    stats.Note("missing bundle: " + path);
                    continue;
                }
                ProgressBar("Parsing " + Path.GetFileName(path), 0f);
                var bundle = new NfsuBundle(path);
                int dCount = 0, sCount = 0, tCount = 0;
                foreach (var d in bundle.Drawables())
                {
                    if (d.Unsupported || d.VertCount == 0)
                    {
                        stats.UnsupportedDrawables++;
                        continue;
                    }
                    if (d.Stride != 24)
                    {
                        stats.UnsupportedDrawables++;
                        stats.Note(string.Format("vertex stride {0} not supported: {1} {2}",
                            d.Stride, Key(d.BinKey), d.Name));
                        continue;
                    }
                    if (d.Slots.Length >= 2)
                        foreach (var k in d.Slots)
                            if (k != 0)
                                multiSlotTexKeys.Add(k);
                    if (!drawables.ContainsKey(d.BinKey))
                    {
                        drawables.Add(d.BinKey, d);
                        dCount++;
                    }
                }
                foreach (var s in bundle.Sections())
                {
                    intoSections.Add(s);
                    sCount++;
                }
                foreach (var pack in bundle.TexturePacks())
                {
                    foreach (var r in pack.Records)
                    {
                        tCount++;
                        if (!texSources.ContainsKey(r.BinKey))
                            texSources.Add(r.BinKey, new TextureSource { Record = r, Blob = pack.Pixels });
                    }
                }
                stats.Note(string.Format("{0}: +{1} new meshes, {2} sections, {3} texture records",
                    Path.GetFileName(path), dCount, sCount, tCount));
            }
        }

        public string AnalyseReport(List<NfsuSection> sections)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "bundles {0}   unique meshes with geometry {1}   unsupported drawables {2}   texture records {3}",
                opt.BundlePaths.Count, drawables.Count, stats.UnsupportedDrawables, texSources.Count));

            var fmt = new SortedDictionary<int, int>();
            foreach (var ts in texSources.Values)
            {
                int f = ts.Record.Format;
                fmt[f] = fmt.ContainsKey(f) ? fmt[f] + 1 : 1;
            }
            sb.Append("texture formats:");
            foreach (var kv in fmt)
                sb.Append(string.Format("  {0} x{1}", NfsuDdsWriter.FormatName(kv.Key), kv.Value));
            sb.AppendLine();

            var ids = ParseIdFilter(opt.SectionFilter);
            int totalInstances = 0, shown = 0;
            sb.AppendLine("sections:");
            foreach (var s in sections)
            {
                bool accepted = ids.Count == 0 || ids.Contains((uint)s.Id);
                totalInstances += s.Instances.Count;
                if (!accepted || shown >= 60)
                    continue;
                shown++;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0}  types {1}  instances {2}", s.Id, s.Types.Count, s.Instances.Count));
            }
            if (shown == 60)
                sb.AppendLine("  ... (60 of " + sections.Count + " shown)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "sections {0}   instances total {1}", sections.Count, totalInstances));
            sb.AppendLine("selected by filter: " + CountSelected(sections, ids, opt.MaxSections));
            return sb.ToString();
        }

        string CountSelected(List<NfsuSection> sections, HashSet<uint> ids, int max)
        {
            int n = 0, inst = 0;
            foreach (var s in sections)
            {
                if (ids.Count > 0 && !ids.Contains((uint)s.Id))
                    continue;
                if (max > 0 && n >= max)
                    break;
                n++;
                inst += s.Instances.Count;
            }
            return n + " sections / " + inst + " instances";
        }

        // ------------------------------------------------------------------ textures
        /// <summary>Sections the options select: id filter first, then the MaxSections cap.</summary>
        List<NfsuSection> AcceptedSections(List<NfsuSection> sections, out int matching)
        {
            var ids = ParseIdFilter(opt.SectionFilter);
            var accepted = new List<NfsuSection>();
            matching = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (ids.Count > 0 && !ids.Contains((uint)s.Id))
                    continue;
                matching++;
                if (opt.MaxSections > 0 && accepted.Count >= opt.MaxSections)
                    continue;
                accepted.Add(s);
            }
            return accepted;
        }

        /// <summary>Texture BinKeys the accepted sections actually reference, so a batch import
        /// only writes the .dds files it needs instead of every pack record in the bundle.</summary>
        void CollectTextureKeys(List<NfsuSection> accepted)
        {
            wantedTexKeys.Clear();
            foreach (var sec in accepted)
            {
                foreach (var ins in sec.Instances)
                {
                    uint binKey = ins.TypeIndex < sec.Types.Count ? sec.Types[ins.TypeIndex].BinKey : 0u;
                    NfsuDrawable d;
                    if (binKey == 0 || !drawables.TryGetValue(binKey, out d))
                        continue;
                    if (StartsWithAny(d.Name, skipPrefixes))
                        continue;
                    foreach (var sm in d.Submeshes)
                    {
                        int slot = sm.Slot;
                        if (slot < 0 || slot >= d.Slots.Length || d.Slots[slot] == 0)
                            continue;
                        wantedTexKeys.Add(d.Slots[slot]);
                    }
                }
            }
        }

        public void ImportTextures(List<NfsuSection> sections = null)
        {
            if (!opt.WriteTextures || texturesDone)
                return;
            texturesDone = true;
            if (sections != null && opt.TexturesForSelectedOnly)
            {
                int matching;
                CollectTextureKeys(AcceptedSections(sections, out matching));
            }
            string folder = EnsureFolder(opt.ProjectFolder + "/" + TexSub);
            int written = 0, skipped = 0;
            var paths = new List<string>();
            var used = new HashSet<string>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kv in texSources)
                {
                    if (wantedTexKeys.Count > 0 && !wantedTexKeys.Contains(kv.Key))
                    {
                        skipped++;
                        continue;
                    }
                    if (ProgressBar("Writing textures", (float)(written + skipped) / Math.Max(1, texSources.Count)))
                    {
                        cancelled = true;
                        return;
                    }
                    var src = kv.Value;
                    var rec = src.Record;
                    byte[] dds = NfsuDdsWriter.BuildDds(rec, src.Blob);
                    if (dds == null)
                    {
                        stats.TexturesUnsupportedFormat++;
                        if (stats.TexturesUnsupportedFormat <= 8)
                            stats.Note(string.Format("texture {0} {1}: format {2} not written",
                                Key(rec.BinKey), rec.Name, NfsuDdsWriter.FormatName(rec.Format)));
                        continue;
                    }
                    string baseName = NfsuDdsWriter.RecordFileName(rec);
                    string name = baseName;
                    int n = 1;
                    while (!used.Add(name))
                        name = baseName + "_" + (n++);
                    string path = folder + "/" + name + ".dds";
                    File.WriteAllBytes(path, dds);
                    src.AssetPath = path;
                    paths.Add(path);
                    written++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            foreach (var kv in texSources)
            {
                if (string.IsNullOrEmpty(kv.Value.AssetPath))
                    continue;
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(kv.Value.AssetPath);
                if (t == null)
                    stats.Note("Unity did not import " + kv.Value.AssetPath);
                kv.Value.Texture = t;
            }
            stats.TexturesWritten = written;
        }

        // ------------------------------------------------------------------ materials
        Material GetMaterial(uint texKey)
        {
            TextureSource src = null;
            if (texKey != 0)
                texSources.TryGetValue(texKey, out src);
            if (src == null)
                stats.MissingMeshTextures++;

            NfsuAlphaClass cls;
            switch (opt.AlphaMode)
            {
                case NfsuAlphaMode.Opaque: cls = NfsuAlphaClass.Opaque; break;
                case NfsuAlphaMode.Cutout: cls = NfsuAlphaClass.Cutout; break;
                case NfsuAlphaMode.Blend: cls = NfsuAlphaClass.Blend; break;
                case NfsuAlphaMode.Additive: cls = NfsuAlphaClass.Additive; break;
                default:
                    // Auto. Without a texture that Unity actually imported the class would be
                    // guesswork, and a blended or additive material with no texture draws a flat
                    // coloured quad over whatever is behind it, so those stay solid surfaces.
                    cls = src != null && src.Texture != null
                        ? ClassOf(src)
                        : NfsuAlphaClass.Opaque;
                    break;
            }

            string mode = NfsuAlphaClassifier.ModeName(cls);
            string shaderName = NfsuAlphaClassifier.ShaderName(cls);

            string cacheKey = Key(texKey) + ":" + mode;
            Material cached;
            if (matCache.TryGetValue(cacheKey, out cached) && cached != null)
                return cached;

            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                stats.Note("shader not found: " + shaderName + " - import the .shader files first");
                return null;
            }

            string folder = EnsureFolder(opt.ProjectFolder + "/" + MatSub);
            string label = src != null && !string.IsNullOrEmpty(src.Record.Name)
                ? NfsuDdsWriter.Sanitize(src.Record.Name)
                : (texKey == 0 ? "no_texture" : "texture");
            string path = string.Format(CultureInfo.InvariantCulture, "{0}/0x{1:X8}_{2}_{3}.mat",
                folder, texKey, label, mode);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                if (src != null && src.Texture != null)
                    mat.mainTexture = src.Texture;
                if (cls == NfsuAlphaClass.Cutout)
                    mat.SetFloat("_Cutoff", 0.5f);
                AssetDatabase.CreateAsset(mat, path);
                stats.MaterialsCreated++;
                switch (cls)
                {
                    case NfsuAlphaClass.Cutout: stats.MaterialsCutout++; break;
                    case NfsuAlphaClass.Blend: stats.MaterialsBlend++; break;
                    case NfsuAlphaClass.Additive: stats.MaterialsAdditive++; break;
                    default: stats.MaterialsOpaque++; break;
                }
            }
            else
            {
                bool changed = false;
                if (mat.shader != shader)
                {
                    mat.shader = shader;
                    changed = true;
                }
                if (src != null && src.Texture != null && mat.mainTexture != src.Texture)
                {
                    mat.mainTexture = src.Texture;
                    changed = true;
                }
                if (cls == NfsuAlphaClass.Cutout && mat.HasProperty("_Cutoff") && Mathf.Abs(mat.GetFloat("_Cutoff") - 0.5f) > 1e-4f)
                {
                    mat.SetFloat("_Cutoff", 0.5f);
                    changed = true;
                }
                if (changed)
                    EditorUtility.SetDirty(mat);
            }
            matCache[cacheKey] = mat;
            return mat;
        }

        /// <summary>Alpha class of one texture record, measured once per record (Auto mode).</summary>
        NfsuAlphaClass ClassOf(TextureSource src)
        {
            uint key = src.Record.BinKey;
            NfsuAlphaClass cls;
            if (classCache.TryGetValue(key, out cls))
                return cls;
            cls = NfsuAlphaClassifier.Classify(src.Record, src.Blob, multiSlotTexKeys.Contains(key));
            classCache[key] = cls;
            return cls;
        }

        /// <summary>
        /// The same decision the Auto material mode makes, exposed for diagnostics: a parity dump of
        /// class + features per record is what proves the C# rule engine matches the Python model
        /// (NfsuSelfTest.DumpAlphaClasses vs nfs_probe/alpha_csv.py).
        /// </summary>
        public bool AlphaDecision(uint texKey, out NfsuTextureRecord rec, out byte[] blob,
                                  out bool multiSlot, out NfsuAlphaClass cls)
        {
            TextureSource src;
            if (texKey == 0 || !texSources.TryGetValue(texKey, out src))
            {
                rec = null;
                blob = null;
                multiSlot = false;
                cls = NfsuAlphaClass.Opaque;
                return false;
            }
            rec = src.Record;
            blob = src.Blob;
            multiSlot = multiSlotTexKeys.Contains(texKey);
            cls = NfsuAlphaClassifier.Classify(rec, blob, multiSlot);
            return true;
        }

        /// <summary>Texture BinKeys the importer holds records for (diagnostics / parity dumps).</summary>
        public List<uint> TextureKeys()
        {
            return new List<uint>(texSources.Keys);
        }

        // ------------------------------------------------------------------ meshes
        Mesh GetMesh(uint binKey, bool mirror, out Material[] materials)
        {
            MeshVariant mv;
            if (!meshCache.TryGetValue(binKey, out mv))
            {
                mv = new MeshVariant();
                meshCache.Add(binKey, mv);
            }
            if (mv.Failed)
            {
                materials = Array.Empty<Material>();
                return null;
            }

            NfsuDrawable d;
            if (!drawables.TryGetValue(binKey, out d))
            {
                mv.Failed = true;
                materials = Array.Empty<Material>();
                return null;
            }
            if (mv.Materials.Length == 0)
                mv.Materials = BuildMaterials(d);
            materials = mv.Materials;

            Mesh variant = mirror ? mv.Mirrored : mv.Base;
            if (variant != null)
                return variant;

            variant = BuildMesh(d, mirror, mv);
            if (variant == null)
            {
                mv.Failed = true;
                return null;
            }
            if (mirror)
                mv.Mirrored = variant;
            else
                mv.Base = variant;
            return variant;
        }

        /// <summary>
        /// Mesh variant with the instance's full linear transform baked into the vertices. Used when
        /// the matrix cannot be expressed as rotation * axis-aligned scale without moving geometry
        /// more than <see cref="NfsuImportOptions.ShearToleranceM"/>; the instance then keeps only
        /// its translation. Cached per drawable and matrix, so repeated placements share one mesh.
        /// </summary>
        Mesh GetMeshBaked(uint binKey, float[] m, out Material[] materials)
        {
            MeshVariant mv;
            if (!meshCache.TryGetValue(binKey, out mv))
            {
                mv = new MeshVariant();
                meshCache.Add(binKey, mv);
            }
            if (mv.Failed)
            {
                materials = Array.Empty<Material>();
                return null;
            }

            NfsuDrawable d;
            if (!drawables.TryGetValue(binKey, out d))
            {
                mv.Failed = true;
                materials = Array.Empty<Material>();
                return null;
            }
            if (mv.Materials.Length == 0)
                mv.Materials = BuildMaterials(d);
            materials = mv.Materials;

            string key = MatrixKey(m);
            Mesh mesh;
            if (mv.Baked.TryGetValue(key, out mesh))
                return mesh;

            mesh = BuildMesh(d, false, mv, m);
            if (mesh == null)
            {
                mv.Failed = true;
                materials = Array.Empty<Material>();
                return null;
            }
            mv.Baked[key] = mesh;
            return mesh;
        }

        Material[] BuildMaterials(NfsuDrawable d)
        {
            int sub = Math.Max(1, d.Submeshes.Count);
            var list = new Material[sub];
            for (int i = 0; i < sub; i++)
            {
                uint texKey = 0;
                if (i < d.Submeshes.Count)
                {
                    int slot = d.Submeshes[i].Slot;
                    if (slot >= 0 && slot < d.Slots.Length)
                        texKey = d.Slots[slot];
                }
                list[i] = GetMaterial(texKey);
            }
            return list;
        }

        Mesh BuildMesh(NfsuDrawable d, bool mirror, MeshVariant mv, float[] bake = null)
        {
            int vc = d.VertCount;
            int stride = d.Stride;
            var v = d.Verts;
            var verts = new Vector3[vc];
            var uvs = new Vector2[vc];
            var cols = new Color32[vc];

            for (int i = 0; i < vc; i++)
            {
                int o = i * stride;
                float x = BitConverter.ToSingle(v, o);
                float y = BitConverter.ToSingle(v, o + 4);
                float z = BitConverter.ToSingle(v, o + 8);
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) ||
                    Math.Abs(x) > 1e6f || Math.Abs(y) > 1e6f || Math.Abs(z) > 1e6f)
                {
                    stats.Note("non-finite or absurd vertex in " + Key(d.BinKey) + " " + d.Name + " at #" + i);
                    return null;
                }
                verts[i] = bake == null
                    ? new Vector3(mirror ? -x : x, z, -y)
                    : ApplyMatrix(bake, new Vector3(x, z, -y));

                float u = BitConverter.ToSingle(v, o + 16);
                float vv = BitConverter.ToSingle(v, o + 20);
                if (!IsFinite(u) || !IsFinite(vv))
                {
                    u = 0f;
                    vv = 0f;
                }
                uvs[i] = new Vector2(u, opt.UvFlipV ? 1f - vv : vv);

                byte b0 = v[o + 12], b1 = v[o + 13], b2 = v[o + 14], b3 = v[o + 15];
                cols[i] = new Color32(b2, b1, b0, b3);       // D3DCOLOR is B,G,R,A in memory
            }

            int indexCount = d.TriCount * 3;
            if (indexCount * 2 > d.Indices.Length)
                indexCount = d.Indices.Length / 6 * 3;
            var idx = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                ushort raw = BitConverter.ToUInt16(d.Indices, i * 2);
                if (raw >= vc)
                {
                    stats.Note(string.Format("index {0} >= {1} verts in {2} {3}",
                        raw, vc, Key(d.BinKey), d.Name));
                    return null;
                }
                idx[i] = raw;
            }

            int submeshes = Math.Max(1, d.Submeshes.Count);
            var mesh = new Mesh
            {
                name = string.Format(CultureInfo.InvariantCulture, "{0}_{1}{2}", Key(d.BinKey),
                    NfsuDdsWriter.Sanitize(d.Name),
                    bake != null ? "_m" + MatrixKey(bake) : (mirror ? "_mir" : "")),
                subMeshCount = submeshes,
            };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.colors32 = cols;

            // A baked variant already carries the whole linear transform, so its winding only needs
            // reversing when that transform reflects (negative determinant).
            bool flip = bake != null ? Determinant(bake) < 0f : opt.FlipWinding ^ mirror;
            if (d.Submeshes.Count == 0)
            {
                mesh.SetTriangles(Reorder(idx, 0, indexCount, flip), 0);
                stats.Triangles += indexCount / 3;
            }
            else
            {
                int covered = 0;
                for (int s = 0; s < d.Submeshes.Count; s++)
                {
                    var sm = d.Submeshes[s];
                    int count = sm.IndexCount / 3 * 3;
                    int start = sm.IndexStart;
                    if (count <= 0 || start < 0 || start + count > indexCount)
                        continue;
                    mesh.SetTriangles(Reorder(idx, start, count, flip), s);
                    covered += count;
                }
                if (covered == 0)
                    mesh.SetTriangles(Reorder(idx, 0, indexCount, flip), 0);
                stats.Triangles += Math.Max(covered, indexCount) / 3;
            }

            stats.Vertices += vc;
            if (bake != null)
                stats.BakedMeshes++;
            else if (mirror)
                stats.MirroredMeshes++;
            else
                stats.MeshesBuilt++;

            if (!opt.SaveMeshAssets)
            {
                mesh.UploadMeshData(false);
                return mesh;
            }

            string folder = EnsureFolder(opt.ProjectFolder + "/" + MeshSub);
            string path = folder + "/" + mesh.name + ".mesh";
            var asset = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (asset == null)
            {
                mesh.UploadMeshData(false);
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            // Re-import: refill the existing asset so scene references survive.
            asset.Clear(false);
            asset.subMeshCount = submeshes;
            asset.SetVertices(verts);
            asset.SetUVs(0, uvs);
            asset.colors32 = cols;
            for (int s = 0; s < submeshes; s++)
                asset.SetTriangles(mesh.GetTriangles(s), s);
            asset.UploadMeshData(false);
            UnityEngine.Object.DestroyImmediate(mesh);
            return asset;
        }

        static int[] Reorder(int[] src, int start, int count, bool flipWinding)
        {
            var tri = new int[count];
            Array.Copy(src, start, tri, 0, count);
            if (flipWinding)
                for (int i = 0; i + 2 < count; i += 3)
                {
                    int t = tri[i + 1];
                    tri[i + 1] = tri[i + 2];
                    tri[i + 2] = t;
                }
            return tri;
        }

        // ------------------------------------------------------------------ scene
        public GameObject BuildScene(List<NfsuSection> sections)
        {
            int matching;
            var accepted = AcceptedSections(sections, out matching);
            stats.SectionsSeen = matching;

            if (opt.ReplaceExistingRoot)
            {
                var old = GameObject.Find(opt.RootName);
                if (old != null)
                    UnityEngine.Object.DestroyImmediate(old);
            }
            var root = new GameObject(opt.RootName);

            for (int i = 0; i < accepted.Count; i++)
            {
                var sec = accepted[i];
                if (ProgressBar(string.Format("Section {0} ({1}/{2})", sec.Id, i + 1, accepted.Count),
                        (float)i / accepted.Count))
                {
                    cancelled = true;
                    return root;
                }
                var secGo = new GameObject(string.Format(CultureInfo.InvariantCulture, "Section_{0}", sec.Id));
                secGo.transform.SetParent(root.transform, false);
                if (opt.SetLayers)
                    secGo.layer = LayerFor("NFSU_Other");

                for (int k = 0; k < sec.Instances.Count; k++)
                {
                    var ins = sec.Instances[k];
                    uint binKey = ins.TypeIndex < sec.Types.Count ? sec.Types[ins.TypeIndex].BinKey : 0u;
                    NfsuDrawable d;
                    if (binKey == 0 || !drawables.TryGetValue(binKey, out d))
                    {
                        stats.UnresolvedTypes++;
                        stats.InstancesSkipped++;
                        continue;
                    }
                    if (StartsWithAny(d.Name, skipPrefixes))
                    {
                        stats.InstancesSkipped++;
                        continue;
                    }
                    CreateInstance(secGo.transform, d, ins, k);
                }
                stats.SectionsImported++;
            }
            return root;
        }

        /// <summary>
        /// Diagnostic: logs the full placement math for the first <paramref name="max"/> instances of
        /// one mesh name, so a decomposition/baking decision can be argued from printed numbers.
        /// </summary>
        public void DescribePlacements(List<NfsuSection> sections, string meshName, int max)
        {
            foreach (var sec in sections)
            {
                for (int k = 0; k < sec.Instances.Count && max > 0; k++)
                {
                    var ins = sec.Instances[k];
                    uint binKey = ins.TypeIndex < sec.Types.Count ? sec.Types[ins.TypeIndex].BinKey : 0u;
                    NfsuDrawable d;
                    if (binKey == 0 || !drawables.TryGetValue(binKey, out d))
                        continue;
                    if (!string.Equals(d.Name, meshName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    max--;
                    Debug.Log(PlacementDump(sec, d, ins));
                }
                if (max <= 0)
                    return;
            }
        }

        static string PlacementDump(NfsuSection sec, NfsuDrawable d, NfsuInstance ins)
        {
            var sb = new StringBuilder();
            var game = new float[9];
            for (int i = 0; i < 9; i++)
                game[i] = ins.Rot[i] / 8192f;
            float[] exact = ConvertBasis(game);
            bool mir = Determinant(exact) < 0f;
            float[] unity = exact;
            if (mir)
            {
                var g2 = (float[])game.Clone();
                g2[0] = -g2[0];
                g2[1] = -g2[1];
                g2[2] = -g2[2];
                unity = ConvertBasis(g2);
            }

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "placement check section {0} {1} {2} pos {3:F1},{4:F1},{5:F1}  mirror={6}",
                sec.Id, Key(d.BinKey), d.Name, ins.Pos[0], ins.Pos[1], ins.Pos[2], mir));
            sb.AppendLine("  packed   " + Arr(game));
            sb.AppendLine("  unity    " + Arr(unity));
            sb.AppendLine("  local bb " + Arr(d.BBox));

            float sx = ColumnLength(unity, 0), sy = ColumnLength(unity, 1), sz = ColumnLength(unity, 2);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  col len  {0:F4} {1:F4} {2:F4}", sx, sy, sz));
            var m = new Matrix4x4();
            m.SetColumn(0, new Vector4(unity[0] / sx, unity[3] / sx, unity[6] / sx, 0f));
            m.SetColumn(1, new Vector4(unity[1] / sy, unity[4] / sy, unity[7] / sy, 0f));
            m.SetColumn(2, new Vector4(unity[2] / sz, unity[5] / sz, unity[8] / sz, 0f));
            var fwd = m.GetColumn(2);
            var up = m.GetColumn(1);
            var orient = Quaternion.LookRotation(fwd, up);
            var e = orient.eulerAngles;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  fwd {0:F4},{1:F4},{2:F4}  up {3:F4},{4:F4},{5:F4}  dot {6:F4}  orient euler {7:F2},{8:F2},{9:F2}",
                fwd.x, fwd.y, fwd.z, up.x, up.y, up.z, Vector3.Dot(fwd, up), e.x, e.y, e.z));

            var approx = Matrix4x4.TRS(Vector3.zero, orient, new Vector3(sx, sy, sz));
            var c0 = approx.GetColumn(0);
            var c1 = approx.GetColumn(1);
            var c2 = approx.GetColumn(2);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  approx   {0:F4},{1:F4},{2:F4} {3:F4},{4:F4},{5:F4} {6:F4},{7:F4},{8:F4}",
                c0.x, c0.y, c0.z, c1.x, c1.y, c1.z, c2.x, c2.y, c2.z));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  err      {0:F3} m",
                DecomposeError(unity, orient, new Vector3(sx, sy, sz), d.BBox)));
            return sb.ToString();
        }

        static string Arr(float[] a)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++)
                sb.Append(a[i].ToString("0.####", CultureInfo.InvariantCulture)).Append(i == a.Length - 1 ? "" : " ");
            return sb.ToString();
        }

        void CreateInstance(Transform parent, NfsuDrawable d, NfsuInstance ins, int ordinal)
        {
            float[] game = new float[9];
            for (int i = 0; i < 9; i++)
                game[i] = ins.Rot[i] / 8192f;

            float[] exact = ConvertBasis(game);          // the transform as the game stores it
            float[] unity = exact;
            bool mirror = opt.BakeMirrorMeshes && Determinant(unity) < 0f;
            if (mirror)
            {
                // G = G' * diag(-1,1,1): the packed triples are the columns of G, so column 0 (the
                // object's local x axis, indices 0..2) carries the other half of the reflection that
                // the mirrored mesh carries. Negating a ROW instead mirrors the placement about the
                // world x axis: 277 of the RA batch's placements then stick out of their own stored
                // AABB by up to 37 m.
                game[0] = -game[0];
                game[1] = -game[1];
                game[2] = -game[2];
                unity = ConvertBasis(game);
            }

            float sx = ColumnLength(unity, 0);
            float sy = ColumnLength(unity, 1);
            float sz = ColumnLength(unity, 2);
            if (sx < 1e-4f || sy < 1e-4f || sz < 1e-4f)
            {
                stats.InstancesSkipped++;
                stats.Note("degenerate placement matrix for " + Key(d.BinKey) + " " + d.Name);
                return;
            }

            var rot = new Matrix4x4();
            rot.SetColumn(0, new Vector4(unity[0] / sx, unity[3] / sx, unity[6] / sx, 0f));
            rot.SetColumn(1, new Vector4(unity[1] / sy, unity[4] / sy, unity[7] / sy, 0f));
            rot.SetColumn(2, new Vector4(unity[2] / sz, unity[5] / sz, unity[8] / sz, 0f));
            var orient = Quaternion.LookRotation(rot.GetColumn(2), rot.GetColumn(1));

            // A Unity transform is rotation * axis-aligned scale, so a matrix with shear cannot be
            // placed exactly; measure the cost and bake the matrix into the mesh when it matters.
            bool baked = false;
            if (opt.BakeShearedPlacements)
            {
                float err = DecomposeError(unity, orient, new Vector3(sx, sy, sz), d.BBox);
                if (err > opt.ShearToleranceM)
                {
                    baked = true;
                    if (stats.ShearedInstances < 12)
                        stats.Note(string.Format(CultureInfo.InvariantCulture,
                            "sheared placement baked exactly ({0:N2} m off) for {1} {2} at {3:N1},{4:N1},{5:N1}",
                            err, Key(d.BinKey), d.Name, ins.Pos[0], ins.Pos[1], ins.Pos[2]));
                }
            }

            Material[] materials;
            Mesh mesh = baked ? GetMeshBaked(d.BinKey, exact, out materials)
                              : GetMesh(d.BinKey, mirror, out materials);
            if (mesh == null)
            {
                stats.InstancesSkipped++;
                return;
            }

            var go = new GameObject(SanitizeName(d.Name) + "_" + ordinal);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(ins.Pos[0], ins.Pos[2], -ins.Pos[1]);
            if (baked)
            {
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                stats.ShearedInstances++;
            }
            else
            {
                go.transform.rotation = orient;
                go.transform.localScale = new Vector3(sx, sy, sz);
                if (mirror)
                    stats.MirroredInstances++;
            }

            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            filter.sharedMesh = mesh;
            renderer.sharedMaterials = materials;

            if (opt.SetLayers)
                go.layer = LayerFor(d.Name);
            if (opt.MarkStatic)
                go.isStatic = true;
            stats.InstancesCreated++;

            if (opt.VerifyPlacementAABB)
                VerifyPlacement(go.transform, mesh, ins, d);

            if (opt.AddMeshColliders && StartsWithAny(d.Name, colliderPrefixes))
            {
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mesh;
                stats.CollidersCreated++;
            }
        }

        /// <summary>
        /// Checks the placement we just built against the world AABB the scenery record stores for
        /// that instance (i16 metres, game axes). The stored box is padded, so the honest test is
        /// CONTAINMENT of our box inside it: a correct pipeline stays inside for 96% of RA's
        /// placements (mean overhang 0.32 m, padding outliers aside), while a transposed placement
        /// matrix - the classic mistake, it mirrors every angled object about the object's own up
        /// axis - sticks out of 8.6% of them by over 5 m. The <c>Decisive</c> counters repeat the
        /// test on the subset where the two readings cannot both fit (long mesh + strongly
        /// antisymmetric matrix): 98.8% pass when the layout is right, only 52% when it is transposed.
        /// </summary>
        void VerifyPlacement(Transform t, Mesh mesh, NfsuInstance ins, NfsuDrawable d)
        {
            bool empty = true;
            for (int i = 0; i < 6; i++)
                if (ins.BBox[i] != 0) { empty = false; break; }
            if (empty)
                return;

            var m = t.localToWorldMatrix;
            var b = mesh.bounds;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) == 0 ? b.min.x : b.max.x,
                                    (i & 2) == 0 ? b.min.y : b.max.y,
                                    (i & 4) == 0 ? b.min.z : b.max.z);
                p = m.MultiplyPoint3x4(p);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }

            // game box (x, y horizontal, z up) -> Unity (x, z, -y)
            float e0x = ins.BBox[0], e0y = ins.BBox[2], e0z = -ins.BBox[4];
            float e1x = ins.BBox[3], e1y = ins.BBox[5], e1z = -ins.BBox[1];
            float overhang = Mathf.Max(
                Mathf.Max(e0x - minX, maxX - e1x),
                Mathf.Max(Mathf.Max(e0y - minY, maxY - e1y), Mathf.Max(e0z - minZ, maxZ - e1z)));
            if (overhang < 0f)
                overhang = 0f;                              // inside: padding slack, not an error

            stats.PlacementAabbChecked++;
            if (overhang > stats.WorstPlacementAabbErr)
                stats.WorstPlacementAabbErr = overhang;
            if (overhang > NfsuImportStats.AabbSlackM)
                stats.PlacementAabbBad++;

            if (IsDecisivePlacement(d, ins))
            {
                stats.PlacementDecisive++;
                if (overhang > NfsuImportStats.AabbSlackM)
                {
                    stats.PlacementDecisiveBad++;
                    if (stats.PlacementDecisiveBad <= 12)
                        stats.Note(string.Format(CultureInfo.InvariantCulture,
                            "placement leaves its own world AABB by {0:N1} m (decisive case) for {1} {2} at {3:N1},{4:N1},{5:N1}",
                            overhang, Key(d.BinKey), d.Name, ins.Pos[0], ins.Pos[1], ins.Pos[2]));
                }
            }
        }

        /// <summary>
        /// True when a wrong matrix layout could not hide inside this instance's AABB: the mesh is
        /// long in the horizontal plane and the matrix is far from symmetric, so a transpose moves
        /// the box by many metres.
        /// </summary>
        static bool IsDecisivePlacement(NfsuDrawable d, NfsuInstance ins)
        {
            float lx = d.BBox[3] - d.BBox[0], ly = d.BBox[4] - d.BBox[1];
            if (lx < 0.5f || ly < 0.5f)
                return false;
            if (Mathf.Max(lx, ly) / Mathf.Max(0.01f, Mathf.Min(lx, ly)) < 2.5f)
                return false;
            float norm = 0f, anti = 0f;
            for (int i = 0; i < 9; i++)
                norm += ins.Rot[i] * (float)ins.Rot[i];
            norm = (float)Math.Sqrt(norm);
            if (norm < 1e-4f)
                return false;
            // |A - A^T| over the three pairs a matrix shares with its transpose
            anti = (float)Math.Sqrt((ins.Rot[1] - ins.Rot[3]) * (float)(ins.Rot[1] - ins.Rot[3])
                                    + (ins.Rot[2] - ins.Rot[6]) * (float)(ins.Rot[2] - ins.Rot[6])
                                    + (ins.Rot[5] - ins.Rot[7]) * (float)(ins.Rot[5] - ins.Rot[7]));
            return anti / norm > 0.3f;
        }

        // ------------------------------------------------------------------ helpers
        static float ColumnLength(float[] m, int column)
        {
            float a = m[column], b = m[3 + column], c = m[6 + column];
            return (float)Math.Sqrt(a * a + b * b + c * c);
        }

        static float Determinant(float[] m)
        {
            return m[0] * (m[4] * m[8] - m[5] * m[7])
                 - m[1] * (m[3] * m[8] - m[5] * m[6])
                 + m[2] * (m[3] * m[7] - m[4] * m[6]);
        }

        /// <summary>
        /// Applies one of our <c>float[9]</c> matrices (row-major, see <see cref="ConvertBasis"/>) to a
        /// point. Consistency matters more than style here: a column of such a matrix lives at indices
        /// c, 3+c, 6+c, which is what <see cref="ColumnLength"/> and the <c>SetColumn</c> calls in
        /// <see cref="CreateInstance"/> read, so the contraction must pair the point's components with
        /// the rows. Contracting the other way applies the transpose and mirrors every angled
        /// placement about the object's own up axis.
        /// </summary>
        static Vector3 ApplyMatrix(float[] m, Vector3 p)
        {
            return new Vector3(m[0] * p.x + m[1] * p.y + m[2] * p.z,
                               m[3] * p.x + m[4] * p.y + m[5] * p.z,
                               m[6] * p.x + m[7] * p.y + m[8] * p.z);
        }

        /// <summary>Cache key and mesh-name suffix for a linear transform, quantised to 1e-4.</summary>
        static string MatrixKey(float[] m)
        {
            var sb = new StringBuilder(64);
            for (int i = 0; i < 9; i++)
            {
                float v = m[i];
                if (Math.Abs(v) < 5e-5f)
                    v = 0f;                                   // keeps -0 and +0 in one bucket
                sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append('x');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Largest gap between what the exact transform does and what rotation*scale in Unity would
        /// do, measured over the drawable's local AABB, so a small wobble on a small prop and a big
        /// one on a city block are both caught.
        /// </summary>
        static float DecomposeError(float[] m, Quaternion rot, Vector3 scale, float[] bbox)
        {
            var approx = Matrix4x4.TRS(Vector3.zero, rot, scale);
            float worst = 0f;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) == 0 ? bbox[0] : bbox[3],
                                    (i & 2) == 0 ? bbox[1] : bbox[4],
                                    (i & 4) == 0 ? bbox[2] : bbox[5]);
                p = new Vector3(p.x, p.z, -p.y);               // game-space AABB -> Unity axes
                worst = Mathf.Max(worst, Vector3.Distance(ApplyMatrix(m, p), approx.MultiplyPoint(p)));
            }
            return worst;
        }

        /// <summary>
        /// A_unity = C * A_game * C^-1 for the (x,y,z) -> (x,z,-y) change of basis.
        ///
        /// The packed scenery matrix stores its COLUMNS consecutively: the game applies
        /// x' = m0*x + m3*y + m6*z, so G[k,c] = packed[c*3+k] (proven against the world AABB each
        /// instance record carries - see AGENTS.md). The array this returns is the Unity-space matrix
        /// in plain ROW-MAJOR order, which is the layout the rest of this file uses: a column c lives
        /// at indices c, 3+c, 6+c (what <see cref="ColumnLength"/> and <see cref="Matrix4x4.SetColumn"/>
        /// read in CreateInstance) and a point contraction is <see cref="ApplyMatrix"/>. Returning the
        /// transpose here silently mirrors every angled placement about the object's own up axis.
        /// </summary>
        static float[] ConvertBasis(float[] game)
        {
            // G (row-major) = transpose of the packed array, because the packed triples are columns.
            var g = new float[9];
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 3; k++)
                    g[k * 3 + c] = game[c * 3 + k];

            var tmp = new float[9];                                   // tmp = C * G   (row-major)
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    float sum = 0f;
                    for (int k = 0; k < 3; k++)
                        sum += BasisUnityOfGame[r * 3 + k] * g[k * 3 + c];
                    tmp[r * 3 + c] = sum;
                }
            var result = new float[9];                                // C * G * C^-1, row-major
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    float sum = 0f;
                    for (int k = 0; k < 3; k++)
                        sum += tmp[r * 3 + k] * BasisGameOfUnity[k * 3 + c];
                    result[r * 3 + c] = sum;
                }
            return result;
        }

        static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        static string SanitizeName(string s)
        {
            var cleaned = NfsuDdsWriter.Sanitize(s);
            return string.IsNullOrEmpty(cleaned) ? "instance" : cleaned;
        }

        static HashSet<uint> ParseIdFilter(string filter)
        {
            var set = new HashSet<uint>();
            if (string.IsNullOrEmpty(filter))
                return set;
            foreach (var part in filter.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                uint id;
                if (uint.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    set.Add(id);
            }
            return set;
        }

        int LayerFor(string meshName)
        {
            string layer = "NFSU_Other";
            if (!string.IsNullOrEmpty(meshName))
                foreach (var pair in PrefixLayers)
                    if (meshName.StartsWith(pair[0], StringComparison.OrdinalIgnoreCase))
                    {
                        layer = pair[1];
                        break;
                    }
            int index;
            if (layerCache.TryGetValue(layer, out index))
                return index;
            index = EnsureLayer(layer);
            layerCache[layer] = index;
            return index;
        }

        static int EnsureLayer(string name)
        {
            try
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
                if (assets == null || assets.Length == 0)
                    return 0;
                var so = new SerializedObject(assets[0]);
                var layers = so.FindProperty("layers");
                if (layers == null)
                    return 0;
                for (int i = 8; i < layers.arraySize; i++)
                    if (layers.GetArrayElementAtIndex(i).stringValue == name)
                        return i;
                for (int i = 8; i < layers.arraySize; i++)
                {
                    var el = layers.GetArrayElementAtIndex(i);
                    if (string.IsNullOrEmpty(el.stringValue))
                    {
                        el.stringValue = name;
                        so.ApplyModifiedProperties();
                        return i;
                    }
                }
                Debug.LogWarning("NFSU: no free user layer slot for " + name);
            }
            catch (Exception e)
            {
                Debug.LogWarning("NFSU: could not add layer " + name + ": " + e.Message);
            }
            return 0;
        }

        static string EnsureFolder(string folder)
        {
            folder = folder.Replace("\\", "/");
            if (AssetDatabase.IsValidFolder(folder))
                return folder;
            var parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            return current;
        }

        static bool ProgressBar(string label, float t)
        {
            return EditorUtility.DisplayCancelableProgressBar("NFSU map import", label, Mathf.Clamp01(t));
        }
    }
}
