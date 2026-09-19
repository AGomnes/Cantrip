using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Syntax;

namespace GameplayEffects.Runtime
{
    // Built-in verbs. The real core is small (change, move, create, destroy, apply/remove, emit);
    // everything else is a readable macro over the primitives in Interpreter.Actions.
    public sealed partial class Interpreter
    {
        private void RegisterBuiltinVerbs()
        {
            // Core verbs ------------------------------------------------------------------------
            RegisterVerb("change", VerbChange);
            RegisterVerb("move", VerbMove);
            RegisterVerb("create", VerbCreate);
            RegisterVerb("destroy", VerbDestroy);
            RegisterVerb("apply", VerbApply);
            RegisterVerb("remove", VerbRemove);
            RegisterVerb("emit", VerbEmit);

            // Macros ----------------------------------------------------------------------------
            RegisterVerb("deal", VerbDeal);
            RegisterVerb("damage", VerbDeal);
            RegisterVerb("attack", VerbAttack);
            RegisterVerb("heal", VerbHeal);
            RegisterVerb("block", VerbBlock);
            RegisterVerb("gain_block", VerbBlock);
            RegisterVerb("draw", VerbDraw);
            RegisterVerb("discard", call => VerbMoveCards(call, Zones.Discard, "discarded"));
            RegisterVerb("exhaust", call => VerbMoveCards(call, Zones.Exhaust, "exhausted"));
            RegisterVerb("shuffle", VerbShuffle);
            RegisterVerb("gain", call => VerbGainOrLose(call, AssignOperator.Add));
            RegisterVerb("lose", call => VerbGainOrLose(call, AssignOperator.Subtract));
            RegisterVerb("add", VerbAdd);
            RegisterVerb("choose", VerbChoose);
            RegisterVerb("cancel", VerbCancel);
            RegisterVerb("kill", VerbKill);
            RegisterVerb("log", call => Log(string.Join(" ", Enumerable.Range(0, call.ArgumentCount).Select(i => Show(call.Argument(i))))));
        }

        private static string Show(Value value) => value.Kind == ValueKind.Text ? value.Text! : value.ToString();

        // Core ------------------------------------------------------------------------------

        /// <summary>
        /// <c>change hp by -5 to target</c>, or the compact <c>change hp -5 on target</c>. Without a
        /// target the stat's nearest holder changes, exactly as an assignment would.
        /// </summary>
        private void VerbChange(VerbCall call)
        {
            ExprNode node = call.ArgumentNode(0) ?? throw call.Error("expected a stat name, as in `change hp by -5`.");
            ExprNode? targetNode = call.Node.Clause("to") ?? call.Node.Clause("of");
            ExprNode? amountNode = call.Node.Clause("by");
            bool negate = false;

            // The compact form parses as `(hp - 5) on target`; take the pieces back apart.
            if (node is BinaryExpr { Operator: BinaryOperator.On } on)
            {
                targetNode ??= on.Right;
                node = on.Left;
            }

            string stat;
            switch (node)
            {
                case NameExpr name:
                    stat = name.Name;
                    break;
                case BinaryExpr { Operator: BinaryOperator.Add or BinaryOperator.Subtract, Left: NameExpr name } sum when amountNode == null:
                    stat = name.Name;
                    amountNode = sum.Right;
                    negate = sum.Operator == BinaryOperator.Subtract;
                    break;
                default:
                    throw call.Error("expected a stat name, as in `change hp by -5`.");
            }

            if (amountNode == null)
            {
                amountNode = call.ArgumentNode(1) ?? throw call.Error("expected an amount.");
                if (amountNode is BinaryExpr { Operator: BinaryOperator.On } amountOn)
                {
                    targetNode ??= amountOn.Right;
                    amountNode = amountOn.Left;
                }
            }

            Num amount = EvaluateNumber(amountNode, call.Context);
            if (negate) amount = -amount;

            IReadOnlyList<Entity> targets;
            if (targetNode != null)
            {
                targets = Evaluate(targetNode, call.Context).AsEntities();
            }
            else
            {
                Entity? holder = StatHolder(stat, call.Context);
                targets = holder == null ? Array.Empty<Entity>() : new[] { holder };
            }

            foreach (Entity target in targets.ToArray()) ChangeStat(target, stat, AssignOperator.Add, amount, call.Context, call.Span);
        }

