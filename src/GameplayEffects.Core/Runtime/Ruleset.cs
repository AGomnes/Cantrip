using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Diagnostics;
using GameplayEffects.Syntax;

namespace GameplayEffects.Runtime
{
    public enum LoopProtection
    {
        /// <summary>Only the depth cap applies.</summary>
        DepthOnly,

        /// <summary>
        /// A listener can never be re-triggered by its own consequences. Fan-out is still allowed:
        /// "whenever an enemy dies, deal 1 to all" may fire for each death in a chain reaction,
        /// but a listener whose effect re-raises its own trigger stops after one pass.
        /// </summary>
        OncePerChain,
    }

    public enum ListenerOrdering
    {
        Priority,
        PlayOrder,
        ActivePlayer,
    }

    public enum TriggerResolution
    {
        /// <summary>After-phase listeners wait until the current action finishes. Slay the Spire style.</summary>
        Queued,

        /// <summary>After-phase listeners run the moment their event is raised.</summary>
        Immediate,
    }

    /// <summary>
    /// Rules that content is written against (section 4.4). These change results, so they live in
    /// content where mod authors can see them, unlike execution speed which lives in code.
    /// </summary>
    public sealed class Ruleset
    {
        public static Ruleset Default => new Ruleset();

        public bool BeforeEvents { get; set; } = true;
        public bool InsteadEvents { get; set; } = true;
        public bool AfterEvents { get; set; } = true;

        public LoopProtection Loops { get; set; } = LoopProtection.OncePerChain;

        /// <summary>Maximum depth of a causal chain before it is cut off with a warning trace.</summary>
        public int MaxDepth { get; set; } = 50;

        public IReadOnlyList<ListenerOrdering> Ordering { get; set; } =
            new[] { ListenerOrdering.Priority, ListenerOrdering.PlayOrder, ListenerOrdering.ActivePlayer };

        public IReadOnlyList<ModifierLayer> ModifierLayers { get; set; } =
            new[] { ModifierLayer.Add, ModifierLayer.Multiply, ModifierLayer.Clamp, ModifierLayer.Override };

        public TriggerResolution Triggers { get; set; } = TriggerResolution.Queued;

        /// <summary>Cards drawn at the start of each player turn.</summary>
        public int HandSize { get; set; } = 5;

        /// <summary>Cards beyond this are discarded instead of drawn.</summary>
        public int MaxHandSize { get; set; } = 10;

        /// <summary>
        /// Interpreter steps allowed per top-level action. This is the mod sandbox's step limit: a
        /// runaway loop in downloaded content becomes an error, not a frozen game.
        /// </summary>
        public int MaxStepsPerAction { get; set; } = 100_000;

        /// <summary>Applies the settings from a <c>ruleset</c> block over the defaults.</summary>
        public static Ruleset FromSyntax(RulesetDeclNode syntax, DiagnosticBag diagnostics)
        {
            var rules = new Ruleset();
            if (syntax == null) return rules;

            foreach (PropertyNode setting in syntax.Settings)
            {
                List<string> words = setting.Values.SelectMany(Content.EntityDefinition.ReadWords).Select(w => w.ToLowerInvariant()).ToList();

                switch (setting.Name)
                {
                    case "events":
                        rules.BeforeEvents = words.Contains("before");
                        rules.InsteadEvents = words.Contains("instead") || words.Contains("instead_of");
                        rules.AfterEvents = words.Contains("after");
                        break;

                    case "loops":
                        rules.Loops = words.Contains("once_per_chain") ? LoopProtection.OncePerChain : LoopProtection.DepthOnly;
                        int? depth = NumberAfter(setting, "max_depth");
                        if (depth.HasValue) rules.MaxDepth = Math.Max(1, depth.Value);
                        break;

                    case "ordering":
                        rules.Ordering = ParseEnumList<ListenerOrdering>(setting, words, diagnostics, new Dictionary<string, ListenerOrdering>
                        {
                            ["priority"] = ListenerOrdering.Priority,
                            ["play_order"] = ListenerOrdering.PlayOrder,
                            ["active_player"] = ListenerOrdering.ActivePlayer,
                        });
                        break;

                    case "modifier_layers":
                        rules.ModifierLayers = ParseEnumList<ModifierLayer>(setting, words, diagnostics, new Dictionary<string, ModifierLayer>
                        {
                            ["add"] = ModifierLayer.Add,
                            ["multiply"] = ModifierLayer.Multiply,
                            ["clamp"] = ModifierLayer.Clamp,
                            ["override"] = ModifierLayer.Override,
                        });
                        break;

                    case "triggers":
                        rules.Triggers = words.Contains("immediate") ? TriggerResolution.Immediate : TriggerResolution.Queued;
                        break;

                    case "hand_size":
                        rules.HandSize = FirstNumber(setting) ?? rules.HandSize;
                        break;

                    case "max_hand_size":
                        rules.MaxHandSize = FirstNumber(setting) ?? rules.MaxHandSize;
                        break;

                    case "max_steps":
                        rules.MaxStepsPerAction = FirstNumber(setting) ?? rules.MaxStepsPerAction;
                        break;

                    default:
                        diagnostics.Warn(
                            "GE0201",
                            $"Unknown ruleset setting `{setting.Name}`.",
                            setting.Span,
                            Suggest.Closest(setting.Name, new[]
                            {
                                "events", "loops", "ordering", "modifier_layers", "triggers", "hand_size", "max_hand_size", "max_steps",
                            }));
                        break;
                }
            }

            return rules;
        }

        private static int? FirstNumber(PropertyNode setting) =>
            setting.Values.OfType<NumberExpr>().Select(n => (int?)n.Value.ToInt()).FirstOrDefault();

        private static int? NumberAfter(PropertyNode setting, string name)
        {
            for (int i = 0; i + 1 < setting.Values.Count; i++)
            {
                if (setting.Values[i] is NameExpr n && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) && setting.Values[i + 1] is NumberExpr number)
                    return number.Value.ToInt();
            }
            return null;
        }

        private static IReadOnlyList<T> ParseEnumList<T>(
            PropertyNode setting,
            List<string> words,
            DiagnosticBag diagnostics,
            Dictionary<string, T> names)
        {
            var result = new List<T>();
            foreach (string word in words)
            {
                if (names.TryGetValue(word, out T value))
                {
                    if (!result.Contains(value)) result.Add(value);
                }
                else
                {
                    diagnostics.Error("GE0202", $"Unknown value `{word}` for `{setting.Name}`.", setting.Span, Suggest.Closest(word, names.Keys));
                }
            }

            // Anything left out keeps its default relative position at the end, so a partial list
            // still yields a total order and results stay deterministic.
            foreach (T value in names.Values)
            {
                if (!result.Contains(value)) result.Add(value);
            }

            return result;
        }
    }
}
