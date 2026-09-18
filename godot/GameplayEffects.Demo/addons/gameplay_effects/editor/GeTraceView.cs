#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// The causality tree of a running game: what happened, what caused it, and the line of content
    /// it came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries are pulled, never pushed. Godot's debugger channel is capped at 2048 queued messages
    /// and 32768 characters a second, and one busy battle can record thousands of steps, so the
    /// game keeps a ring buffer and this asks for what it has not seen. When the buffer has trimmed,
    /// the count is shown rather than the gap: a tree that silently skipped a hundred steps would be
    /// worse than one that admits it.
    /// </para>
    /// <para>
    /// Tracing is off in a game nobody is watching. Turning it on from here is a request to the
    /// game, not a local setting, which is why the checkbox only reflects what the game says back.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class GeTraceView : Control
    {
        private const int WhatColumn = 0;
        private const int KindColumn = 1;
        private const int WhereColumn = 2;

        private static readonly Color WarningColour = new Color(1.0f, 0.84f, 0.4f);
        private static readonly Color QuietColour = new Color(0.68f, 0.72f, 0.78f);

        private readonly Dictionary<long, TreeItem> _rows = new Dictionary<long, TreeItem>();
        private readonly List<Godot.Collections.Dictionary> _pending = new List<Godot.Collections.Dictionary>();

        private Action<string, Godot.Collections.Array>? _send;

        private Tree? _tree;
        private TreeItem? _root;
        private Label? _status;
        private CheckBox? _tracing;
        private CheckBox? _follow;
        private Button? _fetch;
        private Button? _clear;
        private Button? _pause;
        private Button? _step;
        private Button? _resume;
        private Label? _held;
        private Timer? _poll;

        private long _cursor;
        private long _dropped;
        private bool _connected;

        /// <summary>Raised when someone picks a step, so the dock can open the line behind it.</summary>
        public event Action<string, int, int>? NavigateRequested;

        /// <summary>
        /// Wires the view to one debug session. Everything it wants, it asks for through
        /// <paramref name="send"/>; it never reaches into the game itself.
        /// </summary>
        public void Initialize(Action<string, Godot.Collections.Array> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));

            // The session tab takes its title from the Control's name.
            Name = "Gameplay Effects";
            SizeFlagsVertical = SizeFlags.ExpandFill;

            var root = new VBoxContainer();
            root.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(root);

            var bar = new HBoxContainer();
            root.AddChild(bar);

            _tracing = new CheckBox
            {
                Text = "Record",
                TooltipText = "Ask the running game to record its causality trace. It is off until asked, because it is not free.",
            };
            _tracing.Toggled += OnTracingToggled;
            bar.AddChild(_tracing);

            _follow = new CheckBox { Text = "Follow", ButtonPressed = true, TooltipText = "Keep asking for new steps." };
            bar.AddChild(_follow);

            _fetch = new Button { Text = "Fetch", TooltipText = "Ask for the steps recorded since the last one shown." };
            _fetch.Pressed += RequestBatch;
            bar.AddChild(_fetch);

            _clear = new Button { Text = "Clear", TooltipText = "Empty this view. The game keeps what it has." };
            _clear.Pressed += ClearRows;
            bar.AddChild(_clear);

            bar.AddChild(new VSeparator());

            // Stepping is the other half of reading a trace: the tree says what already happened,
            // these say what happens next and let it happen one trigger at a time.
            _pause = new Button { Text = "Pause", TooltipText = "Hold queued triggers. Whatever is running finishes first." };
            _pause.Pressed += () => Request(GeProtocol.Pause, new Godot.Collections.Array());
            bar.AddChild(_pause);

            _step = new Button { Text = "Step", TooltipText = "Resolve exactly one queued trigger." };
            _step.Pressed += () => Request(GeProtocol.Step, new Godot.Collections.Array());
            bar.AddChild(_step);

            _resume = new Button { Text = "Resume", TooltipText = "Let the rest of the queue resolve." };
            _resume.Pressed += () => Request(GeProtocol.Resume, new Godot.Collections.Array());
            bar.AddChild(_resume);

            _held = new Label { ClipText = true, CustomMinimumSize = new Vector2(240, 0) };
            bar.AddChild(_held);

            _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, Text = "No game running." };
            bar.AddChild(_status);

            _tree = new Tree
            {
                Columns = 3,
                ColumnTitlesVisible = true,
                HideRoot = true,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SelectMode = Tree.SelectModeEnum.Row,
            };
            _tree.SetColumnTitle(WhatColumn, "What happened");
            _tree.SetColumnTitle(KindColumn, "Kind");
            _tree.SetColumnTitle(WhereColumn, "Where");
            _tree.SetColumnExpand(KindColumn, false);
            _tree.SetColumnCustomMinimumWidth(KindColumn, 90);
            _tree.SetColumnExpand(WhereColumn, false);
            _tree.SetColumnCustomMinimumWidth(WhereColumn, 220);
            _tree.ItemActivated += OnActivated;
            root.AddChild(_tree);

            _root = _tree.CreateItem();

            _poll = new Timer { WaitTime = 0.5, Autostart = false };
            _poll.Timeout += OnPoll;
            AddChild(_poll);
        }

        /// <summary>The game is on the other end. Ask who it is.</summary>
        public void OnStarted()
        {
            _connected = true;
            _cursor = 0;
            _dropped = 0;
            ClearRows();
            Request(GeProtocol.Hello, new Godot.Collections.Array());
            _poll?.Start();
        }

        public void OnStopped()
        {
            _connected = false;
            _poll?.Stop();
            if (_status != null) _status.Text = "The game has stopped. What it sent is still here.";
        }

        /// <summary>One answer from the game.</summary>
        public void Receive(string name, Godot.Collections.Dictionary payload)
        {
            switch (name)
            {
                case GeProtocol.Welcome:
                    OnWelcome(payload);
                    break;
                case GeProtocol.Trace:
                    OnTrace(payload);
                    break;
                case GeProtocol.StepState:
                    OnStepState(payload);
                    break;
                case GeProtocol.Reloaded:
                case GeProtocol.Failed:
                    OnMessage(payload);
                    break;
            }
        }

        // What the game said -------------------------------------------------------------------

        /// <summary>
        /// Where the game stands after a pause, step or breakpoint. A paused game is asked for its
        /// trace straight away: what led to the stop is the reason anyone stopped there.
        /// </summary>
        private void OnStepState(Godot.Collections.Dictionary payload)
        {
            bool paused = payload["paused"].AsBool();
            string message = payload["message"].AsString();
            string next = payload["next"].AsString();
            int pending = payload["pending"].AsInt32();

            if (_held != null)
            {
                string where = payload["file"].AsString();
                string at = where.Length == 0
                    ? string.Empty
                    : $" ({System.IO.Path.GetFileName(where)}:{payload["line"].AsInt32()})";

                _held.Text = message.Length > 0
                    ? message
                    : !paused ? (pending > 0 ? $"running, {pending} queued" : "running")
                    : next.Length > 0 ? $"held before {next}{at}"
                    : "held, nothing queued";
            }

            if (_step != null) _step.Disabled = pending == 0 || !payload["steppable"].AsBool();
            if (_resume != null) _resume.Disabled = !paused;
            if (_pause != null) _pause.Disabled = paused;

            if (paused) RequestBatch();
        }

        private void OnWelcome(Godot.Collections.Dictionary payload)
        {
            bool tracing = payload.ContainsKey("tracing") && payload["tracing"].AsBool();
            if (_tracing != null) _tracing.SetPressedNoSignal(tracing);

            if (_status == null) return;
            _status.Text = $"{payload.GetValueOrDefault("definitions", 0)} definition(s), turn {payload.GetValueOrDefault("turn", 0)}, " +
                           $"content {payload.GetValueOrDefault("fingerprint", "")}" + (tracing ? ", recording" : ", not recording");
        }

        private void OnTrace(Godot.Collections.Dictionary payload)
        {
            _cursor = payload.ContainsKey("next") ? payload["next"].AsInt64() : _cursor;
            _dropped = payload.ContainsKey("dropped") ? payload["dropped"].AsInt64() : _dropped;

            if (payload.ContainsKey("entries"))
            {
                foreach (Variant entry in payload["entries"].AsGodotArray()) Add(entry.AsGodotDictionary());
            }

            UpdateStatus();

            // The game had more than one batch could carry, so come straight back for the rest.
            if (payload.ContainsKey("more") && payload["more"].AsBool()) RequestBatch();
        }

        private void OnMessage(Godot.Collections.Dictionary payload)
        {
            if (_status == null) return;

            string message = payload.ContainsKey("message") ? payload["message"].AsString() : string.Empty;
            if (message.Length > 0) _status.Text = message;
        }

        // The tree -------------------------------------------------------------------------------

        private void Add(Godot.Collections.Dictionary entry)
        {
            if (_tree == null || _root == null) return;

            long id = entry.ContainsKey("id") ? entry["id"].AsInt64() : 0;
            if (id == 0 || _rows.ContainsKey(id)) return;

            long parent = entry.ContainsKey("parent") ? entry["parent"].AsInt64() : 0;

            // A step whose cause was trimmed away hangs at the root rather than disappearing.
            TreeItem anchor = parent != 0 && _rows.TryGetValue(parent, out TreeItem? found) ? found : _root;
            TreeItem row = _tree.CreateItem(anchor);

            string what = entry.ContainsKey("text") ? entry["text"].AsString() : string.Empty;
            string source = entry.ContainsKey("source") ? entry["source"].AsString() : string.Empty;
            string kind = entry.ContainsKey("kind") ? entry["kind"].AsString() : string.Empty;
            string file = entry.ContainsKey("file") ? entry["file"].AsString() : string.Empty;
            int line = entry.ContainsKey("line") ? entry["line"].AsInt32() : 0;
            int column = entry.ContainsKey("column") ? entry["column"].AsInt32() : 0;

            row.SetText(WhatColumn, source.Length > 0 ? $"{what}   ({source})" : what);
            row.SetText(KindColumn, kind);
            row.SetText(WhereColumn, file.Length == 0 ? string.Empty : $"{System.IO.Path.GetFileName(file)}:{line}");
            if (kind == "warning" || kind == "loop") row.SetCustomColor(KindColumn, WarningColour);
            else row.SetCustomColor(KindColumn, QuietColour);

            if (entry.ContainsKey("values"))
            {
                Godot.Collections.Dictionary values = entry["values"].AsGodotDictionary();
                if (values.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (Variant key in values.Keys) parts.Add($"{key.AsString()}={values[key].AsString()}");
                    row.SetTooltipText(WhatColumn, string.Join(", ", parts));
                }
            }

            row.SetMetadata(WhatColumn, new Godot.Collections.Dictionary { ["file"] = file, ["line"] = line, ["column"] = column });
            _rows[id] = row;

            if (_follow != null && _follow.ButtonPressed) _tree.ScrollToItem(row, true);
        }

        private void ClearRows()
        {
            _rows.Clear();
            if (_tree == null) return;

            _tree.Clear();
            _root = _tree.CreateItem();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_status == null) return;

            string dropped = _dropped > 0 ? $", {_dropped} dropped before this" : string.Empty;
            _status.Text = $"{_rows.Count} step(s){dropped}" + (_connected ? string.Empty : " (the game has stopped)");
        }

        // Asking ---------------------------------------------------------------------------------

        private void OnPoll()
        {
            if (_connected && _follow != null && _follow.ButtonPressed && _tracing != null && _tracing.ButtonPressed) RequestBatch();
        }

        private void RequestBatch() =>
            Request(GeProtocol.TraceFetch, new Godot.Collections.Array { _cursor, TraceBatch.DefaultMax });

        private void OnTracingToggled(bool on) =>
            Request(GeProtocol.TraceEnable, new Godot.Collections.Array { on, 2000 });

        private void Request(string name, Godot.Collections.Array arguments) => _send?.Invoke(name, arguments);

        private void OnActivated()
        {
            TreeItem? selected = _tree?.GetSelected();
            if (selected == null) return;

            Variant metadata = selected.GetMetadata(WhatColumn);
            if (metadata.VariantType != Variant.Type.Dictionary) return;

            Godot.Collections.Dictionary where = metadata.AsGodotDictionary();
            string file = where["file"].AsString();
            if (file.Length == 0) return;

            NavigateRequested?.Invoke(file, where["line"].AsInt32(), where["column"].AsInt32());
        }
    }
}
#endif
