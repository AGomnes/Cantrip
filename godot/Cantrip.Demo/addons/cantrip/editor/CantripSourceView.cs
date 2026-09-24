#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Cantrip.Diagnostics;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The dock's editor: one <c>.cantrip</c> file, written and saved here, checked as it is typed,
    /// and the place every "jump to source" in the dock lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Godot has no API for opening a non-script text file at a line.
    /// <see cref="EditorInterface.EditScript(Script, int, int, bool)"/> needs a
    /// <see cref="Script"/>, and a <c>.cantrip</c> file is not one; <see cref="EditorInterface.SelectFile"/>
    /// only reveals a file in the FileSystem dock, which leaves the designer to open it and count
    /// lines themselves. So the addon carries its own <see cref="CodeEdit"/>, which is also the one
    /// place the syntax highlighting is visible.
    /// </para>
    /// <para>
    /// A whitespace-sensitive language in an editor that indents differently is a trap, so the
    /// indentation is not left to Godot's defaults. The buffer is asked what one step already is
    /// (<see cref="CantripIndent"/>), and that many spaces is what Tab, a new line and a pasted
    /// block all produce. Never a tab: the lexer counts one as four columns, so a tab in a file
    /// written with two spaces moves where a block ends without looking as though it has.
    /// </para>
    /// <para>
    /// Reading and writing go through <see cref="GodotContentLoader"/> and
    /// <see cref="FileAccess"/> like everything else in the addon, never <c>System.IO</c>, and a
    /// save tells the editor's file system so the importer picks the file up. "Open externally" is
    /// still there for anyone who would rather use their own editor; both routes now end in the
    /// same place, because an external change is noticed rather than silently overwritten.
    /// </para>
    /// <para>
    /// Nothing here opens a dialog. A file changed on disk under an unsaved buffer puts one line
    /// across the top with two buttons; switching files parks the buffer and asks nothing. A dialog
    /// per file would be a storm, and dropping the text would be worse than either.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripSourceView : VBoxContainer
    {
        /// <summary>How long the typing has to stop before the buffer is parsed and linted.</summary>
        private const double IdleSeconds = 0.35;

        /// <summary>The gutter holding one character per line that has a problem on it.</summary>
        private const int ProblemGutter = 0;

        private const string ErrorMark = "!";
        private const string WarningMark = "*";
        private const string NoteMark = ".";

        private static readonly Color ErrorColour = new Color(1.0f, 0.47f, 0.42f);
        private static readonly Color WarningColour = new Color(1.0f, 0.84f, 0.4f);
        private static readonly Color NoteColour = new Color(0.65f, 0.75f, 0.9f);
        private static readonly Color QuietColour = new Color(0.65f, 0.67f, 0.72f);

        private readonly CantripSyntaxHighlighter _highlighter = new CantripSyntaxHighlighter();
        private readonly Dictionary<string, int> _carets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Diagnostic> _here = new List<Diagnostic>();

        /// <summary>
        /// Files whose change on disk has been reported but not answered. Kept per file rather than
        /// as one flag: switching away from a file and back must bring its warning back with it, or
        /// the next save would go over a teammate's work with nothing on screen having said so.
        /// </summary>
        private readonly HashSet<string> _conflicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CantripWorkspace? _workspace;
        private Func<string>? _runTests;

        private Label? _path;
        private Button? _save;
        private Button? _revert;
        private Button? _test;
        private Button? _openExternally;

        private HBoxContainer? _conflict;
        private Label? _conflictText;

        private CantripCodeEdit? _code;
        private Label? _problem;
        private Button? _fix;
        private Timer? _idle;

        private Diagnostic? _atCaret;
        private EditorFileSystem? _files;
        private int _width = CantripIndent.DefaultWidth;
        private bool _reading;
        private bool _repairing;
        private bool _spaced = true;
        private bool _rediscover;
        private bool? _markedDirty;

        /// <summary>The file on show, as <c>res://content/cards.cantrip</c>, or null.</summary>
        public string? CurrentPath { get; private set; }

        /// <summary>Lines in the buffer on show.</summary>
        public int LineCount => _code == null ? 0 : _code.GetLineCount();

        /// <summary>One step of indentation in the buffer on show, in spaces.</summary>
        public int IndentWidth => _width;

        /// <summary>True when the buffer on show differs from the file it came from.</summary>
        public bool IsDirty => _workspace != null && _workspace.Drafts.IsDirty(CurrentPath);

        /// <summary>Raised when the tab's own title should change, because something is unsaved.</summary>
        public event Action? TitleChanged;

        /// <summary>
        /// The tab's own title, counting the buffers waiting to be saved. The dock is often the
        /// only part of the editor on screen, and a file switched away from still has to say that
        /// it is waiting.
        /// </summary>
        public string Title
        {
            get
            {
                int unsaved = _workspace?.Drafts.UnsavedCount ?? 0;
                return unsaved == 0 ? "Source" : $"Source ({unsaved})";
            }
        }

        public void Initialize(CantripWorkspace workspace, Func<string>? runTests)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _runTests = runTests;

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

            _save = new Button { Text = "Save", TooltipText = "Write this buffer to the file (Ctrl+S)." };
            _save.Pressed += () => Save();
            bar.AddChild(_save);

            _revert = new Button
            {
                Text = "Revert",
                TooltipText = "Throw away the unsaved changes in this buffer and read the file again.",
            };
            _revert.Pressed += OnRevert;
            bar.AddChild(_revert);

            _test = new Button
            {
                Text = "Run tests",
                TooltipText =
                    "Run the content's test blocks against what is in the buffers, saved or not. " +
                    "They run without the names and verbs your game registers in C#, as `cantrip test` does.",
            };
            _test.Pressed += OnRunTests;
            bar.AddChild(_test);

            _openExternally = new Button
            {
                Text = "Open externally",
                TooltipText = "Open the file in whatever the system uses for text files.",
            };
            _openExternally.Pressed += OnOpenExternally;
            bar.AddChild(_openExternally);

            _conflict = new HBoxContainer { Visible = false };
            AddChild(_conflict);

            _conflictText = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            _conflictText.AddThemeColorOverride("font_color", WarningColour);
            _conflict.AddChild(_conflictText);

            var keepMine = new Button { Text = "Keep mine", TooltipText = "Leave the buffer as it is. Saving will overwrite the file." };
            keepMine.Pressed += OnKeepMine;
            _conflict.AddChild(keepMine);

            var takeTheirs = new Button { Text = "Take theirs", TooltipText = "Throw away this buffer and read the file as it now is." };
            takeTheirs.Pressed += OnRevert;
            _conflict.AddChild(takeTheirs);

            _code = new CantripCodeEdit
            {
                Editable = false,
                GuttersDrawLineNumbers = true,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SyntaxHighlighter = _highlighter,
                Text = string.Empty,
                DrawTabs = true,
                IndentAutomatic = true,
                IndentUseSpaces = true,
                IndentSize = _width,
                // Godot's default list is `:`, `{`, `[` and `(`; only the first opens a block here.
                IndentAutomaticPrefixes = new Godot.Collections.Array<string> { ":" },
            };
            _code.AddGutter(ProblemGutter);
            _code.SetGutterName(ProblemGutter, "cantrip_problems");
            _code.SetGutterType(ProblemGutter, TextEdit.GutterType.String);
            _code.SetGutterWidth(ProblemGutter, 16);
            _code.SetGutterDraw(ProblemGutter, true);
            _code.TextChanged += OnTextChanged;
            _code.CaretChanged += OnCaretChanged;
            AddChild(_code);

            var strip = new HBoxContainer();
            AddChild(strip);

            _problem = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            strip.AddChild(_problem);

            _fix = new Button
            {
                Text = "Apply fix",
                Disabled = true,
                TooltipText = "Put the suggested word in place of the one on this line.",
            };
            _fix.Pressed += OnApplyFix;
            strip.AddChild(_fix);

            _idle = new Timer { OneShot = true, WaitTime = IdleSeconds };
            _idle.Timeout += Recheck;
            AddChild(_idle);

            // Godot switches this on for a node whose script overrides _ShortcutInput, but only
            // once it is ready; saying so here means Ctrl+S works from the first keystroke.
            SetProcessShortcutInput(true);

            _workspace.Changed += ShowProblems;
            _workspace.Drafts.Changed += UpdateChrome;

            // The editor tells us when anything on disk changed, which is the only way to notice a
            // file edited elsewhere under a buffer that is still open here.
            _files = EditorInterface.Singleton?.GetResourceFilesystem();
            if (_files != null) _files.FilesystemChanged += OnFilesystemChanged;

            UpdateChrome();
        }

        public override void _ExitTree()
        {
            if (_workspace != null)
            {
                _workspace.Changed -= ShowProblems;
                _workspace.Drafts.Changed -= UpdateChrome;
            }
            if (_files != null)
            {
                _files.FilesystemChanged -= OnFilesystemChanged;
                _files = null;
            }
        }

        /// <summary>
        /// Ctrl+S (Cmd+S on a Mac) saves, as it does everywhere else in the editor, but only while
        /// the caret is in this buffer: the same keystroke over the scene tree still saves the
        /// scene. Accepting the event is what keeps the editor from doing both.
        /// </summary>
        public override void _ShortcutInput(InputEvent @event)
        {
            if (_code == null || !_code.HasFocus()) return;
            if (!(@event is InputEventKey key) || !key.Pressed || key.Echo) return;
            if (key.Keycode != Key.S || !(key.CtrlPressed || key.MetaPressed) || key.ShiftPressed || key.AltPressed) return;

            Save();
            AcceptEvent();
        }

        /// <summary>
        /// Shows a file and puts the caret on <paramref name="line"/>, both 1-based as diagnostics
        /// and spans report them. Line 0 means "just show the file".
        /// </summary>
        /// <remarks>
        /// Switching away from a buffer with changes pending asks nothing and loses nothing: the
        /// text is parked in <see cref="CantripWorkspace.Drafts"/> with its caret, and coming back
        /// puts both where they were. The dock keeps marking itself until every buffer is saved.
        /// </remarks>
        public void Open(string? resPath, int line = 0, int column = 0)
        {
            if (_code == null || _path == null) return;
            if (string.IsNullOrEmpty(resPath)) return;

            if (!string.Equals(resPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                Park();
                Read(resPath!);
            }
            GoTo(line, column);
        }

        /// <summary>
        /// Throws away whatever is unsaved in this buffer and reads the file again, which is what
        /// Revert and Take theirs both do. The caret stays on the line it was on.
        /// </summary>
        public void Reread()
        {
            if (CurrentPath == null) return;

            int line = _code == null ? 0 : _code.GetCaretLine() + 1;
            _conflicted.Remove(CurrentPath);   // the buffer is about to become the file
            _workspace?.Drafts.Closed(CurrentPath);
            Read(CurrentPath);
            GoTo(line, 0);
        }

        /// <summary>
        /// Writes the buffer to its file and tells the editor, so the importer reads it and a
        /// running game's next <c>ReloadContent</c> sees it. Returns false, having said why, when
        /// there is nothing to save or the file could not be written.
        /// </summary>
        public bool Save()
        {
            if (_code == null || _workspace == null || CurrentPath == null) return false;
            if (!_workspace.Drafts.IsDirty(CurrentPath))
            {
                Say("Nothing to save.", QuietColour);
                return false;
            }

            string text = _code.Text;
            string path = CurrentPath;

            using (FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
            {
                if (file == null)
                {
                    Error error = FileAccess.GetOpenError();
                    GD.PushError($"Cantrip: could not write {path} ({error}).");
                    Say($"Could not write {path}: {error}.", ErrorColour);
                    return false;
                }
                file.StoreString(text);
            }

            _workspace.Drafts.Saved(path, text);
            _conflicted.Remove(path);
            _code.TagSavedVersion();
            Reimport(path);

            // Discovery again, not just a recheck: saving is when a file new to the project, or one
            // deleted while the dock was open, should join or leave the content. The text just
            // written is handed over rather than read back, because the importer may be a moment
            // behind and an imported copy is what a read would prefer.
            _workspace.ReloadAfterSaving(path, text);
            Say($"Saved {path}.", QuietColour);
            UpdateChrome();
            return true;
        }

        /// <summary>
        /// Parses and lints now rather than waiting for the typing to stop. The Run tests button
        /// and the headless self-test both need the workspace to hold what is on screen before they
        /// ask it anything.
        /// </summary>
        public void Recheck()
        {
            _idle?.Stop();
            if (_workspace == null) return;

            // Discovery is skipped while someone types, but not when the editor has said the
            // project's files moved: then the file list itself is what changed.
            if (_rediscover)
            {
                _rediscover = false;
                _workspace.Reload();   // raises Changed, which is what redraws the marks
                return;
            }

            _workspace.Recheck();
        }

        // Reading and writing -----------------------------------------------------------------------

        private void Read(string resPath)
        {
            if (_code == null || _path == null || _workspace == null) return;

            HideConflict();

            // An unsaved buffer wins over the file: it is the designer's work, and it is what every
            // panel has been checking since they typed it.
            string? text = _workspace.Drafts.TextFor(resPath);
            if (text == null)
            {
                if (!GodotContentLoader.TryRead(resPath, out string fromDisk))
                {
                    // Nothing is known about this file any more, so nothing may be said about it:
                    // the marks and the message from whatever was here before go too.
                    CurrentPath = null;
                    _path.Text = resPath;
                    Fill($"# Could not read {resPath}.\n# It may have been deleted, or never imported.");
                    _code.Editable = false;
                    ShowProblems();
                    Say($"Could not read {resPath}. It may have been deleted, or never imported.", ErrorColour);
                    return;
                }

                _workspace.Drafts.Opened(resPath, fromDisk);
                text = fromDisk;
            }

            CurrentPath = resPath;
            Fill(text);
            _code.Editable = true;

            if (_carets.TryGetValue(resPath, out int line)) GoTo(line + 1, 0);
            UpdateChrome();
            ShowProblems();

            // A file that changed underneath an unsaved buffer says so again on the way back in.
            if (_conflicted.Contains(resPath)) ShowConflict(Conflict(resPath));
        }

        /// <summary>
        /// Puts text into the buffer without it counting as typing, and sets the indentation from
        /// what the text already uses. This is the one place the width is decided.
        /// </summary>
        private void Fill(string text)
        {
            if (_code == null) return;

            _width = CantripIndent.Detect(text);
            _code.IndentSize = _width;
            _code.IndentUseSpaces = true;

            // A file already written with tabs is left exactly as it is; one written with spaces is
            // kept that way, whatever route text arrives by. See <see cref="Repair"/>.
            _spaced = !CantripIndent.HasIndentingTab(text);

            _reading = true;
            try
            {
                // The highlighter is told the text directly: it is asked for one line at a time, and
                // reading the whole CodeEdit back per line would allocate the file over and over.
                _highlighter.SetSource(text);
                _code.Text = text;
                _code.TagSavedVersion();
            }
            finally
            {
                _reading = false;
            }
        }

        /// <summary>Remembers where the caret was, so coming back to a file lands where it left.</summary>
        private void Park()
        {
            if (_code == null || CurrentPath == null) return;
            _carets[CurrentPath] = _code.GetCaretLine();
        }

        private void GoTo(int line, int column)
        {
            // Nothing is open when the file could not be read, and moving a caret about in the
            // message that says so would only take the message off the strip again.
            if (_code == null || line <= 0 || CurrentPath == null) return;

            int index = Math.Min(Math.Max(0, line - 1), Math.Max(0, _code.GetLineCount() - 1));
            _code.SetCaretLine(index);
            _code.SetCaretColumn(Math.Max(0, column - 1));
            _code.SetLineAsCenterVisible(index);
            OnCaretChanged();
        }

        private static void Reimport(string path)
        {
            EditorFileSystem? files = EditorInterface.Singleton?.GetResourceFilesystem();
            if (files == null) return;

            files.UpdateFile(path);

            // Reimporting a file the editor has never imported is an error, not a no-op, and a file
            // saved for the first time has no sidecar yet. UpdateFile has already scheduled it.
            if (FileAccess.FileExists(ContentPaths.ImportMarkerOf(path))) files.ReimportFiles(new[] { path });
        }

        // What happens while someone types ----------------------------------------------------------

        private void OnTextChanged()
        {
            if (_reading || _repairing || _code == null || _workspace == null || CurrentPath == null) return;

            // One read: a buffer is a fresh string every time it is asked for, and on a long file
            // that is the most expensive thing a keystroke does here.
            string text = _code.Text;
            if (Repair(text)) text = _code.Text;

            _workspace.Drafts.Edited(CurrentPath, text);
            _highlighter.SetSource(text);
            UpdateChrome();
            _idle?.Start(IdleSeconds);
        }

        /// <summary>
        /// Widens any tab that has arrived in a line's indentation into the columns it already
        /// stood for, and answers whether it had to. Typing and pasting are both headed off before
        /// they get this far; this is for the routes that cannot be, above all a block of text
        /// dropped on the editor from another window, which <c>TextEdit</c> handles itself.
        /// </summary>
        /// <remarks>
        /// Only a buffer that was read without an indenting tab in it is kept that way. A file
        /// someone wrote with tabs is left alone: its tabs mean what they mean, and rewriting a
        /// file's indentation because a character was typed into it would be a far worse surprise
        /// than the one this prevents. The whole thing is one undo step, and costs an
        /// <see cref="string.IndexOf(char)"/> on the buffer when there is no tab to find, which is
        /// almost always. The lines are only walked when a tab is indenting one of them: a tab
        /// inside a string is part of the text and is nobody's business here.
        /// </remarks>
        private bool Repair(string text)
        {
            if (_code == null || !_spaced || !CantripIndent.HasIndentingTab(text)) return false;

            int caretLine = _code.GetCaretLine();
            int caretColumn = _code.GetCaretColumn();
            int lines = _code.GetLineCount();
            bool any = false;

            _repairing = true;
            try
            {
                for (int i = 0; i < lines; i++)
                {
                    string line = _code.GetLine(i);
                    if (line.IndexOf('\t', 0, CantripIndent.LeadingCharacters(line)) < 0) continue;

                    string widened = CantripIndent.WithoutTabs(line);
                    if (!any)
                    {
                        _code.BeginComplexOperation();
                        any = true;
                    }
                    _code.SetLine(i, widened);
                    if (i == caretLine) caretColumn += widened.Length - line.Length;
                }

                if (any)
                {
                    _code.EndComplexOperation();
                    _code.SetCaretLine(caretLine);
                    _code.SetCaretColumn(Math.Max(0, caretColumn));
                }
            }
            finally
            {
                _repairing = false;
            }

            if (any) Say("Tabs in the indentation became the spaces they already stood for.", QuietColour);
            return any;
        }

        private void OnCaretChanged()
        {
            if (_code == null) return;

            int line = _code.GetCaretLine() + 1;
            _atCaret = null;
            foreach (Diagnostic diagnostic in _here)
            {
                if (diagnostic.Span.Line != line) continue;
                _atCaret = diagnostic;
                break;
            }

            if (_atCaret != null)
            {
                Say($"{_atCaret.Span.Line}:{_atCaret.Span.Column}  {_atCaret.Code}  {Message(_atCaret)}", ColourOf(_atCaret.Severity));
            }
            else if (_here.Count > 0)
            {
                Say($"{_here.Count} problem(s) in this file.", QuietColour);
            }
            else if (CurrentPath != null && !IsContent(CurrentPath))
            {
                // "No problems" would be a lie about a file nothing looks at.
                Say($"{CurrentPath} is outside the content the dock loads, so nothing checks it.", WarningColour);
            }
            else
            {
                Say("No problems in this file.", QuietColour);
            }

            if (_fix != null) _fix.Disabled = !CanFix(_atCaret);
        }

        /// <summary>
        /// Puts what the last check said about this file into the gutter, the line colours and the
        /// strip. The workspace has already done the thinking; this only draws it.
        /// </summary>
        private void ShowProblems()
        {
            if (_code == null) return;

            _here.Clear();
            if (_workspace != null && CurrentPath != null)
            {
                foreach (Diagnostic diagnostic in _workspace.ProblemsIn(CurrentPath)) _here.Add(diagnostic);
            }

            int lines = _code.GetLineCount();
            for (int i = 0; i < lines; i++)
            {
                _code.SetLineGutterText(i, ProblemGutter, string.Empty);
                _code.SetLineBackgroundColor(i, new Color(0, 0, 0, 0));
            }

            foreach (Diagnostic diagnostic in _here)
            {
                int line = diagnostic.Span.Line - 1;
                if (line < 0 || line >= lines) continue;

                // A line with two problems on it gets the mark of the worse one.
                Color colour = ColourOf(diagnostic.Severity);
                string mark = MarkOf(diagnostic.Severity);
                if (Rank(_code.GetLineGutterText(line, ProblemGutter)) >= Rank(mark)) continue;

                _code.SetLineGutterText(line, ProblemGutter, mark);
                _code.SetLineGutterItemColor(line, ProblemGutter, colour);
                _code.SetLineBackgroundColor(line, new Color(colour.R, colour.G, colour.B, 0.12f));
            }

            OnCaretChanged();
            UpdateChrome();
        }

        private void OnFilesystemChanged()
        {
            if (_workspace == null) return;

            // This is the moment the project's files can have moved without the dock writing them,
            // so the next check discovers afresh and reads afresh: a file added elsewhere joins the
            // content, and one deleted elsewhere leaves it instead of being reported as unreadable.
            // The check is put on the same idle timer as typing, because this fires for every change
            // to the project, most of which have nothing to do with content.
            _rediscover = true;
            if (_workspace.IsLoaded) _idle?.Start(IdleSeconds);

            if (CurrentPath == null || _code == null) return;

            if (!GodotContentLoader.TryRead(CurrentPath, out string onDisk))
            {
                // The file is gone. The buffer is not: it is the only copy of this text left, and
                // saving writes the file again.
                Say(
                    IsDirty
                        ? $"{CurrentPath} was deleted. Your unsaved text is still here; Save writes the file again."
                        : $"{CurrentPath} was deleted.",
                    WarningColour);
                return;
            }

            if (_workspace.Drafts.SameAsDisk(CurrentPath, onDisk)) return;

            if (!_workspace.Drafts.IsDirty(CurrentPath))
            {
                // Nothing of the designer's to lose, so take the file: this is what happens when a
                // teammate's change arrives, or when they saved from their own editor.
                int line = _code.GetCaretLine() + 1;
                _workspace.Drafts.Opened(CurrentPath, onDisk);
                Fill(onDisk);
                GoTo(line, 0);
                Say($"{CurrentPath} changed on disk and was reread.", QuietColour);
                return;
            }

            // Their text is recorded, so the same change cannot raise the bar twice; the buffer is
            // left exactly as it is until one of the two buttons is pressed.
            _workspace.Drafts.DiskChanged(CurrentPath, onDisk);
            _conflicted.Add(CurrentPath);
            ShowConflict(Conflict(CurrentPath));
        }

        private static string Conflict(string path) =>
            $"{path} changed on disk while you have unsaved changes here.";

        private void ShowConflict(string message)
        {
            if (_conflict == null || _conflictText == null) return;

            _conflictText.Text = message;
            _conflict.Visible = true;
        }

        private void HideConflict()
        {
            if (_conflict != null) _conflict.Visible = false;
        }

        // Buttons -----------------------------------------------------------------------------------

        /// <summary>
        /// Keep mine only puts the line away. The buffer is untouched and a later save goes over the
        /// file, which is what the button says it does.
        /// </summary>
        private void OnKeepMine()
        {
            if (CurrentPath != null) _conflicted.Remove(CurrentPath);
            HideConflict();
        }

        private void OnRevert()
        {
            HideConflict();
            Reread();
            Recheck();
        }

        private void OnRunTests()
        {
            if (_runTests == null)
            {
                Say("No test runner is attached.", QuietColour);
                return;
            }

            Recheck();
            Say(_runTests(), QuietColour);
        }

        private void OnOpenExternally()
        {
            if (CurrentPath == null) return;

            // GlobalizePath is what turns res:// into something the operating system can open; in an
            // exported game it would answer nothing useful, which is why this is editor-only.
            string absolute = ProjectSettings.GlobalizePath(CurrentPath);
            Error opened = OS.ShellOpen(absolute);
            if (opened != Error.Ok) GD.PushWarning($"Cantrip: could not open {absolute} ({opened}).");
        }

        private void OnApplyFix() => ApplyFix(_atCaret);

        /// <summary>
        /// Puts a diagnostic's suggestion in place of the text its span covers. The core gives a
        /// code, a span, a message and often the word it meant; this is the last of the four being
        /// worth something rather than being read out.
        /// </summary>
        private bool ApplyFix(Diagnostic? diagnostic)
        {
            if (_code == null || !CanFix(diagnostic)) return false;

            int line = diagnostic!.Span.Line - 1;
            int start = diagnostic.Span.Column - 1;
            string text = _code.GetLine(line);

            _code.BeginComplexOperation();
            _code.SetLine(line, text.Substring(0, start) + diagnostic.Suggestion + text.Substring(start + diagnostic.Span.Length));
            _code.EndComplexOperation();

            _code.SetCaretLine(line);
            _code.SetCaretColumn(start + diagnostic.Suggestion!.Length);
            OnTextChanged();
            return true;
        }

        /// <summary>
        /// True when the file on show is one the dock loads. A project that points
        /// <c>cantrip/content/folder</c> at one folder can still open a <c>.cantrip</c> file outside
        /// it from the FileSystem dock, and that file is never parsed or linted with the rest.
        /// </summary>
        private bool IsContent(string path)
        {
            if (_workspace == null) return false;

            foreach (string file in _workspace.Files)
            {
                if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private bool CanFix(Diagnostic? diagnostic)
        {
            if (_code == null || diagnostic == null || string.IsNullOrEmpty(diagnostic.Suggestion)) return false;
            if (diagnostic.Span.Length <= 0 || diagnostic.Span.Column <= 0) return false;

            int line = diagnostic.Span.Line - 1;
            if (line < 0 || line >= _code.GetLineCount()) return false;

            int start = diagnostic.Span.Column - 1;
            return start + diagnostic.Span.Length <= _code.GetLine(line).Length;
        }

        // Chrome ------------------------------------------------------------------------------------

        private void UpdateChrome()
        {
            bool open = CurrentPath != null;
            bool dirty = IsDirty;

            if (_path != null && open)
            {
                int elsewhere = (_workspace?.Drafts.UnsavedCount ?? 0) - (dirty ? 1 : 0);
                _path.Text = (dirty ? "* " : string.Empty) + CurrentPath +
                             (elsewhere > 0 ? $"   ({elsewhere} other file(s) unsaved)" : string.Empty);

                // An override is a theme change, and a theme change redraws the control. This runs
                // on every keystroke, and the colour only has two states.
                if (dirty != _markedDirty)
                {
                    _markedDirty = dirty;
                    _path.AddThemeColorOverride("font_color", dirty ? WarningColour : QuietColour);
                }
            }

            if (_save != null) _save.Disabled = !dirty;
            if (_revert != null) _revert.Disabled = !dirty;
            if (_openExternally != null) _openExternally.Disabled = !open;
            if (_test != null) _test.Disabled = _runTests == null;
            if (!dirty) HideConflict();

            TitleChanged?.Invoke();
        }

        private void Say(string message, Color colour)
        {
            if (_problem == null) return;

            _problem.Text = message;
            _problem.AddThemeColorOverride("font_color", colour);
        }

        private static string Message(Diagnostic diagnostic) =>
            string.IsNullOrEmpty(diagnostic.Suggestion)
                ? diagnostic.Message
                : $"{diagnostic.Message} Did you mean `{diagnostic.Suggestion}`?";

        private static Color ColourOf(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => ErrorColour,
            DiagnosticSeverity.Warning => WarningColour,
            _ => NoteColour,
        };

        private static string MarkOf(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => ErrorMark,
            DiagnosticSeverity.Warning => WarningMark,
            _ => NoteMark,
        };

        private static int Rank(string mark) => mark switch
        {
            ErrorMark => 3,
            WarningMark => 2,
            NoteMark => 1,
            _ => 0,
        };

        // Headless checks ---------------------------------------------------------------------------

        /// <summary>
        /// Opens a file and reports what came of it, then exercises the editing the dock cannot be
        /// clicked through headlessly: the indentation, a buffer typed into and saved, a buffer
        /// parked while another file is open, a problem found in unsaved text, and a suggested fix
        /// applied. A check of its own that fails says FAILED, which fails the run.
        /// </summary>
        public IReadOnlyList<string> SelfTest(string? resPath)
        {
            var report = new List<string>();
            if (resPath == null)
            {
                report.Add("source: nothing to open");
                return report;
            }

            Open(resPath, 1, 1);
            int runs = _code == null ? 0 : _highlighter.Highlight(_code.Text);
            report.Add($"source: opened {resPath}, {LineCount} line(s), {runs} highlighted run(s)");
            report.Add(IndentSelfTest());
            report.Add(EditingSelfTest());
            report.Add(FilesSelfTest());
            report.Add(CostSelfTest());
            return report;
        }

        /// <summary>
        /// A file the checks below may write and take away again. It goes in the folder this
        /// project's content is loaded from, because that is a folder that certainly exists and
        /// certainly is loaded: a check that wrote somewhere else would fail in every project that
        /// sets <c>cantrip/content/folder</c>, which is most of them. A name already taken is left
        /// alone rather than written over, and <c>.local.</c> keeps it out of version control.
        /// </summary>
        private string? Scratch(string name)
        {
            if (_workspace == null) return null;

            string folder = _workspace.ContentFolder;
            if (!DirAccess.DirExistsAbsolute(folder)) return null;

            string path = ContentPaths.Combine(folder, name);
            return FileAccess.FileExists(path) ? null : path;
        }

        private static bool WriteFile(string path, string text)
        {
            using (FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
            {
                if (file == null) return false;
                file.StoreString(text);
            }
            Reimport(path);
            return true;
        }

        private void Remove(string path)
        {
            _workspace?.Drafts.Closed(path);
            _conflicted.Remove(path);
            if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)) CurrentPath = null;
            DirAccess.RemoveAbsolute(path);
            DirAccess.RemoveAbsolute(ContentPaths.ImportMarkerOf(path));
            EditorInterface.Singleton?.GetResourceFilesystem()?.UpdateFile(path);
        }

        /// <summary>
        /// What one check after a pause in typing costs on this project: every file read, loaded and
        /// linted. It fails nothing, because the number is about the machine as much as the code;
        /// it is printed so that a project whose checks have become slow says so in CI.
        /// </summary>
        private string CostSelfTest()
        {
            if (_workspace == null) return "check: no workspace";

            _workspace.ForgetCachedText();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _workspace.Recheck();
            double cold = clock.Elapsed.TotalMilliseconds;

            const int runs = 5;
            clock.Restart();
            for (int i = 0; i < runs; i++) _workspace.Recheck();
            double warm = clock.Elapsed.TotalMilliseconds / runs;

            return $"check: {_workspace.Files.Count} file(s), {Milliseconds(cold)} ms reading them all, " +
                   $"{Milliseconds(warm)} ms while typing";
        }

        private static string Milliseconds(double value) =>
            value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Indenting is the one thing a <c>.cantrip</c> editor can get wrong without looking wrong,
        /// so this asks Godot to indent for real and measures what it inserted.
        /// </summary>
        private string IndentSelfTest()
        {
            if (_code == null) return Failed("indent", "no editor");

            string text = "card \"Indent\"\n  cost 1\n  effect:\n    block 1\n";
            Fill(text);

            var wrong = new List<string>();
            if (_width != 2) wrong.Add($"read the buffer's step as {_width}, not 2");
            if (!_code.IndentUseSpaces) wrong.Add("would indent with tabs");
            if (_code.IndentSize != _width) wrong.Add($"is set to indent by {_code.IndentSize}");
            if (!_code.IndentAutomatic) wrong.Add("would not carry the indent on to the next line");

            // What Godot actually inserts, which is the only answer that counts.
            _code.SetCaretLine(1);
            _code.SetCaretColumn(0);
            _code.DoIndent();
            string indented = _code.GetLine(1);
            if (indented != "  " + "  cost 1") wrong.Add($"indenting a line gave `{indented}`");
            if (CantripIndent.HasTab(_code.Text)) wrong.Add("put a tab in the buffer");

            string continued = CantripIndent.Continue("  effect:", _width);
            if (continued != "    ") wrong.Add($"a new line after `effect:` would start with {continued.Length} space(s)");

            // Four spaces where the file uses four, and still never a tab.
            Fill("card \"Wide\"\n    cost 1\n");
            if (_width != 4 || _code.IndentSize != 4) wrong.Add($"read a four-space file's step as {_width}");
            if (CantripIndent.Unit(_width).IndexOf('\t') >= 0) wrong.Add("a step is not spaces");

            // Put the view back the way the check found it.
            if (CurrentPath != null) Reread();

            return wrong.Count == 0
                ? "indent: spaces only, matching the buffer (2 and 4 both read back)"
                : Failed("indent", string.Join("; ", wrong));
        }

        /// <summary>
        /// Types into a scratch file, saves it, parks it, checks it while unsaved and fixes a
        /// problem in it. The file is named so that the repository ignores it, and it is taken away
        /// again whatever happens.
        /// </summary>
        private string EditingSelfTest()
        {
            if (_code == null || _workspace == null) return Failed("editing", "no editor");

            string? scratch = Scratch("selftest.local.cantrip");
            if (scratch == null) return $"editing: skipped, nowhere to write under {_workspace.ContentFolder}";

            const string typed = "card \"Selftest\"\n  cost 1\n  target enemy\n  effect:\n    deal 5 to targt\n";
            string? previous = CurrentPath;
            var wrong = new List<string>();

            try
            {
                if (!WriteFile(scratch, "# a scratch file the self-test writes and takes away again\n"))
                    return Failed("editing", $"could not make {scratch} ({FileAccess.GetOpenError()})");
                _workspace.Reload();

                Open(scratch);
                if (CurrentPath != scratch) wrong.Add("could not open the scratch file");
                if (!_code.Editable) wrong.Add("the buffer is not editable");

                // Typing.
                _code.Text = typed;
                OnTextChanged();
                if (!IsDirty) wrong.Add("typing did not mark the buffer unsaved");
                if (Title == "Source") wrong.Add("the tab is not marked while a buffer is unsaved");

                // Checked as typed, against the folder as saved with this buffer in place of its file.
                Recheck();
                Diagnostic? unknown = Find("CT302", scratch);
                if (unknown == null) wrong.Add("the unsaved buffer was not linted");
                else if (unknown.Suggestion != "target") wrong.Add($"CT302 suggested `{unknown.Suggestion}`");
                if (_workspace.Content.Find("Selftest", "card") == null) wrong.Add("the unsaved buffer's card is not in the content");
                if (_code.GetLineGutterText(4, ProblemGutter).Length == 0) wrong.Add("the problem's line is not marked in the gutter");

                // Parking: another file, and back again, with the work still there.
                if (previous != null)
                {
                    Open(previous);
                    if (!_workspace.Drafts.IsDirty(scratch)) wrong.Add("switching files threw the draft away");
                    Open(scratch);
                    if (_code.Text != typed) wrong.Add("coming back to the file did not bring the draft back");
                }

                // The suggestion, used rather than read out.
                _code.SetCaretLine(4);
                OnCaretChanged();
                if (!ApplyFix(_atCaret)) wrong.Add("the suggested fix could not be applied");
                else if (_code.GetLine(4) != "    deal 5 to target") wrong.Add($"the fix gave `{_code.GetLine(4)}`");

                // Saving.
                string wanted = _code.Text;
                if (!Save()) wrong.Add("the buffer would not save");
                if (IsDirty) wrong.Add("the buffer is still marked unsaved after saving");
                if (!GodotContentLoader.TryRead(scratch, out string written) || written != wanted)
                    wrong.Add("the file on disk is not what was in the buffer");
                if (_workspace.Content.Find("Selftest", "card") == null) wrong.Add("the saved card is not in the content");
                if (Find("CT302", scratch) != null) wrong.Add("the fixed problem is still reported");

                wrong.AddRange(RunningTheTests(_workspace.Content.Tests.Count));
                wrong.AddRange(WindowsLineEndings(scratch));
                wrong.AddRange(PastedAndDroppedTabs());
            }
            finally
            {
                Remove(scratch);
                _workspace.Reload();
                if (previous != null) Open(previous, 1, 1);
            }

            return wrong.Count == 0
                ? "editing: typed, checked unsaved, parked, fixed and saved a file"
                : Failed("editing", string.Join("; ", wrong));
        }

        /// <summary>
        /// The Run tests button, which runs nothing itself: it brings the buffers up to date and
        /// presses the Tests tab's own runner. What is checked here is that the runner really did
        /// see an unsaved buffer, by putting a test block in one and counting the results. The block
        /// uses nothing but the card in the same buffer, so it says the same thing in any project.
        /// </summary>
        private IEnumerable<string> RunningTheTests(int saved)
        {
            if (_code == null || _workspace == null || _runTests == null) yield break;

            _code.Text += "\ntest \"Selftest from the buffer\"\n  enemy hp 20\n  play Selftest on enemy\n  expect enemy.hp == 15\n";
            OnTextChanged();

            // The button, not the callback behind it: bringing the buffers up to date before the
            // runner is pressed is half of what the button does, and the half worth checking.
            OnRunTests();
            string summary = _problem?.Text ?? string.Empty;
            if (_workspace.Content.Tests.Count != saved + 1)
                yield return $"a test block typed into the buffer left {_workspace.Content.Tests.Count} block(s) loaded, not {saved + 1}";
            if (Ran(summary) != saved + 1) yield return $"Run tests said `{summary}` of {saved + 1} block(s)";
        }

        /// <summary>How many results are behind a summary such as <c>12 passed, 1 failed</c>.</summary>
        private static int Ran(string summary)
        {
            int total = 0;
            foreach (string part in summary.Split(','))
            {
                string trimmed = part.Trim();
                int space = trimmed.IndexOf(' ');
                if (space > 0 && int.TryParse(trimmed.Substring(0, space), out int count)) total += count;
            }
            return total;
        }

        /// <summary>
        /// A file written on Windows, which is what every <c>.cantrip</c> file in a Windows checkout
        /// of this repository is. Godot's buffer drops the carriage returns, so unless the dock
        /// compares like with like, such a file looks unsaved from the moment it is opened and can
        /// never be put back.
        /// </summary>
        private IEnumerable<string> WindowsLineEndings(string scratch)
        {
            if (_code == null) yield break;

            WriteFile(scratch, "card \"Selftest\"\r\n  cost 1\r\n  target enemy\r\n");
            Reread();

            if (IsDirty) yield return "a file written with \\r\\n looked unsaved as soon as it was opened";

            string opened = _code.Text;
            _code.Text = opened + "  # typed\n";
            OnTextChanged();
            if (!IsDirty) yield return "typing into a file written with \\r\\n did not mark it unsaved";

            _code.Text = opened;
            OnTextChanged();
            if (IsDirty) yield return "typing into a file written with \\r\\n and undoing it left the file unsaved";
        }

        /// <summary>
        /// The two routes a tab can arrive by that are not typing: pasted, and dropped on the editor
        /// from another window. A paste is headed off before the text lands; a drop is handled by
        /// <c>TextEdit</c> itself, so that one has to be caught afterwards.
        /// </summary>
        private IEnumerable<string> PastedAndDroppedTabs()
        {
            if (_code == null) yield break;

            // Headless has no clipboard, and an editor with one belongs to whoever is using it.
            if (DisplayServer.HasFeature(DisplayServer.Feature.Clipboard))
            {
                string theirs = DisplayServer.ClipboardGet();
                try
                {
                    Fill("card \"Selftest\"\n  cost 1\n");
                    DisplayServer.ClipboardSet("\ttarget enemy\n");
                    _code.SetCaretLine(1);
                    _code.SetCaretColumn(0);
                    _code.Paste();
                }
                finally
                {
                    DisplayServer.ClipboardSet(theirs);
                }

                if (CantripIndent.HasIndentingTab(_code.Text)) yield return "a block pasted with tabs kept them";
            }

            Fill("card \"Selftest\"\n  cost 1\n");
            _code.SetCaretLine(1);
            _code.SetCaretColumn(0);
            _code.InsertTextAtCaret("\ttarget enemy\n");
            OnTextChanged();

            if (CantripIndent.HasIndentingTab(_code.Text)) yield return "a block dropped in with tabs kept them";
            if (_code.GetLine(1) != "    target enemy") yield return $"a dropped tab became `{_code.GetLine(1)}`";

            // A file that was already written with tabs is left exactly as its author wrote it.
            Fill("card \"Selftest\"\n\tcost 1\n");
            _code.SetCaretLine(1);
            _code.SetCaretColumn(_code.GetLine(1).Length);
            _code.InsertTextAtCaret("0");
            OnTextChanged();
            if (!CantripIndent.HasIndentingTab(_code.Text)) yield return "a file written with tabs had them taken away underneath it";
        }

        /// <summary>
        /// A file added and then deleted outside the dock. The editor says the project's files
        /// changed, and the next check has to discover as well as read: a list that only ever
        /// shrinks would report a deleted file as one it could not read.
        /// </summary>
        private string FilesSelfTest()
        {
            if (_workspace == null) return Failed("files", "no workspace");

            string? scratch = Scratch("selftest-discovery.local.cantrip");
            if (scratch == null) return $"files: skipped, nowhere to write under {_workspace.ContentFolder}";

            var wrong = new List<string>();
            int before = _workspace.Files.Count;

            try
            {
                WriteFile(scratch, "card \"Discovered\"\n  cost 1\n");
                OnFilesystemChanged();
                Recheck();
                if (_workspace.Content.Find("Discovered", "card") == null)
                    wrong.Add("a file added outside the dock did not join the content");

                Remove(scratch);
                OnFilesystemChanged();
                Recheck();
                if (_workspace.Files.Count != before) wrong.Add($"{_workspace.Files.Count} file(s) are loaded where {before} were before");
                if (Find(GodotContentLoader.UnreadableCode, scratch) != null)
                    wrong.Add("a file deleted outside the dock is still reported as one that could not be read");
            }
            finally
            {
                Remove(scratch);
                _workspace.Reload();
            }

            return wrong.Count == 0
                ? "files: a file added and deleted outside the dock joined and left the content"
                : Failed("files", string.Join("; ", wrong));
        }

        private Diagnostic? Find(string code, string file)
        {
            if (_workspace == null) return null;

            foreach (Diagnostic diagnostic in _workspace.ProblemsIn(file))
            {
                if (diagnostic.Code == code) return diagnostic;
            }
            return null;
        }

        private static string Failed(string what, string why)
        {
            string failure = $"{what}: {CantripPlugin.SelfTestFailed}, {why}";
            GD.PushError("Cantrip self-test: " + failure);
            return failure;
        }
    }
}
#endif
