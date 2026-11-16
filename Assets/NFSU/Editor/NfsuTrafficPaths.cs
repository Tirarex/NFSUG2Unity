// The route data of Tracks\TRACKB####.lzc as GameObjects in the open scene.
//
// Two things are drawn, because the format stores two different things:
//   * the route's ROAD SURFACE (chunk 0x00034132, indexed by the 32 m grid of 0x00034131): each
//     record is a road PATCH whose four points are its corners - A and D along one side edge (D of
//     one patch is A of the next: 13737 such joins in TRACKB1001) and B = A + w, C = D + w on the
//     other side, w being the lateral vector (mean |w| 3.5 m = a lane, mean patch length 5.5 m).
//     The corners are shared between neighbouring patches, so the quads tile the surface; they are
//     drawn as stored, with no assumed width. One mesh per route file, since each route carries its
//     own network (31 distinct link arrays for 31 files).
//   * the ambient-traffic CORRIDORS (chunk 0x00034121): a straight A-B pair plus a speed-class
//     value and the traffic-vehicle set name, so they become one LineRenderer each, coloured by
//     that value. These are 400-1300 m long, i.e. they are not links, they are the long legs the
//     traffic cars run along.
//
// Coordinates use the map builder's conversion: game point (x, y, z-up) becomes Unity (x, height,
// -y). The corridor record stores no elevation and the route bbox Z is a constant default
// (-127/+127 in every file), so everything here is drawn at one flat height.
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace NFSU
{
    public static class NfsuTrafficPaths
    {
        public const string RootName = "NFSU_TRAFFIC";
        public const string Folder = "Assets/NFSU/TrafficPaths";
        public const string MaterialPath = Folder + "/NFSU_TrafficLine.mat";

        /// <summary>Unity Y of the corridor lines; the format stores no per-point elevation.</summary>
        public static float CorridorHeight = 8f;
        /// <summary>Unity Y of the road-network ribbons, a little below the corridor lines.</summary>
        public static float RoadHeight = 4f;
        public static float CorridorWidth = 3f;
        /// <summary>
        /// Road patches are drawn from the record's own four corners, so their width is the game's,
        /// not an option. This is the measured mean, filled in by Import.
        /// </summary>
        public static float MeanRoadWidth { get; private set; }
        public static bool DrawCorridors = true;
        public static bool DrawRoadNetwork = true;
        public static bool DrawRouteBoxes = true;
        public static bool ReplaceExistingRoot = true;
        /// <summary>Empty = every route file; otherwise comma-separated route ids, e.g. "1001,1105".</summary>
        public static string RouteIdFilter = "";

        [MenuItem("NFSU/Import Traffic Paths")]
        public static void ImportFromMenu()
        {
            Debug.Log("NFSU traffic paths\n" + Import(ParseFilter()));
        }

        static ICollection<int> ParseFilter()
        {
            if (string.IsNullOrEmpty(RouteIdFilter))
                return null;
            var set = new HashSet<int>();
            foreach (var part in RouteIdFilter.Split(','))
            {
                int id;
                if (int.TryParse(part.Trim(), out id))
                    set.Add(id);
            }
            return set.Count == 0 ? null : set;
        }

        /// <summary>
        /// Builds the road network and the traffic corridors of every TRACKB route (or of the route
        /// ids in <paramref name="onlyRouteIds"/>) straight into the open scene. Returns a report
        /// string; outside the scene it writes one material and one .mesh per route.
        /// </summary>
        public static string Import(ICollection<int> onlyRouteIds)
        {
            var files = NfsuRoute.FindRouteFiles();
            if (files.Count == 0)
                return "No TRACKB*.lzc found above " + Application.dataPath;

            if (ReplaceExistingRoot)
            {
                var old = GameObject.Find(RootName);
                if (old != null)
                    Object.DestroyImmediate(old);
            }

            var material = EnsureMaterial();
            var root = new GameObject(RootName);

            int routes = 0, lines = 0, boxes = 0, meshes = 0;
            long linkTotal = 0, triTotal = 0;
            var sets = new SortedDictionary<string, int>();
            var bounds = new Bounds();
            bool first = true;

            foreach (var f in files)
            {
                var route = new NfsuRoute(f);
                if (onlyRouteIds != null && !onlyRouteIds.Contains(route.RouteId))
                    continue;
                routes++;

                var go = new GameObject("TRACKB" + route.RouteId);
                go.transform.SetParent(root.transform, false);

                // ---- road network: one flat ribbon mesh per route
                if (DrawRoadNetwork)
                {
                    var links = route.RoadLinks();
                    Mesh mesh = BuildPatches(links);
                    if (mesh != null)
                    {
                        var holder = new GameObject("road_network");
                        holder.transform.SetParent(go.transform, false);
                        var filter = holder.AddComponent<MeshFilter>();
                        filter.sharedMesh = SaveMesh(mesh, "RoadNetwork_TRACKB" + route.RouteId);
                        holder.AddComponent<MeshRenderer>().sharedMaterial = material;
                        meshes++;
                        linkTotal += links.Count;
                        triTotal += filter.sharedMesh.triangles.Length / 3;
                        if (first)
                        {
                            bounds = filter.sharedMesh.bounds;
                            first = false;
                        }
                        else
                            bounds.Encapsulate(filter.sharedMesh.bounds);
                    }
                }

                // ---- traffic corridors: one LineRenderer each
                if (DrawCorridors)
                {
                    foreach (var s in route.TrafficSegments())
                    {
                        if (string.IsNullOrEmpty(s.Name) || float.IsNaN(s.Ax) || Mathf.Abs(s.Ax) > 20000f)
                            continue;       // unused slot of the game's fixed table
                        var seg = new GameObject("seg_" + s.Slot + "_" + s.Name);
                        seg.transform.SetParent(go.transform, false);
                        var line = seg.AddComponent<LineRenderer>();
                        line.sharedMaterial = material;
                        line.widthMultiplier = CorridorWidth;
                        line.useWorldSpace = false;
                        line.alignment = LineAlignment.TransformZ;
                        line.positionCount = 2;
                        line.SetPosition(0, P(s.Ax, s.Ay, CorridorHeight));
                        line.SetPosition(1, P(s.Bx, s.By, CorridorHeight));
                        Color c = Colour(s.Value);
                        line.startColor = c;
                        line.endColor = c;
                        lines++;
                        int n;
                        sets.TryGetValue(s.Name, out n);
                        sets[s.Name] = n + 1;
                        Encapsulate(ref bounds, ref first, line.GetPosition(0));
                        Encapsulate(ref bounds, ref first, line.GetPosition(1));
                    }
                }

                // ---- plan-view route box
                if (DrawRouteBoxes)
                {
                    float[] mn, mx;
                    if (route.TryBBox(out mn, out mx))
                    {
                        var boxGo = new GameObject("bbox");
                        boxGo.transform.SetParent(go.transform, false);
                        var box = boxGo.AddComponent<LineRenderer>();
                        box.sharedMaterial = material;
                        box.widthMultiplier = 1f;
                        box.useWorldSpace = false;
                        box.alignment = LineAlignment.TransformZ;
                        box.loop = true;
                        box.positionCount = 4;
                        box.SetPosition(0, P(mx[0], mn[1], CorridorHeight));
                        box.SetPosition(1, P(mx[0], mx[1], CorridorHeight));
                        box.SetPosition(2, P(mn[0], mx[1], CorridorHeight));
                        box.SetPosition(3, P(mn[0], mn[1], CorridorHeight));
                        box.startColor = new Color(1f, 1f, 1f, 0.5f);
                        box.endColor = box.startColor;
                        boxes++;
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = root;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format("routes {0}  road meshes {1}  road patches {2}  patch tris {3}  " +
                                        "corridor lines {4}  route boxes {5}  vehicle sets {6}",
                                        routes, meshes, linkTotal, triTotal, lines, boxes, sets.Count));
            sb.AppendLine(string.Format("patches use the record's own corners: mean road width {0:F2} m, " +
                                        "surface at Y {1}; corridor lines {2} m wide at Y {3}",
                                        MeanRoadWidth, RoadHeight, CorridorWidth, CorridorHeight));
            sb.AppendLine(string.Format("world bounds center ({0:F1}, {1:F1}, {2:F1})  size ({3:F1}, {4:F1}, {5:F1})",
                bounds.center.x, bounds.center.y, bounds.center.z, bounds.size.x, bounds.size.y, bounds.size.z));
            foreach (var kv in sets)
                sb.AppendLine(string.Format("   {0,-24} {1}", kv.Key, kv.Value));
            return sb.ToString();
        }

        /// <summary>
        /// One flat mesh per route: every 0x00034132 record is a ROAD PATCH - its four points are
        /// the corners of a quadrilateral (A and D are the corners along one side edge, and D of one
        /// patch is A of the next; B = A + w and C = D + w are the other side, w being the lateral
        /// road-width vector). So the game's own corners become the surface: no assumed width.
        /// Triangles are ordered so their normal points up, which is the convention the map builder
        /// already proved on scenery (game triangles whose right-hand normal is +Z render as the
        /// visible side in Unity).
        /// </summary>
        static Mesh BuildPatches(List<NfsuRoadLink> links)
        {
            if (links.Count == 0)
                return null;
            var verts = new Vector3[links.Count * 4];
            var cols = new Color32[links.Count * 4];
            var uvs = new Vector2[links.Count * 4];
            var tris = new int[links.Count * 6];
            Color32 col = new Color32(90, 200, 255, 255);
            int vi = 0, ti = 0;
            float widthSum = 0f;
            int widthN = 0;
            foreach (var l in links)
            {
                Vector3 a = P(l.X[0], l.Y[0], RoadHeight);
                Vector3 b = P(l.X[1], l.Y[1], RoadHeight);
                Vector3 c = P(l.X[2], l.Y[2], RoadHeight);
                Vector3 d = P(l.X[3], l.Y[3], RoadHeight);
                verts[vi] = a;
                verts[vi + 1] = b;
                verts[vi + 2] = c;
                verts[vi + 3] = d;
                cols[vi] = col;
                cols[vi + 1] = col;
                cols[vi + 2] = col;
                cols[vi + 3] = col;
                uvs[vi] = Vector2.zero;
                uvs[vi + 1] = Vector2.zero;
                uvs[vi + 2] = Vector2.zero;
                uvs[vi + 3] = Vector2.zero;
                float w = Mathf.Sqrt(Mathf.Pow(l.X[1] - l.X[0], 2f) + Mathf.Pow(l.Y[1] - l.Y[0], 2f));
                if (w > 0.01f)
                {
                    widthSum += w;
                    widthN++;
                }
                // game-space cross product of (D-A) x (C-A): positive means the ring A,D,C,B is
                // counter-clockwise in the game's Z-up frame, i.e. its right-hand normal is +Z
                float cross = (l.X[3] - l.X[0]) * (l.Y[2] - l.Y[0]) - (l.Y[3] - l.Y[0]) * (l.X[2] - l.X[0]);
                if (cross >= 0f)
                {
                    tris[ti++] = vi;
                    tris[ti++] = vi + 3;
                    tris[ti++] = vi + 2;
                    tris[ti++] = vi;
                    tris[ti++] = vi + 2;
                    tris[ti++] = vi + 1;
                }
                else
                {
                    tris[ti++] = vi;
                    tris[ti++] = vi + 1;
                    tris[ti++] = vi + 2;
                    tris[ti++] = vi;
                    tris[ti++] = vi + 2;
                    tris[ti++] = vi + 3;
                }
                vi += 4;
            }
            MeanRoadWidth = widthN > 0 ? widthSum / widthN : 0f;
            var mesh = new Mesh { name = "road_patches" };
            ApplyIndexFormat(mesh, verts.Length);
            mesh.vertices = verts;
            mesh.colors32 = cols;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        static void Encapsulate(ref Bounds bounds, ref bool first, Vector3 p)
        {
            if (first)
            {
                bounds = new Bounds(p, Vector3.zero);
                first = false;
            }
            bounds.Encapsulate(p);
        }

        /// <summary>The map builder's conversion for a point: game (x,y,z-up) to Unity (x,z,-y).</summary>
        static Vector3 P(float x, float y, float height)
        {
            return new Vector3(x, height, -y);
        }

        /// <summary>
        /// Cool to warm by the corridor record's +0x38 float (3.1..29.6 over all 31 routes). Slow
        /// corridors (~4) are blue, fast ones (~20) orange; it is the only per-corridor value besides
        /// the two endpoints, so this is what makes the set readable in the scene.
        /// </summary>
        static Color Colour(float v)
        {
            float t = Mathf.Clamp01((v - 3f) / 19f);
            return Color.Lerp(new Color(0.15f, 0.65f, 1f), new Color(1f, 0.45f, 0.05f), t);
        }

        static Mesh SaveMesh(Mesh mesh, string fileName)
        {
            EnsureFolder();
            string path = Folder + "/" + fileName + ".mesh";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                existing.Clear();
                ApplyIndexFormat(existing, mesh.vertices.Length);
                existing.vertices = mesh.vertices;
                existing.colors32 = mesh.colors32;
                existing.uv = mesh.uv;
                existing.triangles = mesh.triangles;
                existing.RecalculateBounds();
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(mesh);
                return existing;
            }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        /// <summary>A reused .mesh asset keeps its old index format, which silently breaks a bigger rebuild.</summary>
        static void ApplyIndexFormat(Mesh mesh, int vertexCount)
        {
            mesh.indexFormat = vertexCount > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        }

        static Material EnsureMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat != null)
                return mat;
            var shader = Shader.Find("NFSU/Unlit VertexColor");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            mat = new Material(shader);
            EnsureFolder();
            AssetDatabase.CreateAsset(mat, MaterialPath);
            return mat;
        }

        static void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(Folder))
                return;
            if (!AssetDatabase.IsValidFolder("Assets/NFSU"))
                AssetDatabase.CreateFolder("Assets", "NFSU");
            AssetDatabase.CreateFolder("Assets/NFSU", "TrafficPaths");
        }
    }
}
