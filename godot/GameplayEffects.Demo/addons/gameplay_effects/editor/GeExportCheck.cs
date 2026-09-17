#if TOOLS
using System.Collections.Generic;
using Godot;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// Fails an export whose <c>.ge</c> content would not reach the build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Content that is missing from the pack is the adapter's worst failure mode: everything works
    /// in the editor, the exported game starts, and then no card exists. A file reaches the build
    /// one of two ways, and this checks both: it was imported, so the resource is packed, or a
    /// non-resource include filter such as <c>*.ge</c> carries the raw file in. Anything the
    /// exclude filter drops reaches the build no way at all.
    /// </para>
    /// <para>
    /// The verdict is delivered through <see cref="EditorExportPlatform.AddMessage"/> with
    /// <see cref="EditorExportPlatform.ExportMessageType.Error"/>, which fails the export rather
    /// than printing a line nobody reads. The scan runs at the start, where it can still stop the
    /// build; a second, quieter pass at the end compares what the exporter actually offered us
    /// against what we expected, because only that pass sees the export's own filtering.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class GeExportCheck : EditorExportPlugin
    {
        private const string Category = "Gameplay Effects";

        private readonly List<string> _expected = new List<string>();
        private readonly HashSet<string> _offered = new HashSet<string>();

        public override string _GetName() => "gameplay_effects_content_check";

        public override void _ExportBegin(string[] features, bool isDebug, string path, uint flags)
        {
            _expected.Clear();
            _offered.Clear();

            EditorExportPreset? preset = GetExportPreset();
            if (preset == null) return;

            string includeFilter = preset.GetIncludeFilter();
            string excludeFilter = preset.GetExcludeFilter();

            foreach (string file in GodotContentLoader.Discover(ContentPaths.ResourceScheme))
            {
                if (ContentPaths.MatchesFilters(file, excludeFilter))
                {
                    Report($"{file} is dropped by this preset's exclude filter, so the exported game will not find it.");
                    continue;
                }

                bool imported = FileAccess.FileExists(ContentPaths.ImportMarkerOf(file));
                bool carriedRaw = ContentPaths.MatchesFilters(file, includeFilter) || preset.HasExportFile(file);

                if (imported || carriedRaw) _expected.Add(file);
                else Report($"{file} is neither imported nor matched by this preset's non-resource filters, so it will be missing from the export. Reimport it, or add *.ge to the filters that include files.");
            }
        }

        public override void _ExportFile(string path, string type, string[] features)
        {
            if (ContentPaths.IsContentFile(path)) _offered.Add(path);
        }

        public override void _ExportEnd()
        {
            foreach (string file in _expected)
            {
                if (!_offered.Contains(file))
                    Report($"{file} was expected in the export but the exporter never offered it. Check the preset's resource selection.");
            }

            _expected.Clear();
            _offered.Clear();
        }

        private void Report(string message)
        {
            EditorExportPlatform? platform = GetExportPlatform();
            if (platform != null) platform.AddMessage(EditorExportPlatform.ExportMessageType.Error, Category, message);
            else GD.PushError($"{Category}: {message}");
        }
    }
}
#endif
