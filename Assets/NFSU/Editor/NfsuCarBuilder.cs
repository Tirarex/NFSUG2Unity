using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NFSU.Runtime;
using UnityEditor;
using UnityEngine;

namespace NFSU
{
    /// <summary>Options for <see cref="NfsuCarBuilder"/>.</summary>
    public sealed class NfsuCarImportOptions
    {
        /// <summary><c>Cars\&lt;NAME&gt;\GEOMETRY.BIN</c> files to import.</summary>
        public readonly List<string> CarPaths = new List<string>();
        /// <summary>Files that hold the TPK packs the cars reference (their textures).</summary>
        public readonly List<string> PackPaths = new List<string>();

        public string CarFilter = "";                 // "4DR_SEDAN,TAXI" - empty means every traffic car
        public string ProjectFolder = "Assets/NFSU";
        public string RootName = "NFSU_TRAFFIC_CARS";

        public NfsuAlphaMode AlphaMode = NfsuAlphaMode.Auto;
        public bool UvFlipV = true;
        public bool WriteTextures = true;
        public bool SaveMeshAssets = true;
        public bool BuildWheelRig = true;
        public bool AddLodGroup = true;
        public bool AddBoxCollider = true;
        public bool SavePrefab = true;
        public bool ReplaceExistingRoot = true;
        public int MaxCars;                           // 0 = unlimited

