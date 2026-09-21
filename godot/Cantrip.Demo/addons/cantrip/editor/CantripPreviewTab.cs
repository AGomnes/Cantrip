#if TOOLS
using System;
using System.Collections.Generic;
using System.Text;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// What a definition will say on the card frame: its description, its flavour, the keywords it
    /// leans on, and the hash to paste into <c>text_checked</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With live values on, the definition is instantiated into a throwaway game and described
    /// through <see cref="DescriptionBuilder.Describe(Entity, CardRuntime, Entity)"/>, so every
    /// number goes through the same modifier channels the rules would use. That is what shows a
    /// designer "6 becomes 9 while Strength is up" instead of the printed 6.
    /// </para>
    /// <para>
    /// Each preview builds its own runtime. Reusing one would let the last definition previewed
    /// leave statuses on the dummy and quietly change the next definition's numbers, which is a
    /// worse lie than showing printed values.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripPreviewTab : VBoxContainer
    {
        private const int NameColumn = 0;

        private CantripWorkspace? _workspace;
        private Action<string, int, int>? _navigate;

        private Tree? _list;
        private LineEdit? _filter;
        private CheckBox? _live;
        private RichTextLabel? _text;
        private Label? _hash;
        private Button? _copyHash;

        private string? _selectedKind;
        private string? _selectedName;

        public void Initialize(CantripWorkspace workspace, Action<string, int, int> navigate)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));

            Name = "Preview";
            SizeFlagsVertical = SizeFlags.ExpandFill;

            var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);

            var left = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) };
            split.AddChild(left);

            _filter = new LineEdit { PlaceholderText = "filter" };
            _filter.TextChanged += _ => Fill();
            left.AddChild(_filter);

            _list = new Tree
            {
                Columns = 1,
                HideRoot = true,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SelectMode = Tree.SelectModeEnum.Row,
            };
            _list.ItemSelected += OnSelected;
            _list.ItemActivated += OnActivated;
            left.AddChild(_list);

            var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            split.AddChild(right);

            _live = new CheckBox
            {
                Text = "Live values",
                ButtonPressed = true,
                TooltipText = "Describe the definition inside a throwaway game, so modifiers apply.",
            };
            _live.Toggled += _ => Describe();
            right.AddChild(_live);

            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            right.AddChild(scroll);

            _text = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = true,
                SelectionEnabled = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            scroll.AddChild(_text);

            var hashRow = new HBoxContainer();
            right.AddChild(hashRow);

            _hash = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            hashRow.AddChild(_hash);

            _copyHash = new Button
            {
                Text = "Copy text_checked",
                Disabled = true,
                TooltipText = "Copy the line to paste into the definition once its text has been reviewed.",
            };
            _copyHash.Pressed += OnCopyHash;
            hashRow.AddChild(_copyHash);

            _workspace.Changed += Refresh;
            Refresh();
        }

        public override void _ExitTree()
        {
            if (_workspace != null) _workspace.Changed -= Refresh;
        }

        public void Refresh()
        {
            Fill();
            Describe();
        }

        /// <summary>Lists what is loaded, kind by kind, so a card is not lost among fifty statuses.</summary>
        private void Fill()
        {
            if (_list == null || _workspace == null) return;

            string filter = _filter?.Text?.Trim() ?? string.Empty;

            _list.Clear();
            TreeItem root = _list.CreateItem();
            var groups = new SortedDictionary<string, TreeItem>(StringComparer.Ordinal);
            TreeItem? reselect = null;

            foreach (EntityDefinition definition in Sorted(_workspace.Content))
            {
                if (filter.Length > 0 && definition.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (!groups.TryGetValue(definition.KindName, out TreeItem? group))
                {
                    group = _list.CreateItem(root);
                    group.SetText(NameColumn, definition.KindName);
                    group.SetSelectable(NameColumn, false);
                    groups[definition.KindName] = group;
                }

                TreeItem row = _list.CreateItem(group);
                row.SetText(NameColumn, definition.Name);
                row.SetMetadata(NameColumn, Location(definition));

                if (string.Equals(definition.KindName, _selectedKind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(definition.Name, _selectedName, StringComparison.OrdinalIgnoreCase))
                {
                    reselect = row;
                }
            }

            // Keep the designer's selection across a reload: they are usually editing that very card.
            reselect?.Select(NameColumn);
        }

        private static IEnumerable<EntityDefinition> Sorted(ContentLibrary content)
        {
            var definitions = new List<EntityDefinition>();
            foreach (EntityDefinition definition in content.Definitions)
            {
                if (definition.KindName == "resource") continue;
                definitions.Add(definition);
            }

            definitions.Sort((left, right) =>
            {
                int kind = string.CompareOrdinal(left.KindName, right.KindName);
                return kind != 0 ? kind : string.CompareOrdinal(left.Name, right.Name);
            });
            return definitions;
        }

        private void OnSelected()
        {
            TreeItem? selected = _list?.GetSelected();
            if (selected == null) return;

            Variant metadata = selected.GetMetadata(NameColumn);
            if (metadata.VariantType != Variant.Type.Dictionary) return;

            Godot.Collections.Dictionary location = metadata.AsGodotDictionary();
            _selectedKind = location["kind"].AsString();
            _selectedName = location["name"].AsString();
            Describe();
        }

        private void OnActivated()
        {
            TreeItem? selected = _list?.GetSelected();
            if (selected == null || _navigate == null) return;

            Variant metadata = selected.GetMetadata(NameColumn);
            if (metadata.VariantType != Variant.Type.Dictionary) return;

            Godot.Collections.Dictionary location = metadata.AsGodotDictionary();
            string file = location["file"].AsString();
            if (file.Length > 0) _navigate(file, location["line"].AsInt32(), location["column"].AsInt32());
        }

        /// <summary>Selects the first definition there is, for a headless check.</summary>
        public bool SelectFirst()
        {
            if (_list == null) return false;

            TreeItem? root = _list.GetRoot();
            TreeItem? group = root?.GetFirstChild();
            TreeItem? first = group?.GetFirstChild();
            if (first == null) return false;

            first.Select(NameColumn);
            OnSelected();
            return true;
        }

        private EntityDefinition? Selected()
        {
            if (_workspace == null || _selectedName == null) return null;
            return _selectedKind == null
                ? _workspace.Content.Find(_selectedName)
                : _workspace.Content.Find(_selectedName, _selectedKind);
        }

        private void Describe()
        {
            if (_text == null) return;

            EntityDefinition? definition = Selected();
            if (definition == null || _workspace == null)
            {
                _text.Text = string.Empty;
                if (_hash != null) _hash.Text = string.Empty;
                if (_copyHash != null) _copyHash.Disabled = true;
                return;
            }

            _text.Text = Render(_workspace.Content, definition, _live?.ButtonPressed ?? false);

            string hash = DescriptionBuilder.EffectHash(definition);
            if (_hash != null) _hash.Text = $"text_checked \"{hash}\"";
            if (_copyHash != null) _copyHash.Disabled = false;
        }

        private void OnCopyHash()
        {
            if (_hash == null || _hash.Text.Length == 0) return;
            DisplayServer.ClipboardSet(_hash.Text);
        }

        // Rendering ---------------------------------------------------------------------------------

        /// <summary>
        /// The whole preview as BBCode: the description at whichever level the definition uses, its
        /// flavour, its keyword tooltips, and an enemy's moves and current intent.
        /// </summary>
        private static string Render(ContentLibrary content, EntityDefinition definition, bool live)
        {
            var builder = new DescriptionBuilder(content);
            var text = new StringBuilder();

            CardRuntime? runtime = null;
            Entity? entity = null;
            string? note = null;

            if (live)
            {
                runtime = TryBuildGame(content, definition, out entity, out note);
            }

            Description description = entity != null && runtime != null
                ? builder.Describe(entity, runtime, Dummy(runtime))
                : builder.Describe(definition);

            text.Append("[b]").Append(Escape(description.Name)).Append("[/b]  [i]")
                .Append(Escape(definition.KindName)).Append("[/i]");
            if (description.Cost != null) text.Append("   cost ").Append(Value(description.Cost));
            text.Append('\n');

            text.Append("[color=#8d94a3]").Append(Level(description.Level));
            text.Append(entity == null ? " · printed values" : " · live values");
            text.Append("[/color]\n\n");

            text.Append(Segments(description)).Append('\n');

            if (!string.IsNullOrEmpty(description.Flavour))
            {
                text.Append("\n[i][color=#8d94a3]").Append(Escape(description.Flavour!)).Append("[/color][/i]\n");
            }

            if (description.Tooltips.Count > 0)
            {
                text.Append("\n[b]Keywords[/b]\n");
                foreach (KeywordTooltip tooltip in description.Tooltips)
                {
                    text.Append("  [b]").Append(Escape(tooltip.Name)).Append("[/b]: ")
                        .Append(Segments(tooltip.Description)).Append('\n');
                }
            }

            if (definition.Moves.Count > 0)
            {
                text.Append("\n[b]Moves[/b]\n");
                foreach (MoveDefinition move in definition.Moves)
                {
                    Description described = builder.DescribeMove(definition, move.Name, runtime, entity);
                    text.Append("  [b]").Append(Escape(move.Name)).Append("[/b]: ").Append(Segments(described)).Append('\n');
                }

                if (entity != null && runtime != null)
                {
                    Description intent = builder.DescribeIntent(entity, runtime);
                    text.Append("  [b]Intent[/b]: ")
                        .Append(intent.IsEmpty ? "[color=#8d94a3]not rolled yet[/color]" : Segments(intent))
                        .Append('\n');
                }
            }

            if (note != null) text.Append("\n[color=#e0a03c]").Append(Escape(note)).Append("[/color]\n");

            return text.ToString();
        }

        /// <summary>
        /// A one-definition game: a player, a dummy to aim at, and the definition itself brought to
        /// life the way its kind is brought to life in a real game.
        /// </summary>
        private static CardRuntime? TryBuildGame(ContentLibrary content, EntityDefinition definition, out Entity? entity, out string? note)
        {
            entity = null;
            note = null;

            try
            {
                var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 1 });
                runtime.CreatePlayer();

                Entity dummy = runtime.State.Spawn("Preview target", EntityKind.Actor, null, Team.Enemy, Zones.Board);
                dummy.SetBase("max_hp", 50);
                dummy.SetBase("hp", 50);
                dummy.SetBase("block", 0);

                runtime.StartBattle(shuffle: false, drawOpeningHand: false);

                switch (definition.Kind)
                {
                    case EntityKind.Card:
                        entity = runtime.AddCard(definition.Name, Zones.Hand);
                        break;
                    case EntityKind.Status:
                    case EntityKind.Keyword:
                        entity = runtime.ApplyStatus(definition.Name, dummy);
                        break;
                    case EntityKind.Relic:
                    case EntityKind.Item:
                        entity = runtime.AddRelic(definition.Name);
                        break;
                    case EntityKind.Ability:
                        entity = runtime.GrantAbility(definition.Name, runtime.Player!);
                        break;
                    case EntityKind.Actor:
                        entity = runtime.SpawnEnemy(definition.Name);
                        break;
                }

                if (entity == null) note = $"{definition.KindName} has no live form, so these are the printed values.";
                return runtime;
            }
            catch (Exception error) when (error is RuntimeError || error is DslException
                                          || error is ArgumentException || error is InvalidOperationException)
            {
                // Content that cannot be instantiated is exactly what a designer is looking at when
                // it is broken, so say why and fall back rather than show nothing.
                entity = null;
                note = "Live values are unavailable: " + error.Message;
                return null;
            }
        }

        private static Entity? Dummy(CardRuntime runtime)
        {
            IReadOnlyList<Entity> enemies = runtime.State.Actors(Team.Enemy);
            return enemies.Count > 0 ? enemies[0] : null;
        }

        private static string Segments(Description description)
        {
            if (description.IsEmpty) return "[color=#8d94a3](no rules text)[/color]";

            var text = new StringBuilder();
            foreach (DescriptionSegment segment in description.Segments)
            {
                if (segment.Kind == SegmentKind.Text)
                {
                    text.Append(Escape(segment.Text));
                    continue;
                }
                text.Append(Value(segment));
            }
            return text.ToString();
        }

        /// <summary>A value, with its printed number struck through when a modifier has moved it.</summary>
        private static string Value(DescriptionSegment segment)
        {
            string colour = segment.Trend switch
            {
                ValueTrend.Buffed => "#6fcf6f",
                ValueTrend.Debuffed => "#e06c6c",
                _ => "#e8c46a",
            };

            return segment.IsChanged
                ? $"[s][color=#8d94a3]{Escape(segment.BaseText)}[/color][/s] [color={colour}]{Escape(segment.Text)}[/color]"
                : $"[color={colour}]{Escape(segment.Text)}[/color]";
        }

        private static string Level(DescriptionLevel level) => level switch
        {
            DescriptionLevel.Custom => "custom text",
            DescriptionLevel.Override => "text_override",
            _ => "automatic text",
        };

        /// <summary>BBCode's only escape: a left bracket that should be read as one.</summary>
        private static string Escape(string text) => text.Replace("[", "[lb]");

        private static Godot.Collections.Dictionary Location(EntityDefinition definition)
        {
            var location = new Godot.Collections.Dictionary();
            location["kind"] = definition.KindName;
            location["name"] = definition.Name;
            location["file"] = definition.Syntax.Span.File;
            location["line"] = definition.Syntax.Span.Line;
            location["column"] = definition.Syntax.Span.Column;
            return location;
        }

        /// <summary>Describes everything that is loaded, both ways, and reports it for headless checks.</summary>
        public string SelfTest()
        {
            if (_workspace == null) return "preview: no workspace";

            int described = 0;
            int failed = 0;
            foreach (EntityDefinition definition in Sorted(_workspace.Content))
            {
                try
                {
                    Render(_workspace.Content, definition, live: false);
                    Render(_workspace.Content, definition, live: true);
                    described++;
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    failed++;
                    GD.PushError($"Cantrip: could not describe {definition}. {error.Message}");
                }
            }

            bool selected = SelectFirst();
            return $"preview: {described} described, {failed} failed, first selection {(selected ? "shown" : "empty")}";
        }
    }
}
#endif
