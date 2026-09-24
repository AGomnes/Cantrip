#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Cantrip.Diagnostics;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The addon's dock: one workspace, four views of it, and a button that reloads the lot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel owns nothing but arrangement. Every tab is a projection of the same
    /// <see cref="CantripWorkspace"/>, and the one thing they share beyond it is this panel's
    /// <see cref="ShowSource"/>: a problem, a failing test and a definition all point at a file and
    /// a line, and all three land in the same editor.
    /// </para>
    /// <para>
    /// The other way round is <see cref="RunTests"/>. The editor's Run tests button does not run
    /// anything itself: it brings the buffers up to date and then asks the panel, which presses the
    /// Tests tab's own runner, so there is one runner in the dock and one list of results.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripPanel : VBoxContainer
    {
        private CantripWorkspace? _workspace;

        private TabContainer? _tabs;
        private Button? _reload;
        private Label? _status;
        private CantripProblemsTab? _problems;
        private CantripTestsTab? _tests;
        private CantripPreviewTab? _preview;
        private CantripSourceView? _source;

        public void Initialize(CantripWorkspace workspace)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

            // The dock tab takes its title from the Control's name.
            Name = "Cantrip";
            SizeFlagsVertical = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(320, 0);

            var bar = new HBoxContainer();
            AddChild(bar);

            _reload = new Button
            {
                Text = "Reload",
                TooltipText = "Read the cantrip/ project settings, then discover, load and lint every .cantrip file again.",
            };
            _reload.Pressed += OnReload;
            bar.AddChild(_reload);

            _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            bar.AddChild(_status);

            _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(_tabs);

            _problems = new CantripProblemsTab();
            _tabs.AddChild(_problems);
            _problems.Initialize(_workspace, ShowSource);

            _tests = new CantripTestsTab();
            _tabs.AddChild(_tests);
            _tests.Initialize(_workspace, ShowSource);

            _preview = new CantripPreviewTab();
            _tabs.AddChild(_preview);
            _preview.Initialize(_workspace, ShowSource);

            _source = new CantripSourceView();
            _tabs.AddChild(_source);
            _source.Initialize(_workspace, RunTests);
            _source.TitleChanged += MarkSourceTab;

            _workspace.Changed += UpdateStatus;
            UpdateStatus();
        }

        public override void _ExitTree()
        {
            if (_source != null) _source.TitleChanged -= MarkSourceTab;
            if (_workspace != null) _workspace.Changed -= UpdateStatus;
        }

        /// <summary>
        /// The Source tab says so when a buffer is unsaved, because the dock is often the only part
        /// of it on screen and a file switched away from still has to show that it is waiting.
        /// </summary>
        private void MarkSourceTab()
        {
            if (_tabs == null || _source == null) return;

            int index = _tabs.GetTabIdxFromControl(_source);
            if (index >= 0) _tabs.SetTabTitle(index, _source.Title);
        }

        /// <summary>
        /// Runs the content's tests against what is in the editor's buffers, saved or not, and says
        /// what happened in one line. A failure brings the Tests tab forward, where each test says
        /// PASS or FAIL and a double-click opens the line it failed on; a clean run leaves the
        /// designer where they were typing.
        /// </summary>
        private string RunTests()
        {
            if (_tests == null || _tabs == null) return "No test runner.";

            _tests.RunAll();
            if (_tests.Failed > 0)
            {
                int index = _tabs.GetTabIdxFromControl(_tests);
                if (index >= 0) _tabs.CurrentTab = index;
            }

            return _tests.Summary;
        }

        /// <summary>
        /// Shows a file at a line in the editor and brings that tab forward. Everything clickable
        /// in the dock ends up here, because nothing else in Godot can open a <c>.cantrip</c> file
        /// at a line.
        /// </summary>
        public void ShowSource(string file, int line, int column)
        {
            if (_source == null || _tabs == null) return;

            _source.Open(file, line, column);
            int index = _tabs.GetTabIdxFromControl(_source);
            if (index >= 0) _tabs.CurrentTab = index;
        }

        /// <summary>
        /// Settings first, so that a folder or a host name changed in Project Settings counts
        /// without switching the plugin off and on.
        /// </summary>
        private void OnReload()
        {
            if (_workspace == null) return;

            _workspace.ReadSettings();
            _workspace.Reload();
        }

        private void UpdateStatus()
        {
            if (_status == null || _workspace == null) return;

            _status.Text = _workspace.IsLoaded
                ? $"{_workspace.Files.Count} file(s), {Definitions()} definition(s) · " +
                  $"{_workspace.ErrorCount} error(s), {_workspace.WarningCount} warning(s), {_workspace.NoteCount} note(s) · {_workspace.Fingerprint}"
                : "Not loaded yet.";
        }

        private int Definitions()
        {
            int count = 0;
            if (_workspace == null) return count;

            foreach (object _ in _workspace.Content.Definitions) count++;
            return count;
        }

        /// <summary>
        /// Exercises every panel without a mouse: reload, list the problems, run the tests, describe
        /// everything, then open the first file at the first problem and edit, check and save one.
        /// Clicking is what a headless editor cannot do; this is everything underneath it.
        /// </summary>
        public IReadOnlyList<string> SelfTest()
        {
            var report = new List<string>();
            if (_workspace == null)
            {
                report.Add("dock: not initialised");
                return report;
            }

            _workspace.Reload();
            report.Add(
                $"content: {_workspace.Files.Count} file(s), {Definitions()} definition(s), " +
                $"{_workspace.Content.Tests.Count} test(s), fingerprint {_workspace.Fingerprint}");
            report.Add(SettingsSelfTest());

            if (_problems != null) report.Add(_problems.SelfTest());
            if (_tests != null) report.Add(_tests.SelfTest());
            if (_preview != null) report.Add(_preview.SelfTest());

            Diagnostic? first = _problems?.First();
            string? file = first?.Span.File ?? (_workspace.Files.Count > 0 ? _workspace.Files[0] : null);
            if (_source != null) report.AddRange(_source.SelfTest(file));

            if (first != null) ShowSource(first.Span.File, first.Span.Line, first.Span.Column);
            report.Add($"dock: {(_tabs == null ? 0 : _tabs.GetTabCount())} tab(s) built");
            return report;
        }

        /// <summary>
        /// Presses Reload with a project setting changed, then again with it put back, which proves
        /// that the button reads settings afresh rather than only when the plugin starts. The
        /// setting changes in memory only; nothing here saves the project.
        /// </summary>
        private string SettingsSelfTest()
        {
            if (_workspace == null || _reload == null) return "settings: not initialised";

            const string probe = "cantrip_selftest_probe";
            string key = CantripWorkspace.HostNamesSetting;
            Variant before = ProjectSettings.HasSetting(key) ? ProjectSettings.GetSetting(key) : default;

            ProjectSettings.SetSetting(key, probe);
            _reload.EmitSignal(BaseButton.SignalName.Pressed);
            bool read = _workspace.HostNames.Contains(probe);

            // Setting null takes away a setting that was not there before.
            ProjectSettings.SetSetting(key, before);
            _reload.EmitSignal(BaseButton.SignalName.Pressed);
            bool readAgain = !_workspace.HostNames.Contains(probe);

            if (read && readAgain) return "settings: Reload reads the project settings again";

            string failure = $"settings: {CantripPlugin.SelfTestFailed}, Reload " +
                (read ? "kept a setting that had been taken away" : "did not see a changed setting");
            GD.PushError("Cantrip self-test: " + failure);
            return failure;
        }
    }
}
#endif
