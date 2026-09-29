#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The one place plain adapter data becomes Godot data. Everything a game receives is a
    /// <see cref="Godot.Collections.Dictionary"/> or <see cref="Godot.Collections.Array"/> of
    /// primitives with snake_case keys, and every entity is an <see cref="int"/> id.
    /// </summary>
    /// <remarks>
    /// The shapes are kept here, rather than built where they are needed, because they are a public
    /// contract: a game reads <c>event["amount"]</c> and a renamed key breaks it silently. The views
    /// this maps from live in <c>shared/</c> and are tested without an engine; this file only turns
    /// them into Variants.
    /// </remarks>
    public static class VariantMap
    {
        /// <summary>Entity ids cross as ints, and nothing is id 0, so 0 reads as "none".</summary>
        public const int NoEntity = 0;

        // Events ---------------------------------------------------------------------------------

        /// <summary>
        /// One resolved event. <c>amount</c> is the whole number a UI prints; <c>amount_raw</c> is
        /// the exact fixed-point value for anything that must stay bit-exact, because rounding a
        /// number and calling it the truth is how a replay drifts from the game it replays.
        /// </summary>
        public static Godot.Collections.Dictionary Event(EventRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var values = new Godot.Collections.Dictionary();
            foreach (KeyValuePair<string, Value> entry in record.Values) values[entry.Key] = ToVariant(entry.Value);

            var after = new Godot.Collections.Dictionary();
            foreach (KeyValuePair<int, IReadOnlyDictionary<string, int>> entity in record.After)
            {
                var stats = new Godot.Collections.Dictionary();
                foreach (KeyValuePair<string, int> stat in entity.Value) stats[stat.Key] = stat.Value;
                after[entity.Key] = stats;
            }

            return new Godot.Collections.Dictionary
            {
                ["seq"] = record.Sequence,
                ["name"] = record.Name,
                ["phase"] = record.PhaseName,
                ["time"] = record.Time,
                ["source"] = record.Source,
                ["target"] = record.Target,
                ["card"] = record.Card,
                ["amount"] = record.AmountInt,
                ["amount_raw"] = record.AmountRaw,
                ["replaced"] = record.Replaced,
                ["tags"] = Strings(record.Tags),
                ["values"] = values,
                ["after"] = after,
            };
        }

        // Entities -------------------------------------------------------------------------------

        public static Godot.Collections.Dictionary Entity(EntityView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));

            var stats = new Godot.Collections.Dictionary();
            foreach (KeyValuePair<string, int> stat in view.Stats) stats[stat.Key] = stat.Value;

            var statuses = new Godot.Collections.Array();
            for (int i = 0; i < view.Statuses.Count; i++) statuses.Add(Status(view.Statuses[i]));

            return new Godot.Collections.Dictionary
            {
                ["id"] = view.Id,
                ["name"] = view.Name,
                ["kind"] = view.Kind,
                ["team"] = view.Team,
                ["zone"] = view.Zone,
                ["position"] = view.Position,
                ["lane"] = view.Lane,
                ["rank"] = view.Rank,
                ["alive"] = view.Alive,
                ["dead"] = view.Dead,
                ["removed"] = view.Removed,
                ["intent"] = view.Intent,
                ["owner"] = view.Owner,
                ["source"] = view.Source,
                ["party_member"] = view.PartyMember,
                ["acted"] = view.Acted,
                ["tags"] = Strings(view.Tags),
                ["stats"] = stats,
                ["statuses"] = statuses,
                ["abilities"] = Ids(view.Abilities),
            };
        }

        public static Godot.Collections.Dictionary Status(StatusView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));

            return new Godot.Collections.Dictionary
            {
                ["id"] = view.Id,
                ["name"] = view.Name,
                ["counter"] = view.Counter,
                ["stacks"] = view.Stacks,
                ["duration"] = view.Duration,
                ["hidden"] = view.Hidden,
                ["tags"] = Strings(view.Tags),
            };
        }

        // Descriptions ---------------------------------------------------------------------------

        /// <param name="targetId">
        /// Who the description is aimed at, for an intent. <see cref="NoEntity"/> everywhere else,
        /// so every description dictionary has the same keys whatever made it.
        /// </param>
        public static Godot.Collections.Dictionary Description(DescriptionView view, int targetId = NoEntity)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));

            var segments = new Godot.Collections.Array();
            for (int i = 0; i < view.Segments.Count; i++) segments.Add(Segment(view.Segments[i]));

            var tooltips = new Godot.Collections.Array();
            for (int i = 0; i < view.Tooltips.Count; i++)
            {
                tooltips.Add(new Godot.Collections.Dictionary
                {
                    ["name"] = view.Tooltips[i].Name,
                    ["plain"] = view.Tooltips[i].Plain,
                    ["bbcode"] = view.Tooltips[i].BBCode,
                });
            }

            // "cost" is always here, null for an entity that has none. It used to be left out, so a
            // reader who never thought to call has() read a missing key as an empty dictionary.
            var description = new Godot.Collections.Dictionary
            {
                ["cost"] = view.Cost == null ? default(Variant) : Segment(view.Cost),
                ["name"] = view.Name,
                ["target"] = targetId,
                ["target_name"] = view.Against,
                ["line"] = view.Line,
                ["level"] = view.Level,
                ["plain"] = view.Plain,
                ["bbcode"] = view.BBCode,
                ["flavour"] = view.Flavour,
                ["empty"] = view.IsEmpty,
                ["segments"] = segments,
                ["tooltips"] = tooltips,
            };

            return description;
        }

        public static Godot.Collections.Dictionary Segment(SegmentView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));

            return new Godot.Collections.Dictionary
            {
                ["kind"] = view.Kind,
                ["text"] = view.Text,
                ["placeholder"] = view.Placeholder,
                ["has_number"] = view.HasNumber,
                ["base"] = view.Base,
                ["current"] = view.Current,
                ["base_text"] = view.BaseText,
                ["changed"] = view.Changed,
                ["trend"] = view.Trend,
                ["lower_is_better"] = view.LowerIsBetter,
            };
        }

        // Diagnostics ----------------------------------------------------------------------------

        /// <summary>
        /// One problem, with its location kept as the <c>res://</c> path the editor can open. A
        /// diagnostic a designer cannot click through to is barely a diagnostic.
        /// </summary>
        public static Godot.Collections.Dictionary Diagnostic(Diagnostic diagnostic)
        {
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));

            return new Godot.Collections.Dictionary
            {
                ["severity"] = Words.SeverityName(diagnostic.Severity),
                ["code"] = diagnostic.Code ?? string.Empty,
                ["message"] = diagnostic.Message ?? string.Empty,
                ["suggestion"] = diagnostic.Suggestion ?? string.Empty,
                ["file"] = diagnostic.Span.File ?? string.Empty,
                ["line"] = diagnostic.Span.Line,
                ["column"] = diagnostic.Span.Column,
            };
        }

        public static Godot.Collections.Array Diagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            var array = new Godot.Collections.Array();
            if (diagnostics == null) return array;

            foreach (Diagnostic diagnostic in diagnostics) array.Add(Diagnostic(diagnostic));
            return array;
        }

        /// <summary>
        /// What loading or reloading content came to: <c>ok</c>, the number of <c>errors</c> and
        /// <c>warnings</c>, and the <c>diagnostics</c> themselves.
        /// </summary>
        /// <remarks>
        /// <c>ok</c> is here because every caller of a bare diagnostics array had to write the same
        /// scan for a severity of "error" before it knew whether its game had content to play, and
        /// the addon's own debugger channel already answered the question with a flag.
        /// </remarks>
        public static Godot.Collections.Dictionary ContentReport(IEnumerable<Diagnostic> diagnostics)
        {
            int errors = 0;
            int warnings = 0;
            var array = new Godot.Collections.Array();
            if (diagnostics != null)
            {
                foreach (Diagnostic diagnostic in diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error) errors++;
                    else if (diagnostic.Severity == DiagnosticSeverity.Warning) warnings++;
                    array.Add(Diagnostic(diagnostic));
                }
            }

            return new Godot.Collections.Dictionary
            {
                ["ok"] = errors == 0,
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["diagnostics"] = array,
            };
        }

        // Refusals -------------------------------------------------------------------------------

        /// <summary>
        /// The shape every refusal crosses in: <c>accepted</c> false, a snake_case <c>reason</c> and
        /// a <c>message</c> to show. A refusing call that also answers something when it succeeds
        /// passes that key and its empty value, so the key is never simply absent.
        /// </summary>
        public static Godot.Collections.Dictionary Refused(string reason, string message, string alsoKey = "", Variant alsoValue = default)
        {
            var refusal = new Godot.Collections.Dictionary
            {
                ["accepted"] = false,
                ["reason"] = reason,
                ["message"] = message,
            };

            if (!string.IsNullOrEmpty(alsoKey)) refusal[alsoKey] = alsoValue;
            return refusal;
        }

        // Zones ----------------------------------------------------------------------------------

        /// <summary>
        /// Says so in the Output panel when a zone is not one the rules know.
        /// </summary>
        /// <remarks>
        /// A warning and not a refusal: the core lets a game invent zones of its own, and that is
        /// deliberate. But nothing catches a typo either — <c>AddCard("Guard", "hnd")</c> makes a
        /// real card in a pile nothing will ever draw from — and a warning is the only thing that
        /// tells the two apart without taking the ability away.
        /// </remarks>
        public static void WarnUnknownZone(string zone, string calledFrom)
        {
            if (Zones.IsWellKnown(zone)) return;

            GD.PushWarning(
                "Cantrip: " + calledFrom + " was given the zone \"" + zone + "\", which the rules do not know. "
                + "A zone of your own works, but a misspelt one is a pile nothing will ever draw from. The known zones are "
                + KnownZones + ".");
        }

        /// <summary>The well-known zones for a message; the empty one is named rather than shown.</summary>
        private static readonly string KnownZones = BuildKnownZones();

        private static string BuildKnownZones()
        {
            var named = new List<string>();
            foreach (string zone in Zones.WellKnown)
            {
                if (zone.Length > 0) named.Add(zone);
            }
            return string.Join(", ", named) + ", and \"\" for none";
        }

        // Choices --------------------------------------------------------------------------------

        /// <summary>
        /// A decision the rules are waiting on. The options are entity ids and views both: a UI
        /// needs the names to show, and the ids to answer with. An offer of content that does not
        /// exist yet, as <c>discover</c> makes, has <c>mode</c> "offer": its <c>option_ids</c> are
        /// the numbers 1, 2, 3... and each option is the candidate's name, kind, tags and rules
        /// text, which <paramref name="describe"/> supplies.
        /// </summary>
        /// <remarks>
        /// The request says <c>mode</c> rather than <c>kind</c> because <c>kind</c> already means
        /// three other things inside this one dictionary — what an entity is, what keyword declared
        /// an offered definition, and whether a segment is text or a value — and the vocabulary of
        /// <c>options[i]["kind"]</c> changes with it.
        /// </remarks>
        public static Godot.Collections.Dictionary Choice(
            int requestId,
            PendingChoice choice,
            IReadOnlyList<string>? stats = null,
            Func<Cantrip.Content.EntityDefinition, string>? describe = null)
        {
            if (choice == null) throw new ArgumentNullException(nameof(choice));

            var options = new Godot.Collections.Array();
            var ids = new Godot.Collections.Array();
            if (choice.IsOffer)
            {
                for (int i = 0; i < choice.Definitions.Count; i++)
                {
                    Cantrip.Content.EntityDefinition offered = choice.Definitions[i];
                    options.Add(new Godot.Collections.Dictionary
                    {
                        ["name"] = offered.Name,
                        ["kind"] = offered.KindName,
                        ["tags"] = Strings(offered.Tags),
                        ["text"] = describe?.Invoke(offered) ?? string.Empty,
                    });
                    ids.Add(ChoiceBridge.OfferId(i));
                }
            }
            else
            {
                for (int i = 0; i < choice.Options.Count; i++)
                {
                    options.Add(Entity(EntityView.Of(choice.Options[i], stats)));
                    ids.Add(choice.Options[i].Id);
                }
            }

            return new Godot.Collections.Dictionary
            {
                ["id"] = requestId,
                ["mode"] = choice.IsOffer ? "offer" : "entities",
                ["prompt"] = choice.Prompt,
                ["min"] = choice.Min,
                ["max"] = choice.Max,
                ["chooser"] = choice.Chooser?.Id ?? NoEntity,
                ["option_ids"] = ids,
                ["options"] = options,
                ["file"] = choice.Span.File ?? string.Empty,
                ["line"] = choice.Span.Line,
            };
        }

        // Values ---------------------------------------------------------------------------------

        /// <summary>
        /// A rules value as a Variant. Entities and lists of entities become ids, because an id is
        /// the only reference that survives a snapshot restore.
        /// </summary>
        public static Variant ToVariant(Value value)
        {
            switch (value.Kind)
            {
                case ValueKind.Number:
                    return value.Number.ToDouble();
                case ValueKind.Bool:
                    return !value.Number.IsZero;
                case ValueKind.Text:
                    return value.Text ?? string.Empty;
                case ValueKind.Entity:
                    return value.Entity?.Id ?? NoEntity;
                case ValueKind.List:
                {
                    var ids = new Godot.Collections.Array();
                    foreach (Entity entity in value.AsEntities()) ids.Add(entity.Id);
                    return ids;
                }
                case ValueKind.Definition:
                    return value.Definition?.Name ?? string.Empty;
                case ValueKind.Qualified:
                    return value.Qualified == null ? string.Empty : value.Qualified.Qualifier + ":" + value.Qualified.Name;
                case ValueKind.Range:
                    return new Godot.Collections.Array { value.Number.ToDouble(), value.RangeHigh.ToDouble() };
                default:
                    return default;
            }
        }

        /// <summary>
        /// A Variant back to a rules value, for a game answering <c>within(...)</c> or a custom
        /// name. An array can only mean entities: the rules have no list-of-numbers value, so
        /// <c>[3, 7]</c> is unambiguous and a bare number is always a number.
        /// </summary>
        public static Value ToValue(Variant variant, GameState? state)
        {
            switch (variant.VariantType)
            {
                case Variant.Type.Nil:
                    return Value.None;
                case Variant.Type.Bool:
                    return Value.FromBool(variant.AsBool());
                case Variant.Type.Int:
                    return Value.FromNumber(Num.FromInt(variant.AsInt32()));
                case Variant.Type.Float:
                    // Num is fixed point with six decimals. Rounding rather than truncating is what
                    // keeps 0.7 from script arriving as 0.699999.
                    return Value.FromNumber(Num.FromRaw((long)Math.Round(variant.AsDouble() * Num.Scale)));
                case Variant.Type.String:
                case Variant.Type.StringName:
                    return Value.FromText(variant.AsString());
                case Variant.Type.Array:
                case Variant.Type.PackedInt32Array:
                case Variant.Type.PackedInt64Array:
                    return Entities(variant, state);
                default:
                    return Value.None;
            }
        }

        private static Value Entities(Variant variant, GameState? state)
        {
            var entities = new List<Entity>();
            if (state == null) return Value.FromEntities(entities);

            foreach (Variant item in variant.AsGodotArray())
            {
                Entity? entity = state.Find(item.AsInt32());
                if (entity != null) entities.Add(entity);
            }
            return Value.FromEntities(entities);
        }

        // Lists ----------------------------------------------------------------------------------

        public static Godot.Collections.Array Ids(IEnumerable<int> ids)
        {
            var array = new Godot.Collections.Array();
            if (ids == null) return array;

            foreach (int id in ids) array.Add(id);
            return array;
        }

        public static Godot.Collections.Array Ids(IEnumerable<Entity> entities)
        {
            var array = new Godot.Collections.Array();
            if (entities == null) return array;

            foreach (Entity entity in entities) array.Add(entity.Id);
            return array;
        }

        /// <summary>Reads entity ids a game passed in, ignoring anything that is not a number.</summary>
        public static int[] ToIds(Godot.Collections.Array? ids)
        {
            if (ids == null || ids.Count == 0) return new int[0];

            var result = new List<int>(ids.Count);
            foreach (Variant id in ids)
            {
                if (id.VariantType == Variant.Type.Int || id.VariantType == Variant.Type.Float) result.Add(id.AsInt32());
            }
            return result.ToArray();
        }

        public static Godot.Collections.Array Strings(IEnumerable<string> values)
        {
            var array = new Godot.Collections.Array();
            if (values == null) return array;

            foreach (string value in values) array.Add(value);
            return array;
        }

        public static string[] ToStrings(Godot.Collections.Array? values)
        {
            if (values == null || values.Count == 0) return new string[0];

            var result = new List<string>(values.Count);
            foreach (Variant value in values) result.Add(value.AsString());
            return result.ToArray();
        }

        /// <summary>
        /// The marshal the host uses for game-supplied names and functions, so the addon converts
        /// values in exactly one way.
        /// </summary>
        public sealed class Marshal : IValueMarshal
        {
            public Marshal(GameState? state = null) => State = state;

            /// <summary>Needed to turn ids back into entities; without it an id list reads as empty.</summary>
            public GameState? State { get; set; }

            public Variant ToVariant(Value value) => VariantMap.ToVariant(value);

            public Value ToValue(Variant variant, GameState? state) => VariantMap.ToValue(variant, state ?? State);
        }
    }
}