        /// <summary><c>move target to discard</c>, <c>move card to draw, top</c>.</summary>
        private void VerbMove(VerbCall call)
        {
            string zone = ZoneName(call.Node.Clause("to") ?? call.Node.Clause("into") ?? call.Node.Clause("onto"), call);
            bool top = call.Flag("top");
            foreach (Entity entity in call.Argument(0).AsEntities().ToArray())
                MoveCard(entity, zone, "moved", call.Context, top);
        }

        private static string ZoneName(ExprNode? node, VerbCall call) => node switch
        {
            NameExpr name => NormalizeZone(name.Name),
            StringExpr text => NormalizeZone(text.Value),
            null => throw call.Error("expected a destination zone, as in `to discard`."),
            _ => throw call.Error("the destination must be a zone name such as `hand`, `draw` or `discard`."),
        };

        private static string NormalizeZone(string zone) => zone.ToLowerInvariant() switch
        {
            "draw_pile" => Zones.Draw,
            "discard_pile" => Zones.Discard,
            "exhaust_pile" => Zones.Exhaust,
            var other => other,
        };

        /// <summary><c>create Shiv 2 into hand</c>, <c>create Slime</c>.</summary>
        private void VerbCreate(VerbCall call)
        {
            EntityDefinition definition = RequireDefinition(call, 0, "card", "enemy", "actor", "relic", "item");
            int count = call.Number(1, Num.One).ToInt();
            ExprNode? zoneNode = call.Node.Clause("into") ?? call.Node.Clause("to") ?? call.Node.Clause("onto");
            string? zone = zoneNode == null ? null : ZoneName(zoneNode, call);
            Entity? owner = call.Context.Controller;

            var created = new List<Entity>();
            for (int i = 0; i < count; i++) created.Add(Create(definition, owner, zone, call.Context));
            call.Context.SetLocal("created", Value.FromEntities(created));
        }

        private void VerbDestroy(VerbCall call)
        {
            IReadOnlyList<Entity> targets = call.ArgumentCount > 0 ? call.Argument(0).AsEntities() : new[] { call.Context.Self! };
            foreach (Entity entity in targets.ToArray()) Destroy(entity, call.Context);
        }

        /// <summary><c>apply Poison 3 to target</c>, <c>apply Slow 40% for 3s to enemies</c>.</summary>
        private void VerbApply(VerbCall call)
        {
            EntityDefinition definition = RequireDefinition(call, 0, "status", "keyword");
            if (definition.Kind != EntityKind.Status && definition.Kind != EntityKind.Keyword)
                throw call.Error($"`{definition.Name}` is a {definition.KindName}, not a status.");

            Num stacks = call.Number(1, Num.One);

            long? duration = null;
            if (call.Node.Clause("for") != null)
            {
                Value length = call.Clause("for");
                if (!State.Clock.TryConvert(ToNumber(length, call.Span), length.Unit, out long units))
                    throw call.Error($"`{length}` is not a duration this game's clock understands.");
                duration = units;
            }

            IReadOnlyList<Entity> targets = call.Node.Clause("to") != null
                ? call.Clause("to").AsEntities()
                : DefaultTargets(call);

            foreach (Entity target in targets.ToArray()) ApplyStatus(definition, target, stacks, duration, call.Context, call.Span);
        }

        /// <summary><c>remove Frozen from owner</c>, <c>remove tag:dot from target</c>, <c>remove self</c>.</summary>
        private void VerbRemove(VerbCall call)
        {
            Value what = call.Argument(0);
            IReadOnlyList<Entity> hosts;

            if (call.Node.Clause("from") != null)
            {
                hosts = call.Clause("from").AsEntities();
            }
            else if (what.Kind == ValueKind.Entity || what.Kind == ValueKind.List)
            {
                RemoveMatching(call.Context.Self ?? call.Context.Controller!, what, call.Context, call.Span);
                return;
            }
            else
            {
                hosts = DefaultTargets(call);
            }

            foreach (Entity host in hosts.ToArray()) RemoveMatching(host, what, call.Context, call.Span);
        }

