#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The editor's copy of the project's content: one library, loaded exactly the way a running
    /// game loads it, plus everything the dock's panels project from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loading goes through <see cref="GodotContentLoader"/> rather than
    /// <see cref="ContentLibrary.LoadFolder"/>, so paths stay <c>res://</c>-relative and in the same
    /// ordinal order the game will use. A diagnostic whose file the designer cannot open is worse
    /// than no diagnostic at all, and load order decides listener order, so a workspace that
    /// discovered content its own way would be quietly reporting on a different game.
    /// </para>
    /// <para>
    /// This is a plain C# object rather than a <see cref="GodotObject"/>: the panels are Controls
    /// that come and go with the dock, and a plain <see cref="Changed"/> event cannot outlive them
    /// as a connected signal can. One event covers everything, because every panel shows a
    /// projection of the same reload.
    /// </para>
    /// </remarks>
    public sealed class CantripWorkspace
    {
        /// <summary>Where content lives, so a project can keep <c>.cantrip</c> files in one folder.</summary>
        public const string ContentFolderSetting = "cantrip/content/folder";

        /// <summary>Verbs a game registers from C#, so CT301 does not fire on every one of them.</summary>
        public const string HostVerbsSetting = "cantrip/lint/host_verbs";

        /// <summary>Events a game raises from C#, for CT304 and CT305.</summary>
        public const string HostEventsSetting = "cantrip/lint/host_events";

        /// <summary>Names a game's host resolves, for CT302.</summary>
        public const string HostNamesSetting = "cantrip/lint/host_names";

        /// <summary>Lint codes this user does not want to see, kept per user in the editor settings.</summary>
        public const string SuppressedSetting = "cantrip/lint/suppressed_codes";

        /// <summary>
        /// Reported when the linter itself fails. It sits in the adapter's own CT09xx range beside
        /// <see cref="GodotContentLoader.UnreadableCode"/>, because it says something about the
        /// tools rather than about the content.
        /// </summary>
        public const string LintFailedCode = "CT0902";

        private static readonly IReadOnlyList<Diagnostic> None = new Diagnostic[0];

        private readonly List<Diagnostic> _load = new List<Diagnostic>();
        private readonly List<Diagnostic> _lint = new List<Diagnostic>();
        private readonly List<Diagnostic> _problems = new List<Diagnostic>();
        private readonly SortedSet<string> _suppressed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        public CantripWorkspace()
        {
            ContentFolder = Setting(ContentFolderSetting, ContentPaths.ResourceScheme);
            foreach (string verb in Words(Setting(HostVerbsSetting, string.Empty))) HostVerbs.Add(verb);
            foreach (string name in Words(Setting(HostEventsSetting, string.Empty))) HostEvents.Add(name);
            foreach (string name in Words(Setting(HostNamesSetting, string.Empty))) HostNames.Add(name);
            foreach (string code in Words(EditorSetting(SuppressedSetting))) _suppressed.Add(code);
        }

        /// <summary>Raised once per reload, after everything below has been rebuilt.</summary>
        public event Action? Changed;

        /// <summary>The folder content is discovered under. Defaults to the whole project.</summary>
        public string ContentFolder { get; set; }

        public ContentLibrary Content { get; private set; } = new ContentLibrary();

        /// <summary>The files discovered on the last reload, in load order.</summary>
        public IReadOnlyList<string> Files { get; private set; } = new string[0];

        /// <summary>
        /// Rises by one per reload. A panel that caches anything derived from the content (a preview
        /// runtime, a test result) compares against this instead of rebuilding on every redraw.
        /// </summary>
        public int Generation { get; private set; }

        /// <summary>True once <see cref="Reload"/> has run, so a panel can say "not loaded yet".</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>What the parser said, per file, in load order.</summary>
        public IReadOnlyList<Diagnostic> LoadProblems => _load;

        /// <summary>What the linter said, already filtered by <see cref="Suppressed"/>.</summary>
        public IReadOnlyList<Diagnostic> LintProblems => _lint;

        /// <summary>Both of the above, in source order: file, then line, then column, then code.</summary>
        public IReadOnlyList<Diagnostic> Problems => _problems;

        /// <summary>Verbs the game registers in C#. Seeded from project settings; editor tools may add more.</summary>
        public ISet<string> HostVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ISet<string> HostEvents { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ISet<string> HostNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Lint codes left out of <see cref="LintProblems"/>.</summary>
        public IReadOnlyCollection<string> Suppressed => _suppressed;

        public int ErrorCount => _problems.Count(d => d.Severity == DiagnosticSeverity.Error);

        public int WarningCount => _problems.Count(d => d.Severity == DiagnosticSeverity.Warning);

        public int NoteCount => _problems.Count(d => d.Severity == DiagnosticSeverity.Info);

        /// <summary>What is loaded, as the core hashes it. Shown so a designer can tell two states apart.</summary>
        public string Fingerprint => Content.Fingerprint;

        /// <summary>
        /// Discovers, loads and lints the project's content, then tells the panels. Everything is
        /// rebuilt from scratch: a library that kept definitions from a file the designer has since
        /// deleted would report problems nobody can fix.
        /// </summary>
        public void Reload()
        {
            var library = new ContentLibrary();
            IReadOnlyList<string> files = GodotContentLoader.Discover(ContentFolder);
            DiagnosticBag load = GodotContentLoader.LoadInto(library, files);

            Content = library;
            Files = files;

            _load.Clear();
            _load.AddRange(load);

            _lint.Clear();
            _lint.AddRange(Lint(library));

            _problems.Clear();
            _problems.AddRange(_load);
            _problems.AddRange(_lint);
            _problems.Sort(InSourceOrder);

            Generation++;
            IsLoaded = true;
            Changed?.Invoke();
        }

        /// <summary>Stops reporting a lint code, remembers that in the editor settings, and re-lints.</summary>
        public void Suppress(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || !_suppressed.Add(code.Trim())) return;
            SaveSuppressed();
            Reload();
        }

        public void Unsuppress(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || !_suppressed.Remove(code.Trim())) return;
            SaveSuppressed();
            Reload();
        }

        /// <summary>True for a code the linter raises, which is the only kind worth suppressing.</summary>
        public static bool IsSuppressable(string? code) =>
            !string.IsNullOrEmpty(code) && (code!.StartsWith("CT3", StringComparison.Ordinal) || code.StartsWith("CT4", StringComparison.Ordinal));

        /// <summary>Every problem in one file, for grouping. Ordered like <see cref="Problems"/>.</summary>
        public IReadOnlyList<Diagnostic> ProblemsIn(string file) =>
            _problems.Where(d => string.Equals(d.Span.File, file, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// Runs the linter over a library, with the options this project asks for. A failure here is
        /// reported as a diagnostic rather than thrown: an editor panel that disappears because one
        /// content file confused a check is worse than a panel that says so.
        /// </summary>
        private IReadOnlyList<Diagnostic> Lint(ContentLibrary library)
        {
            var options = new LintOptions();
            foreach (string code in _suppressed) options.Suppressed.Add(code);
            foreach (string verb in HostVerbs) options.HostVerbs.Add(verb);
            foreach (string name in HostEvents) options.HostEvents.Add(name);
            foreach (string name in HostNames) options.HostNames.Add(name);

            try
            {
                return Linter.Lint(library, options);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                GD.PushError($"Cantrip: the linter failed. {error.Message}");
                return new[]
                {
                    new Diagnostic(
                        DiagnosticSeverity.Warning,
                        LintFailedCode,
                        $"The linter could not finish, so static checks are missing from this list. {error.Message}",
                        SourceSpan.None),
                };
            }
        }

        private void SaveSuppressed()
        {
            EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
            settings?.SetSetting(SuppressedSetting, string.Join(", ", _suppressed));
        }

        /// <summary>
        /// A project setting, which travels with the project. Host verbs and the content folder
        /// belong here rather than in the editor settings: they are facts about the game, and every
        /// person working on it needs the same answer.
        /// </summary>
        private static string Setting(string key, string fallback)
        {
            Variant value = ProjectSettings.GetSetting(key, fallback);
            string text = value.AsString();
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        /// <summary>An editor setting, which is this person's preference and stays on this machine.</summary>
        private static string EditorSetting(string key)
        {
            EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
            // Reading a setting that was never written logs an engine error, so ask first.
            if (settings == null || !settings.HasSetting(key)) return string.Empty;
            return settings.GetSetting(key).AsString();
        }

        /// <summary>Splits a setting written as <c>CT306, CT310</c> or <c>CT306 CT310</c>.</summary>
        private static IEnumerable<string> Words(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) yield break;

            foreach (string part in value!.Split(new[] { ',', ' ', ';', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }

        /// <summary>The order the command-line tool prints in, so a problem reads the same anywhere.</summary>
        private static int InSourceOrder(Diagnostic left, Diagnostic right)
        {
            int file = string.CompareOrdinal(left.Span.File, right.Span.File);
            if (file != 0) return file;
            if (left.Span.Line != right.Span.Line) return left.Span.Line.CompareTo(right.Span.Line);
            if (left.Span.Column != right.Span.Column) return left.Span.Column.CompareTo(right.Span.Column);
            return string.CompareOrdinal(left.Code, right.Code);
        }

        /// <summary>Empty diagnostics, for a panel that wants a list before the first load.</summary>
        public static IReadOnlyList<Diagnostic> NoProblems => None;
    }
}
#endif
