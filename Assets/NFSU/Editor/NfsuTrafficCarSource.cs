// One place for the two measured facts about the traffic cars in a game install: which bundles
// they are, and which packs hold the textures they reference. The self test and the importer
// window both need them, so neither of them can drift away from the other.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NFSU
{
    public static class NfsuTrafficCarSource
    {
        /// <summary>
        /// The packs the traffic cars reference, measured with <c>nfs_probe\car_tex_sources.py</c>:
        /// every traffic drawable resolves exactly 4 slot keys - its own <c>*_SKIN1</c> (which lives
        /// in the shared <c>Cars\TEXTURES.BIN</c>, whose pixel blob is a JDLZ block chain, or in the
        /// car's own pack as with AMBULANCE) plus the three shared parts TIREBACK, CARBOTTOM and
        /// WINDOW_TRAFFIC, all of them in <c>Global\GLOBALB.BUN</c>. Nothing else is needed, so the
        /// list stays at exactly these four files - every pack costs a block chain to decode.
        /// </summary>
        static readonly string[][] PackFiles =
        {
            new[] { "Cars", "TEXTURES.BIN" },
            new[] { @"Cars\AMBULANCE", "TEXTURES.BIN" },
            new[] { "Global", "GLOBALA.BUN" },
            new[] { "Global", "GLOBALB.BUN" },
        };

        /// <summary>Walks up from Assets/ to the game install folder, or null if it is not there.</summary>
        public static string GameRoot()
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

        /// <summary>One file of the game install, or null. Folder may be nested ("Cars\AMBULANCE").</summary>
        public static string FindFile(string gameRoot, string folder, string name)
        {
            if (string.IsNullOrEmpty(gameRoot))
                return null;
            string candidate = Path.Combine(Path.Combine(gameRoot, folder), name);
            return File.Exists(candidate) ? candidate : null;
        }

        /// <summary>
        /// Every <c>Cars\*\GEOMETRY.BIN</c> in the install - 39 in this one. The builder decides
        /// which of them are traffic cars (<c>NfsuCarBundle.LooksLikeTraffic</c>: 15 here), so the
        /// caller does not have to know the names.
        /// </summary>
        public static List<string> CarBundles(string gameRoot)
        {
            var list = new List<string>();
            string cars = string.IsNullOrEmpty(gameRoot) ? null : Path.Combine(gameRoot, "Cars");
            if (cars == null || !Directory.Exists(cars))
                return list;
            foreach (var f in Directory.GetFiles(cars, "GEOMETRY.BIN", SearchOption.AllDirectories))
                list.Add(f.Replace('\\', '/'));
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>The texture packs the traffic cars need, as existing absolute paths.</summary>
        public static List<string> Packs(string gameRoot)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(gameRoot))
                return list;
            foreach (var pair in PackFiles)
            {
                string p = FindFile(gameRoot, pair[0], pair[1]);
                if (p != null)
                    list.Add(p.Replace('\\', '/'));
                else
                    Debug.LogWarning("NFSU: traffic-car texture pack not found: " +
                                     pair[0] + "\\" + pair[1]);
            }
            return list;
        }
    }
}
