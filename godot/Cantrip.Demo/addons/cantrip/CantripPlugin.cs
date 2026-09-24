#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The editor half of the addon. Everything here is compiled out of an exported game: the
    /// Godot SDK only defines TOOLS (and only references the editor assembly) in the Debug
    /// configuration, so without this guard an export would not compile at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The [Tool] attribute is not optional either. Without it the plugin silently never runs:
    /// the build succeeds, Godot logs nothing, and _EnterTree is simply never called.
    /// </para>
    /// <para>
    /// This is also the only place the importer is registered, and an unregistered importer is a
    /// silent failure of its own: <c>.cantrip</c> files are simply never imported, so an exported game
    /// ships with no content at all. Every registration below is undone in
    /// <see cref="_ExitTree"/>, because the editor reloads this assembly on every build and a dock
    /// that was not removed comes back twice.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripPlugin : EditorPlugin
    {
        /// <summary>
        /// Runs the dock's work once and prints the result. It is how a headless editor, which has
        /// no mouse, can still prove that the panels build and that loading, linting, testing and
        /// describing all work against the project's real content. A check of the dock's own that
        /// fails makes the editor exit with 1.
        /// </summary>
        private const string SelfTestFlag = "--cantrip-selftest";

        /// <summary>What a self-test line says when a check of the dock's own has failed.</summary>
        internal const string SelfTestFailed = "FAILED";

        private CantripImportPlugin? _importPlugin;
        private CantripExportCheck? _exportCheck;
        private CantripSyntaxHighlighter? _highlighter;
        private CantripWorkspace? _workspace;
        private CantripPanel? _panel;
        private EditorDock? _dock;
        private CantripDebuggerPlugin? _debugger;

        public override string _GetPluginName() => "Cantrip";

        public override void _EnterTree()
        {
            _importPlugin = new CantripImportPlugin();
            AddImportPlugin(_importPlugin);

            _exportCheck = new CantripExportCheck();
            AddExportPlugin(_exportCheck);

            _workspace = new CantripWorkspace();

            _panel = new CantripPanel();
            _panel.Initialize(_workspace);

            // A dock is its own object in 4.6; AddControlToDock and the bottom-panel calls are both
            // deprecated in favour of this. Diagnostics are wide and list-shaped, so it opens at the
            // bottom, beside Output and Debugger.
            _dock = new EditorDock { Title = "Cantrip", DefaultSlot = EditorDock.DockSlot.Bottom };
            _dock.AddChild(_panel);
            AddDock(_dock);

            // Belt and braces: the script editor only opens Script resources, so this does nothing
            // for .cantrip files today. It costs nothing and will be right the day that changes; the
            // highlighting a designer actually sees is in the dock's own editor.
            _highlighter = new CantripSyntaxHighlighter();
            EditorInterface.Singleton?.GetScriptEditor()?.RegisterSyntaxHighlighter(_highlighter);

            // A step in the trace of a running game opens the content line that caused it, in the
            // same editor a diagnostic or a failing test opens.
            _debugger = new CantripDebuggerPlugin();
            _debugger.NavigateRequested += OnNavigateRequested;
            AddDebuggerPlugin(_debugger);

            _workspace.Reload();
            GD.Print(
                $"Cantrip: dock ready, {_workspace.Files.Count} content file(s), " +
                $"{_workspace.ErrorCount} error(s), {_workspace.WarningCount} warning(s), {_workspace.NoteCount} note(s).");

            if (SelfTestRequested()) RunSelfTest();
        }

        public override void _ExitTree()
        {
            if (_debugger != null)
            {
                _debugger.NavigateRequested -= OnNavigateRequested;
                _debugger.Close();
                RemoveDebuggerPlugin(_debugger);
                _debugger = null;
            }

            if (_highlighter != null)
            {
                EditorInterface.Singleton?.GetScriptEditor()?.UnregisterSyntaxHighlighter(_highlighter);
                _highlighter = null;
            }

            if (_dock != null)
            {
                RemoveDock(_dock);
                _dock.QueueFree();   // takes the panel with it
                _dock = null;
                _panel = null;
            }

            // The workspace holds the dock's unsaved buffers, and it goes when the assembly does,
            // which is on every C# build. Nothing may be written to disk without being asked, but
            // the work must not disappear in silence either.
            int unsaved = _workspace?.Drafts.UnsavedCount ?? 0;
            if (unsaved > 0)
            {
                GD.PushWarning(
                    $"Cantrip: the dock closed with {unsaved} unsaved content buffer(s), which are gone. " +
                    "Building the C# project reloads the addon, so save content before you build.");
            }

            _workspace = null;

            if (_exportCheck != null)
            {
                RemoveExportPlugin(_exportCheck);
                _exportCheck = null;
            }

            if (_importPlugin != null)
            {
                RemoveImportPlugin(_importPlugin);
                _importPlugin = null;
            }
        }

        private void OnNavigateRequested(string file, int line, int column) => _panel?.ShowSource(file, line, column);

        private static bool SelfTestRequested()
        {
            foreach (string argument in OS.GetCmdlineArgs())
            {
                if (argument == SelfTestFlag) return true;
            }
            foreach (string argument in OS.GetCmdlineUserArgs())
            {
                if (argument == SelfTestFlag) return true;
            }
            return false;
        }

        private void RunSelfTest()
        {
            if (_panel == null) return;

            var lines = new List<string>(_panel.SelfTest());

            // The debugger's tabs are the one part of the addon a headless editor would otherwise
            // never touch: they only ever exist inside a live session.
            if (_debugger != null && _workspace != null) lines.AddRange(_debugger.SelfTest(_workspace.Content));

            bool failed = false;
            foreach (string line in lines)
            {
                GD.Print("Cantrip self-test: " + line);
                failed |= line.Contains(SelfTestFailed, StringComparison.Ordinal);
            }

            // Most lines are a report: a count of failing content tests is about the content, not
            // the dock. A check of the dock's own says FAILED, and fails the run with it.
            if (failed) GetTree().Quit(1);
        }
    }
}
#endif
