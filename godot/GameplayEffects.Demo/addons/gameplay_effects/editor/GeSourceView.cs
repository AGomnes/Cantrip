#if TOOLS
using System;
using Godot;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// Shows one <c>.ge</c> file at a line, which is where every "jump to source" in the dock lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Godot has no API for opening a non-script text file at a line.
    /// <see cref="EditorInterface.EditScript(Script, int, int, bool)"/> needs a
    /// <see cref="Script"/>, and a <c>.ge</c> file is not one; <see cref="EditorInterface.SelectFile"/>
    /// only reveals a file in the FileSystem dock, which leaves the designer to open it and count
    /// lines themselves. So the addon carries its own read-only <see cref="CodeEdit"/>, which is
    /// also the one place the syntax highlighting is visible.
    /// </para>
    /// <para>
    /// Reading goes through <see cref="GodotContentLoader"/> like everything else in the addon,
    /// never <c>System.IO</c>. "Open externally" is there for the editing the view deliberately does
    /// not do: this is a viewer, and a half-finished editor for a whitespace-sensitive language
    /// would be a trap.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class GeSourceView : VBoxContainer
    {
        private readonly GeSyntaxHighlighter _highlighter = new GeSyntaxHighlighter();

        private Label? _path;
        private Button? _openExternally;
        private Button? _reload;
        private CodeEdit? _code;

        /// <summary>The file on show, as <c>res://content/cards.ge</c>, or null.</summary>
        public string? CurrentPath { get; private set; }

        /// <summary>Lines in the file on show.</summary>
        public int LineCount => _code == null ? 0 : _code.GetLineCount();

        public void Initialize()
        {
            Name = "Source";
            SizeFlagsVertical = SizeFlags.ExpandFill;

            var bar = new HBoxContainer();
            AddChild(bar);

            _path = new Label
            {
                Text = "No file open. Double-click a problem, a test or a definition.",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
            };
            bar.AddChild(_path);

            _reload = new Button { Text = "Reread", TooltipText = "Read the file from disk again." };
            _reload.Pressed += OnReread;
            bar.AddChild(_reload);

            _openExternally = new Button
            {
                Text = "Open externally",
                TooltipText = "Open the file in whatever the system uses for text files.",
            };
            _openExternally.Pressed += OnOpenExternally;
            bar.AddChild(_openExternally);

            _code = new CodeEdit
            {
                Editable = false,
                GuttersDrawLineNumbers = true,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SyntaxHighlighter = _highlighter,
                Text = string.Empty,
            };
            AddChild(_code);

            UpdateButtons();
        }

        /// <summary>
        /// Shows a file and puts the caret on <paramref name="line"/>, both 1-based as diagnostics
        /// and spans report them. Line 0 means "just show the file".
        /// </summary>
        public void Open(string? resPath, int line = 0, int column = 0)
        {
            if (_code == null || _path == null) return;
            if (string.IsNullOrEmpty(resPath)) return;

            if (!string.Equals(resPath, CurrentPath, StringComparison.OrdinalIgnoreCase)) Read(resPath!);
            GoTo(line, column);
        }

        /// <summary>Rereads the file on show, for after an edit somewhere else.</summary>
        public void Reread()
        {
            if (CurrentPath == null) return;

            int line = _code == null ? 0 : _code.GetCaretLine() + 1;
            Read(CurrentPath);
            GoTo(line, 0);
        }

        private void Read(string resPath)
        {
            if (_code == null || _path == null) return;

            if (!GodotContentLoader.TryRead(resPath, out string text))
            {
                CurrentPath = null;
                _path.Text = resPath;
                _code.Text = $"# Could not read {resPath}.\n# It may have been deleted, or never imported.";
                _highlighter.SetSource(_code.Text);
                UpdateButtons();
                return;
            }

            CurrentPath = resPath;
            _path.Text = resPath;

            // The highlighter is told the text directly: it is asked for one line at a time, and
            // reading the whole CodeEdit back per line would allocate the file over and over.
            _highlighter.SetSource(text);
            _code.Text = text;
            UpdateButtons();
        }

        private void GoTo(int line, int column)
        {
            if (_code == null || line <= 0) return;

            int index = Math.Min(Math.Max(0, line - 1), Math.Max(0, _code.GetLineCount() - 1));
            _code.SetCaretLine(index);
            _code.SetCaretColumn(Math.Max(0, column - 1));
            _code.SetLineAsCenterVisible(index);
        }

        private void OnReread() => Reread();

        private void OnOpenExternally()
        {
            if (CurrentPath == null) return;

            // GlobalizePath is what turns res:// into something the operating system can open; in an
            // exported game it would answer nothing useful, which is why this is editor-only.
            string absolute = ProjectSettings.GlobalizePath(CurrentPath);
            Error opened = OS.ShellOpen(absolute);
            if (opened != Error.Ok) GD.PushWarning($"Gameplay Effects: could not open {absolute} ({opened}).");
        }

        private void UpdateButtons()
        {
            if (_openExternally != null) _openExternally.Disabled = CurrentPath == null;
            if (_reload != null) _reload.Disabled = CurrentPath == null;
        }

        /// <summary>Opens a file and reports what came of it, for headless checks.</summary>
        public string SelfTest(string? resPath)
        {
            if (resPath == null) return "source: nothing to open";

            Open(resPath, 1, 1);
            int runs = _code == null ? 0 : _highlighter.Highlight(_code.Text);
            return $"source: opened {resPath}, {LineCount} line(s), {runs} highlighted run(s)";
        }
    }
}
#endif