        /// <summary><c>emit charged 2 to target</c>: raise a content-defined event.</summary>
        private void VerbEmit(VerbCall call)
        {
            string name = call.ArgumentNode(0) switch
            {
                NameExpr n => n.Name,
                StringExpr s => s.Value,
                _ => throw call.Error("expected an event name."),
            };

            var gameEvent = new GameEvent(name.ToLowerInvariant())
            {
                Source = call.Context.Source,
                Target = call.Node.Clause("to") != null ? call.Clause("to").Entity ?? call.Clause("to").AsEntities().FirstOrDefault() : call.Context.Target,
                Card = call.Context.Card,
                Amount = call.Number(1, Num.Zero),
            };
            if (call.Context.Self != null) foreach (string tag in call.Context.Self.Tags) gameEvent.Tags.Add(tag);
            Raise(gameEvent, call.Context);
        }

        // Macros ----------------------------------------------------------------------------

        /// <summary><c>deal 6 to target</c>, <c>deal stacks to owner, ignore block</c>, <c>deal 4 to all enemies as fire</c>.</summary>
        private void VerbDeal(VerbCall call)
        {
            Num amount = call.Number(0, Num.Zero);
            IReadOnlyList<Entity> targets = call.Targets("to");
            if (targets.Count == 0 && call.Node.Clause("to") == null)
                throw call.Error("no target. Write `deal N to <who>` or give the card a `target`.");

            // Damage carries the tags of whatever is dealing it: the card being played, or the
            // status or relic whose listener is running.
            var tags = new List<string>();
            if (call.Context.Self != null && call.Context.Self.Kind != EntityKind.Actor) tags.AddRange(call.Context.Self.Tags);

            ExprNode? asNode = call.Node.Clause("as");
            if (asNode != null)
            {
                switch (asNode)
                {
                    case NameExpr name: tags.Add(name.Name); break;
                    case QualifiedExpr qualified: tags.Add(qualified.Name); break;
                    case StringExpr text: tags.Add(text.Value); break;
                }
            }

            bool ignoreBlock = call.Flag("ignore_block") || call.Flag("pierce") || call.Flag("unblockable") || call.Flag("true_damage");

            Num dealt = Num.Zero;
            foreach (Entity target in targets.ToArray())
                dealt += DealDamage(call.Context.Source, target, amount, tags, ignoreBlock, call.Context, call.Span);

            BindResult(call, dealt);
        }

        /// <summary>
        /// Binds what a verb actually achieved to the name in its <c>into</c> clause, as in
        /// <c>deal 4 to all enemies into dealt</c>.
        /// </summary>
        /// <remarks>
        /// Damage, healing and block are all worth asking about after the fact, because what lands is
        /// not what was asked for: block absorbs it, modifiers change it, a target dies part way. The
        /// alternative in content was to read a history counter either side of the line and subtract,
        /// which is a lot of ceremony for "how much did that do".
        /// </remarks>
        private static void BindResult(VerbCall call, Num total)
        {
            if (call.Node.Clause("into") is not NameExpr name) return;

            call.Context.SetLocal(name.Name, Value.FromNumber(total));
        }

