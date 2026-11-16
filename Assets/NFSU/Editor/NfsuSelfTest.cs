// Headless cross-check of the C# reader against the validated Python reference reader.
//
//   "C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe" -batchmode -nographics ^
//     -projectPath "<project>" -executeMethod NFSU.NfsuSelfTest.RunAll ^
//     -logFile "<log>" -quit
//
// DumpCsv writes a line-oriented dump of a bundle that the Python reference reader writes
// identically (see cs_parity_check.py), so the C# port can be diffed against the
// byte-checked Python layout parsing. The Import* methods exercise the whole asset and scene
// build without any GUI: textures, materials, meshes, GameObjects, layers, colliders.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NFSU
{
    public static class NfsuSelfTest
    {
        public const string OutDir = @"C:/Users/Admin/AppData/Local/Temp/nfs_probe/cs_dump";
        public const string RxScenePath = "Assets/NFSU/Scenes/RX_smoke.unity";
        public const string RaScenePath = "Assets/NFSU/Scenes/RA_batch.unity";
        public const string RxRootName = "NFSU_MAP_RX";
        public const string RaRootName = "NFSU_MAP_RA";

        /// <summary>The 40 lowest-numbered RA sections: city-scale smoke test batch.</summary>
        const string RaBatchIds = "101,102,103,104,105,106,107,108,109,111,112,113,114,115,116," +
                                  "117,118,121,122,123,124,125,126,127,128,129,130,131,132,133," +
                                  "134,135,136,137,201,202,203,204,205,206";

        [MenuItem("NFSU/Self Test - dump RX + build RX scene")]
        public static void RunAll()
        {
            DumpCsv("STREAML1RX.BUN", "rx_cs.csv");
            // One clean sweep before the first build: a material's file name encodes its alpha
            // mode, so leftovers from an older probe would be reused and hide the current rule.
            using (var cleaner = new NfsuMapBuilder(new NfsuImportOptions()))
                cleaner.ClearGeneratedAssets();
            ImportRxIntoScene();
            DumpCsv("STREAML1RA.BUN", "ra_cs.csv");
            DumpAlphaClasses("STREAML1RA.BUN", "GLOBALA.BUN", "GLOBALB.BUN");
            DumpTrackB();
            ImportRaBatchIntoScene();
            // The car parse and the wheel rule are diffed against the Python twin by
            // nfs_probe\cars_parity.py; the scene build is what shows the wheel rig as real assets.
            DumpCarsCsv();
            BuildCarsScene();
            Debug.Log("NFSU self test finished, dumps in " + OutDir);
        }

        [MenuItem("NFSU/Self Test - dump RA parse to CSV")]
        public static void DumpRaCsv()
        {
            DumpCsv("STREAML1RA.BUN", "ra_cs.csv");
        }

        /// <summary>
        /// Dumps every Tracks\TRACKB####.lzc: one inventory row per chunk of the decompressed
        /// stream (with the record count the shipped loader would compute), one row per
        /// ambient-traffic corridor of the 0x00034121 chain, and one row per road link of
        /// 0x00034132 with its coordinates as the record's own 1/8-metre integers. The row layouts
        /// are identical to nfs_probe/nfsu_route.py, so trackb_parity.py can diff the two readers
        /// the same way cs_parity_check.py diffs the bundle parse. It also re-runs the grid
        /// containment test (every link must start within half a cell of a cell that lists it),
        /// which is what proves the /8 scale and the +0x08/+0x10 field offsets on the C# side.
        /// </summary>
        [MenuItem("NFSU/Self Test - dump TRACKB routes to CSV")]
        public static void DumpTrackB()
        {
            Directory.CreateDirectory(OutDir);
            var inv = new StringBuilder();
            var paths = new StringBuilder();
            var links = new StringBuilder();
            inv.AppendLine("route,depth,magic,size,lead,count");
            paths.AppendLine("route,off,slot,list,delta,ax,ay,bx,by,val,name");
            links.AppendLine("route,idx,x0,y0,x1,y1,x2,y2,x3,y3,f00,f02,f04,f06,f18,f1a,f1c,f1e");

            var perRoute = new StringBuilder();
            var names = new SortedDictionary<string, int>();
            int files = 0, rows = 0, segments = 0, live = 0, linkCount = 0, cells = 0;
            int cornerFirst = 0, cornerAll = 0, pairs = 0, pairsBeyond = 0;
            float worstPair = 0f;
            foreach (var f in NfsuRoute.FindRouteFiles())
            {
                var route = new NfsuRoute(f);
                files++;
                var routeLinks = route.RoadLinks();
                var grid = route.Grid();
                var segs = route.TrafficSegments();
                var idx = route.GridIndexStats();
                cornerFirst += idx.FirstCornerInside;
                cornerAll += idx.AllCornersInside;
                pairs += idx.Pairs;
                pairsBeyond += idx.PairsBeyondCell;
                worstPair = Mathf.Max(worstPair, idx.WorstPair);
                linkCount += routeLinks.Count;
                cells += grid.Count;
                foreach (var r in route.Inventory())
                {
                    rows++;
                    inv.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},0x{2:X8},{3},{4},{5}",
                        route.RouteId, r.Depth, r.Magic, r.Size, r.Lead, r.Count));
                }
                foreach (var s in segs)
                {
                    segments++;
                    if (!float.IsNaN(s.Ax) && Math.Abs(s.Ax) < 20000f && s.Name.Length > 0)
                    {
                        live++;
                        int n;
                        names.TryGetValue(s.Name, out n);
                        names[s.Name] = n + 1;
                    }
                    paths.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}",
                        route.RouteId, s.Offset, s.Slot, s.ListId, s.Delta,
                        NfsuRoute.F(s.Ax), NfsuRoute.F(s.Ay), NfsuRoute.F(s.Bx), NfsuRoute.F(s.By),
                        NfsuRoute.F(s.Value), s.Name));
                }
                // coordinates in the record's own 1/8 m units: integer text, so the diff is exact
                foreach (var l in routeLinks)
                    links.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17}",
                        route.RouteId, l.Index,
                        (int)(l.X[0] / NfsuRoute.LinkUnit), (int)(l.Y[0] / NfsuRoute.LinkUnit),
                        (int)(l.X[1] / NfsuRoute.LinkUnit), (int)(l.Y[1] / NfsuRoute.LinkUnit),
                        (int)(l.X[2] / NfsuRoute.LinkUnit), (int)(l.Y[2] / NfsuRoute.LinkUnit),
                        (int)(l.X[3] / NfsuRoute.LinkUnit), (int)(l.Y[3] / NfsuRoute.LinkUnit),
                        l.Units[0], l.Units[1], l.Units[2], l.Units[3],
                        l.Units[12], l.Units[13], l.Units[14], l.Units[15]));
                float[] mn, mx;
                route.TryBBox(out mn, out mx);
                int usedCells = grid.FindAll(c => c.X < 99999f).Count;
                if (mn != null)
                    perRoute.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "   {0,-16} id {1} bbox ({2:F2}, {3:F2}) .. ({4:F2}, {5:F2})  cells {6,4}  " +
                        "patches {7,6}  segs {8,3}  corners in cells {9,6}/{10,6} all {11,6}  " +
                        "pairs {12,6} beyond {13,5}  worst {14,5:F2} m",
                        System.IO.Path.GetFileName(f), route.RouteId, mn[0], mn[1], mx[0], mx[1],
                        usedCells, routeLinks.Count, segs.Count, idx.FirstCornerInside,
                        routeLinks.Count, idx.AllCornersInside, idx.Pairs, idx.PairsBeyondCell,
                        idx.WorstPair));
                else
                    perRoute.AppendLine(string.Format("   {0,-16} id {1} no bbox chunk  cells {2,4}  links {3,6}  segs {4,3}",
                        System.IO.Path.GetFileName(f), route.RouteId, usedCells, routeLinks.Count, segs.Count));
            }

            File.WriteAllText(Path.Combine(OutDir, "trackb_inv_cs.csv"), inv.ToString());
            File.WriteAllText(Path.Combine(OutDir, "trackb_paths_cs.csv"), paths.ToString());
            File.WriteAllText(Path.Combine(OutDir, "trackb_links_cs.csv"), links.ToString());
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0} route files, {1} inventory rows, {2} corridor records ({3} with points), " +
                "{4} road patches, {5} grid cells", files, rows, segments, live, linkCount, cells));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "road-patch decode check: {0}/{1} patches have their first corner and {2}/{1} have all " +
                "four corners inside a cell that lists them; {3} (cell,patch) pairs, {4} further than " +
                "half a cell from the patch ring, worst {5:F2} m",
                cornerFirst, linkCount, cornerAll, pairs, pairsBeyond, worstPair));
            sb.AppendLine("traffic-vehicle sets by placements:");
            foreach (var kv in names)
                sb.AppendLine(string.Format("   {0,-24} {1}", kv.Key, kv.Value));
            sb.Append(perRoute);
            Debug.Log("NFSU TRACKB dump\n" + sb);
        }

        /// <summary>
        /// Builds traffic geometry for two routes in the open scene: the big city route 1001 (12 253
        /// road links) and the small route 1301 (582). Run headless it checks that the road-network
        /// decode feeds a mesh cleanly and prints the counts.
        /// </summary>
        [MenuItem("NFSU/Self Test - build traffic geometry for routes 1001 + 1301")]
        public static void ImportTrafficIntoScene()
        {
            Debug.Log("NFSU traffic import\n" + NfsuTrafficPaths.Import(new[] { 1001, 1301 }));
        }

        /// <summary>
        /// Builds the road surface and traffic corridors of all 31 routes into a fresh empty scene and
        /// saves it as Assets/NFSU/Scenes/TRAFFIC_all.unity - the review artifact for the route decode
        /// (the map scene stays untouched; open RA_batch.unity and use the menu instead to see the
        /// traffic in the middle of the city).
        /// </summary>
        [MenuItem("NFSU/Self Test - build all routes into TRAFFIC_all scene")]
        public static void BuildTrafficScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string report = NfsuTrafficPaths.Import(null);
            if (!AssetDatabase.IsValidFolder("Assets/NFSU/Scenes"))
                AssetDatabase.CreateFolder("Assets/NFSU", "Scenes");
            if (EditorSceneManager.SaveScene(scene, "Assets/NFSU/Scenes/TRAFFIC_all.unity"))
                report += "\nsaved Assets/NFSU/Scenes/TRAFFIC_all.unity";
            Debug.Log("NFSU traffic all routes\n" + report);
        }

        /// <summary>Dump + scene build in one headless pass: the cheapest full check of the route reader.</summary>
        [MenuItem("NFSU/Self Test - dump TRACKB then build traffic geometry")]
        public static void DumpTrackBAndTraffic()
        {
            DumpTrackB();
            ImportTrafficIntoScene();
        }

        // ------------------------------------------------------------------ traffic cars

        /// <summary>Every <c>Cars\*\GEOMETRY.BIN</c>; the builder decides which are traffic cars.</summary>
        static List<string> TrafficCarPaths()
        {
            return NfsuTrafficCarSource.CarBundles(NfsuTrafficCarSource.GameRoot());
        }

        /// <summary>
        /// The packs the traffic cars reference - see <see cref="NfsuTrafficCarSource.Packs"/>, which
        /// also carries the measurement that says these four are enough.
        /// </summary>
        static List<string> TrafficPackPaths()
        {
            return NfsuTrafficCarSource.Packs(NfsuTrafficCarSource.GameRoot());
        }

        static NfsuCarImportOptions CarOptions(List<string> carPaths, string filter, int maxCars)
        {
            var opt = new NfsuCarImportOptions();
            foreach (var p in carPaths)
                opt.CarPaths.Add(p);
            foreach (var p in TrafficPackPaths())
                opt.PackPaths.Add(p);
            opt.CarFilter = filter ?? "";
            opt.MaxCars = maxCars;
            return opt;
        }

        /// <summary>
        /// Parts and wheel plans to CSV, for the Python twin to diff
        /// (<c>nfs_probe\cars_parity.py</c>). Two files: <c>cars_cs.csv</c> one row per drawable,
        /// <c>carwheels_cs.csv</c> one row per decided wheel mount. Coordinates stay in the game's
        /// own Z-up metres so the diff has no basis convention inside it.
        /// </summary>
        [MenuItem("NFSU/Self Test - dump traffic cars to CSV")]
        public static void DumpCarsCsv()
        {
            var paths = TrafficCarPaths();
            var sbParts = new StringBuilder();
            sbParts.AppendLine("kind,car,key,name,group,lod,verts,tris,stride,submeshes,slots,frames");
            var sbWheels = new StringBuilder();
            sbWheels.AppendLine(
                "car,radius,width,halfwidth,floorz,bodyfloor,roadz,patches,source,corner,front," +
                "left,socketkey,x,y,z,patchinner,patchouter,skin,outerface,sidewallgap,groundgap," +
                "xbits,ybits,zbits,outerfacebits,widthbits,patchinnerbits,patchouterbits");
            // The raw socket tables, at full precision: when C# and Python disagree on a wheel
            // mount by one unit in the last place, this table says whether the reader or the rule
            // disagrees.
            var sbFrames = new StringBuilder();
            sbFrames.AppendLine("car,part,index,key,x,y,z,xbits,ybits,zbits");
            int cars = 0, parts = 0, wheels = 0, sockets = 0, skipped = 0;

            foreach (var p in paths)
            {
                NfsuCarBundle car;
                try
                {
                    car = new NfsuCarBundle(p);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("NFSU self test: " + p + ": " + e.Message);
                    continue;
                }
                if (!car.LooksLikeTraffic())
                {
                    skipped++;
                    continue;
                }
                cars++;
                foreach (var part in car.Parts)
                {
                    var d = part.Drawable;
                    var slots = new StringBuilder();
                    foreach (var s in d.Slots)
                        slots.Append(s.ToString("X8")).Append('|');
                    sbParts.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2:X8},{3},{4},{5},{6},{7},{8},{9},{10},{11}",
                        part.IsWheel ? "wheel" : (part.IsBody ? "body" : "other"),
                        car.CarName, d.BinKey, d.Name, part.Group, part.Lod,
                        d.VertCount, d.TriCount, d.Stride, d.Submeshes.Count,
                        slots, part.Frames.Count));
                    for (int i = 0; i < part.Frames.Count; i++)
                    {
                        var f = part.Frames[i];
                        // The decimal text is for a human; the last three columns are the float32 bit
                        // patterns, so the diff against the Python twin compares the reader's output
                        // EXACTLY, with no decimal formatting in between - F7 alone cannot tell a
                        // correct read from one that is off by a unit in the last place.
                        sbFrames.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "{0},{1},{2},{3:X8},{4:F7},{5:F7},{6:F7},{7:X8},{8:X8},{9:X8}",
                            car.CarName, d.Name, i, f.Key, f.X, f.Y, f.Z,
                            BitConverter.SingleToInt32Bits(f.X),
                            BitConverter.SingleToInt32Bits(f.Y),
                            BitConverter.SingleToInt32Bits(f.Z)));
                        sockets++;
                    }
                    parts++;
                }
                var plan = new NfsuCarBuilder.WheelPlanResult();
                if (!NfsuCarBuilder.PlanWheels(car, plan))
                {
                    sbWheels.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},0,0,0,0,0,0,0,none,{1}", car.CarName, plan.Problem));
                    continue;
                }
                foreach (var c in plan.Corners)
                {
                    sbWheels.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1:F7},{2:F7},{3:F7},{4:F7},{5:F7},{6:F7},{7},{8},{9},{10},{11}," +
                        "{12:X8},{13:F7},{14:F7},{15:F7},{16:F7},{17:F7},{18:F7},{19:F7},{20:F7}," +
                        "{21:F7},{22:X8},{23:X8},{24:X8},{25:X8},{26:X8},{27:X8},{28:X8}",
                        car.CarName, plan.Radius, plan.Width, plan.HalfWidth, plan.FloorZ,
                        plan.BodyFloorZ, plan.RoadZ, plan.Patches, (int)c.Source, c.Name,
                        c.Front ? 1 : 0, c.Left ? 1 : 0,
                        c.SocketKey, c.X, c.Y, c.Z, c.PatchInnerY, c.PatchOuterY,
                        c.Skin, c.OuterFace, c.SidewallGap, c.GroundGap,
                        // The decimal text cannot tell a correct mount from one that is off by one
                        // unit in the last place, and the rule is pure float arithmetic, so the bit
                        // patterns of the values the mounts are made of go in as well - the patch's
                        // own |y| edges included, which is what tells a different vertex in the
                        // patch box apart from different float arithmetic.
                        BitConverter.SingleToInt32Bits(c.X),
                        BitConverter.SingleToInt32Bits(c.Y),
                        BitConverter.SingleToInt32Bits(c.Z),
                        BitConverter.SingleToInt32Bits(c.OuterFace),
                        BitConverter.SingleToInt32Bits(plan.Width),
                        BitConverter.SingleToInt32Bits(c.PatchInnerY),
                        BitConverter.SingleToInt32Bits(c.PatchOuterY)));
                    wheels++;
                }
            }

            string f1 = Path.Combine(OutDir, "cars_cs.csv");
            string f2 = Path.Combine(OutDir, "carwheels_cs.csv");
            string f3 = Path.Combine(OutDir, "carframes_cs.csv");
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(f1, sbParts.ToString());
            File.WriteAllText(f2, sbWheels.ToString());
            File.WriteAllText(f3, sbFrames.ToString());
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "NFSU self test: traffic cars {0} dumped ({1} parts, {2} wheel mounts, {3} socket " +
                "rows, {4} non-traffic bundles skipped) -> {5} , {6} , {7}",
                cars, parts, wheels, sockets, skipped, f1, f2, f3));
        }

        /// <summary>Builds the traffic cars as real assets and wires their wheel rigs.</summary>
        [MenuItem("NFSU/Self Test - build traffic cars into scene")]
        public static void BuildTrafficCars()
        {
            var opt = CarOptions(TrafficCarPaths(), "", 0);
            var report = NfsuCarBuilder.Import(opt);
            Debug.Log("\n" + report.Report());
        }

        /// <summary>
        /// All 15 traffic cars into a fresh scene saved as <c>Assets/NFSU/Scenes/CARS.unity</c>,
        /// laid out side by side on a ground plane with their tyres touching it: what the wheel rule
        /// has to be judged against. A car whose sidewalls are buried or sticking out, or whose
        /// origin is not one tyre radius above the road, is visible in that setup immediately.
        /// </summary>
        [MenuItem("NFSU/Self Test - build all traffic cars into CARS scene")]
        public static void BuildCarsScene()
        {
            var opt = CarOptions(TrafficCarPaths(), "", 0);
            string scenePath = "Assets/NFSU/Scenes/CARS.unity";
            string folder = Path.GetDirectoryName(scenePath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets/NFSU", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var report = NfsuCarBuilder.Import(opt);
            Debug.Log("\n" + report.Report());
            int renderers = UnityEngine.Object
                .FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length;
            int colliders = UnityEngine.Object
                .FindObjectsByType<BoxCollider>(FindObjectsSortMode.None).Length;
            int rigs = UnityEngine.Object
                .FindObjectsByType<NFSU.Runtime.NfsuTrafficCarRig>(FindObjectsSortMode.None).Length;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "NFSU self test: CARS scene: {0} renderers, {1} box colliders, {2} wheel rigs",
                renderers, colliders, rigs));
            LayoutCarsForReview(opt.RootName);
            SaveScene(scene, scenePath);
        }

        /// <summary>
        /// Lines the imported cars up along Unity Z, all facing +X, each dropped so its lowest tyre
        /// contact patch sits on y = 0 and puts an unlit plane under them.
        /// <para>
        /// The tyres, not the physics box: the box's bottom plane is a chassis box that the model's
        /// own wheel patches put 0.12-0.39 m ABOVE the road surface, so dropping a car by
        /// <c>GroundOffset</c> sinks the body 0.15-0.3 m into the review plane. Both numbers are
        /// logged, together with the LODGroup state as it stands in the scene, because a LODGroup
        /// saved with <c>m_Enabled: 0</c> draws A, B and C at once - the flat wheel quads that LOD C
        /// bakes then sit in front of the real tyres, which is exactly what a scene review must not
        /// be looking at. Unlit plane because the car materials are unlit and the empty scene has no
        /// light.
        /// </para>
        /// </summary>
        static void LayoutCarsForReview(string rootName)
        {
            var root = GameObject.Find(rootName);
            if (root == null)
            {
                Debug.LogWarning("NFSU self test: no scene root named " + rootName);
                return;
            }

            var rigs = new List<NFSU.Runtime.NfsuTrafficCarRig>();
            foreach (Transform t in root.transform)
            {
                var rig = t.GetComponent<NFSU.Runtime.NfsuTrafficCarRig>();
                if (rig != null)
                    rigs.Add(rig);
            }
            rigs.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            float cursor = 0f;
            float deepest = 0f;
            int lodOn = 0, lodLevels = 0;
            var sb = new StringBuilder();
            foreach (var rig in rigs)
            {
                float contact = rig.ContactOffset;      // the road plane, per the wheel patches
                float ground = rig.GroundOffset;        // the shipped collision floor
                var lg = rig.GetComponent<LODGroup>();
                var lods = lg == null ? null : lg.GetLODs();
                var lodText = new StringBuilder();
                if (lg == null)
                    lodText.Append("none");
                else
                {
                    if (lg.enabled)
                        lodOn++;
                    lodLevels += lods.Length;
                    lodText.Append(lg.enabled ? "on " : "OFF ");
                    for (int i = 0; i < lods.Length; i++)
                        lodText.Append(string.Format(CultureInfo.InvariantCulture,
                            "[{0:F2}:{1}]", lods[i].screenRelativeTransitionHeight,
                            lods[i].renderers.Length));
                }
                Bounds b = rig.Bounds;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-13} tyre contact {1,7:F3} m, box floor {2,7:F3} m, LOD {3,-28} " +
                    "bounds {4:F2} x {5:F2} x {6:F2} m",
                    rig.name, contact, ground, lodText, b.size.x, b.size.y, b.size.z));
                float width = Mathf.Max(0.5f, b.size.z);
                rig.transform.localPosition = new Vector3(0f, -contact, cursor + width * 0.5f);
                rig.InvalidateBounds();     // the cached bounds are world-space and just moved
                cursor += width + 2f;
                if (b.size.y > deepest)
                    deepest = b.size.y;
            }
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "NFSU self test: {0} cars laid out on the review plane ({1} with an enabled " +
                "LODGroup, {2} levels in total):\n{3}", rigs.Count, lodOn, lodLevels, sb));

            var shader = Shader.Find("Unlit/Color");
            if (shader == null)
            {
                Debug.LogWarning("NFSU self test: Unlit/Color not found - no review plane");
                return;
            }
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "REVIEW_PLANE";
            plane.transform.SetParent(root.transform, false);
            plane.transform.localPosition = new Vector3(0f, -0.001f, cursor * 0.5f);
            plane.transform.localScale = new Vector3(30f, 1f, Mathf.Max(30f, cursor + 10f));
            var mat = new Material(shader);
            mat.color = new Color(0.18f, 0.18f, 0.2f, 1f);
            var mr = plane.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            UnityEngine.Object.DestroyImmediate(plane.GetComponent<Collider>());
        }

        [MenuItem("NFSU/Self Test - dump cars CSV then build CARS scene")]
        public static void DumpCarsAndBuildScene()
        {
            DumpCarsCsv();
            BuildCarsScene();
        }

        /// <summary>
        /// Dumps the alpha class the importer would pick for every texture record it can see, together
        /// with the measured features and the multi-slot flag the decision uses. The row layout is
        /// identical to nfs_probe/alpha_csv.py, so the C# rule engine can be diffed against the Python
        /// model that the thresholds were chosen with (alpha_parity.py).
        /// </summary>
        [MenuItem("NFSU/Self Test - dump alpha classes")]
        public static void DumpAlphaClasses()
        {
            DumpAlphaClasses("STREAML1RA.BUN", "GLOBALA.BUN", "GLOBALB.BUN");
        }

        static void DumpAlphaClasses(params string[] bundleFiles)
        {
            Directory.CreateDirectory(OutDir);
            var opt = new NfsuImportOptions { RootName = "NFSU_ALPHA_DUMP", WriteTextures = false };
            foreach (var f in bundleFiles)
            {
                string folder = f.StartsWith("Global", StringComparison.OrdinalIgnoreCase) ? "Global" : "Tracks";
                string found = FindGameFile(folder, Path.GetFileName(f));
                if (found != null)
                    opt.BundlePaths.Add(found.Replace("\\", "/"));
            }

            var sections = new List<NfsuSection>();
            var builder = new NfsuMapBuilder(opt);
            builder.LoadBundles(sections);

            var keys = builder.TextureKeys();
            keys.Sort();
            var sb = new StringBuilder();
            var perClass = new SortedDictionary<string, int>();
            int measured = 0;
            foreach (uint key in keys)
            {
                NfsuTextureRecord rec;
                byte[] blob;
                bool multi;
                NfsuAlphaClass cls;
                if (!builder.AlphaDecision(key, out rec, out blob, out multi, out cls))
                    continue;
                NfsuAlphaFeatures f;
                if (NfsuDdsWriter.MeasureAlpha(rec, blob, out f))
                    measured++;
                string name = NfsuAlphaClassifier.ModeName(cls);   // tally by mode text
                int n;
                perClass.TryGetValue(name, out n);
                perClass[name] = n + 1;

                sb.Append("ALPHA,").Append(H(key)).Append(',').Append(NfsuDdsWriter.Sanitize(rec.Name))
                  .Append(',').Append(H(rec.GroupId)).Append(',').Append(rec.Format)
                  .Append(',').Append(rec.Width).Append(',').Append(rec.Height)
                  .Append(',').Append(multi ? 1 : 0).Append(',').Append(cls)
                  .Append(',').Append(f.Sampled);
                sb.Append(',').Append(f.MeanA.ToString("F6", CultureInfo.InvariantCulture));
                sb.Append(',').Append(f.Frac0.ToString("F6", CultureInfo.InvariantCulture));
                sb.Append(',').Append(f.Frac15.ToString("F6", CultureInfo.InvariantCulture));
                sb.Append(',').Append(f.Corr.ToString("F6", CultureInfo.InvariantCulture));
                sb.Append(',').Append(f.LLow.ToString("F6", CultureInfo.InvariantCulture));
                sb.Append(',').Append(f.LHigh.ToString("F6", CultureInfo.InvariantCulture));
                sb.Append(',').Append(f.Black.ToString("F6", CultureInfo.InvariantCulture));
                sb.Append(',').Append(f.Spread.ToString("F6", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            string path = Path.Combine(OutDir, "alpha_cs.csv");
            File.WriteAllText(path, sb.ToString());
            var tally = new StringBuilder();
            foreach (var kv in perClass)
                tally.Append("  ").Append(kv.Key).Append(' ').Append(kv.Value);
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "NFSU alpha dump: {0} records ({1} with measured alpha),{2} -> {3}",
                keys.Count, measured, tally, path));
        }

        static string GameBundle(string name)
        {
            string path = FindGameFile("Tracks", name);
            if (path == null)
                throw new FileNotFoundException("could not find Tracks\\" + name + " above " + Application.dataPath);
            return path;
        }

        /// <summary>Walks up from Assets/ to the game install folder, or null if it is not there.</summary>
        static string GameRoot()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string root = Path.Combine(dir.FullName, "Need for Speed Underground");
                if (Directory.Exists(root))
                    return root;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>Walks up from Assets/ to the game install and looks for one of its files.</summary>
        static string FindGameFile(string folder, string name)
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string root = Path.Combine(dir.FullName, "Need for Speed Underground");
                string candidate = Path.Combine(Path.Combine(root, folder), name);
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// Adds the Global packs to a scene build. They hold no scenery sections at all (0 sections,
        /// one stray STD quad), so they cannot change what is placed - they only add texture records,
        /// and the city's most-used glow texture HEADLIGHTGLOW (1 271 RA placements) exists only in
        /// GLOBALB.BUN. That pack is also where the A8/L8 records live, so it exercises that path.
        /// </summary>
        static void AddGlobalPacks(NfsuImportOptions opt)
        {
            foreach (var name in new[] { "GLOBALA.BUN", "GLOBALB.BUN" })
            {
                string path = FindGameFile("Global", name);
                if (path != null)
                    opt.BundlePaths.Add(path.Replace("\\", "/"));
                else
                    Debug.LogWarning("NFSU self test: Global pack " + name + " not found");
            }
        }

        static string H(uint v) { return string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", v); }

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------------ parse dump
        [MenuItem("NFSU/Dump RX bundle to CSV")]
        public static void DumpRx()
        {
            DumpCsv("STREAML1RX.BUN", "rx_cs.csv");
        }

        static void DumpCsv(string bundleFileName, string outFileName)
        {
            Directory.CreateDirectory(OutDir);
            var bundle = new NfsuBundle(GameBundle(bundleFileName));
            var sb = new StringBuilder();
            int geo = 0, inst = 0, tex = 0;

            foreach (var d in bundle.Drawables())
            {
                if (d.Unsupported || d.VertCount == 0)
                    continue;
                int sum = 0;
                foreach (var sm in d.Submeshes)
                    sum += sm.IndexCount;
                sb.Append("GEO,").Append(H(d.BinKey)).Append(',').Append(NfsuDdsWriter.Sanitize(d.Name))
                  .Append(',').Append(d.VertCount).Append(',').Append(d.TriCount)
                  .Append(',').Append(d.Submeshes.Count).Append(',').Append(d.Stride)
                  .Append(',').Append(sum).Append(',');
                for (int i = 0; i < d.Slots.Length; i++)
                {
                    if (i > 0)
                        sb.Append(' ');
                    sb.Append(H(d.Slots[i]));
                }
                sb.AppendLine();
                geo++;
            }

            foreach (var pack in bundle.TexturePacks())
            {
                foreach (var r in pack.Records)
                {
                    sb.Append("TEX,").Append(H(r.BinKey)).Append(',').Append(NfsuDdsWriter.Sanitize(r.Name))
                      .Append(',').Append(r.Width).Append(',').Append(r.Height)
                      .Append(',').Append(r.Format).Append(',').Append(r.Offset).Append(',').Append(r.Size)
                      .Append(',').Append(NfsuDdsWriter.MipCount(r.Width, r.Height, r.Format, r.Size))
                      .Append(',').Append(r.LogW).Append('/').Append(r.LogH)
                      .Append(',').Append(r.Byte4E).Append('/').Append(r.Byte4F)
                      .AppendLine();
                    tex++;
                }
            }

            foreach (var sec in bundle.Sections())
            {
                sb.Append("SEC,").Append(sec.Id).Append(',').Append(sec.Types.Count)
                  .Append(',').Append(sec.Instances.Count).Append(",");
                for (int i = 0; i < sec.Types.Count; i++)
                {
                    if (i > 0)
                        sb.Append(' ');
                    sb.Append(H(sec.Types[i].BinKey));
                }
                sb.AppendLine();
                for (int i = 0; i < sec.Instances.Count; i++)
                {
                    var ins = sec.Instances[i];
                    sb.Append("INST,").Append(sec.Id).Append(',').Append(i)
                      .Append(',').Append(ins.TypeIndex).Append(',').Append(ins.Flags).Append(',');
                    for (int k = 0; k < 3; k++)
                    {
                        if (k > 0)
                            sb.Append(' ');
                        sb.Append(F(ins.Pos[k]));
                    }
                    sb.Append(',');
                    for (int k = 0; k < 9; k++)
                    {
                        if (k > 0)
                            sb.Append(' ');
                        sb.Append(ins.Rot[k]);
                    }
                    sb.Append(',');
                    for (int k = 0; k < 6; k++)
                    {
                        if (k > 0)
                            sb.Append(' ');
                        sb.Append(ins.BBox[k]);
                    }
                    sb.AppendLine();
                    inst++;
                }
            }

            string path = Path.Combine(OutDir, outFileName);
            File.WriteAllText(path, sb.ToString());
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "NFSU dump {0}: {1} geometries, {2} texture records, {3} placement records -> {4}",
                bundleFileName, geo, tex, inst, path));
        }

        /// <summary>
        /// Prints the placement math the builder uses for a few known instances, so a surprising
        /// bake/decompose decision can be read from the log instead of guessed at.
        /// </summary>
        [MenuItem("NFSU/Self Test - describe placement math")]
        public static void DebugPlacements()
        {
            var opt = new NfsuImportOptions { RootName = "NFSU_DEBUG" };
            opt.BundlePaths.Add(GameBundle("STREAML1RA.BUN").Replace("\\", "/"));
            var sections = new List<NfsuSection>();
            var builder = new NfsuMapBuilder(opt);
            builder.LoadBundles(sections);
            foreach (var meshName in new[]
            {
                "XB_RES_CONDOC_1B_00", "XO_WATERTOWERB_1B_00", "XW_CONDO_WALLA_1B_00",
                "XO_STREETLIGHTC_1A_00", "XW_WOOD_RLIGHTSA_1A_00",
            })
                builder.DescribePlacements(sections, meshName, 2);
        }

        // ------------------------------------------------------------------ scene builds
        [MenuItem("NFSU/Build RX scene (no GUI action)")]
        public static void ImportRxIntoScene()
        {
            // RX's own packs hold almost nothing that its sections use (82 keys live in the other
            // track bundles), so "only the textures the selected sections need" would write none.
            var opt = new NfsuImportOptions
            {
                RootName = RxRootName,
                ColliderPrefixes = "TRS,TER",
                TexturesForSelectedOnly = false,
            };
            opt.BundlePaths.Add(GameBundle("STREAML1RX.BUN").Replace("\\", "/"));
            AddGlobalPacks(opt);
            BuildIntoScene(opt, "STREAML1RX.BUN", RxScenePath, "RX");
        }

        /// <summary>
        /// City-scale check: 40 RA sections, so mirrored placements, MeshColliders, layers and
        /// the per-batch texture selection all run against real city data.
        /// </summary>
        [MenuItem("NFSU/Build RA batch scene (40 sections)")]
        public static void ImportRaBatchIntoScene()
        {
            var opt = new NfsuImportOptions
            {
                RootName = RaRootName,
                SectionFilter = RaBatchIds,
                ColliderPrefixes = "TRS,TER,EAR,GRO",
            };
            opt.BundlePaths.Add(GameBundle("STREAML1RA.BUN").Replace("\\", "/"));
            AddGlobalPacks(opt);
            BuildIntoScene(opt, "STREAML1RA.BUN", RaScenePath, "RA batch");
        }

        static void BuildIntoScene(NfsuImportOptions opt, string bundleFileName, string scenePath, string label)
        {
            var sections = new List<NfsuSection>();
            var builder = new NfsuMapBuilder(opt);
            builder.LoadBundles(sections);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            builder.ImportTextures(sections);
            GameObject root = builder.BuildScene(sections);
            LogSceneStats(builder, root, label);
            SaveScene(scene, scenePath);
            builder.Dispose();
        }

        static void LogSceneStats(NfsuMapBuilder builder, GameObject root, string label)
        {
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            int renderers = 0, colliders = 0;
            var perLayer = new SortedDictionary<string, int>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                bounds.Encapsulate(r.bounds);
                renderers++;
                string name = string.IsNullOrEmpty(LayerMask.LayerToName(r.gameObject.layer))
                    ? "layer " + r.gameObject.layer
                    : LayerMask.LayerToName(r.gameObject.layer);
                int n;
                perLayer.TryGetValue(name, out n);
                perLayer[name] = n + 1;
            }
            foreach (var c in root.GetComponentsInChildren<MeshCollider>())
                colliders++;

            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "renderers {0}  colliders {1}", renderers, colliders));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "world bounds center {0}  size {1}", V(bounds.center), V(bounds.size)));
            sb.AppendLine("objects per layer:");
            foreach (var kv in perLayer)
                sb.AppendLine(string.Format("   {0,-16} {1}", kv.Key, kv.Value));
            Debug.Log("NFSU " + label + " scene\n" + builder.Stats.Report() + sb);
        }

        static void SaveScene(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (!AssetDatabase.IsValidFolder("Assets/NFSU"))
                AssetDatabase.CreateFolder("Assets", "NFSU");
            if (!AssetDatabase.IsValidFolder("Assets/NFSU/Scenes"))
                AssetDatabase.CreateFolder("Assets/NFSU", "Scenes");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, path);
        }

        static string V(Vector3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2}, {2:F2})", v.x, v.y, v.z);
        }
    }
}
