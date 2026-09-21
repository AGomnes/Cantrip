#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Cantrip.Diagnostics;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// Every problem in the project's content, grouped by file: what the parser could not read and
    /// what the linter thinks will not work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The core does the thinking; this projects it. Both kinds of diagnostic carry a
    /// <c>SourceSpan</c>, so both are equally clickable, and they are shown in one list in source
    /// order rather than in two, because a designer wants "what is wrong with this file", not "what
    /// is wrong according to which pass".
    /// </para>
    /// <para>
    /// Suppression only offers itself for lint codes. A parse error means the file did not load, and
    /// hiding that would leave the content silently missing definitions.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripProblemsTab : VBoxContainer
    {
        private const int WhereColumn = 0;
        private const int CodeColumn = 1;
        private const int MessageColumn = 2;

        private static readonly Color ErrorColour = new Color(1.0f, 0.47f, 0.42f);
        private static readonly Color WarningColour = new Color(1.0f, 0.84f, 0.4f);
        private static readonly Color NoteColour = new Color(0.65f, 0.75f, 0.9f);
        private static readonly Color FileColour = new Color(0.78f, 0.82f, 0.88f);

        private CantripWorkspace? _workspace;
        private Action<string, int, int>? _navigate;

        private Tree? _tree;
        private Label? _summary;
        private Label? _suppressedLabel;
        private Button? _suppress;
        private Button? _clearSuppressions;
        private CheckBox? _showErrors;
        private CheckBox? _showWarnings;
        private CheckBox? _showNotes;

        public void Initialize(CantripWorkspace workspace, Action<string, int, int> navigate)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));

            Name = "Problems";
            SizeFlagsVertical = SizeFlags.ExpandFill;

            var filters = new HBoxContainer();
            AddChild(filters);

            _showErrors = Toggle(filters, "Errors", true);
            _showWarnings = Toggle(filters, "Warnings", true);
            _showNotes = Toggle(filters, "Notes", true);

            _summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            filters.AddChild(_summary);

            _suppress = new Button
            {
                Text = "Suppress code",
                Disabled = true,
                TooltipText = "Stop reporting this lint code. Remembered in your editor settings.",
            };
            _suppress.Pressed += OnSuppress;
            filters.AddChild(_suppress);

            var suppressions = new HBoxContainer();
            AddChild(suppressions);

            _suppressedLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            suppressions.AddChild(_suppressedLabel);

            _clearSuppressions = new Button { Text = "Clear suppressions", Disabled = true };
            _clearSuppressions.Pressed += OnClearSuppressions;
            suppressions.AddChild(_clearSuppressions);

            _tree = new Tree
            {
                Columns = 3,
                ColumnTitlesVisible = true,
                HideRoot = true,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SelectMode = Tree.SelectModeEnum.Row,
            };
            _tree.SetColumnTitle(WhereColumn, "Where");
            _tree.SetColumnTitle(CodeColumn, "Code");
            _tree.SetColumnTitle(MessageColumn, "Problem");
            _tree.SetColumnExpand(WhereColumn, false);
            _tree.SetColumnCustomMinimumWidth(WhereColumn, 220);
            _tree.SetColumnExpand(CodeColumn, false);
            _tree.SetColumnCustomMinimumWidth(CodeColumn, 70);
            _tree.ItemActivated += OnActivated;
            _tree.ItemSelected += OnSelected;
            AddChild(_tree);

            _workspace.Changed += Refresh;
            Refresh();
        }

        public override void _ExitTree()
        {
            // The workspace outlives this panel, so a handler left behind would keep a freed Control
            // alive and fire into it on the next reload.
            if (_workspace != null) _workspace.Changed -= Refresh;
        }

        /// <summary>Rebuilds the list from the workspace. Cheap enough to do on every reload.</summary>
        public void Refresh()
        {
            if (_tree == null || _workspace == null) return;

            _tree.Clear();
            TreeItem root = _tree.CreateItem();

            var byFile = new SortedDictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
            foreach (Diagnostic diagnostic in _workspace.Problems)
            {
                if (!Wanted(diagnostic.Severity)) continue;

                string file = string.IsNullOrEmpty(diagnostic.Span.File) ? "(no file)" : diagnostic.Span.File;
                if (!byFile.TryGetValue(file, out List<Diagnostic>? list))
                {
                    list = new List<Diagnostic>();
                    byFile[file] = list;
                }
                list.Add(diagnostic);
            }

            foreach (KeyValuePair<string, List<Diagnostic>> file in byFile)
            {
                TreeItem group = _tree.CreateItem(root);
                group.SetText(WhereColumn, Short(file.Key));
                group.SetCustomColor(WhereColumn, FileColour);
                group.SetText(MessageColumn, Count(file.Value.Count, "problem"));
                group.SetSelectable(CodeColumn, false);
                group.SetMetadata(WhereColumn, Location(file.Key, 1, 1, string.Empty));

                foreach (Diagnostic diagnostic in file.Value)
                {
                    TreeItem row = _tree.CreateItem(group);
                    row.SetText(WhereColumn, $"{diagnostic.Span.Line}:{diagnostic.Span.Column}");
                    row.SetText(CodeColumn, diagnostic.Code);
                    row.SetText(MessageColumn, Message(diagnostic));
                    row.SetTooltipText(MessageColumn, diagnostic.ToString());

                    Color colour = ColourOf(diagnostic.Severity);
                    row.SetCustomColor(WhereColumn, colour);
                    row.SetCustomColor(CodeColumn, colour);
                    row.SetMetadata(WhereColumn, Location(diagnostic.Span.File, diagnostic.Span.Line, diagnostic.Span.Column, diagnostic.Code));
                }
            }

            if (_summary != null)
            {
                _summary.Text = _workspace.IsLoaded
                    ? $"{_workspace.ErrorCount} error(s), {_workspace.WarningCount} warning(s), {_workspace.NoteCount} note(s) in {Count(_workspace.Files.Count, "file")}"
                    : "Not loaded yet.";
            }

            UpdateSuppressions();
            OnSelected();
        }

        private bool Wanted(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => _showErrors?.ButtonPressed ?? true,
            DiagnosticSeverity.Warning => _showWarnings?.ButtonPressed ?? true,
            _ => _showNotes?.ButtonPressed ?? true,
        };

        private void UpdateSuppressions()
        {
            if (_workspace == null) return;

            bool any = _workspace.Suppressed.Count > 0;
            if (_suppressedLabel != null)
            {
                _suppressedLabel.Text = any
                    ? "Suppressed: " + string.Join(", ", _workspace.Suppressed)
                    : "Nothing suppressed.";
            }
            if (_clearSuppressions != null) _clearSuppressions.Disabled = !any;
        }

        private void OnSelected()
        {
            if (_suppress == null) return;
            _suppress.Disabled = SelectedCode() == null;
        }

        private void OnActivated()
        {
            if (_tree == null || _navigate == null) return;

            TreeItem? selected = _tree.GetSelected();
            if (selected == null) return;

            Variant metadata = selected.GetMetadata(WhereColumn);
            if (metadata.VariantType != Variant.Type.Dictionary) return;

            Godot.Collections.Dictionary location = metadata.AsGodotDictionary();
            string file = location["file"].AsString();
            if (file.Length == 0) return;

            _navigate(file, location["line"].AsInt32(), location["column"].AsInt32());
        }

        private void OnSuppress()
        {
            string? code = SelectedCode();
            if (code != null) _workspace?.Suppress(code);
        }

        private void OnClearSuppressions()
        {
            if (_workspace == null) return;

            foreach (string code in new List<string>(_workspace.Suppressed)) _workspace.Unsuppress(code);
        }

        /// <summary>The lint code of the selected row, or null when it is not one that can be hidden.</summary>
        private string? SelectedCode()
        {
            TreeItem? selected = _tree?.GetSelected();
            if (selected == null) return null;

            Variant metadata = selected.GetMetadata(WhereColumn);
            if (metadata.VariantType != Variant.Type.Dictionary) return null;

            string code = metadata.AsGodotDictionary()["code"].AsString();
            return CantripWorkspace.IsSuppressable(code) ? code : null;
        }

        private static Godot.Collections.Dictionary Location(string file, int line, int column, string code)
        {
            var location = new Godot.Collections.Dictionary();
            location["file"] = file;
            location["line"] = line;
            location["column"] = column;
            location["code"] = code;
            return location;
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

        private static string Short(string file) =>
            ContentPaths.IsResourcePath(file) ? ContentPaths.WithoutScheme(file) : file;

        private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

        private static CheckBox Toggle(Node parent, string text, bool pressed)
        {
            var box = new CheckBox { Text = text, ButtonPressed = pressed };
            parent.AddChild(box);
            return box;
        }

        /// <summary>One line about what is on show, for headless checks.</summary>
        public string SelfTest()
        {
            Refresh();
            if (_workspace == null) return "problems: no workspace";

            var lines = new List<string>
            {
                $"problems: {_workspace.ErrorCount} error(s), {_workspace.WarningCount} warning(s), {_workspace.NoteCount} note(s)",
            };
            foreach (Diagnostic diagnostic in _workspace.Problems) lines.Add("    " + diagnostic);
            return string.Join("\n", lines);
        }

        /// <summary>The first problem with a file, which is where a headless check jumps to.</summary>
        public Diagnostic? First()
        {
            if (_workspace == null) return null;
            foreach (Diagnostic diagnostic in _workspace.Problems)
            {
                if (!string.IsNullOrEmpty(diagnostic.Span.File)) return diagnostic;
            }
            return null;
        }
    }
}
#endif
