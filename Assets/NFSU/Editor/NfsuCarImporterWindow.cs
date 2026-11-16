// Editor window that drives NfsuCarBuilder: pick the car bundles, decide what to build
// (textures, meshes, wheel rig, LODs, collider, prefab), then build the traffic cars as real
// project assets and drop them into the open scene.
//
// Everything is produced under Assets/NFSU (Textures / Materials / TrafficCars / TrafficCars/Meshes)
// so the scene can be saved; the game install is never written to.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NFSU
{
    public sealed class NfsuCarImporterWindow : EditorWindow
    {
        static readonly string[] AlphaModeNames = { "Auto", "Opaque", "Cutout", "Blend", "Additive" };

        /// <summary>Measured on the 15 traffic cars: every drawable uses 4 texture slots.</summary>
        const int ExpectedSlots = 4;

        readonly NfsuCarImportOptions opt = new NfsuCarImportOptions();

        string gameDir = "";
        Vector2 scroll;
        string report = "";
        bool carListFoldout = true;
        bool packFoldout = true;
        int trafficCars;

        [MenuItem("NFSU/Import Traffic Cars")]
        static void Open()
        {
            var w = GetWindow<NfsuCarImporterWindow>("NFSU Traffic Cars");
            w.minSize = new Vector2(460f, 420f);
            w.Show();
        }

        void OnEnable()
        {
            gameDir = NfsuTrafficCarSource.GameRoot() ?? "";
            if (opt.CarPaths.Count == 0)
                SetCars();
            if (opt.PackPaths.Count == 0)
                opt.PackPaths.AddRange(NfsuTrafficCarSource.Packs(gameDir));
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                EditorGUILayout.HelpBox(
                    "No game install found above the project folder. The cars and their texture " +
                    "packs are read straight from the game files, so nothing can be imported " +
                    "until the install is next to the project (or the paths below are set by hand).",
                    MessageType.Warning);
            }

            EditorGUILayout.LabelField("Cars", EditorStyles.boldLabel);
            carListFoldout = EditorGUILayout.Foldout(carListFoldout,
                string.Format("{0} car bundles ({1} of them are traffic cars)",
                              opt.CarPaths.Count, trafficCars > 0 ? trafficCars.ToString() : "15"));
            if (carListFoldout)
            {
                for (int i = 0; i < opt.CarPaths.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    string path = opt.CarPaths[i];
                    string next = EditorGUILayout.TextField("Car " + (i + 1), path);
                    if (next != path)
                        opt.CarPaths[i] = next;
                    if (GUILayout.Button("...", GUILayout.Width(30f)))
                    {
                        string picked = EditorUtility.OpenFilePanel("car GEOMETRY.BIN", gameDir, "BIN");
                        if (!string.IsNullOrEmpty(picked))
                            opt.CarPaths[i] = picked.Replace("\\", "/");
                    }
                    if (GUILayout.Button("x", GUILayout.Width(22f)))
                    {
                        opt.CarPaths.RemoveAt(i);
                        EditorGUILayout.EndHorizontal();
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("All Cars folders"))
                    SetCars();
                if (GUILayout.Button("Add car bundle..."))
                {
                    string picked = EditorUtility.OpenFilePanel("car GEOMETRY.BIN", gameDir, "BIN");
                    if (!string.IsNullOrEmpty(picked))
                        opt.CarPaths.Add(picked.Replace("\\", "/"));
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();
            opt.CarFilter = EditorGUILayout.TextField("Car names", opt.CarFilter,
                "Comma or space separated substring match, empty = every traffic car. " +
                "The 15 are 4DR_SEDAN, 4DR_SEDAN02, AMBULANCE, BUS, COUPE, FIRETRUCK, HATCHBACK, " +
                "HATCHBACK02, MINIVAN, PANELVAN, PARCELVAN, PICKUP, SUV, TAXI, TAXI02.");
            opt.MaxCars = EditorGUILayout.IntField("Max cars", opt.MaxCars,
                "0 = no limit. 15 traffic cars is 163 renderers, 73 meshes and about 22k vertices.");

            EditorGUILayout.Space();
            packFoldout = EditorGUILayout.Foldout(packFoldout,
                "Texture packs (" + opt.PackPaths.Count + ")");
            if (packFoldout)
            {
                for (int i = 0; i < opt.PackPaths.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    string path = opt.PackPaths[i];
                    string next = EditorGUILayout.TextField("Pack " + (i + 1), path);
                    if (next != path)
                        opt.PackPaths[i] = next;
                    if (GUILayout.Button("...", GUILayout.Width(30f)))
                    {
                        string picked = EditorUtility.OpenFilePanel("texture pack", gameDir, "BUN,BIN");
                        if (!string.IsNullOrEmpty(picked))
                            opt.PackPaths[i] = picked.Replace("\\", "/");
                    }
                    if (GUILayout.Button("x", GUILayout.Width(22f)))
                    {
                        opt.PackPaths.RemoveAt(i);
                        EditorGUILayout.EndHorizontal();
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("The 4 packs the traffic cars need"))
                    SetPacks();
                if (GUILayout.Button("Add pack..."))
                {
                    string picked = EditorUtility.OpenFilePanel("texture pack", gameDir, "BUN,BIN");
                    if (!string.IsNullOrEmpty(picked))
                        opt.PackPaths.Add(picked.Replace("\\", "/"));
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox(
                    "Every traffic drawable references exactly " + ExpectedSlots +
                    " texture keys: its own *_SKIN1 plus the shared TIREBACK, CARBOTTOM and " +
                    "WINDOW_TRAFFIC. Those four records live in Cars\\TEXTURES.BIN, " +
                    "Cars\\AMBULANCE\\TEXTURES.BIN and Global\\GLOBALB.BUN, so this list is enough - " +
                    "each extra pack is a JDLZ block chain to decode.", MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);
            int am = Array.IndexOf(AlphaModeNames, opt.AlphaMode.ToString());
            am = EditorGUILayout.Popup("Alpha mode", am < 0 ? 0 : am, AlphaModeNames,
                "Auto runs the shared classifier. Traffic-car art comes out Opaque on its own " +
                "(skins are DXT1 with no keyed alpha, WINDOW_TRAFFIC is A8), so Auto and Opaque " +
                "agree here - 18 materials, all opaque, measured.");
            opt.AlphaMode = (NfsuAlphaMode)Mathf.Clamp(am, 0, AlphaModeNames.Length - 1);
            opt.UvFlipV = EditorGUILayout.Toggle("Flip UV v", opt.UvFlipV,
                "D3D UV origin is top-left, Unity is bottom-left - keep this on unless the skins " +
                "sit upside down.");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("What to build", EditorStyles.boldLabel);
            opt.WriteTextures = EditorGUILayout.Toggle("Write .dds textures", opt.WriteTextures,
                "Unity imports the .dds itself; no BC decoder is involved.");
            opt.SaveMeshAssets = EditorGUILayout.Toggle("Save .mesh assets", opt.SaveMeshAssets,
                "Off keeps the meshes in memory only, and the scene cannot then be saved with them.");
            opt.BuildWheelRig = EditorGUILayout.Toggle("Build wheel rig", opt.BuildWheelRig,
                "Mount (steer pivot) -> Spin (roll) -> tyre per corner, plus NfsuTrafficCarRig on " +
                "the car root, so the tyres can roll and steer at runtime.");
            opt.AddLodGroup = EditorGUILayout.Toggle("Add LODGroup", opt.AddLodGroup,
                "_A / _B / _C drawables become three LOD levels; the game's switch distances are " +
                "not stored, so they come from the mesh sizes.");
            opt.AddBoxCollider = EditorGUILayout.Toggle("Add BoxCollider", opt.AddBoxCollider,
                "The bundle's own physics box, which is slightly smaller than the body mesh.");
            opt.SavePrefab = EditorGUILayout.Toggle("Save prefab", opt.SavePrefab,
                "One prefab per car under Assets/NFSU/TrafficCars, ready to spawn on a traffic path.");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Project output", EditorStyles.boldLabel);
            opt.RootName = EditorGUILayout.TextField("Scene root object", opt.RootName);
            opt.ProjectFolder = EditorGUILayout.TextField("Asset folder", opt.ProjectFolder);
            opt.ReplaceExistingRoot = EditorGUILayout.Toggle("Replace existing root",
                opt.ReplaceExistingRoot,
                "Off keeps the cars from an earlier run in the scene as well.");

            EditorGUILayout.Space();
            if (GUILayout.Button("Import into scene", GUILayout.Height(30f)))
                Import();

            if (!string.IsNullOrEmpty(report))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(report, GUILayout.MinHeight(200f));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Open prefabs"))
                    Reveal(opt.ProjectFolder + "/" + NfsuCarBuilder.CarSub);
                if (GUILayout.Button("Open meshes"))
                    Reveal(opt.ProjectFolder + "/" + NfsuCarBuilder.MeshSub);
                if (GUILayout.Button("Open textures"))
                    Reveal(opt.ProjectFolder + "/Textures");
                if (GUILayout.Button("Open materials"))
                    Reveal(opt.ProjectFolder + "/Materials");
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        static void Reveal(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                EditorUtility.RevealInFinder(Path.GetFullPath(folder));
        }

        void SetCars()
        {
            opt.CarPaths.Clear();
            opt.CarPaths.AddRange(NfsuTrafficCarSource.CarBundles(gameDir));
        }

        void SetPacks()
        {
            opt.PackPaths.Clear();
            opt.PackPaths.AddRange(NfsuTrafficCarSource.Packs(gameDir));
        }

        void Import()
        {
            if (opt.CarPaths.Count == 0)
            {
                report = "No car bundles listed. \"All Cars folders\" fills the list from the install.";
                return;
            }
            try
            {
                var t0 = EditorApplication.timeSinceStartup;
                var stats = NfsuCarBuilder.Import(opt);
                trafficCars = stats.CarsImported;

                var root = GameObject.Find(opt.RootName);
                if (root != null)
                {
                    Selection.activeGameObject = root;
                    EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                }
                report = string.Format("imported in {0:F1}s\r\n\r\n{1}",
                    EditorApplication.timeSinceStartup - t0, stats.Report());
                Debug.Log("NFSU traffic cars\r\n" + report);
            }
            catch (Exception e)
            {
                report = "Import failed: " + e;
                Debug.LogError(report);
            }
        }
    }
}
