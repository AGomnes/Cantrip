#nullable enable
using System;
using System.Collections.Generic;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The path rules the adapter needs on both sides of the engine boundary: which files are
    /// content, what order they load in, and whether a path is something the engine owns.
    /// </summary>
    /// <remarks>
    /// This lives in <c>shared/</c> and names no engine type, so the rules can be tested without
    /// starting Godot. That matters more than it sounds: load order decides listener order, and a
    /// determinism bug that only reproduces inside the editor is a bad afternoon.
    /// </remarks>
    public static class ContentPaths
    {
        /// <summary>The extension the importer claims, with its dot.</summary>
        public const string Extension = ".cantrip";

        /// <summary>Everything shipped with the game, read-only once exported.</summary>
        public const string ResourceScheme = "res://";

        /// <summary>The writable per-user folder, which is where mods and generated content live.</summary>
        public const string UserScheme = "user://";

        /// <summary>
        /// Suffixes Godot leaves beside a source file. The editor writes <c>.import</c> next to an
        /// imported file; an export replaces the source with a <c>.remap</c> pointing at the
        /// imported copy. Either way the original path is still the one to load, so discovery has
        /// to see through both.
        /// </summary>
        private static readonly string[] Markers = { ".import", ".remap" };

        /// <summary>True for a DSL source file, whatever the case of its extension.</summary>
        public static bool IsContentFile(string? path) =>
            !string.IsNullOrEmpty(path) && path!.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the path is one of Godot's sidecars rather than a file to read.</summary>
        public static bool IsMarker(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (string marker in Markers)
                if (path!.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Strips a single <c>.import</c> or <c>.remap</c> suffix. An exported project lists
        /// <c>cards.cantrip.remap</c> where the editor lists <c>cards.cantrip</c>, and both mean the same
        /// resource.
        /// </summary>
        public static string StripMarker(string? path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            foreach (string marker in Markers)
                if (path!.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                    return path.Substring(0, path.Length - marker.Length);
            return path!;
        }

        /// <summary>The <c>.import</c> sidecar the editor writes next to an imported source file.</summary>
        public static string ImportMarkerOf(string path) => path + ".import";

        /// <summary>
        /// Ordering for content loading: ordinal, because the engine's determinism rests on every
        /// machine loading the same files in the same order, and culture-aware comparison does not
        /// promise that.
        /// </summary>
        public static int Compare(string? left, string? right) =>
            string.CompareOrdinal(left ?? string.Empty, right ?? string.Empty);

        /// <summary>
        /// The given paths, de-duplicated and sorted ordinally. Empty entries are dropped rather
        /// than reported: a manifest with a blank line is not worth an error.
        /// </summary>
        public static IReadOnlyList<string> Ordered(IEnumerable<string>? paths)
        {
            var unique = new List<string>();
            if (paths == null) return unique;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (seen.Add(path)) unique.Add(path);
            }

            unique.Sort(Compare);
            return unique;
        }

        public static bool IsResourcePath(string? path) =>
            path != null && path.StartsWith(ResourceScheme, StringComparison.Ordinal);

        public static bool IsUserPath(string? path) =>
            path != null && path.StartsWith(UserScheme, StringComparison.Ordinal);

        /// <summary>
        /// True for a path the engine resolves itself. Anything else is an operating-system path,
        /// which an exported game cannot use to reach its own content.
        /// </summary>
        public static bool IsEnginePath(string? path) => IsResourcePath(path) || IsUserPath(path);

        /// <summary>Joins a folder and an entry without doubling or dropping the separator.</summary>
        public static string Combine(string? folder, string? name)
        {
            if (string.IsNullOrEmpty(folder)) return name ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return folder!;
            return folder!.EndsWith("/", StringComparison.Ordinal) ? folder + name : folder + "/" + name;
        }

        /// <summary>The path with its engine scheme removed, which is what export filters match.</summary>
        public static string WithoutScheme(string? path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (IsResourcePath(path)) return path!.Substring(ResourceScheme.Length);
            if (IsUserPath(path)) return path!.Substring(UserScheme.Length);
            return path!;
        }

        /// <summary>
        /// Whether a path is covered by one of Godot's export filter lists, such as
        /// <c>"*.cantrip, data/*.json"</c>. The engine matches these against the path relative to
        /// <c>res://</c>, case-insensitively, with <c>*</c> spanning folder separators, so this
        /// does the same: a check that disagrees with the exporter would be worse than no check.
        /// </summary>
        public static bool MatchesFilters(string? path, string? filters)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(filters)) return false;

            foreach (string filter in filters!.Split(','))
            {
                string trimmed = filter.Trim();
                if (trimmed.Length != 0 && Matches(path, trimmed)) return true;
            }
            return false;
        }

        /// <summary>One glob, with <c>*</c> and <c>?</c>, matched the way Godot's <c>matchn</c> does.</summary>
        public static bool Matches(string? path, string? filter)
        {
            if (string.IsNullOrEmpty(filter)) return false;
            string subject = WithoutScheme(path);

            int s = 0, f = 0, starF = -1, starS = 0;
            while (s < subject.Length)
            {
                if (f < filter!.Length && (filter[f] == '?' || Same(filter[f], subject[s])))
                {
                    s++;
                    f++;
                }
                else if (f < filter.Length && filter[f] == '*')
                {
                    // Remember the star and retry from here, one character later, if the rest fails.
                    starF = f++;
                    starS = s;
                }
                else if (starF >= 0)
                {
                    f = starF + 1;
                    s = ++starS;
                }
                else
                {
                    return false;
                }
            }

            while (f < filter!.Length && filter[f] == '*') f++;
            return f == filter.Length;
        }

        private static bool Same(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
    }
}