        /// <summary>
        /// <c>attack enemy with created.first</c>: the attacker deals its own <c>attack</c> stat, and
        /// is itself the source of that damage.
        /// </summary>
        /// <remarks>
        /// That last part is the whole point, and it is why this is built in rather than written as a
        /// content verb. Inside a content verb `deal` comes from whoever called the verb, so a
        /// creature could never be the source of its own hit and `on damaged(source:owner)` on the
        /// creature never fired. Lifelink, deathtouch and "whenever this deals damage" all need it.
        /// </remarks>
        private void VerbAttack(VerbCall call)
        {
            Entity attacker = AttackerOf(call);

            IReadOnlyList<Entity> targets = call.ArgumentNode(0) != null
                ? call.Argument(0).AsEntities()
                : call.Targets("to");

            if (targets.Count == 0)
                throw call.Error("nobody to attack. Write `attack <who>`, or give the effect a `target`.");

            Num swing = Num.FromInt(attacker.GetInt("attack"));

            // An attack carries the attacker's own tags, the way a status's damage carries its.
            var tags = new List<string>(attacker.Tags);
            switch (call.Node.Clause("as"))
            {
                case NameExpr name: tags.Add(name.Name); break;
                case QualifiedExpr qualified: tags.Add(qualified.Name); break;
                case StringExpr text: tags.Add(text.Value); break;
            }

            bool ignoreBlock = call.Flag("ignore_block") || call.Flag("pierce") || call.Flag("unblockable") || call.Flag("true_damage");

            Num landed = Num.Zero;
            foreach (Entity target in targets.ToArray())
                landed += DealDamage(attacker, target, swing, tags, ignoreBlock, call.Context, call.Span);

            BindResult(call, landed);
        }

        /// <summary>
        /// Who is swinging: whatever <c>with</c> names, else the running entity when that is an actor
        /// (a creature attacking inside its own move or listener), else its controller — so a card or
        /// relic that says <c>attack</c> swings with the player rather than with itself.
        /// </summary>
        private static Entity AttackerOf(VerbCall call)
        {
            if (call.Node.Clause("with") != null)
            {
                IReadOnlyList<Entity> named = call.Clause("with").AsEntities();
                if (named.Count == 0) throw call.Error("`with` did not name anything to attack with.");
                return named[0];
            }

            if (call.Context.Self?.Kind == EntityKind.Actor) return call.Context.Self;

            return call.Context.Controller
                ?? throw call.Error("nothing to attack with. Name one with `with <who>`.");
        }

        private void VerbHeal(VerbCall call)
        {
            Num amount = call.Number(0, Num.Zero);
            foreach (Entity target in SelfTargets(call).ToArray()) Heal(call.Context.Source, target, amount, call.Context, call.Span);
        }

        private void VerbBlock(VerbCall call)
        {
            Num amount = call.Number(0, Num.Zero);
            foreach (Entity target in SelfTargets(call).ToArray()) GainBlock(call.Context.Source, target, amount, call.Context, call.Span);
        }

        /// <summary>
        /// <c>draw 2</c>, or <c>draw 1 to player</c>.
        /// </summary>
        /// <remarks>
        /// Naming who draws is what lets a creature draw for you. A creature controls itself, so a
        /// bare `draw` inside one would draw for the creature, whose zones hold no cards, and nobody
        /// would get anything.
        /// </remarks>
        private void VerbDraw(VerbCall call)
        {
            int count = call.Number(0, Num.One).ToInt();

            if (call.Node.Clause("to") != null)
            {
                foreach (Entity who in call.Clause("to").AsEntities().ToArray()) Draw(who, count, call.Context);
                return;
            }

            Entity actor = call.Context.Controller ?? throw call.Error("nobody to draw for.");
            Draw(actor, count, call.Context);
        }

        /// <summary><c>discard 2</c> asks the chooser; <c>discard hand</c> or <c>exhaust self</c> names the cards.</summary>
        private void VerbMoveCards(VerbCall call, string zone, string eventName)
        {
            IReadOnlyList<Entity> cards;
            Value first = call.ArgumentCount > 0 ? call.Argument(0) : Value.FromEntity(call.Context.Self);

            if (first.Kind == ValueKind.Number)
            {
                Entity actor = call.Context.Controller ?? throw call.Error("nobody to choose for.");
                IReadOnlyList<Entity> hand = State.ZoneOf(actor, Zones.Hand).Where(c => c != call.Context.Card).ToList();
                int count = Math.Min(first.Number.ToInt(), hand.Count);
                cards = Choose($"{call.Verb} {count}", hand, count, count, actor, call);
            }
            else
            {
                cards = first.AsEntities();
            }

            foreach (Entity card in cards.ToArray()) MoveCard(card, zone, eventName, call.Context);
        }