        /// <summary>
        /// A car is imported when <see cref="CarFilter"/> is empty, or when any one of its
        /// comma / semicolon / space separated entries is a substring of the car's folder name
        /// (case-insensitive), so "TAXI,SUV" picks both taxi variants and the SUV.
        /// </summary>
        public bool Matches(string carName)
        {
            if (string.IsNullOrEmpty(CarFilter))
                return true;
            foreach (var raw in CarFilter.Split(new[] { ',', ';', ' ', '\t' },
                                                StringSplitOptions.RemoveEmptyEntries))
                if (carName.IndexOf(raw.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }

    /// <summary>Counters and notes for one traffic-car import run.</summary>
    public sealed class NfsuCarBuildStats
    {
        public int CarsSeen;
        public int CarsImported;
        public int CarsFiltered;
        public int PartsImported;
        public int PartsUnsupported;
        public int MeshesBuilt;
        public int MeshAssets;
        public int MaterialsCreated;
        public int MaterialsReused;
        public int MaterialsUsed;
        public int MaterialsOpaque;
        public int MaterialsCutout;
        public int MaterialsBlend;
        public int MaterialsAdditive;
        public int SlotsWithoutTexture;
        public int TexturesWritten;
        public int TexturesReused;
        public int TexturesMissing;
        public int WheelsBuilt;
        public int WheelsMirrored;
        public int WheelRigs;
        /// <summary>Corners taken from the model's own wheel patches (the shipped answer).</summary>
        public int WheelsFromPatch;
        /// <summary>Corners completed by mirroring a patch across the car's axes.</summary>
        public int WheelsFromPatchMirror;
        /// <summary>Corners that had no patch and fell back to the <c>0x0013401A</c> frame table.</summary>
        public int WheelsFromSocket;
        /// <summary>Corners with neither patch nor socket: placed from the physics box only.</summary>
        public int WheelsFromBody;
        /// <summary>Largest |tyre outer face - wheel-patch outer edge| over the patch corners.</summary>
        public float WorstPatchFitM;
        public int LodGroups;
        /// <summary>LODGroups that read back from the saved prefab exactly as they were set.</summary>
        public int LodGroupsVerified;
        /// <summary>Renderers registered into an LOD level (a shortfall is a note, not a counter).</summary>
        public int LodRenderersRegistered;
        public int Colliders;
        public int PrefabsSaved;
        public int Vertices;
        public int Triangles;

        /// <summary>
        /// Largest |tyre outer face - body skin in the tyre's own footprint|: how flush the
        /// sidewall sits against the body. Positive means the tyre is proud of the body.
        /// </summary>
        public float WorstSidewallGapM;
        /// <summary>Largest amount by which a tyre's outer face sits UNDER the body skin.</summary>
        public float WorstBurialM;
        /// <summary>
        /// Largest |road plane - physics box floor|: the box is a chassis box, so its bottom plane
        /// sits this far above the surface the tyres actually touch.
        /// </summary>
        public float WorstGroundGapM;

        readonly StringBuilder _notes = new StringBuilder();
        int _noteCount;

        public void Note(string s)
        {
            if (_noteCount++ >= 40)
                return;
            _notes.Append("  - ").AppendLine(s);
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== NFSU traffic car import ===");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "cars {0} seen, {1} imported, {2} filtered out", CarsSeen, CarsImported, CarsFiltered));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "parts {0} built, {1} unsupported (skinned / not stride-36)",
                PartsImported, PartsUnsupported));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "meshes {0} built, {1} saved as assets; {2} vertices, {3} triangles",
                MeshesBuilt, MeshAssets, Vertices, Triangles));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "materials {0} in use ({1} created, {2} reused from earlier runs): {3} opaque / " +
                "{4} cutout / {5} blend / {6} additive; {7} mesh texture slots with no texture",
                MaterialsUsed, MaterialsCreated, MaterialsReused, MaterialsOpaque, MaterialsCutout,
                MaterialsBlend, MaterialsAdditive, SlotsWithoutTexture));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "textures {0} written, {1} already there, {2} keys with no record in any pack",
                TexturesWritten, TexturesReused, TexturesMissing));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "wheel rigs {0}, wheel nodes {1} ({2} on the mirrored side), LODGroups {3} " +
                "({4} read back correct from the saved prefab, {5} renderers registered), " +
                "BoxColliders {6}, prefabs {7}",
                WheelRigs, WheelsBuilt, WheelsMirrored, LodGroups, LodGroupsVerified,
                LodRenderersRegistered, Colliders, PrefabsSaved));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "wheel mounts: {0} from the model's own wheel patches, {1} mirrored from a patch, " +
                "{2} from the frame table, {3} from the body/box only",
                WheelsFromPatch, WheelsFromPatchMirror, WheelsFromSocket, WheelsFromBody));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "wheel fit: worst sidewall gap {0:F3} m (outer face vs body skin), worst burial " +
                "{1:F3} m, worst patch fit {2:F3} m (outer face vs the patch's own edge), worst " +
                "road-plane gap {3:F3} m (the road sits under the physics box floor)",
                WorstSidewallGapM, WorstBurialM, WorstPatchFitM, WorstGroundGapM));
            if (_noteCount > 0)
            {
                sb.AppendLine("notes:");
                sb.Append(_notes);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Turns <c>Cars\&lt;NAME&gt;\GEOMETRY.BIN</c> traffic-car bundles into real Unity assets: a
    /// mesh per drawable (submesh per texture slot), materials through the shared alpha classifier,
    /// textures through the shared DDS writer and Unity's own DDS importer, a wheel rig whose tyres
    /// can roll and steer, an LODGroup over the A/B/C drawables, a BoxCollider from the physics
    /// block, and a prefab.
    /// <para>
    /// Basis: the model is stored game space, Z-up (forward +X, left +Y, up +Z, metres, origin on
    /// the road). The builder bakes the same (x,y,z) -&gt; (x,z,-y) change the map builder uses into
    /// the vertices, so the car faces Unity +X with Unity +Y up and the root needs no rotation. The
    /// rig is expressed in that frame: steer about Unity +Y (the game's up), roll about Unity +Z
    /// (the game's lateral axis, negated because the mapping flips it).
    /// </para>
    /// <para>
    /// Wheel placement comes from the model itself, not from the <c>0x0013401A</c> frame table. The
    /// traffic art bakes a flat rectangle under every wheel into its lower-detail body - one per
    /// corner, its x span equal to the tyre's own diameter, its y span equal to the tyre's width,
    /// all its vertices in one plane, which is the road plane (4DR_SEDAN: 0.727 x 0.224 m at
    /// z = -0.191 against a tyre of 2r = 0.709 and w = 0.221). That patch IS the shipped wheel
    /// position: it yields a 2.68 m wheelbase on a 4.54 m sedan, while the frame table's translations
    /// sit at x = +2.04/-2.16 on the same body, a 4.2 m wheelbase no road car has, with an axle 0.28
    /// to 0.38 m above the one the patches give (nfs_probe/car_wheel_patches.py). So the axle x is the
    /// patch's x centre, the tyre is centred in the patch's y band, and the axle height is the patch's
    /// plane plus the tyre radius - which puts the tread exactly on the road plane the art drew.
    /// 52 of the 60 corners of the 15 traffic bundles come from patches; the two that bake none
    /// (BUS, FIRETRUCK: two LODs, nothing under the A-body floor) fall back to the frame table and
    /// say so in the report.
    /// </para>
    /// <para>
    /// The tyre mesh is modelled one-sided at the origin - its tread runs from its own y = 0 plane out
    /// to y = width - so the mount is the tyre's inner face and it is recentred radially only, never
    /// laterally; the side it was not modelled for gets a 180 degree turn about Unity X, a proper
    /// rotation, so the winding and the normals stay valid instead of needing a mirrored mesh.
    /// </para>
    /// </summary>
    public sealed class NfsuCarBuilder
    {
        public const string CarSub = "TrafficCars";
        public const string MeshSub = "TrafficCars/Meshes";

        /// <summary>
        /// screenRelativeTransitionHeight per LOD level, highest detail first. The bundle stores no
        /// switch distances, and deriving them from the vertex-count ratio put LOD A at 0.9984 - it
        /// only drew at (almost) full screen height, so a scene review saw all three levels together.
        /// These are the importer's own ladder: at a 60 degree FOV a 4.5 m car keeps LOD A out to
        /// about 15 m, LOD B to about 38 m, LOD C beyond that.
        /// </summary>
        public static readonly float[] LodTransitionHeights = { 1.0f, 0.25f, 0.10f, 0.04f };

        const string TexSub = "Textures";
        const string MatSub = "Materials";

        /// <summary>Byte offsets inside a car vertex record (stride 36), per the measured layout.</summary>
        const int OffPos = 0;
        const int OffNormal = 12;
        const int OffColour = 24;
        const int OffUv = 28;

        sealed class TexSrc
        {
            public NfsuTextureRecord Record;
            public byte[] Blob;
            public Texture2D Texture;
            public string AssetPath;
            public NfsuAlphaClass Class = NfsuAlphaClass.Opaque;
            public bool ClassDone;
        }

        /// <summary>Where one corner's mount came from (the CSV <c>source</c> column).</summary>
        public enum NfsuWheelSource
        {
            None = 0,
            /// <summary>The corner's own wheel patch in the lower-detail body.</summary>
            Patch = 1,
            /// <summary>Completed by mirroring another corner's patch across the car's axes.</summary>
            PatchMirror = 2,
            /// <summary>No patch baked: the <c>0x0013401A</c> frame table (BUS, FIRETRUCK).</summary>
            Socket = 3,
            /// <summary>No patch and no table: placed from the physics box only (never used).</summary>
            Body = 4,
        }

        /// <summary>One wheel mount decided by <see cref="PlanWheels"/>.</summary>
        public sealed class WheelPlan
        {
            public string Name = "";
            public bool Front;
            public bool Left;                 // the game's +Y side
            public NfsuWheelSource Source = NfsuWheelSource.None;
            public uint SocketKey;
            public float X, Y, Z;             // the mount, game metres
            public float PatchInnerY;         // the wheel patch's |y| edges (0 when there is none)
            public float PatchOuterY;
            public float OuterFace;           // |Y| + tyre width: the tyre's outer plane
            public float Skin;                // body skin |y| inside the tyre's own footprint
            public bool SkinSet;              // BodySkinNear is only cheap enough to run once
            public float SidewallGap;         // OuterFace - Skin: > 0 means the tyre is proud
            public float GroundGap;           // Z - radius - physics box floor: the road under the box
        }

        readonly NfsuCarImportOptions opt;
        readonly NfsuCarBuildStats stats = new NfsuCarBuildStats();
        readonly Dictionary<uint, TexSrc> texSources = new Dictionary<uint, TexSrc>();
        readonly Dictionary<string, Material> matCache = new Dictionary<string, Material>();
        readonly Dictionary<uint, Mesh> meshCache = new Dictionary<uint, Mesh>();
        readonly HashSet<uint> wantedKeys = new HashSet<uint>();
        readonly HashSet<uint> multiSlotKeys = new HashSet<uint>();
        readonly HashSet<string> _noted = new HashSet<string>();
        bool texturesDone;

        public NfsuCarBuilder(NfsuCarImportOptions options)
        {
            opt = options;
        }

        public static NfsuCarBuildStats Import(NfsuCarImportOptions options)
        {
            var builder = new NfsuCarBuilder(options);
            builder.Run();
            return builder.stats;
        }

        // ------------------------------------------------------------------ run

        void Run()
        {
            ReadTexturePacks();

            var old = GameObject.Find(opt.RootName);
            if (opt.ReplaceExistingRoot && old != null)
                UnityEngine.Object.DestroyImmediate(old);
            var root = new GameObject(opt.RootName);

            var carPaths = new List<string>();
            foreach (var p in opt.CarPaths)
            {
                if (!File.Exists(p))
                {
                    stats.Note("missing car bundle: " + p);
                    continue;
                }
                carPaths.Add(p);
            }
            carPaths.Sort(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Pass 1: read the bundles and collect the texture keys every car references.
                // Pass 2: build them. The two passes are necessary because the texture pass runs
                // exactly once (it writes each .dds one time and Unity then imports it), so the
                // keys of ALL cars have to be known before it runs - collecting them per car and
                // importing per car wrote only the first car's skin and left 14 of the 18
                // materials pointing at .dds files that were never written.
                var cars = new List<NfsuCarBundle>();
                foreach (var path in carPaths)
                {
                    NfsuCarBundle car;
                    try
                    {
                        car = new NfsuCarBundle(path);
                    }
                    catch (Exception e)
                    {
                        stats.Note("unreadable " + path + ": " + e.Message);
                        continue;
                    }
                    stats.CarsSeen++;
                    stats.PartsUnsupported += car.UnsupportedParts;
                    if (!car.LooksLikeTraffic())
                    {
                        stats.Note(string.Format(CultureInfo.InvariantCulture,
                            "{0}: not a traffic car ({1} parts, {2} unsupported) - skipped",
                            car.CarName, car.Parts.Count, car.UnsupportedParts));
                        continue;
                    }
                    if (!opt.Matches(car.CarName) ||
                        (opt.MaxCars > 0 && cars.Count >= opt.MaxCars))
                    {
                        stats.CarsFiltered++;
                        continue;
                    }
                    cars.Add(car);
                    CollectTextureKeys(car);
                }

                ImportTextures();

                foreach (var car in cars)
                    BuildCar(car, root);
            }
            finally
            {
                AssetDatabase.Refresh();
            }
        }

        void ReadTexturePacks()
        {
            foreach (var path in opt.PackPaths)
            {
                if (!File.Exists(path))
                {
                    stats.Note("missing texture pack: " + path);
                    continue;
                }
                List<NfsuTexturePack> packs;
                try
                {
                    packs = new NfsuBundle(path).TexturePacks();
                }
                catch (Exception e)
                {
                    stats.Note("texture pack unreadable " + Path.GetFileName(path) + ": " + e.Message);
                    continue;
                }
                foreach (var p in packs)
                {
                    foreach (var r in p.Records)
                    {
                        if (texSources.ContainsKey(r.BinKey) ||
                            r.Offset < 0 || r.Offset + r.Size > p.Pixels.Length)
                            continue;
                        texSources[r.BinKey] = new TexSrc { Record = r, Blob = p.Pixels };
                    }
                }
            }
        }

        // ------------------------------------------------------------------ one car

        void BuildCar(NfsuCarBundle car, GameObject root)
        {
            var bodyLods = new SortedList<char, NfsuCarBundle.NfsuCarPart>();
            var wheelLods = new SortedList<char, NfsuCarBundle.NfsuCarPart>();
            foreach (var p in car.Parts)
            {
                if (p.IsBody && !bodyLods.ContainsKey(p.Lod))
                    bodyLods[p.Lod] = p;
                else if (p.IsWheel && !wheelLods.ContainsKey(p.Lod))
                    wheelLods[p.Lod] = p;
            }
            if (bodyLods.Count == 0)
            {
                stats.Note(car.CarName + ": no BODY part - skipped");
                return;
            }

            // The textures are already written and imported: Run collects every car's keys and
            // imports them before the first car is built, so a material here always finds its
            // Texture2D.
            var carRoot = new GameObject(car.CarName);
            carRoot.transform.SetParent(root.transform, false);

            // ---- body LODs
            var lodChars = new List<char>();
            var lodRenderers = new List<List<Renderer>>();
            foreach (var kv in bodyLods)
            {
                var mesh = MeshOf(kv.Value, null);
                if (mesh == null)
                    continue;
                var go = new GameObject("Body_" + kv.Key);
                go.transform.SetParent(carRoot.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = mesh;
                ApplyMaterials(mr, kv.Value.Drawable);
                lodChars.Add(kv.Key);
                lodRenderers.Add(new List<Renderer> { mr });
                stats.PartsImported++;
            }

            // ---- wheels: each corner owns a mount (steer pivot), a spin node and the tyre LODs
            Dictionary<char, List<Renderer>> tyreRenderers = null;
            if (opt.BuildWheelRig && wheelLods.Count > 0)
                tyreRenderers = BuildWheels(car, carRoot, wheelLods);

            if (tyreRenderers != null)
                stats.WheelRigs++;

            // ---- LODGroup, with the tyres folded into the level of their own LOD letter
            LODGroup lodGroup = null;
            if (opt.AddLodGroup && lodRenderers.Count > 0)
            {
                var sets = new Renderer[lodRenderers.Count][];
                int registered = 0;
                int available = 0;
                foreach (var l in lodRenderers)
                    available += l.Count;
                if (tyreRenderers != null)
                    foreach (var kv in tyreRenderers)
                        available += kv.Value.Count;
                for (int i = 0; i < lodRenderers.Count; i++)
                {
                    var list = new List<Renderer>(lodRenderers[i]);
                    List<Renderer> extra;
                    if (tyreRenderers != null && tyreRenderers.TryGetValue(lodChars[i], out extra))
                        list.AddRange(extra);
                    sets[i] = list.ToArray();
                    registered += list.Count;
                }
                var lg = carRoot.AddComponent<LODGroup>();
                var lods = new LOD[sets.Length];
                for (int i = 0; i < lods.Length; i++)
                {
                    // A fixed descending ladder, because the game's own switch distances are not in
                    // the bundle and the mesh vertex ratio is not a usable stand-in: measured in the
                    // saved CARS scene, the ratios 437/659 and 211/659 serialized as transition
                    // heights 0.9984 and 0.352, so LOD A only drew at (almost) full screen height and
                    // every level was on screen together. With 1.0 / 0.25 / 0.10 a 4.5 m car keeps
                    // LOD A out to about 15 m and LOD B to about 38 m (screen height over distance at
                    // a 60 degree FOV), which is the range a map review actually looks at.
                    float h = i < LodTransitionHeights.Length
                        ? LodTransitionHeights[i]
                        : 0.02f;
                    lods[i] = new LOD(h, sets[i]);
                }
                lg.SetLODs(lods);
                lg.fadeMode = LODFadeMode.None;
                // A LODGroup that ends up saved with m_Enabled: 0 draws EVERY level at once - the same
                // car three times over, with the flat wheel quads that LOD C bakes sitting in front of
                // the real tyres. Set the flag explicitly and check the saved asset below.
                lg.enabled = true;
                lodGroup = lg;
                stats.LodGroups++;
                stats.LodRenderersRegistered += registered;
                if (available != registered)
                    stats.Note(car.CarName + ": " + (available - registered) +
                               " renderer(s) not registered in any LOD level");
            }

            // ---- collider: the game's own physics box, not the mesh bounds (the mesh reaches past
            // the mirrors, mirrors and spoilers)
            if (opt.AddBoxCollider && car.HasPhysicsBox)
            {
                var col = carRoot.AddComponent<BoxCollider>();
                col.center = new Vector3(
                    (car.PhysicsBox[0] + car.PhysicsBox[3]) * 0.5f,
                    (car.PhysicsBox[2] + car.PhysicsBox[5]) * 0.5f,
                    -(car.PhysicsBox[1] + car.PhysicsBox[4]) * 0.5f);
                col.size = new Vector3(
                    Math.Abs(car.PhysicsBox[3] - car.PhysicsBox[0]),
                    Math.Abs(car.PhysicsBox[5] - car.PhysicsBox[2]),
                    Math.Abs(car.PhysicsBox[4] - car.PhysicsBox[1]));
                stats.Colliders++;
            }

            int layer = LayerMask.NameToLayer("NFSU_Other");
            if (layer >= 0)
                carRoot.layer = layer;

            if (opt.SavePrefab)
            {
                string folder = EnsureFolder(opt.ProjectFolder + "/" + CarSub);
                string path = folder + "/" + NfsuDdsWriter.Sanitize(car.CarName) + ".prefab";
                if (PrefabUtility.SaveAsPrefabAsset(carRoot, path))
                {
                    stats.PrefabsSaved++;
                    if (lodGroup != null)
                        VerifySavedLodGroup(car.CarName, path, lodGroup);
                }
                else
                    stats.Note(car.CarName + ": prefab not saved to " + path);
            }
            stats.CarsImported++;
        }

        /// <summary>
        /// Reads the saved prefab back and checks that the LODGroup survived it: enabled, and with
        /// the same number of levels at the same heights. The CARS scene was once reviewed with every
        /// LOD drawn at once because the component serialized <c>m_Enabled: 0</c>, and no in-memory
        /// counter can see that - only the asset on disk can.
        /// </summary>
        void VerifySavedLodGroup(string carName, string prefabPath, LODGroup set)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var got = asset == null ? null : asset.GetComponent<LODGroup>();
            if (got == null)
            {
                stats.Note(carName + ": the saved prefab has no LODGroup");
                return;
            }
            var expect = set.GetLODs();
            var now = got.GetLODs();
            var sb = new StringBuilder();
            bool bad = !got.enabled;
            if (!got.enabled)
                sb.Append(" enabled=false;");
            if (now.Length != expect.Length)
                sb.Append(" levels " + now.Length + " instead of " + expect.Length + ";");
            else
                for (int i = 0; i < now.Length; i++)
                {
                    if (Mathf.Abs(now[i].screenRelativeTransitionHeight -
                                  expect[i].screenRelativeTransitionHeight) > 1e-4f)
                        sb.Append(" height[" + i + "]=" +
                                  now[i].screenRelativeTransitionHeight.ToString("F3",
                                      CultureInfo.InvariantCulture) + ";");
                    if (now[i].renderers.Length != expect[i].renderers.Length)
                        sb.Append(" renderers[" + i + "]=" + now[i].renderers.Length + ";");
                }
            if (sb.Length > 0)
                stats.Note(carName + ": saved LODGroup wrong -" + sb);
            else
                stats.LodGroupsVerified++;
        }

        /// <summary>
        /// The outcome of <see cref="PlanWheels"/>: the tyre's own dimensions, the road plane the
        /// model implies, and the four mounts. Exposed so the self test can dump it and the Python
        /// twin can diff it.
        /// </summary>
        public sealed class WheelPlanResult
        {
            public float Radius;
            public float Width;
            public float HalfWidth;           // the physics box's half-width
            public float FloorZ;              // the physics box's bottom plane (a chassis box)
            public float BodyFloorZ;          // the highest-detail body's own lowest z, model ~ 0
            public float RoadZ;               // the road plane: the wheel patches' own plane
            public int Patches;               // wheel patches the model bakes (0, or 4 per car)
            public bool AuthoredLeft;         // which side the tyre mesh was modelled for
            public readonly List<WheelPlan> Corners = new List<WheelPlan>();
            public string Problem = "";
        }

        /// <summary>
        /// The wheel patch the traffic art bakes into its lower-detail bodies: a flat rectangle that
        /// is the tyre's own footprint (see <see cref="FindWheelPatches"/>).
        /// </summary>
        public sealed class WheelPatch
        {
            public bool Front;
            public bool Left;
            public char Lod;                  // which body carries it
            public int Verts;
            public float X0, X1;              // along the car's length, game metres
            public float InnerY, OuterY;      // the tyre well's |y| edges
            public float Z;                   // the flat plane it lies in: the road plane
            public float AxleX { get { return (X0 + X1) * 0.5f; } }
        }

        /// <summary>
        /// Axle x as a share of the body's half-length, used only when a bundle has neither a wheel
        /// patch nor a frame table - which no traffic bundle has (AMBULANCE has no
        /// <c>0x0013401A</c> chunk but does bake patches, so it never comes here). The value is a
        /// leftover of the old socket-based rule and is deliberately NOT used for BUS/FIRETRUCK,
        /// which do carry a table: it is a last resort that keeps a wheel rig alive, and it is
        /// reported in the stats.
        /// </summary>
        public const float FallbackAxleRatio = 0.94f;

        /// <summary>
        /// Decides where the four wheels of a car go. Public because the self test dumps it to CSV
        /// and the Python twin diffs that - the rule must have exactly one implementation.
        /// <para>
        /// Primary source: the wheel patches the art bakes into the model's own lower-detail bodies
        /// (<see cref="FindWheelPatches"/>). Each gives everything: the axle x is its x centre, the
        /// tyre is centred in its y band, and its plane is the road, so the axle height is
        /// plane + tyre radius and the tread touches that plane exactly. Over the 13 bundles that
        /// bake them (52 corners) that yields real wheelbases (4DR_SEDAN 2.68 m on a 4.54 m body,
        /// SUV 3.14 m, PANELVAN 3.10 m) with the road plane 0.12-0.31 m under the body's own floor,
        /// the tyre's outer face a median -0.007 m from the patch's own outer edge, and the sidewall
        /// a median -0.005 m from the body skin (nfs_probe/car_plan_check.py).
        /// </para>
        /// <para>
        /// The <c>0x0013401A</c> frame table is used only where no patch exists (BUS, FIRETRUCK: two
        /// LODs, nothing under the A-body floor - 8 corners) and is reported as such, because the
        /// table is not the wheel mount: on 4DR_SEDAN its translations sit at x = +2.04/-2.16, the
        /// body's extreme ends, a 4.2 m wheelbase, with an axle 0.28-0.38 m above the patch's. Its
        /// lateral reading is still what the fallback uses: the tyre mesh is modelled one-sided at the
        /// origin with its tread running from its own y = 0 plane out to y = width (4DR_SEDAN: y in
        /// [0.001, 0.222], x in [-0.363, 0.363], z in [-0.346, 0.346] - a prism with one sidewall in
        /// its own y = 0 plane), so the mount is the tyre's INNER face, its outer face lands at
        /// |y| + width, and where the table lists a narrow/wide pair the outermost is the one that
        /// puts the sidewall at or outside the body's skin (nfs_probe/car_sidewall_fit.py).
        /// </para>
        /// </summary>
        public static bool PlanWheels(NfsuCarBundle car, WheelPlanResult plan)
        {
            NfsuCarBundle.NfsuCarPart tyreA = null;
            foreach (var p in car.Parts)
                if (p.IsWheel && (tyreA == null || p.Lod < tyreA.Lod))
                    tyreA = p;
            if (tyreA == null)
            {
                plan.Problem = "no tyre part";
                return false;
            }
            var ext = TyreExtents(tyreA.Drawable);
            if (ext == null)
            {
                plan.Problem = "tyre mesh has no usable bounds";
                return false;
            }
            plan.Radius = ext[0];
            plan.Width = ext[1];
            plan.AuthoredLeft = ext[3] >= 0f;     // the tread runs out towards the mesh's own +y

            float halfLen = car.HasPhysicsBox
                ? (car.PhysicsBox[3] - car.PhysicsBox[0]) * 0.5f
                : BodySpan(car)[0];
            plan.HalfWidth = car.HasPhysicsBox
                ? (car.PhysicsBox[4] - car.PhysicsBox[1]) * 0.5f
                : BodySpan(car)[1];
            plan.FloorZ = car.HasPhysicsBox ? car.PhysicsBox[2] : 0f;
            float boxCx = car.HasPhysicsBox ? (car.PhysicsBox[3] + car.PhysicsBox[0]) * 0.5f : 0f;
            plan.BodyFloorZ = BodyFloorZ(car);

            var patches = FindWheelPatches(car, plan.Radius, plan.Width, boxCx);
            plan.Patches = patches.Count;
            bool roadSet = false;
            foreach (var pt in patches)
                if (!roadSet || pt.Z < plan.RoadZ)
                {
                    plan.RoadZ = pt.Z;
                    roadSet = true;
                }

            var sockets = new List<NfsuCarBundle.NfsuCarFrame>();
            foreach (var p in car.Parts)
                if (p.IsBody)
                    sockets.AddRange(p.Frames);

            foreach (bool front in new[] { true, false })
                foreach (bool left in new[] { true, false })
                {
                    var corner = new WheelPlan
                    {
                        Name = (front ? "F" : "R") + (left ? "L" : "R"),
                        Front = front,
                        Left = left,
                    };
                    var pt = FindPatch(patches, front, left);
                    var src = NfsuWheelSource.Patch;
                    if (pt == null)
                    {
                        // complete the corner from its twin: first the same axle's other side (the
                        // left and right patches of one axle agree to a few mm), then the same
                        // side's other axle
                        pt = FindPatch(patches, front, !left);
                        src = NfsuWheelSource.PatchMirror;
                        if (pt == null)
                            pt = FindPatch(patches, !front, left);
                    }
                    if (pt != null)
                    {
                        float ax = (pt.X0 + pt.X1) * 0.5f;
                        if (pt.Front != front)
                            ax = -ax;                       // mirrored along the car's length
                        // Centre the tyre in the well the patch marks: the patch band IS where the
                        // art puts the wheel, so a tyre mesh narrower or wider than that band
                        // differences out evenly over both sides. This is deliberately evaluated in
                        // double: the sum of two float32 values, and both halvings, are EXACT in
                        // double, so the expression rounds exactly once - and the Python twin does
                        // the same. Writing it as a float32-by-float32 sequence is not a stable
                        // contract between the two implementations: the runtime contracts the
                        // multiply-subtract and keeps the band unrounded, which moved 9 of the 60
                        // mounts by one unit in the last place (nfs_probe/car_wheel_ulp.py).
                        float iy = (float)(((double)pt.InnerY + (double)pt.OuterY) * 0.5
                                           - (double)plan.Width * 0.5);
                        if (iy < 0.05f)
                            iy = 0.05f;                     // never across the car's centreline
                        corner.Source = src;
                        corner.X = ax;
                        corner.Y = (left ? 1f : -1f) * iy;
                        corner.Z = pt.Z + plan.Radius;      // tread touches the patch's own plane
                        corner.PatchInnerY = pt.InnerY;
                        corner.PatchOuterY = pt.OuterY;
                    }
                    else
                    {
                        NfsuCarBundle.NfsuCarFrame best = null;
                        foreach (var f in sockets)
                        {
                            if (front != (f.X >= boxCx) || left != (f.Y >= 0f))
                                continue;
                            // The outer candidate is the mount: see the comment on PlanWheels. The
                            // frame keys say nothing on their own - the same four keys show up on
                            // headlight parts of other bundles.
                            if (best == null || Mathf.Abs(f.Y) > Mathf.Abs(best.Y) + 1e-4f)
                                best = f;
                        }
                        if (best != null)
                        {
                            corner.Source = NfsuWheelSource.Socket;
                            corner.SocketKey = best.Key;
                            corner.X = best.X;
                            corner.Y = best.Y;
                            corner.Z = best.Z;
                        }
                        else
                        {
                            // no patch and no frame table: the last resort, from the box only
                            corner.Source = NfsuWheelSource.Body;
                            corner.X = (front ? 1f : -1f) * FallbackAxleRatio * halfLen;
                            corner.Z = plan.Radius + plan.FloorZ;
                            corner.Skin = BodySkinNear(car, corner.X, corner.Z, plan.Radius);
                            corner.Y = (left ? 1f : -1f) *
                                       Mathf.Max(0.05f, corner.Skin - plan.Width);
                        }
                    }
                    if (!corner.SkinSet)
                    {
                        corner.Skin = BodySkinNear(car, corner.X, corner.Z, plan.Radius);
                        corner.SkinSet = true;
                    }
                    corner.OuterFace = Mathf.Abs(corner.Y) + plan.Width;
                    corner.SidewallGap = corner.OuterFace - corner.Skin;
                    corner.GroundGap = corner.Z - plan.Radius - plan.FloorZ;
                    plan.Corners.Add(corner);
                }
            if (plan.Corners.Count < 2)
            {
                plan.Problem = "only " + plan.Corners.Count + " wheel mounts found";
                return false;
            }
            return true;
        }

        /// <summary>How far a patch's x span may be from one tyre diameter, metres.</summary>
        const float PatchSpanTolM = 0.25f;
        /// <summary>A patch is a flat rectangle: the largest allowed spread of its z, metres.</summary>
        const float PatchZSpreadM = 0.03f;
        const int PatchMinVerts = 3;
        /// <summary>A patch must hang this far below the highest-detail body's floor plane.</summary>
        const float PatchFloorDropM = 0.02f;

        /// <summary>
        /// Reads the wheel patches the traffic art bakes into its own lower-detail bodies: one flat
        /// rectangle per corner, built from the body vertices that hang below the highest-detail
        /// body's floor plane. A rectangle is kept only when it reads as a tyre's footprint: its x
        /// span within 25 cm of one tyre diameter, its y span between half and 2.5 tyre widths, its
        /// z spread under 3 cm (it is flat), at least 3 vertices, and its inner edge away from the
        /// car's own centreline. Measured over the 13 bundles that bake them, the x spans are
        /// 0.559-0.728 m against tyre diameters of 0.583-0.776, the y spans 0.214-0.323 m against
        /// tyre widths of 0.181-0.323, and every patch's z spread is 0.000 - the shape the rule
        /// assumes (nfs_probe/car_wheel_patches.py). BUS and FIRETRUCK bake nothing: they have two
        /// LODs and no vertex of BODY_B under BODY_A's floor, so they get no patch.
        /// <para>
        /// Each patch is a corner's tyre well: its x centre is the axle, its y band is where the art
        /// puts the tyre (4DR_SEDAN front-left: x +0.990..+1.717, y +0.645..+0.869, z -0.191 against
        /// a tyre of 2r = 0.709 and w = 0.221), and its plane is the road the tread sits on.
        /// </para>
        /// </summary>
        public static List<WheelPatch> FindWheelPatches(
            NfsuCarBundle car, float radius, float width, float centreX)
        {
            var found = new List<WheelPatch>();
            var bodies = new SortedList<char, NfsuDrawable>();
            foreach (var p in car.Parts)
                if (p.IsBody && !bodies.ContainsKey(p.Lod))
                    bodies.Add(p.Lod, p.Drawable);
            if (bodies.Count < 2)
                return found;
            char top = bodies.Keys[0];
            float floor = BodyFloorZ(car);
            // the cheapest mesh first - that is the one that bakes the patches
            for (int k = bodies.Count - 1; k >= 1; k--)
            {
                char lod = bodies.Keys[k];
                if (lod == top)
                    continue;
                var d = bodies[lod];
                var boxes = new Dictionary<int, float[]>();     // key: front*2 + left
                for (int i = 0; i < d.VertCount; i++)
                {
                    int o = i * d.Stride + OffPos;
                    float x = BitConverter.ToSingle(d.Verts, o);
                    float y = BitConverter.ToSingle(d.Verts, o + 4);
                    float z = BitConverter.ToSingle(d.Verts, o + 8);
                    if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) ||
                        z >= floor - PatchFloorDropM)
                        continue;
                    int key = ((x >= centreX) ? 2 : 0) + ((y >= 0f) ? 1 : 0);
                    float[] b;
                    if (!boxes.TryGetValue(key, out b))
                        boxes[key] = b = new[] { x, x, Math.Abs(y), Math.Abs(y), z, z, 0f };
                    b[0] = Math.Min(b[0], x);
                    b[1] = Math.Max(b[1], x);
                    b[2] = Math.Min(b[2], Math.Abs(y));
                    b[3] = Math.Max(b[3], Math.Abs(y));
                    b[4] = Math.Min(b[4], z);
                    b[5] = Math.Max(b[5], z);
                    b[6] += 1f;
                }
                foreach (var kv in boxes)
                {
                    var b = kv.Value;
                    if (b[6] < PatchMinVerts)
                        continue;
                    float sx = b[1] - b[0];
                    float sy = b[3] - b[2];
                    if (Math.Abs(sx - 2f * radius) > PatchSpanTolM)
                        continue;
                    if (sy < 0.5f * width || sy > 2.5f * width)
                        continue;
                    if (b[5] - b[4] > PatchZSpreadM)
                        continue;
                    if (b[2] < 0.05f)
                        continue;
                    found.Add(new WheelPatch
                    {
                        Front = (kv.Key & 2) != 0,
                        Left = (kv.Key & 1) != 0,
                        Lod = lod,
                        Verts = (int)b[6],
                        X0 = b[0],
                        X1 = b[1],
                        InnerY = b[2],
                        OuterY = b[3],
                        Z = b[4],
                    });
                }
            }
            return found;
        }

        /// <summary>The patch stored for one corner, or null.</summary>
        static WheelPatch FindPatch(List<WheelPatch> patches, bool front, bool left)
        {
            foreach (var pt in patches)
                if (pt.Front == front && pt.Left == left)
                    return pt;
            return null;
        }

        /// <summary>
        /// The lowest z of the highest-detail body: the model's own floor plane. Measured over the 15
        /// traffic cars it is -0.057 .. +0.040 m, so the model's origin sits at the bottom of the body
        /// shell - and NOT on the road: the wheel patches put the road 0.12-0.31 m below it, and the
        /// physics box floor 0.12-0.39 m above that again (a chassis box).
        /// </summary>
        static float BodyFloorZ(NfsuCarBundle car)
        {
            var d = BodyLodA(car);
            if (d == null)
                return 0f;
            float best = float.MaxValue;
            for (int i = 0; i < d.VertCount; i++)
            {
                float z = BitConverter.ToSingle(d.Verts, i * d.Stride + OffPos + 8);
                if (IsFinite(z) && z < best)
                    best = z;
            }
            return best == float.MaxValue ? 0f : best;
        }

        /// <summary>
        /// The body's own outer skin on the side, inside the footprint a tyre would occupy: the
        /// largest |y| over the LOD-A body vertices with |x - axle.x| &lt;= radius and
        /// |z - axle.z| &lt;= radius. That is the plane the tyre has to reach to be visible, and it
        /// is the wheel-arch's outer edge: measured against it, the mount the frame table yields has
        /// the tyre's outer face a median +0.087 m outside the skin (p10 -0.044, p90 +0.187), i.e.
        /// never properly buried, while centring the tyre mesh on its own mid-plane gives a median
        /// -0.072 m and the inner frame candidate -0.10 .. -0.23 m (nfs_probe/car_sidewall_fit.py).
        /// </summary>
        static float BodySkinNear(NfsuCarBundle car, float ax, float az, float radius)
        {
            var d = BodyLodA(car);
            if (d == null)
                return 0f;
            float best = 0f;
            bool any = false;
            for (int i = 0; i < d.VertCount; i++)
            {
                int o = i * d.Stride + OffPos;
                float x = BitConverter.ToSingle(d.Verts, o);
                float z = BitConverter.ToSingle(d.Verts, o + 8);
                if (Math.Abs(x - ax) > radius || Math.Abs(z - az) > radius)
                    continue;
                float y = Math.Abs(BitConverter.ToSingle(d.Verts, o + 4));
                if (!IsFinite(y))
                    continue;
                if (!any || y > best)
                    best = y;
                any = true;
            }
            return best;
        }

        /// <summary>The highest-detail body drawable, or null when the bundle has none.</summary>
        static NfsuDrawable BodyLodA(NfsuCarBundle car)
        {
            NfsuCarBundle.NfsuCarPart best = null;
            foreach (var p in car.Parts)
                if (p.IsBody && (best == null || p.Lod < best.Lod))
                    best = p;
            return best == null ? null : best.Drawable;
        }

        /// <summary>[half length, half width] of the body meshes, used when there is no physics box.</summary>
        static float[] BodySpan(NfsuCarBundle car)
        {
            float lx = 0f, ly = 0f;
            foreach (var p in car.Parts)
            {
                if (!p.IsBody)
                    continue;
                var d = p.Drawable;
                for (int i = 0; i < d.VertCount; i++)
                {
                    int o = i * d.Stride + OffPos;
                    float x = Math.Abs(BitConverter.ToSingle(d.Verts, o));
                    float y = Math.Abs(BitConverter.ToSingle(d.Verts, o + 4));
                    if (x > lx)
                        lx = x;
                    if (y > ly)
                        ly = y;
                }
            }
            return new[] { lx, ly };
        }

        /// <summary>
        /// Builds the four wheel nodes of one car and returns their renderers grouped by LOD letter
        /// so the caller can fold them into the car's LODGroup.
        /// </summary>
        Dictionary<char, List<Renderer>> BuildWheels(
            NfsuCarBundle car, GameObject carRoot,
            SortedList<char, NfsuCarBundle.NfsuCarPart> wheelLods)
        {
            var plan = new WheelPlanResult();
            if (!PlanWheels(car, plan))
            {
                stats.Note(car.CarName + ": " + plan.Problem + " - no wheel rig");
                return null;
            }
            if (plan.Patches == 0)
                stats.Note(car.CarName + ": bakes no wheel patch (two LODs, nothing under the " +
                           "A-body floor) - wheels taken from the 0x0013401A frame table, whose " +
                           "positions are NOT verified by the model's own art");
            else if (plan.Patches < 4)
                stats.Note(car.CarName + ": only " + plan.Patches + " wheel patches baked - the " +
                           "other corners are mirrored from them");
            float radius = plan.Radius;
            // Radial recentring only: the tyre rolls about its axle, and its lateral offset from the
            // mount (the tread runs from the mount outwards) has to stay.
            var pivot = TyrePivot(wheelLods.Values[0].Drawable);

            var rig = carRoot.AddComponent<NfsuTrafficCarRig>();
            var nodes = new List<NfsuTrafficCarRig.WheelNode>();
            var perLod = new Dictionary<char, List<Renderer>>();

            foreach (var c in plan.Corners)
            {
                var mount = new GameObject("Wheel_" + c.Name);
                mount.transform.SetParent(carRoot.transform, false);
                mount.transform.localPosition = new Vector3(
                    c.X,                        // game forward -> Unity forward
                    c.Z,                        // game up      -> Unity up
                    -c.Y);                      // game +Y (left) is Unity -Z

                var spin = new GameObject("Spin_" + c.Name);
                spin.transform.SetParent(mount.transform, false);

                var holder = new GameObject("Tyre_" + c.Name);
                holder.transform.SetParent(spin.transform, false);
                if (c.Left != plan.AuthoredLeft)
                {
                    // Half a turn about the car's forward axis shows the other sidewall. It is a
                    // rotation, not a mirror, so the winding and the normals stay valid; the tyre is
                    // radially symmetric, so nothing about its silhouette changes - and it also flips
                    // the tread offset to the side this corner needs.
                    holder.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
                    stats.WheelsMirrored++;
                }

                foreach (var kv in wheelLods)
                {
                    var mesh = MeshOf(kv.Value, pivot);
                    if (mesh == null)
                        continue;
                    var go = new GameObject("Tyre_" + kv.Key + "_" + c.Name);
                    go.transform.SetParent(holder.transform, false);
                    var mf = go.AddComponent<MeshFilter>();
                    var mr = go.AddComponent<MeshRenderer>();
                    mf.sharedMesh = mesh;
                    ApplyMaterials(mr, kv.Value.Drawable);
                    List<Renderer> list;
                    if (!perLod.TryGetValue(kv.Key, out list))
                        perLod[kv.Key] = list = new List<Renderer>();
                    list.Add(mr);
                    stats.PartsImported++;
                }
                stats.WheelsBuilt++;

                switch (c.Source)
                {
                    case NfsuWheelSource.Patch: stats.WheelsFromPatch++; break;
                    case NfsuWheelSource.PatchMirror: stats.WheelsFromPatchMirror++; break;
                    case NfsuWheelSource.Socket: stats.WheelsFromSocket++; break;
                    case NfsuWheelSource.Body: stats.WheelsFromBody++; break;
                }
                if (c.PatchOuterY > 0f)
                {
                    float fit = Mathf.Abs(c.OuterFace - c.PatchOuterY);
                    if (fit > stats.WorstPatchFitM)
                        stats.WorstPatchFitM = fit;
                }
                float burial = Mathf.Max(0f, -c.SidewallGap);
                if (Mathf.Abs(c.SidewallGap) > stats.WorstSidewallGapM)
                    stats.WorstSidewallGapM = Mathf.Abs(c.SidewallGap);
                if (burial > stats.WorstBurialM)
                    stats.WorstBurialM = burial;
                if (Mathf.Abs(c.GroundGap) > stats.WorstGroundGapM)
                    stats.WorstGroundGapM = Mathf.Abs(c.GroundGap);

                nodes.Add(new NfsuTrafficCarRig.WheelNode
                {
                    name = c.Name,
                    isFront = c.Front,
                    mount = mount.transform,
                    spin = spin.transform,
                    radius = radius,
                });
            }

            rig.Wheels = nodes.ToArray();
            rig.Animate = false;          // parked until something drives it
            rig.InvalidateBounds();
            return perLod;
        }

        /// <summary>
        /// [radius, width, cx, cy, cz] of a tyre drawable, from its own vertices in game space. The
        /// tread width is the narrowest extent; the other two span the wheel's diameter.
        /// </summary>
        static float[] TyreExtents(NfsuDrawable d)
        {
            if (d.VertCount == 0 || d.Stride < OffPos + 12)
                return null;
            float minx = 1e9f, maxx = -1e9f, miny = 1e9f, maxy = -1e9f, minz = 1e9f, maxz = -1e9f;
            for (int i = 0; i < d.VertCount; i++)
            {
                int o = i * d.Stride + OffPos;
                float x = BitConverter.ToSingle(d.Verts, o);
                float y = BitConverter.ToSingle(d.Verts, o + 4);
                float z = BitConverter.ToSingle(d.Verts, o + 8);
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                    return null;
                if (x < minx) minx = x;
                if (x > maxx) maxx = x;
                if (y < miny) miny = y;
                if (y > maxy) maxy = y;
                if (z < minz) minz = z;
                if (z > maxz) maxz = z;
            }
            float sx = maxx - minx, sy = maxy - miny, sz = maxz - minz;
            if (sx <= 1e-4f || sy <= 1e-4f || sz <= 1e-4f)
                return null;
            float width, radius;
            if (sy <= sx && sy <= sz)
            {
                width = sy;
                radius = (sx + sz) * 0.25f;
            }
            else if (sx <= sy && sx <= sz)
            {
                width = sx;
                radius = (sy + sz) * 0.25f;
            }
            else
            {
                width = sz;
                radius = (sx + sy) * 0.25f;
            }
            return new[] { radius, width, (minx + maxx) * 0.5f, (miny + maxy) * 0.5f,
                           (minz + maxz) * 0.5f };
        }

        /// <summary>
        /// The pivot a tyre mesh is built around: its radial centre in x and z (so the roll node
        /// spins it about its own axle) but NOT in y - the mesh's tread starts in its own y = 0
        /// plane and the socket marks that plane, so keeping y unshifted is what puts the sidewall
        /// where the frame table says it is. Centring y as well would move every tyre inboard by
        /// half its width and bury the outer face under the body skin (measured on all 15 traffic
        /// cars: median outer-face-minus-skin goes from +0.09 m to -0.07 m, nfs_probe/
        /// car_sidewall_fit.py).
        /// </summary>
        static Vector3 TyrePivot(NfsuDrawable d)
        {
            var e = TyreExtents(d);
            return e == null ? Vector3.zero : new Vector3(e[2], 0f, e[4]);
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>
        /// Unity-space mesh for one car drawable, cached per BinKey. <paramref name="recentre"/>
        /// subtracts a game-space point, which is how a tyre mesh ends up centred on its own axle
        /// instead of spinning about the model origin.
        /// </summary>
        Mesh MeshOf(NfsuCarBundle.NfsuCarPart part, Vector3? recentre)
        {
            var d = part.Drawable;
            // A car drawable's BinKey is unique, so the key alone identifies the mesh; the high bit
            // only separates the recentred (tyre) variant from the raw one.
            uint cacheKey = d.BinKey ^ (recentre.HasValue ? 0x80000000u : 0u);
            Mesh cached;
            if (meshCache.TryGetValue(cacheKey, out cached) && cached != null)
                return cached;

            int vc = d.VertCount;
            var verts = new Vector3[vc];
            var normals = new Vector3[vc];
            var uvs = new Vector2[vc];
            var cols = new Color32[vc];
            bool anyNormal = false;
            for (int i = 0; i < vc; i++)
            {
                int o = i * d.Stride;
                float x = BitConverter.ToSingle(d.Verts, o + OffPos);
                float y = BitConverter.ToSingle(d.Verts, o + OffPos + 4);
                float z = BitConverter.ToSingle(d.Verts, o + OffPos + 8);
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) ||
                    Math.Abs(x) > 1e4f || Math.Abs(y) > 1e4f || Math.Abs(z) > 1e4f)
                {
                    stats.Note("absurd vertex in " + Key(d.BinKey) + " " + d.Name + " at #" + i);
                    return null;
                }
                if (recentre.HasValue)
                {
                    x -= recentre.Value.x;
                    y -= recentre.Value.y;
                    z -= recentre.Value.z;
                }
                verts[i] = new Vector3(x, z, -y);         // game Z-up -> Unity Y-up

                float nx = BitConverter.ToSingle(d.Verts, o + OffNormal);
                float ny = BitConverter.ToSingle(d.Verts, o + OffNormal + 4);
                float nz = BitConverter.ToSingle(d.Verts, o + OffNormal + 8);
                if (IsFinite(nx) || IsFinite(ny) || IsFinite(nz))
                {
                    normals[i] = new Vector3(nx, nz, -ny).normalized;
                    anyNormal = anyNormal || !Mathf.Approximately(normals[i].sqrMagnitude, 0f);
                }

                float u = BitConverter.ToSingle(d.Verts, o + OffUv);
                float v = BitConverter.ToSingle(d.Verts, o + OffUv + 4);
                if (!IsFinite(u) || !IsFinite(v))
                {
                    u = 0f;
                    v = 0f;
                }
                uvs[i] = new Vector2(u, opt.UvFlipV ? 1f - v : v);

                byte b0 = d.Verts[o + OffColour], b1 = d.Verts[o + OffColour + 1];
                byte b2 = d.Verts[o + OffColour + 2], b3 = d.Verts[o + OffColour + 3];
                cols[i] = new Color32(b2, b1, b0, b3);    // D3DCOLOR is B,G,R,A in memory
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
                    stats.Note(string.Format(CultureInfo.InvariantCulture,
                        "index {0} >= {1} verts in {2} {3}", raw, vc, Key(d.BinKey), d.Name));
                    return null;
                }
                idx[i] = raw;
            }

            var mesh = new Mesh
            {
                name = string.Format(CultureInfo.InvariantCulture, "{0}_{1}{2}", Key(d.BinKey),
                    NfsuDdsWriter.Sanitize(d.Name), recentre.HasValue ? "_c" : ""),
                subMeshCount = Math.Max(1, d.Submeshes.Count),
            };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.colors32 = cols;

            if (d.Submeshes.Count == 0)
            {
                mesh.subMeshCount = 1;
                mesh.SetTriangles(idx, 0);
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
                    var tri = new int[count];
                    for (int k = 0; k < count; k++)
                        tri[k] = idx[start + k];
                    mesh.SetTriangles(tri, s);
                    covered += count;
                }
                if (covered == 0)
                {
                    mesh.subMeshCount = 1;
                    mesh.SetTriangles(idx, 0);
                }
            }

            // Cars do store per-vertex normals (unlike scenery, which has none); they are only used
            // by lit shaders, and ours is unlit, but keeping them makes the asset useful elsewhere.
            if (anyNormal)
                mesh.SetNormals(normals);
            else
                mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            stats.MeshesBuilt++;
            stats.Vertices += vc;
            stats.Triangles += indexCount / 3;

            if (opt.SaveMeshAssets)
            {
                string folder = EnsureFolder(opt.ProjectFolder + "/" + MeshSub);
                string path = folder + "/" + mesh.name + ".mesh";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null)
                {
                    meshCache[cacheKey] = existing;
                    stats.MeshAssets++;
                    return existing;
                }
                var asset = UnityEngine.Object.Instantiate(mesh);
                asset.name = mesh.name;
                AssetDatabase.CreateAsset(asset, path);
                meshCache[cacheKey] = asset;
                stats.MeshAssets++;
                return asset;
            }
            meshCache[cacheKey] = mesh;
            return mesh;
        }

        // ------------------------------------------------------------------ textures, materials

        void CollectTextureKeys(NfsuCarBundle car)
        {
            foreach (var p in car.Parts)
            {
                var d = p.Drawable;
                int used = 0;
                foreach (var sm in d.Submeshes)
                {
                    if (sm.Slot < 0 || sm.Slot >= d.Slots.Length || d.Slots[sm.Slot] == 0)
                        continue;
                    wantedKeys.Add(d.Slots[sm.Slot]);
                    used++;
                }
                if (used > 1)
                    foreach (var sm in d.Submeshes)
                        if (sm.Slot >= 0 && sm.Slot < d.Slots.Length && d.Slots[sm.Slot] != 0)
                            multiSlotKeys.Add(d.Slots[sm.Slot]);
            }
        }

        /// <summary>
        /// Writes every wanted texture record as a .dds under Assets/NFSU/Textures and lets Unity
        /// import it, then loads the imported Texture2D back. Called once, after every car's keys have
        /// been collected (see <see cref="Run"/>): the loop is over the pack records, not over cars,
        /// and Unity's asset pipeline is only run once here.
        /// </summary>
        void ImportTextures()
        {
            if (!opt.WriteTextures || texturesDone)
                return;
            texturesDone = true;
            string folder = EnsureFolder(opt.ProjectFolder + "/" + TexSub);
            var paths = new List<string>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kv in texSources)
                {
                    if (!wantedKeys.Contains(kv.Key))
                        continue;
                    var src = kv.Value;
                    string path = folder + "/" + NfsuDdsWriter.RecordFileName(src.Record) + ".dds";
                    if (File.Exists(path))
                    {
                        src.AssetPath = path;
                        stats.TexturesReused++;
                        continue;
                    }
                    byte[] dds = NfsuDdsWriter.BuildDds(src.Record, src.Blob);
                    if (dds == null)
                    {
                        stats.Note(string.Format(CultureInfo.InvariantCulture,
                            "texture {0} {1}: format {2} not written", Key(src.Record.BinKey),
                            src.Record.Name, NfsuDdsWriter.FormatName(src.Record.Format)));
                        continue;
                    }
                    File.WriteAllBytes(path, dds);
                    src.AssetPath = path;
                    paths.Add(path);
                    stats.TexturesWritten++;
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
                kv.Value.Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(kv.Value.AssetPath);
                if (kv.Value.Texture == null)
                    stats.Note("Unity did not import " + kv.Value.AssetPath);
            }
            foreach (var k in wantedKeys)
                if (!texSources.ContainsKey(k))
                    stats.TexturesMissing++;
        }

        Material MaterialFor(uint texKey)
        {
            TexSrc src = null;
            if (texKey != 0)
                texSources.TryGetValue(texKey, out src);
            if (src == null)
            {
                stats.SlotsWithoutTexture++;
                return null;
            }

            NfsuAlphaClass cls;
            switch (opt.AlphaMode)
            {
                case NfsuAlphaMode.Opaque: cls = NfsuAlphaClass.Opaque; break;
                case NfsuAlphaMode.Cutout: cls = NfsuAlphaClass.Cutout; break;
                case NfsuAlphaMode.Blend: cls = NfsuAlphaClass.Blend; break;
                case NfsuAlphaMode.Additive: cls = NfsuAlphaClass.Additive; break;
                default:
                    if (!src.ClassDone)
                    {
                        src.Class = NfsuAlphaClassifier.Classify(src.Record, src.Blob,
                                                                multiSlotKeys.Contains(texKey));
                        src.ClassDone = true;
                    }
                    cls = src.Class;
                    break;
            }

            string shaderName = NfsuAlphaClassifier.ShaderName(cls);
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                stats.Note("shader not found: " + shaderName + " - import the .shader files first");
                return null;
            }

            string mode = NfsuAlphaClassifier.ModeName(cls);
            if (src.Texture == null)
                NoteOnce("no imported texture for " + Key(texKey) + " " + src.Record.Name +
                         ": the material will render untextured");
            string cacheKey = Key(texKey) + ":" + mode;
            Material cached;
            if (matCache.TryGetValue(cacheKey, out cached) && cached != null)
                return cached;

            string folder = EnsureFolder(opt.ProjectFolder + "/" + MatSub);
            string path = string.Format(CultureInfo.InvariantCulture, "{0}/0x{1:X8}_{2}_{3}.mat",
                folder, texKey, NfsuDdsWriter.Sanitize(src.Record.Name), mode);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = mat == null;
            if (created)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
                stats.MaterialsCreated++;
            }
            else
            {
                // A material asset from an earlier run is reused (its file name encodes the alpha
                // mode, so a changed rule produces a new file rather than a stale one), but its
                // shader and texture are re-pointed at what THIS run resolved.
                stats.MaterialsReused++;
                if (mat.shader != shader)
                    mat.shader = shader;
            }

            bool dirty = created;
            if (src.Texture != null && mat.mainTexture != src.Texture)
            {
                mat.mainTexture = src.Texture;
                dirty = true;
            }
            if (cls == NfsuAlphaClass.Cutout && !Mathf.Approximately(mat.GetFloat("_Cutoff"), 0.5f))
            {
                mat.SetFloat("_Cutoff", 0.5f);
                dirty = true;
            }
            if (dirty && !created)
                EditorUtility.SetDirty(mat);

            // The four-way split counts every material this run uses, new or reused: "0 materials"
            // from a second run would otherwise hide which shader the cars actually render with.
            stats.MaterialsUsed++;
            switch (cls)
            {
                case NfsuAlphaClass.Cutout: stats.MaterialsCutout++; break;
                case NfsuAlphaClass.Blend: stats.MaterialsBlend++; break;
                case NfsuAlphaClass.Additive: stats.MaterialsAdditive++; break;
                default: stats.MaterialsOpaque++; break;
            }
            matCache[cacheKey] = mat;
            return mat;
        }

        /// <summary>One material per submesh, taken from that submesh's texture-slot BinKey.</summary>
        void ApplyMaterials(MeshRenderer mr, NfsuDrawable d)
        {
            int n = Math.Max(1, d.Submeshes.Count);
            var mats = new Material[n];
            if (d.Submeshes.Count == 0)
                mats[0] = MaterialFor(d.Slots.Length > 0 ? d.Slots[0] : 0u);
            else
                for (int s = 0; s < d.Submeshes.Count; s++)
                {
                    int slot = d.Submeshes[s].Slot;
                    mats[s] = slot >= 0 && slot < d.Slots.Length
                        ? MaterialFor(d.Slots[slot])
                        : null;
                }
            mr.sharedMaterials = mats;
        }

        static string EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder))
                return folder;
            int i = folder.LastIndexOf('/');
            if (i <= 0)
                return folder;
            string parent = EnsureFolder(folder.Substring(0, i));
            string name = folder.Substring(i + 1);
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(parent, name);
            return folder;
        }

        static string Key(uint v)
        {
            return string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", v);
        }

        /// <summary>
        /// A note that must appear at most once. Per-part loops reach the same complaint for every
        /// LOD of the same drawable, and a report that repeats a problem 73 times hides the notes
        /// that matter.
        /// </summary>
        void NoteOnce(string note)
        {
            if (!_noted.Add(note))
                return;
            stats.Note(note);
        }

        static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }
    }
}
