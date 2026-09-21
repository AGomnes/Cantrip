#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The content files a game loads, in the order it loads them.
    /// </summary>
    /// <remarks>
    /// Scanning a folder answers "what is on disk"; a manifest answers "what this game is made
    /// of", which is the question that matters once mods, DLC folders or a half-written file are
    /// in play. Paths are kept ordinally sorted because listener order follows load order, and a
    /// game that loads its files in a different order is a different game.
    /// </remarks>
    [Tool]
    [GlobalClass]
    public partial class ContentManifest : Resource
    {
        private string[] _paths = Array.Empty<string>();

        /// <summary>Content paths, ordinally sorted and de-duplicated on assignment.</summary>
        [Export]
        public string[] Paths
        {
            get => _paths;
            set
            {
                IReadOnlyList<string> ordered = ContentPaths.Ordered(value);
                var copy = new string[ordered.Count];
                for (int i = 0; i < ordered.Count; i++) copy[i] = ordered[i];
                _paths = copy;
            }
        }

        /// <summary>Everything under a folder, discovered through <see cref="DirAccess"/>.</summary>
        public static ContentManifest FromFolder(string folder) => FromPaths(GodotContentLoader.Discover(folder));

        public static ContentManifest FromPaths(IEnumerable<string> paths) => new ContentManifest { Paths = AsArray(paths) };

        /// <summary>Adds a path, keeping the list sorted. Adding one twice changes nothing.</summary>
        public void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var combined = new List<string>(_paths.Length + 1);
            combined.AddRange(_paths);
            combined.Add(path);
            Paths = combined.ToArray();
        }

        public bool Contains(string path) => Array.IndexOf(_paths, path) >= 0;

        private static string[] AsArray(IEnumerable<string> paths)
        {
            if (paths is string[] already) return already;

            var list = new List<string>();
            foreach (string path in paths) list.Add(path);
            return list.ToArray();
        }
    }
}
