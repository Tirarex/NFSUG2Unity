// Editor window that drives NfsuMapBuilder: pick bundle(s), filter sections, choose the
// import options, then build the city straight into the open scene.
//
// Everything is produced as project assets (Assets/NFSU/{Textures,Materials,Meshes}) so the
// scene can be saved; the game install is never written to.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NFSU
{
    public sealed class NfsuMapImporterWindow : EditorWindow
    {
        static readonly string[] AlphaModeNames = { "Auto", "Opaque", "Cutout", "Blend", "Additive" };

        readonly NfsuImportOptions opt = new NfsuImportOptions();
        readonly List<NfsuSection> sections = new List<NfsuSection>();

        NfsuMapBuilder cached;
        Vector2 scroll;
        string report = "";
        bool haveAnalysed;
        string gameDir = "";
        string texturesFolder = "Assets/NFSU/Textures";
        string meshesFolder = "Assets/NFSU/Meshes";
        string materialsFolder = "Assets/NFSU/Materials";

        [MenuItem("NFSU/Import Map From Bundle")]
        static void Open()
        {
            var w = GetWindow<NfsuMapImporterWindow>("NFSU Map Import");
            w.minSize = new Vector2(460f, 420f);
            w.Show();
        }

        void OnEnable()
        {
            gameDir = FindGameDir();
            if (opt.BundlePaths.Count == 0)
            {
                string rx = Path.Combine(gameDir, "Tracks", "STREAML1RX.BUN");
                if (File.Exists(rx))
                    opt.BundlePaths.Add(rx);
            }
        }

        void OnDisable()
        {
            if (cached != null)
            {
                cached.Dispose();
                cached = null;
            }
            sections.Clear();
            haveAnalysed = false;
            EditorUtility.ClearProgressBar();
        }

        static string FindGameDir()
        {
            try
            {
                var dir = new DirectoryInfo(Application.dataPath);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    var candidate = Path.Combine(dir.FullName, "Need for Speed Underground");
                    if (Directory.Exists(candidate))
                        return candidate;
                    dir = dir.Parent;
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("Bundles", EditorStyles.boldLabel);
            for (int i = 0; i < opt.BundlePaths.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                string path = opt.BundlePaths[i];
                string next = EditorGUILayout.TextField("Bundle " + (i + 1), path);
                if (next != path)
                {
                    opt.BundlePaths[i] = next;
                    haveAnalysed = false;
                }
                if (GUILayout.Button("...", GUILayout.Width(30f)))
                {
                    string picked = EditorUtility.OpenFilePanel("NFSU bundle", gameDir, "BUN,BIN");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        opt.BundlePaths[i] = picked;
                        haveAnalysed = false;
                    }
                }
                if (GUILayout.Button("x", GUILayout.Width(22f)))
                {
                    opt.BundlePaths.RemoveAt(i);
                    haveAnalysed = false;
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add bundle..."))
            {
                string picked = EditorUtility.OpenFilePanel("NFSU bundle", gameDir, "BUN,BIN");
                if (!string.IsNullOrEmpty(picked))
                {
                    opt.BundlePaths.Add(picked);
                    haveAnalysed = false;
                }
            }
            if (GUILayout.Button("Track RX (smoke test)"))
                SetBundles(Path.Combine(gameDir, "Tracks", "STREAML1RX.BUN"));
            if (GUILayout.Button("Track RA (whole city)"))
                SetBundles(Path.Combine(gameDir, "Tracks", "STREAML1RA.BUN"));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("What to import", EditorStyles.boldLabel);
            opt.SectionFilter = EditorGUILayout.TextField("Section ids", opt.SectionFilter,
                "Comma separated, empty = all. RA has 423 sections.");
            opt.MaxSections = EditorGUILayout.IntField("Max sections", opt.MaxSections,
                "0 = no limit. RA is ~423 sections / 21k instances: batch it with 100-150.");
            opt.SkipPrefixes = EditorGUILayout.TextField("Skip mesh prefixes", opt.SkipPrefixes,
                "Comma separated mesh-name prefixes to leave out entirely. SKYDOME is skipped by " +
                "default: its textures are not in the bundles, so it is a blank shell around the map.");
            opt.ColliderPrefixes = EditorGUILayout.TextField("Collider mesh prefixes", opt.ColliderPrefixes,
                "Instances of these meshes get a MeshCollider (roads/terrain).");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);
            int am = Array.IndexOf(AlphaModeNames, opt.AlphaMode.ToString());
            am = EditorGUILayout.Popup("Alpha mode", am < 0 ? 0 : am, AlphaModeNames,
                "Auto reads the stored alpha bits per texture: opaque stays opaque, " +
                "DXT3/DXT5/DXT1 textures that actually use alpha become cutout.");
            opt.AlphaMode = (NfsuAlphaMode)Mathf.Clamp(am, 0, AlphaModeNames.Length - 1);
            opt.UvFlipV = EditorGUILayout.Toggle("Flip UV v", opt.UvFlipV,
                "D3D UV origin is top-left, Unity is bottom-left - keep this on unless textures sit upside down.");
            opt.FlipWinding = EditorGUILayout.Toggle("Flip triangle winding", opt.FlipWinding,
                "Off by default: the stored index order already faces the same side Unity draws " +
                "(verified on SKYDOME and terrain). Turn on only if faces render see-through.");
            opt.BakeMirrorMeshes = EditorGUILayout.Toggle("Bake mirror meshes", opt.BakeMirrorMeshes,
                "Placement matrices with negative determinant get a mirrored mesh instead of negative scale.");
            opt.BakeShearedPlacements = EditorGUILayout.Toggle("Bake sheared placements", opt.BakeShearedPlacements,
                "A Unity transform is rotation times axis-aligned scale, so matrices that carry shear cannot " +
                "be placed exactly; those get the exact matrix baked into their own mesh variant.");
            opt.ShearToleranceM = EditorGUILayout.FloatField("Shear tolerance (m)", opt.ShearToleranceM,
                "How much of that distortion to tolerate before baking; 0.02 keeps 99% of the " +
                "placements on the shared mesh (only 203 of RA's 21461 need their own).");
            opt.VerifyPlacementAABB = EditorGUILayout.Toggle("Verify placement AABBs", opt.VerifyPlacementAABB,
                "Every placement's box must stay inside the world AABB the bundle stores for it. The report " +
                "prints how many stick out, plus the same count for the decisive subset (long mesh + " +
                "antisymmetric matrix) where a transposed placement matrix cannot hide.");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Project output", EditorStyles.boldLabel);
            opt.RootName = EditorGUILayout.TextField("Scene root object", opt.RootName);
            opt.ProjectFolder = EditorGUILayout.TextField("Asset folder", opt.ProjectFolder);
            opt.WriteTextures = EditorGUILayout.Toggle("Write .dds textures", opt.WriteTextures);
            opt.TexturesForSelectedOnly = EditorGUILayout.Toggle("Textures of selected sections only",
                opt.TexturesForSelectedOnly,
                "Off writes every texture record in the bundle, not just the ones the chosen sections use.");
            opt.ClearGeneratedFirst = EditorGUILayout.Toggle("Clear generated assets first",
                opt.ClearGeneratedFirst,
                "Deletes the .mat / .dds / .mesh assets from an earlier run so nothing stale is reused.");
            opt.SaveMeshAssets = EditorGUILayout.Toggle("Save .mesh assets", opt.SaveMeshAssets,
                "Off keeps meshes in memory only, and the scene cannot then be saved with them.");
            opt.ReplaceExistingRoot = EditorGUILayout.Toggle("Replace existing root", opt.ReplaceExistingRoot);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene setup", EditorStyles.boldLabel);
            opt.SetLayers = EditorGUILayout.Toggle("Assign layers", opt.SetLayers,
                "Adds NFSU_Terrain / NFSU_Building / NFSU_Wall / NFSU_Decor / NFSU_Sign / NFSU_Other to the project layers.");
            opt.MarkStatic = EditorGUILayout.Toggle("Mark instances static", opt.MarkStatic);
            opt.AddMeshColliders = EditorGUILayout.Toggle("Add MeshColliders", opt.AddMeshColliders);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Analyse", GUILayout.Height(28f)))
                Analyse();
            using (new EditorGUI.DisabledScope(opt.BundlePaths.Count == 0))
                if (GUILayout.Button("Import into scene", GUILayout.Height(28f)))
                    Import();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(report))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(report, GUILayout.MinHeight(160f));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Open textures"))
                    EditorUtility.RevealInFinder(Path.GetFullPath(texturesFolder));
                if (GUILayout.Button("Open meshes"))
                    EditorUtility.RevealInFinder(Path.GetFullPath(meshesFolder));
                if (GUILayout.Button("Open materials"))
                    EditorUtility.RevealInFinder(Path.GetFullPath(materialsFolder));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void SetBundles(params string[] paths)
        {
            opt.BundlePaths.Clear();
            foreach (var p in paths)
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    opt.BundlePaths.Add(p.Replace("\\", "/"));
            haveAnalysed = false;
            if (opt.BundlePaths.Count == 0)
                report = "No such bundle under: " + gameDir;
        }

        void Analyse()
        {
            if (cached != null)
                cached.Dispose();
            cached = null;
            sections.Clear();
            var builder = new NfsuMapBuilder(opt);
            try
            {
                var t0 = EditorApplication.timeSinceStartup;
                builder.LoadBundles(sections);
                report = builder.AnalyseReport(sections) +
                         string.Format("\nanalysis took {0:F1}s\n\n{1}",
                             EditorApplication.timeSinceStartup - t0, builder.Stats.Report());
                haveAnalysed = sections.Count > 0;
                cached = builder;
                Debug.Log("NFSU analyse\n" + report);
            }
            catch (Exception e)
            {
                report = "Analyse failed: " + e;
                Debug.LogError(report);
                builder.Dispose();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void Import()
        {
            if (!haveAnalysed || cached == null || sections.Count == 0)
            {
                Analyse();
                if (cached == null || sections.Count == 0)
                    return;
            }

            var builder = cached;
            try
            {
                var t0 = EditorApplication.timeSinceStartup;
                if (opt.ClearGeneratedFirst)
                    builder.ClearGeneratedAssets();
                builder.ImportTextures(sections);
                if (builder.Cancelled)
                {
                    report = "Cancelled during texture export.";
                    return;
                }
                var root = builder.BuildScene(sections);
                if (builder.Cancelled)
                {
                    report = "Cancelled during scene build.\n\n" + builder.Stats.Report();
                    return;
                }
                texturesFolder = opt.ProjectFolder + "/Textures";
                meshesFolder = opt.ProjectFolder + "/Meshes";
                materialsFolder = opt.ProjectFolder + "/Materials";
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                report = string.Format("imported in {0:F1}s\n\n{1}",
                    EditorApplication.timeSinceStartup - t0, builder.Stats.Report());
                Debug.Log("NFSU import\n" + report);
            }
            catch (Exception e)
            {
                report = "Import failed: " + e;
                Debug.LogError(report);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
