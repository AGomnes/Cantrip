#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Testing;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The <c>test</c> blocks in the project's content, and what happens when they run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runner is the core's own, so a test that passes here passes in <c>cantrip test</c> and in
    /// CI. A failure carries the span of the statement that failed, which makes it as clickable as a
    /// diagnostic.
    /// </para>
    /// <para>
    /// Traces are recorded on a second run of the failing test alone, rather than on every test.
    /// Tracing makes every test slower and a passing test's causality tree is of no interest, while
    /// a failing one's is the whole question; the runner is deterministic, so the second run fails
    /// in exactly the same place.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripTestsTab : VBoxContainer
    {
        private const int NameColumn = 0;
        private const int ResultColumn = 1;
        private const int FailureColumn = 2;

        private static readonly Color PassColour = new Color(0.55f, 0.85f, 0.55f);
        private static readonly Color FailColour = new Color(1.0f, 0.47f, 0.42f);
        private static readonly Color IdleColour = new Color(0.7f, 0.72f, 0.76f);

        private readonly Dictionary<string, string> _traces = new Dictionary<string, string>(StringComparer.Ordinal);

        private CantripWorkspace? _workspace;
        private Action<string, int, int>? _navigate;

        private Tree? _tree;
        private TextEdit? _trace;
        private Label? _summary;
        private LineEdit? _filter;
        private CheckBox? _traceFailures;
        private Button? _run;

        private int _ranAtGeneration = -1;

        public void Initialize(CantripWorkspace workspace, Action<string, int, int> navigate)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));

            Name = "Tests";
            SizeFlagsVertical = SizeFlags.ExpandFill;

            var bar = new HBoxContainer();
            AddChild(bar);

            _run = new Button { Text = "Run", TooltipText = "Run every test block in the loaded content." };
            _run.Pressed += RunAll;
            bar.AddChild(_run);

            _filter = new LineEdit
            {
                PlaceholderText = "filter by name",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            _filter.TextSubmitted += _ => RunAll();
            bar.AddChild(_filter);

            _traceFailures = new CheckBox
            {
                Text = "Trace failures",
                ButtonPressed = true,
                TooltipText = "Re-run each failing test with the causality trace on.",
            };
            bar.AddChild(_traceFailures);

            _summary = new Label { ClipText = true };
            bar.AddChild(_summary);

            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);

            _tree = new Tree
            {
                Columns = 3,
                ColumnTitlesVisible = true,
                HideRoot = true,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SelectMode = Tree.SelectModeEnum.Row,
            };
            _tree.SetColumnTitle(NameColumn, "Test");
            _tree.SetColumnTitle(ResultColumn, "Result");
            _tree.SetColumnTitle(FailureColumn, "Failure");
            _tree.SetColumnExpand(ResultColumn, false);
            _tree.SetColumnCustomMinimumWidth(ResultColumn, 70);
            _tree.ItemActivated += OnActivated;
            _tree.ItemSelected += OnSelected;
            split.AddChild(_tree);

            _trace = new TextEdit
            {
                Editable = false,
                PlaceholderText = "The trace of a failing test appears here.",
                CustomMinimumSize = new Vector2(0, 120),
            };
            split.AddChild(_trace);

            _workspace.Changed += Refresh;
            Refresh();
        }

        public override void _ExitTree()
        {
            if (_workspace != null) _workspace.Changed -= Refresh;
        }

        /// <summary>
        /// Lists the tests without running them. Content changes while a designer types, and running
        /// a battle on every keystroke would make the editor feel possessed.
        /// </summary>
        public void Refresh()
        {
            if (_workspace == null) return;

            _traces.Clear();
            _ranAtGeneration = -1;
            Failed = 0;
            if (_trace != null) _trace.Text = string.Empty;

            Fill(null);
            if (_summary != null) _summary.Text = $"{_workspace.Content.Tests.Count} test(s), not run";
        }

        /// <summary>Runs every test whose name matches the filter, then shows what happened.</summary>
        public void RunAll()
        {
            if (_workspace == null || _tree == null) return;

            string? filter = string.IsNullOrWhiteSpace(_filter?.Text) ? null : _filter!.Text.Trim();
            IReadOnlyList<DslTestResult> results;
            try
            {
                results = new DslTestRunner(_workspace.Content).RunAll(filter);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                // A test that takes the runner down should not take the dock with it.
                GD.PushError($"Cantrip: the test runner failed. {error.Message}");
                if (_summary != null) _summary.Text = "The runner failed; see the output.";
                return;
            }

            _traces.Clear();
            if (_traceFailures?.ButtonPressed ?? false)
            {
                var tracing = new DslTestRunner(_workspace.Content) { Trace = true };
                foreach (DslTestResult result in results)
                {
                    if (result.Passed) continue;

                    DslTestResult traced = tracing.Run(result.Test);
                    if (!string.IsNullOrEmpty(traced.Trace)) _traces[result.Name] = traced.Trace!;
                }
            }

            _ranAtGeneration = _workspace.Generation;
            Fill(results);

            int failed = 0;
            foreach (DslTestResult result in results)
            {
                if (!result.Passed) failed++;
            }
            Failed = failed;
            if (_summary != null) _summary.Text = $"{results.Count - failed} passed, {failed} failed";
        }

        private void Fill(IReadOnlyList<DslTestResult>? results)
        {
            if (_tree == null || _workspace == null) return;

            _tree.Clear();
            TreeItem root = _tree.CreateItem();

            if (results == null)
            {
                foreach (TestDefinition test in _workspace.Content.Tests)
                {
                    TreeItem row = _tree.CreateItem(root);
                    row.SetText(NameColumn, test.Name);
                    row.SetText(ResultColumn, "-");
                    row.SetCustomColor(ResultColumn, IdleColour);
                    row.SetMetadata(NameColumn, Location(test.Syntax.Span.File, test.Syntax.Span.Line, test.Syntax.Span.Column, test.Name));
                }
                return;
            }

            foreach (DslTestResult result in results)
            {
                TreeItem row = _tree.CreateItem(root);
                row.SetText(NameColumn, result.Name);
                row.SetText(ResultColumn, result.Passed ? "PASS" : "FAIL");
                row.SetCustomColor(ResultColumn, result.Passed ? PassColour : FailColour);

                if (!result.Passed)
                {
                    row.SetText(FailureColumn, $"{result.FailureSpan.Line}:{result.FailureSpan.Column} {result.Failure}");
                    row.SetTooltipText(FailureColumn, result.Failure ?? string.Empty);
                    row.SetCustomColor(NameColumn, FailColour);
                }

                // A failure points at the statement that failed; a pass points at the test itself.
                SourceSpanLike where = result.Passed
                    ? new SourceSpanLike(result.Test.Syntax.Span.File, result.Test.Syntax.Span.Line, result.Test.Syntax.Span.Column)
                    : new SourceSpanLike(
                        string.IsNullOrEmpty(result.FailureSpan.File) ? result.Test.Syntax.Span.File : result.FailureSpan.File,
                        result.FailureSpan.Line == 0 ? result.Test.Syntax.Span.Line : result.FailureSpan.Line,
                        result.FailureSpan.Column);

                row.SetMetadata(NameColumn, Location(where.File, where.Line, where.Column, result.Name));
            }
        }

        private void OnSelected()
        {
            if (_trace == null || _tree == null) return;

            TreeItem? selected = _tree.GetSelected();
            string? name = NameOf(selected);
            _trace.Text = name != null && _traces.TryGetValue(name, out string? trace) ? trace : string.Empty;
        }

        private void OnActivated()
        {
            if (_tree == null || _navigate == null) return;

            TreeItem? selected = _tree.GetSelected();
            if (selected == null) return;

            Variant metadata = selected.GetMetadata(NameColumn);
            if (metadata.VariantType != Variant.Type.Dictionary) return;

            Godot.Collections.Dictionary location = metadata.AsGodotDictionary();
            string file = location["file"].AsString();
            if (file.Length == 0) return;

            _navigate(file, location["line"].AsInt32(), location["column"].AsInt32());
        }

        private static string? NameOf(TreeItem? item)
        {
            if (item == null) return null;

            Variant metadata = item.GetMetadata(NameColumn);
            if (metadata.VariantType != Variant.Type.Dictionary) return null;

            string name = metadata.AsGodotDictionary()["name"].AsString();
            return name.Length == 0 ? null : name;
        }

        private static Godot.Collections.Dictionary Location(string file, int line, int column, string name)
        {
            var location = new Godot.Collections.Dictionary();
            location["file"] = file;
            location["line"] = line;
            location["column"] = column;
            location["name"] = name;
            return location;
        }

        /// <summary>Whether the results on show were produced from the content now loaded.</summary>
        public bool IsCurrent => _workspace != null && _ranAtGeneration == _workspace.Generation;

        /// <summary>What the last run came to, in the words on the tab's own summary label.</summary>
        public string Summary => _summary?.Text ?? string.Empty;

        /// <summary>How many of the last run's tests failed. Zero before anything has been run.</summary>
        public int Failed { get; private set; }

        private readonly struct SourceSpanLike
        {
            public SourceSpanLike(string file, int line, int column)
            {
                File = file;
                Line = line;
                Column = column;
            }

            public string File { get; }
            public int Line { get; }
            public int Column { get; }
        }

        /// <summary>Runs everything and reports it in one line, for headless checks.</summary>
        public string SelfTest()
        {
            if (_workspace == null) return "tests: no workspace";

            RunAll();
            return "tests: " + (_summary?.Text ?? "no summary") + $" of {_workspace.Content.Tests.Count} block(s)";
        }
    }
}
#endif
