#if TOOLS
using Cantrip.Content;
using Cantrip.Diagnostics;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// Imports <c>.cantrip</c> files into <see cref="CantripContentFile"/> resources, so that content is
    /// packed with the game and can be loaded without touching the file system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The import always writes a resource, even for a file full of errors. A single file cannot
    /// be validated on its own: a card naming a status defined next door is correct, and would be
    /// wrong to reject. So problems are reported to the editor's output and the text is imported
    /// anyway; the runtime loads the whole folder and reports what actually does not resolve.
    /// </para>
    /// <para>
    /// <see cref="_GetResourceType"/> returns "Resource" rather than "CantripContentFile" on purpose.
    /// The global class name imports perfectly well and then fails at run time with "No loader
    /// found for resource", because the type is registered by the editor plugin, which an exported
    /// game does not run. The saved resource still carries the script, so the loaded object really
    /// is a <see cref="CantripContentFile"/>.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripImportPlugin : EditorImportPlugin
    {
        /// <summary>The name Godot records in each <c>.import</c> file.</summary>
        public const string ImporterName = "cantrip.content";

        private const string ReportOption = "diagnostics/report_problems";

        public override string _GetImporterName() => ImporterName;

        public override string _GetVisibleName() => "Cantrip Content";

        public override string[] _GetRecognizedExtensions() => new[] { "cantrip" };

        public override string _GetSaveExtension() => "res";

        public override string _GetResourceType() => "Resource";

        /// <summary>Bump this when the imported form changes, or Godot will keep the stale resources.</summary>
        public override int _GetFormatVersion() => 1;

        public override float _GetPriority() => 1.0f;

        public override int _GetImportOrder() => 0;

        public override int _GetPresetCount() => 1;

        public override string _GetPresetName(int presetIndex) => "Default";

        public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetImportOptions(string path, int presetIndex) =>
            new Godot.Collections.Array<Godot.Collections.Dictionary>
            {
                new Godot.Collections.Dictionary
                {
                    { "name", ReportOption },
                    { "default_value", true },
                },
            };

        public override bool _GetOptionVisibility(string path, StringName optionName, Godot.Collections.Dictionary options) => true;

        /// <summary>
        /// Single-threaded on purpose. Importing a text file costs nothing, and reporting problems
        /// from several threads would interleave a file's diagnostics with another file's.
        /// </summary>
        public override bool _CanImportThreaded() => false;

        public override Error _Import(
            string sourceFile,
            string savePath,
            Godot.Collections.Dictionary options,
            Godot.Collections.Array<string> platformVariants,
            Godot.Collections.Array<string> genFiles)
        {
            string text = FileAccess.GetFileAsString(sourceFile);
            Error opened = FileAccess.GetOpenError();
            if (opened != Error.Ok)
            {
                GD.PushError($"Cantrip: could not read {sourceFile} ({opened}).");
                return opened;
            }

            if (ShouldReport(options)) Report(sourceFile, text);

            var resource = new CantripContentFile
            {
                SourcePath = sourceFile,
                Text = text,
            };

            return ResourceSaver.Save(resource, $"{savePath}.{_GetSaveExtension()}");
        }

        /// <summary>
        /// Parses the file for its diagnostics alone. The message is the one the command-line tool
        /// prints, down to the <c>file:line:column</c> prefix, so a problem reads the same wherever
        /// the designer meets it.
        /// </summary>
        private static void Report(string sourceFile, string text)
        {
            DiagnosticBag diagnostics = new ContentLibrary().LoadText(text, sourceFile);
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error) GD.PushError(diagnostic.ToString());
                else GD.PushWarning(diagnostic.ToString());
            }
        }

        private static bool ShouldReport(Godot.Collections.Dictionary options)
        {
            if (options != null && options.TryGetValue(ReportOption, out Variant value) && value.VariantType == Variant.Type.Bool)
                return value.AsBool();

            return true;
        }
    }
}
#endif