        private void VerbShuffle(VerbCall call)
        {
            Entity actor = call.Context.Controller ?? throw call.Error("nobody to shuffle for.");

            if (call.ArgumentCount == 0)
            {
                ShuffleDiscardIntoDraw(actor, call.Context);
                return;
            }

            // `shuffle Wound 2 into draw` creates copies; `shuffle hand into draw` moves cards.
            Value first = call.Argument(0);
            if (first.Kind == ValueKind.Definition || first.Kind == ValueKind.Text)
            {
                EntityDefinition card = RequireDefinition(call, 0, "card");
                int count = call.Number(1, Num.One).ToInt();
                for (int i = 0; i < count; i++) Create(card, actor, Zones.Draw, call.Context);
            }
            else
            {
                foreach (Entity card in first.AsEntities().ToArray()) MoveCard(card, Zones.Draw, "moved", call.Context);
            }
            ShuffleZone(actor, Zones.Draw);
        }

        /// <summary><c>gain 1 energy</c>, <c>gain 2 Strength</c>, <c>lose 3 hp</c>.</summary>
        private void VerbGainOrLose(VerbCall call, AssignOperator op)
        {
            Num amount = call.Number(0, Num.One);
            string what = call.ArgumentNode(1) switch
            {
                NameExpr n => n.Name,
                StringExpr s => s.Value,
                _ => throw call.Error($"expected what to {call.Verb}, as in `{call.Verb} 1 energy`."),
            };

            IReadOnlyList<Entity> targets = call.Node.Clause("to") != null ? call.Clause("to").AsEntities() : SelfTargets(call);
            foreach (Entity target in targets.ToArray())
            {
                if (IsStatusName(what, target)) AdjustStatusStacks(target, what, op, amount, call.Context, call.Span);
                else ChangeStat(target, what, op, amount, call.Context, call.Span);
            }
        }

        /// <summary><c>add tag:burning to target</c>, or <c>add Weak 2 to target</c> as a synonym for apply.</summary>
        private void VerbAdd(VerbCall call)
        {
            Value first = call.Argument(0);
            if (first.Kind == ValueKind.Definition)
            {
                VerbApply(call);
                return;
            }

            string tag = first.Kind switch
            {
                ValueKind.Qualified => first.Qualified!.Name,
                ValueKind.Text => first.Text!,
                _ => throw call.Error("expected a tag, as in `add tag:burning to target`."),
            };

            IReadOnlyList<Entity> targets = call.Node.Clause("to") != null ? call.Clause("to").AsEntities() : DefaultTargets(call);
            foreach (Entity target in targets) AddTag(target, tag, call.Context);
        }

        /// <summary><c>choose 2 from hand as picked</c>. Binds the result to <c>chosen</c> unless renamed.</summary>
        private void VerbChoose(VerbCall call)
        {
            int count = call.Number(0, Num.One).ToInt();
            IReadOnlyList<Entity> options = call.Node.Clause("from") != null
                ? call.Clause("from").AsEntities()
                : throw call.Error("expected `from <group>`.");

            string name = call.Node.Clause("as") is NameExpr alias ? alias.Name : "chosen";
            Entity? chooser = call.Context.Controller;
            IReadOnlyList<Entity> chosen = Choose($"choose {count}", options, Math.Min(count, options.Count), Math.Min(count, options.Count), chooser, call);

            call.Context.SetLocal(name, chosen.Count == 1 ? Value.FromEntity(chosen[0]) : Value.FromEntities(chosen));
        }

