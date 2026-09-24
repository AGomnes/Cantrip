#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// Finds content files and reads them the way the engine wants, then hands the text to the
    /// rules library.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ContentLibrary.LoadFile"/> and <see cref="ContentLibrary.LoadFolder"/> go through
    /// <c>System.IO</c>, which works in the editor and finds nothing in an exported game: there is
    /// no <c>res://</c> folder once the project is packed. So discovery goes through
    /// <see cref="DirAccess"/>, reading goes through the imported resource or
    /// <see cref="FileAccess"/>, and the only entry point into the library is
    /// <see cref="ContentLibrary.LoadText"/>.
    /// </para>
    /// <para>
    /// Everything is loaded in ordinal path order. Listener order follows load order, so two
    /// machines that disagree about it are playing two different games.
    /// </para>
    /// </remarks>
    public static class GodotContentLoader
    {
        /// <summary>
        /// Reported when a discovered file cannot be read. It sits outside the core's code ranges
        /// (CT01xx parsing, CT3xx-CT4xx linting) because it is an adapter problem, not a content
        /// problem: the text never reached the parser.
        /// </summary>
        public const string UnreadableCode = "CT0901";

        /// <summary>
        /// Deep enough for any content tree, shallow enough that a cyclic link cannot hang the
        /// game on startup.
        /// </summary>
        private const int MaxDepth = 32;

        /// <summary>
        /// Every <c>.cantrip</c> file under a folder, ordinally sorted. A missing folder is a warning
        /// and an empty list, not an exception: a game with a mods folder nobody created yet
        /// should still start.
        /// </summary>
        public static IReadOnlyList<string> Discover(string folder)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(folder)) return found;

            if (!DirAccess.DirExistsAbsolute(folder))
            {
                GD.PushWarning($"Cantrip: no content folder at {folder}.");
                return found;
            }

            Walk(folder, found, 0);
            return ContentPaths.Ordered(found);
        }

        /// <summary>
        /// Reads one file. The imported resource comes first, because in an exported game it is
        /// the only copy that exists; a raw read then covers projects that keep content as loose
        /// files and mods installed under <c>user://</c>, which no importer has ever seen.
        /// </summary>
        public static bool TryRead(string resPath, out string text)
        {
            text = string.Empty;
            if (string.IsNullOrEmpty(resPath)) return false;

            if (ResourceLoader.Exists(resPath))
            {
                // Read past the cache and leave nothing in it. Hot reload asks for the same path again
                // and must get what is on disk now, not what was cached before the designer hit
                // save. And a copy left in the cache can still be found there after the garbage
                // collector has let go of its C# half but before Godot has freed it: replacing that
                // copy, as this once did, crashed a later load whenever the collector ran just then.
                Resource? loaded = ResourceLoader.Load(resPath, string.Empty, ResourceLoader.CacheMode.Ignore);
                if (loaded is CantripContentFile imported)
                {
                    text = imported.Text ?? string.Empty;
                    return true;
                }
            }

            if (FileAccess.FileExists(resPath))
            {
                string raw = FileAccess.GetFileAsString(resPath);
                if (FileAccess.GetOpenError() == Error.Ok)
                {
                    text = raw ?? string.Empty;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Loads the given paths into a library, ordinally sorted, and returns everything the
        /// parser had to say plus any file that could not be read.
        /// </summary>
        public static DiagnosticBag LoadInto(ContentLibrary library, IEnumerable<string> paths) =>
            LoadInto(library, paths, null, null);

        /// <summary>
        /// The same, with the editor's unsaved buffers standing in for their own files, and the
        /// text of the rest remembered between checks.
        /// </summary>
        /// <remarks>
        /// A game passes nothing for either: only the dock has buffers, and only while someone is
        /// typing into one. Everything else is unchanged, load order included, so what the dock
        /// reports is what the game will load once the file is saved. The cache is the dock's,
        /// and the dock empties it whenever the project's files change; reading a content file
        /// goes through its imported resource, which costs more than parsing it does.
        /// </remarks>
        public static DiagnosticBag LoadInto(
            ContentLibrary library,
            IEnumerable<string> paths,
            CantripDrafts? drafts,
            IDictionary<string, string>? cache)
        {
            if (library == null) throw new ArgumentNullException(nameof(library));

            var all = new DiagnosticBag();
            foreach (string path in ContentPaths.Ordered(paths))
            {
                string? draft = drafts?.TextFor(path);
                if (draft != null)
                {
                    all.AddRange(library.LoadText(draft, path));
                    continue;
                }

                if (cache != null && cache.ContainsKey(path))
                {
                    all.AddRange(library.LoadText(cache[path], path));
                    continue;
                }

                if (!TryRead(path, out string text))
                {
                    all.Error(
                        UnreadableCode,
                        $"Could not read `{path}`. An exported game only sees files the export includes, either imported or matched by a non-resource filter.",
                        new SourceSpan(path, 0, 0, 0));
                    continue;
                }

                if (cache != null) cache[path] = text;
                all.AddRange(library.LoadText(text, path));
            }
            return all;
        }

        /// <summary>Discovery and loading in one step, for the common case of a single content folder.</summary>
        public static DiagnosticBag LoadFolder(ContentLibrary library, string folder) =>
            LoadInto(library, Discover(folder));

        private static void Walk(string folder, List<string> found, int depth)
        {
            if (depth > MaxDepth)
            {
                GD.PushWarning($"Cantrip: stopped looking for content more than {MaxDepth} folders below {folder}.");
                return;
            }

            foreach (string file in DirAccess.GetFilesAt(folder))
            {
                // An exported project lists cards.cantrip.remap where the editor lists cards.cantrip, and the
                // editor also lists the .import sidecar. Both name the same loadable path.
                string name = ContentPaths.StripMarker(file);
                if (ContentPaths.IsContentFile(name)) found.Add(ContentPaths.Combine(folder, name));
            }

            foreach (string directory in DirAccess.GetDirectoriesAt(folder))
            {
                if (directory.StartsWith(".", StringComparison.Ordinal)) continue; // .godot, .import
                Walk(ContentPaths.Combine(folder, directory), found, depth + 1);
            }
        }
    }
}
