#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// What one entity in a running game is made of: where it is, what its stats are worth now
    /// against what they started as, the statuses on it, and every rule it has registered.
    /// </summary>
    /// <remarks>
    /// The trace tab answers "why did that happen". This answers the question that comes next,
    /// which is "what is this thing currently doing" — and it answers it with the line of content
    /// behind each rule, because a modifier is invisible in a single number: an enemy with 6 attack
    /// says nothing, while 4 becoming 6 beside the <c>modify</c> line doing it says everything.
    /// </remarks>
    [Tool]
    public partial class CantripInspectorView : Control
    {
        private const int WhatColumn = 0;
        private const int DetailColumn = 1;

        private static readonly Color HeadingColour = new Color(0.78f, 0.82f, 0.88f);
        private static readonly Color ChangedColour = new Color(0.44f, 0.81f, 0.44f);
        private static readonly Color QuietColour = new Color(0.68f, 0.72f, 0.78f);

        private Action<string, Godot.Collections.Array>? _send;

        private ItemList? _entities;
        private Tree? _details;
        private Label? _status;
        private Button? _refresh;

        private readonly List<int> _ids = new List<int>();
        private int _selected;

        /// <summary>Raised when a rule is picked, so the dock can open the content behind it.</summary>
        public event Action<string, int, int>? NavigateRequested;

        public void Initialize(Action<string, Godot.Collections.Array> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));

            Name = "Entities";
            SizeFlagsVertical = SizeFlags.ExpandFill;

            var root = new VBoxContainer();
            root.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(root);

            var bar = new HBoxContainer();
            root.AddChild(bar);

            _refresh = new Button { Text = "Refresh", TooltipText = "Ask the running game what is in play." };
            _refresh.Pressed += RequestEntities;
            bar.AddChild(_refresh);

            _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, Text = "No game running." };
            bar.AddChild(_status);

            var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            root.AddChild(split);

            _entities = new ItemList { CustomMinimumSize = new Vector2(220, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
            _entities.ItemSelected += OnEntitySelected;
            split.AddChild(_entities);

            _details = new Tree
            {
                Columns = 2,
                ColumnTitlesVisible = true,
                HideRoot = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SelectMode = Tree.SelectModeEnum.Row,
            };
            _details.SetColumnTitle(WhatColumn, "What");
            _details.SetColumnTitle(DetailColumn, "Value");
            _details.SetColumnExpand(WhatColumn, true);
            _details.ItemActivated += OnDetailActivated;
            split.AddChild(_details);
        }

        public void OnStarted()
        {
            RequestEntities();
        }

        public void OnStopped()
        {
            if (_status != null) _status.Text = "The game has stopped.";
        }

        /// <summary>
        /// What this tab has made of the answers it was given, for headless checks. A live session
        /// needs a person to stage, so the self-test hands it a real game's replies and asks what it
        /// showed: that is what catches a key renamed on one side of the channel and not the other.
        /// </summary>
        public string SelfTest()
        {
            int rows = 0;
            TreeItem? root = _details?.GetRoot();
            if (root != null)
            {
                foreach (TreeItem group in root.GetChildren())
                {
                    rows++;
                    foreach (TreeItem _ in group.GetChildren()) rows++;
                }
            }

            return $"inspector: {_ids.Count} entit{(_ids.Count == 1 ? "y" : "ies")} listed, {rows} detail row(s)";
        }

        /// <summary>One answer from the game.</summary>
        public void Receive(string name, Godot.Collections.Dictionary payload)
        {
            switch (name)
            {
                case CantripProtocol.EntityList:
                    ShowEntities(payload);
                    break;
                case CantripProtocol.EntityDetail:
                    ShowDetail(payload);
                    break;
                case CantripProtocol.Welcome:
                    // A game that has just reloaded content may have different entities.
                    RequestEntities();
                    break;
                case CantripProtocol.StepState:
                    // Stepping is where watching an entity pays off: a step changes its numbers, so
                    // refresh rather than leaving stale ones on screen next to a live trace.
                    RequestEntities();
                    break;
            }
        }

        // Showing ----------------------------------------------------------------------------------

        private void ShowEntities(Godot.Collections.Dictionary payload)
        {
            if (_entities == null || !payload.ContainsKey("entities")) return;

            _entities.Clear();
            _ids.Clear();

            foreach (Variant item in payload["entities"].AsGodotArray())
            {
                Godot.Collections.Dictionary view = item.AsGodotDictionary();
                int id = view["id"].AsInt32();
                string zone = view["zone"].AsString();

                _ids.Add(id);
                _entities.AddItem($"{view["name"].AsString()}#{id}" + (zone.Length == 0 ? string.Empty : $"  ({zone})"));
            }

            if (_status != null) _status.Text = $"{_ids.Count} entit{(_ids.Count == 1 ? "y" : "ies")} in play";

            // Keep looking at whatever was being looked at, if it is still there.
            int index = _ids.IndexOf(_selected);
            if (index >= 0)
            {
                _entities.Select(index);
                RequestDetail(_selected);
            }
        }

        private void ShowDetail(Godot.Collections.Dictionary payload)
        {
            if (_details == null) return;

            _details.Clear();
            TreeItem root = _details.CreateItem();

            if (!payload.ContainsKey("found") || !payload["found"].AsBool())
            {
                TreeItem gone = _details.CreateItem(root);
                gone.SetText(WhatColumn, "That entity is no longer in the game.");
                return;
            }

            Godot.Collections.Dictionary entity = payload["entity"].AsGodotDictionary();
            Godot.Collections.Dictionary baseStats = payload["base_stats"].AsGodotDictionary();
            Godot.Collections.Dictionary stats = entity["stats"].AsGodotDictionary();

            TreeItem about = Heading(root, payload["definition"].AsString().Length > 0
                ? $"{entity["name"].AsString()}#{entity["id"].AsInt32()} — {payload["definition"].AsString()}"
                : $"{entity["name"].AsString()}#{entity["id"].AsInt32()}");
            Row(about, "kind", $"{entity["kind"].AsString()}, {entity["team"].AsString()}");
            Row(about, "zone", entity["zone"].AsString());
            Row(about, "rules live", payload["active"].AsBool() ? "yes" : "no — its listeners hear nothing here");
            about.SetCollapsed(false);

            // Base beside current, because a modifier is invisible in a single number.
            TreeItem statRows = Heading(root, "Stats");
            foreach (Variant key in stats.Keys)
            {
                string stat = key.AsString();
                int current = stats[key].AsInt32();
                int printed = baseStats.ContainsKey(stat) ? baseStats[stat].AsInt32() : current;

                TreeItem row = Row(statRows, stat, printed == current ? current.ToString() : $"{printed} → {current}");
                if (printed != current) row.SetCustomColor(DetailColumn, ChangedColour);
            }

            TreeItem statuses = Heading(root, "Statuses");
            foreach (Variant item in entity["statuses"].AsGodotArray())
            {
                Godot.Collections.Dictionary status = item.AsGodotDictionary();
                Row(statuses, status["name"].AsString(), $"{status["counter"].AsInt32()}" + (status["hidden"].AsBool() ? "  (hidden)" : string.Empty));
            }

            TreeItem listeners = Heading(root, "Listeners");
            foreach (Variant item in payload["listeners"].AsGodotArray())
            {
                Godot.Collections.Dictionary listener = item.AsGodotDictionary();
                TreeItem row = Row(listeners, listener["text"].AsString(), Where(listener));
                Locate(row, listener);
            }

            TreeItem modifiers = Heading(root, "Modifiers");
            foreach (Variant item in payload["modifiers"].AsGodotArray())
            {
                Godot.Collections.Dictionary modifier = item.AsGodotDictionary();
                TreeItem row = Row(modifiers, modifier["text"].AsString(), Where(modifier));
                Locate(row, modifier);
            }
        }

        private TreeItem Heading(TreeItem root, string text)
        {
            TreeItem item = _details!.CreateItem(root);
            item.SetText(WhatColumn, text);
            item.SetCustomColor(WhatColumn, HeadingColour);
            return item;
        }

        private TreeItem Row(TreeItem parent, string what, string value)
        {
            TreeItem item = _details!.CreateItem(parent);
            item.SetText(WhatColumn, what);
            item.SetText(DetailColumn, value);
            item.SetCustomColor(DetailColumn, QuietColour);
            return item;
        }

        private static string Where(Godot.Collections.Dictionary rule)
        {
            string file = rule["file"].AsString();
            return file.Length == 0 ? string.Empty : $"{System.IO.Path.GetFileName(file)}:{rule["line"].AsInt32()}";
        }

        private static void Locate(TreeItem row, Godot.Collections.Dictionary rule) =>
            row.SetMetadata(WhatColumn, new Godot.Collections.Dictionary
            {
                ["file"] = rule["file"],
                ["line"] = rule["line"],
                ["column"] = rule["column"],
            });

        // Asking -----------------------------------------------------------------------------------

        private void RequestEntities() => _send?.Invoke(CantripProtocol.Entities, new Godot.Collections.Array { string.Empty, string.Empty });

        private void RequestDetail(int entityId) => _send?.Invoke(CantripProtocol.Entity, new Godot.Collections.Array { entityId });

        private void OnEntitySelected(long index)
        {
            if (index < 0 || index >= _ids.Count) return;

            _selected = _ids[(int)index];
            RequestDetail(_selected);
        }

        private void OnDetailActivated()
        {
            TreeItem? selected = _details?.GetSelected();
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