        internal IReadOnlyList<Entity> Choose(string prompt, IReadOnlyList<Entity> options, int min, int max, Entity? chooser, VerbCall call)
        {
            if (options.Count == 0 || max <= 0) return Array.Empty<Entity>();

            var request = new ChoiceRequest(prompt, options, min, max, chooser, call.Span);
            IReadOnlyList<Entity> answer = Chooser.Choose(request, State) ?? Array.Empty<Entity>();

            // Never trust a provider blindly: keep only offered options, without duplicates, and
            // top up with the first remaining options if it answered too few.
            var valid = answer.Where(options.Contains).Distinct().Take(max).ToList();
            foreach (Entity option in options)
            {
                if (valid.Count >= min) break;
                if (!valid.Contains(option)) valid.Add(option);
            }

            State.Trace.Record(State.Clock.Now, "choice", prompt, chooser?.ToString(), span: call.Span,
                values: State.Trace.Enabled ? new Dictionary<string, object> { ["chosen"] = string.Join(", ", valid) } : null);
            return valid;
        }

        private void VerbCancel(VerbCall call)
        {
            GameEvent gameEvent = call.Context.Event ?? throw call.Error("only works inside an `on before_...` or `on instead_of_...` listener.");
            if (gameEvent.Phase == EventPhase.After) throw call.Error("cannot cancel an event that has already happened; listen to `before_` instead.");
            gameEvent.Cancelled = true;
        }

        /// <summary><c>kill lowest hp enemies</c>, <c>kill to target</c>, or bare <c>kill</c> for the effect's target.</summary>
        private void VerbKill(VerbCall call)
        {
            IReadOnlyList<Entity> targets = call.ArgumentCount > 0 ? call.Argument(0).AsEntities() : call.Targets("to");
            foreach (Entity target in targets.ToArray())
            {
                if (target.Kind == EntityKind.Actor) Kill(target, call.Context.Source, call.Context);
            }
        }

        // Helpers ---------------------------------------------------------------------------

        /// <summary>
        /// The definition an argument names. A bare name prefers the kinds the verb works with, so
        /// <c>apply Burn</c> finds the status and <c>create Burn</c> the card when both exist.
        /// </summary>
        private EntityDefinition RequireDefinition(VerbCall call, int index, params string[] preferredKinds)
        {
            string? written = call.ArgumentNode(index) switch
            {
                NameExpr name when !call.Context.TryGetLocal(name.Name, out _) => name.Name,
                StringExpr text => text.Value,
                _ => null,
            };

            if (written != null)
            {
                EntityDefinition? preferred = Content.FindAny(written, preferredKinds);
                if (preferred != null) return preferred;
            }

            return RequireDefinition(call.Argument(index), call);
        }

        private EntityDefinition RequireDefinition(Value value, VerbCall call)
        {
            switch (value.Kind)
            {
                case ValueKind.Definition:
                    return value.Definition!;
                case ValueKind.Text:
                    return Content.Find(value.Text!) ?? throw call.Error($"nothing named `{value.Text}` is defined." + SuggestionText(value.Text!, Content.AllNames));
                case ValueKind.Entity when value.Entity!.Definition != null:
                    return value.Entity!.Definition!;
                default:
                    throw call.Error($"expected the name of something defined in content, not {value}.");
            }
        }

        /// <summary>Targets for verbs aimed at others: the <c>to</c> clause, the effect's target, else the controller.</summary>
        private static IReadOnlyList<Entity> DefaultTargets(VerbCall call)
        {
            if (call.Context.Target != null) return new[] { call.Context.Target };
            Entity? fallback = call.Context.Self?.Kind == EntityKind.Status ? call.Context.Self.Owner : call.Context.Controller;
            return fallback == null ? Array.Empty<Entity>() : new[] { fallback };
        }

        /// <summary>Targets for verbs aimed at yourself (heal, block, gain): the <c>to</c> clause, else the controller.</summary>
        private static IReadOnlyList<Entity> SelfTargets(VerbCall call)
        {
            if (call.Node.Clause("to") != null) return call.Clause("to").AsEntities();
            Entity? self = call.Context.Self?.Kind == EntityKind.Status ? call.Context.Self.Owner : call.Context.Controller;
            return self == null ? Array.Empty<Entity>() : new[] { self };
        }
    }
}
