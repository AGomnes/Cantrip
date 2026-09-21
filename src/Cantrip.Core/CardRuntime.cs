using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;

namespace Cantrip
{
    public enum PlayResult
    {
        Played,
        NotACard,
        NotInHand,
        Unplayable,
        NotEnoughEnergy,
        InvalidTarget,
        Cancelled,

        /// <summary>
        /// The action needs a decision from the player. The game has been rolled back to where it
        /// was; see <see cref="CardRuntime.Pending"/> and answer with <see cref="CardRuntime.Answer(int[])"/>.
        /// </summary>
        ChoicePending,
    }

    public sealed class RuntimeOptions
    {
        public ulong Seed { get; set; } = 1;

        /// <summary>Defaults to a <see cref="TurnClock"/>. Pass a <see cref="TickClock"/> for real-time games.</summary>
        public IGameClock? Clock { get; set; }

        public IEffectHost? Host { get; set; }
        public IChoiceProvider? Chooser { get; set; }

        /// <summary>Overrides the ruleset declared in content.</summary>
        public Ruleset? Rules { get; set; }

        public bool Trace { get; set; }

        public ExecutionMode Execution { get; set; } = ExecutionMode.Headless;
    }

    /// <summary>
    /// The entry point for games: load content, set up actors and decks, then drive
    /// battles through <see cref="StartBattle"/>, <see cref="Play(Entity, Entity)"/> and <see cref="EndTurn"/>, or
    /// real-time play through <see cref="Tick"/> and <see cref="UseAbility"/>.
    /// </summary>
    public sealed class CardRuntime
    {
        public CardRuntime(ContentLibrary content, RuntimeOptions? options = null)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            options ??= new RuntimeOptions();

            Ruleset rules = options.Rules ?? content.BuildRuleset();
            IGameClock clock = options.Clock ?? new TurnClock();

            State = new GameState(content, rules, clock, options.Seed);
            State.Trace.Enabled = options.Trace;
            Interpreter = new Interpreter(State, options.Host);
            if (options.Chooser != null) Interpreter.Chooser = options.Chooser;
            Execution = options.Execution;

            RegisterRuntimeVerbs();
            clock.Advanced += OnClockAdvanced;
        }

        /// <summary>Loads content from text and throws if it has any errors.</summary>
        public static CardRuntime FromText(string text, RuntimeOptions? options = null)
        {
            ContentLibrary content = ContentLibrary.FromText(text);
            content.Diagnostics.ThrowIfErrors();
            return new CardRuntime(content, options);
        }

        public ContentLibrary Content { get; }
        public GameState State { get; }
        public Interpreter Interpreter { get; }
        public ExecutionMode Execution { get; set; }

        public IChoiceProvider Chooser
        {
            get => Interpreter.Chooser;
            set => Interpreter.Chooser = value ?? new FirstOptionChooser();
        }

        public Entity? Player => State.Player;

        /// <summary>Null while a battle is running or before the first one; then whether the player won.</summary>
        public bool? Won { get; private set; }

        public void RegisterVerb(string name, VerbHandler handler) => Interpreter.RegisterVerb(name, handler);

        // Setup --------------------------------------------------------------------------------

        public Entity CreatePlayer(string name = "Player", int hp = 80, int maxEnergy = 3)
        {
            if (State.Player != null) throw new InvalidOperationException("A player already exists.");

            Entity player = State.Spawn(name, EntityKind.Actor, null, Team.Player, Zones.Board);
            player.SetBase("max_hp", hp);
            player.SetBase("hp", hp);
            player.SetBase("block", 0);
            player.SetBase("max_energy", maxEnergy);
            player.SetBase("energy", maxEnergy);
            State.Player = player;
            return player;
        }

        public Entity AddCard(string name, string zone = Zones.Draw, Entity? owner = null)
        {
            EntityDefinition definition = Require(name, "card");
            owner ??= State.Player ?? throw new InvalidOperationException("Create a player before adding cards.");
            return State.Instantiate(definition, owner, Team.Neutral, zone);
        }

        public IReadOnlyList<Entity> AddDeck(params string[] names) => names.Select(n => AddCard(n)).ToList();

        public Entity AddRelic(string name, Entity? owner = null)
        {
            EntityDefinition definition = Content.Find(name, "relic") ?? Require(name, "item");
            owner ??= State.Player ?? throw new InvalidOperationException("Create a player before adding relics.");
            Entity relic = State.Instantiate(definition, owner, Team.Neutral, Zones.Relics);

            Run(owner, context => Interpreter.Raise(new GameEvent("obtained") { Source = owner, Target = relic }, context));
            return relic;
        }

        public Entity SpawnEnemy(string name, int? hp = null)
        {
            EntityDefinition definition = Content.Find(name, "enemy") ?? Require(name, "actor");
            Entity enemy = State.Instantiate(definition, null, Team.Enemy, Zones.Board);

            if (hp.HasValue)
            {
                enemy.SetBase("max_hp", hp.Value);
                enemy.SetBase("hp", hp.Value);
            }
            if (!enemy.HasStat("block")) enemy.SetBase("block", 0);

            if (State.InBattle) RollIntent(enemy);
            return enemy;
        }

        /// <summary>
        /// Applies a status from game code. <paramref name="source"/> defaults to the player, which
        /// matters for <c>flags unique</c> statuses and for <c>source:</c> filters.
        /// </summary>
        public Entity? ApplyStatus(string status, Entity target, int stacks = 1, Entity? source = null)
        {
            source ??= State.Player;
            EntityDefinition definition = Content.FindAny(status, "status", "keyword") ?? Require(status, "status");
            Entity? result = null;
            Run(source, context => result = Interpreter.ApplyStatus(definition, target, stacks, null, context));
            return result;
        }

        /// <summary>Gives an actor an ability (real-time games).</summary>
        public Entity GrantAbility(string name, Entity owner)
        {
            EntityDefinition definition = Require(name, "ability");
            Entity ability = State.Instantiate(definition, owner);
            State.Attach(owner, ability);
            return ability;
        }

        private EntityDefinition Require(string name, string kind)
        {
            EntityDefinition? definition = Content.Find(name, kind);
            if (definition != null) return definition;

            string? suggestion = Suggest.Closest(name, Content.Definitions.Where(d => d.KindName == kind).Select(d => d.Name));
            throw new ArgumentException(
                $"No {kind} named \"{name}\" is loaded." + (suggestion == null ? string.Empty : $" Did you mean \"{suggestion}\"?"),
                nameof(name));
        }

        // Battle flow --------------------------------------------------------------------------

        private bool _skipNextDraw;

        /// <param name="shuffle">Shuffle the draw pile first. Tests turn this off to control draw order.</param>
        /// <param name="drawOpeningHand">Draw the first hand. Tests turn this off to set the hand explicitly.</param>
        public void StartBattle(bool shuffle = true, bool drawOpeningHand = true) =>
            Attempt(
                () => { StartBattleCore(shuffle, drawOpeningHand); return PlayResult.Played; },
                () => { StartBattle(shuffle, drawOpeningHand); return Outcome(); });

        private void StartBattleCore(bool shuffle, bool drawOpeningHand)
        {
            Entity player = State.Player ?? throw new InvalidOperationException("Create a player before starting a battle.");

            State.ResetBattleHistory();
            State.BattleNumber++;
            State.Turn = 0;
            State.InBattle = true;
            Won = null;
            _skipNextDraw = !drawOpeningHand;

            // Enemies that died in an earlier battle are done with. Leaving them in would make every
            // later battle count as already having had enemies, and so end before its own arrive.
            foreach (Entity fallen in State.Entities.Where(e => e.Kind == EntityKind.Actor && e.Team == Team.Enemy && e.IsDead && !e.IsRemoved).ToArray())
                State.Remove(fallen);

            if (!player.HasStat("block")) player.SetBase("block", 0);
            if (shuffle) Interpreter.ShuffleZone(player, Zones.Draw);
            foreach (Entity enemy in State.Actors(Team.Enemy)) RollIntent(enemy);

            Run(player, context => Interpreter.Raise(new GameEvent("battle_start") { Source = player }, context));
            if (CheckBattleOver()) return;

            StartTurn(Team.Player);
        }

        /// <summary>Ends the player's turn, runs the enemies' turn, and starts the next player turn.</summary>
        public void EndTurn() =>
            Attempt(
                () => { EndTurnCore(); return PlayResult.Played; },
                () => { EndTurn(); return Outcome(); });

        private void EndTurnCore()
        {
            if (!State.InBattle) return;
            if (State.ActiveTeam != Team.Player) throw new InvalidOperationException("It is not the player's turn.");

            if (EndTurnFor(Team.Player)) return;
            DiscardHand(State.Player!);

            StartTurn(Team.Enemy);
            if (!State.InBattle) return;

            foreach (Entity enemy in State.Actors(Team.Enemy).ToArray())
            {
                RunEnemyMove(enemy);
                if (!State.InBattle) return;
            }

            if (EndTurnFor(Team.Enemy)) return;
            foreach (Entity enemy in State.Actors(Team.Enemy)) RollIntent(enemy);

            StartTurn(Team.Player);
        }

        private void StartTurn(Team team)
        {
            State.ActiveTeam = team;

            if (team == Team.Player)
            {
                State.Turn++;
                State.ResetTurnHistory();
            }

            foreach (Entity actor in State.Actors(team).ToArray())
            {
                if (!actor.IsAlive) continue;

                // Only work scheduled before this turn began runs now; `next turn:` written during
                // this turn start belongs to the following turn.
                var due = State.Scheduled.Where(s => s.Timing == ScheduleTiming.NextTurn && s.Owner == actor).ToList();

                // Resources that reset and statuses that decay on turn_start are handled by the
                // event itself (reset_on / decay ... on), so they work for any event, not just turns.
                Run(actor, context => Interpreter.Raise(new GameEvent("turn_start") { Source = actor, Target = actor }, context));

                foreach (ScheduledAction action in due)
                {
                    State.Unschedule(action);
                    Run(actor, _ => Interpreter.RunScheduled(action));
                }

                if (CheckBattleOver()) return;
            }

            // Time moves once every actor has started its turn, so work scheduled for this turn
            // (`in 1 turn: gain 1 energy`) runs after the turn-start resets instead of being wiped.
            if (team == Team.Player && State.Turn > 1 && State.Clock is TurnClock turns)
            {
                turns.AdvanceTurn();
                if (!State.InBattle) return;
            }

            if (team == Team.Player && State.Player != null && State.Player.IsAlive)
            {
                if (_skipNextDraw) _skipNextDraw = false;
                else Run(State.Player, context => Interpreter.Draw(State.Player, State.Rules.HandSize, context));
                CheckBattleOver();
            }
        }

        /// <summary>Returns true if the battle ended during the turn end.</summary>
        private bool EndTurnFor(Team team)
        {
            foreach (Entity actor in State.Actors(team).ToArray())
            {
                if (!actor.IsAlive) continue;

                Run(actor, context => Interpreter.Raise(new GameEvent("turn_end") { Source = actor, Target = actor }, context));

                if (CheckBattleOver()) return true;
            }
            return false;
        }

        /// <summary>End-of-turn discard. Retained cards stay; ethereal cards exhaust.</summary>
        private void DiscardHand(Entity player)
        {
            Run(player, context =>
            {
                foreach (Entity card in State.ZoneOf(player, Zones.Hand).ToArray())
                {
                    if (card.HasTag("retain") || card.FindAttached("Retain") != null) continue;
                    if (card.HasTag("ethereal") || card.FindAttached("Ethereal") != null)
                        Interpreter.MoveCard(card, Zones.Exhaust, "exhausted", context);
                    else
                        State.MoveTo(card, Zones.Discard);
                }
            });
        }

        /// <summary>Ends the battle if one side is gone. Returns true if no battle is running afterwards.</summary>
        internal bool CheckBattleOver()
        {
            if (!State.InBattle) return true;

            if (State.Player != null && !State.Player.IsAlive)
            {
                EndBattle(won: false);
                return true;
            }

            bool hadEnemies = State.Entities.Any(e => e.Kind == EntityKind.Actor && e.Team == Team.Enemy && !e.IsRemoved);
            if (hadEnemies && State.Actors(Team.Enemy).Count == 0)
            {
                EndBattle(won: true);
                return true;
            }

            return false;
        }

        private void EndBattle(bool won)
        {
            State.InBattle = false;
            Won = won;
            Entity? player = State.Player;

            Run(player, context =>
            {
                var gameEvent = new GameEvent("battle_end") { Source = player, Target = player };
                gameEvent.Data["won"] = Value.FromBool(won);
                Interpreter.Raise(gameEvent, context);
            });

            // Temporary effects end with the battle; statuses flagged persistent carry over. The
            // reverts raise events of their own, so they run inside an action and drain with it.
            Run(player, _ =>
            {
                foreach (ScheduledAction action in State.Scheduled.ToArray())
                {
                    State.Unschedule(action);
                    if (action.Timing == ScheduleTiming.Until) Interpreter.Revert(action);
                }
            });

            if (player == null) return;

            foreach (Entity status in player.Attached.ToArray())
            {
                if (status.Definition != null && (status.Definition.Flags & StatusFlags.Persistent) != 0) continue;
                State.Remove(status);
            }

            foreach (string zone in new[] { Zones.Hand, Zones.Discard, Zones.Exhaust, Zones.Play, Zones.Powers })
            {
                foreach (Entity card in State.ZoneOf(player, zone).ToArray()) State.MoveTo(card, Zones.Draw);
            }
        }

        // Cards --------------------------------------------------------------------------------

        public bool IsXCost(Entity card) =>
            card.Definition?.Property("cost")?.First is NameExpr { Name: var name } && string.Equals(name, "x", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The resource a card's cost is paid in: <c>energy</c>, or whatever its <c>cost</c> names.
        /// </summary>
        public string CostResourceOf(Entity card) => card.Definition?.CostResource ?? "energy";

        /// <summary>
        /// The card's current cost after modifiers. An X cost spends everything the payer has of the
        /// resource the card is priced in.
        /// </summary>
        public int CostOf(Entity card)
        {
            if (IsXCost(card)) return card.Controller.GetInt(CostResourceOf(card));

            // From the base cost: card.Get("cost") would already have run the cost channel once.
            var query = new ModifierQuery("cost") { Subject = card, Source = card.Controller, Card = card, Tags = card.Tags.ToArray() };
            Num cost = State.Modifiers.Compute(query, card.GetBase("cost"));
            return Math.Max(0, cost.Floor().ToInt());
        }

        /// <summary>Plays a card from hand: checks energy and target, pays, resolves, and drains triggers.</summary>
        public PlayResult Play(Entity card, Entity? target = null)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            int cardId = card.Id;
            int targetId = target?.Id ?? 0;
            return Attempt(
                () => PlayCore(card, target),
                () => Live(cardId) is Entity again ? Play(again, Live(targetId)) : PlayResult.NotACard);
        }

        private PlayResult PlayCore(Entity card, Entity? target)
        {
            if (card.Kind != EntityKind.Card || card.IsRemoved) return PlayResult.NotACard;
            if (card.Zone != Zones.Hand) return PlayResult.NotInHand;
            if (card.HasTag("unplayable")) return PlayResult.Unplayable;

            Entity player = card.Controller;
            int cost = CostOf(card);
            // A card priced in something else is refused the same way, so NotEnoughEnergy now means
            // "not enough of whatever this costs".
            string resource = CostResourceOf(card);
            if (!IsXCost(card) && player.GetInt(resource) < cost) return PlayResult.NotEnoughEnergy;

            if (!TryResolveTarget(card, ref target)) return PlayResult.InvalidTarget;

            if (_runDepth == 0) Interpreter.ResetSteps();
            var context = new EvalContext(card) { Source = player, Target = target, Card = card, Chain = Interpreter.NewChain() };
            if (IsXCost(card)) context.SetLocal("x", Value.FromNumber(Num.FromInt(cost)));

            var gameEvent = new GameEvent("card_played") { Source = player, Target = target, Card = card, Amount = cost };
            foreach (string tag in card.Tags) gameEvent.Tags.Add(tag);

            _runDepth++;
            try
            {
                Interpreter.Raise(
                    gameEvent,
                    context,
                    action: () =>
                    {
                        BlockNode? effect = card.Definition?.Effect;
                        if (effect != null) Interpreter.Execute(effect, context);
                    },
                    committed: () =>
                    {
                        if (cost > 0) Interpreter.ChangeStat(player, resource, AssignOperator.Subtract, cost, context);
                        State.MoveTo(card, Zones.Play);
                        State.RecordHistory("cards_played", player, Num.One);
                        if (card.HasTag("attack")) State.RecordHistory("attacks", player, Num.One);
                    });

                if (gameEvent.Cancelled && card.Zone == Zones.Hand)
                {
                    Interpreter.Drain();
                    return PlayResult.Cancelled;
                }

                // The effect may already have moved the card (exhausted it, shuffled it away).
                if (card.Zone == Zones.Play && !card.IsRemoved)
                {
                    if (card.HasTag("exhaust") || card.FindAttached("Exhaust") != null)
                        Interpreter.MoveCard(card, Zones.Exhaust, "exhausted", context);
                    else if (card.HasTag("power"))
                        State.MoveTo(card, Zones.Powers);
                    else
                        State.MoveTo(card, Zones.Discard);
                }

                Interpreter.Drain();
            }
            catch
            {
                Interpreter.AbandonPending();
                throw;
            }
            finally
            {
                _runDepth--;
            }

            CheckBattleOver();
            return PlayResult.Played;
        }

        public PlayResult Play(string cardName, Entity? target = null)
        {
            Entity player = State.Player ?? throw new InvalidOperationException("There is no player.");
            Entity? card = State.ZoneOf(player, Zones.Hand).FirstOrDefault(c => string.Equals(c.Name, cardName, StringComparison.OrdinalIgnoreCase));
            return card == null ? PlayResult.NotInHand : Play(card, target);
        }

        /// <summary>
        /// What the card asks to be aimed at: <c>enemy</c>, <c>ally</c>, <c>self</c>, <c>any</c>,
        /// or <c>none</c> for a card with no <c>target</c> line.
        /// </summary>
        public string TargetMode(Entity card) => card.Definition?.Word("target") ?? "none";

        /// <summary>
        /// The entities this card may be pointed at right now, in board order, after content's
        /// <c>targetable</c> rules: exactly what <see cref="Play(Entity, Entity)"/> accepts. Empty for a card that
        /// takes no target. A <c>target any</c> card may also be played at nothing, which this
        /// list cannot say, so a UI that wants to offer that asks <see cref="TargetMode"/>.
        /// </summary>
        public IReadOnlyList<Entity> LegalTargets(Entity card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            Entity player = card.Controller;
            switch (TargetMode(card))
            {
                case "enemy": return Targetable(card, player, State.Actors(Opposing(player)));
                case "ally": return Targetable(card, player, State.Actors(player.Team));
                case "self": return new[] { player };
                case "any": return Targetable(card, player, State.Actors());
                default: return Array.Empty<Entity>();
            }
        }

        /// <summary>
        /// Whether <see cref="Play(Entity, Entity)"/> would accept this card now, aimed somewhere legal: it is in
        /// hand, playable, affordable, and a card that needs someone to point at has someone.
        /// Content can still cancel it while it resolves, which no check made in advance can see.
        /// </summary>
        public bool CanPlay(Entity card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (card.Kind != EntityKind.Card || card.IsRemoved || card.Zone != Zones.Hand) return false;
            if (card.HasTag("unplayable")) return false;
            if (!IsXCost(card) && card.Controller.GetInt(CostResourceOf(card)) < CostOf(card)) return false;

            string mode = TargetMode(card);
            return (mode != "enemy" && mode != "ally") || LegalTargets(card).Count > 0;
        }

        private static Team Opposing(Entity actor) => actor.Team == Team.Enemy ? Team.Player : Team.Enemy;

        private bool TryResolveTarget(Entity card, ref Entity? target)
        {
            Entity player = card.Controller;
            Team opposing = Opposing(player);

            switch (TargetMode(card))
            {
                case "enemy":
                {
                    if (target == null)
                    {
                        IReadOnlyList<Entity> enemies = Targetable(card, player, State.Actors(opposing));
                        if (enemies.Count == 0) return false;
                        target = enemies.Count == 1
                            ? enemies[0]
                            : Chooser.Choose(new ChoiceRequest("choose a target", enemies, 1, 1, player, card.Definition!.Syntax.Span), State)?.FirstOrDefault(enemies.Contains) ?? enemies[0];
                    }
                    return target.Kind == EntityKind.Actor && target.IsAlive && target.Team == opposing && IsTargetable(card, player, target);
                }

                case "ally":
                    target ??= player;
                    return target.Kind == EntityKind.Actor && target.IsAlive && target.Team == player.Team && IsTargetable(card, player, target);

                case "self":
                    target = player;
                    return true;

                case "any":
                    return target == null || (target.Kind == EntityKind.Actor && target.IsAlive && IsTargetable(card, player, target));

                default:
                    return true;
            }
        }

        // Target validity ----------------------------------------------------------------------

        private const string TargetableChannel = "targetable";

        /// <summary>
        /// Whether one entity may be named as this card's target. Content adds rules to target
        /// selection through the <c>targetable</c> channel, where a value of zero or less means
        /// "not this one". With no <c>of</c> scope a modifier anchors to its owner, so
        /// <c>modify targetable: set 0</c> on a status hides its host; a scope is how one entity
        /// speaks for others, which is what Taunt needs:
        /// <c>modify targetable of allies where source:enemies, not it.has(Taunt): set 0</c>.
        /// </summary>
        /// <remarks>
        /// Only the <c>target</c> words that name someone ask — <c>enemy</c>, <c>ally</c> and
        /// <c>any</c>. <c>target self</c> is not a choice, so nothing is asked of it. Area and
        /// random effects resolve through the interpreter's own selectors rather than here, which is
        /// deliberate: Taunt constrains what a card may be pointed at, not what a blast reaches.
        /// The query carries the card, so a <c>where</c> on the group must say <c>it.</c> to mean the
        /// candidate; a bare <c>tag:</c> there tests the card being played.
        /// </remarks>
        private bool IsTargetable(Entity card, Entity chooser, Entity candidate)
        {
            if (!State.Modifiers.HasChannel(TargetableChannel)) return true;

            var query = new ModifierQuery(TargetableChannel)
            {
                Subject = candidate,
                Source = chooser,
                Card = card,
                Tags = card.Tags.ToArray(),
            };
            return State.Modifiers.Compute(query, Num.One) > Num.Zero;
        }

        /// <summary>Those of a group the card may actually be pointed at, in the group's own order.</summary>
        private IReadOnlyList<Entity> Targetable(Entity card, Entity chooser, IReadOnlyList<Entity> candidates)
        {
            if (!State.Modifiers.HasChannel(TargetableChannel)) return candidates;

            var allowed = new List<Entity>();
            foreach (Entity candidate in candidates)
            {
                if (IsTargetable(card, chooser, candidate)) allowed.Add(candidate);
            }
            return allowed;
        }

        // Enemies ------------------------------------------------------------------------------

        /// <summary>Picks an enemy's next move from its pattern, so the UI can show intents in advance.</summary>
        public void RollIntent(Entity enemy) => Interpreter.RollIntent(enemy);

        private void RunEnemyMove(Entity enemy)
        {
            if (!enemy.IsAlive || enemy.Intent == null || enemy.Definition == null) return;
            UseMove(enemy, enemy.Intent, State.Player);
            enemy.LastMove = enemy.Intent;
            CheckBattleOver();
        }

        private void UseMove(Entity enemy, string moveName, Entity? target)
        {
            MoveDefinition? move = enemy.Definition?.Moves.FirstOrDefault(m => string.Equals(m.Name, moveName, StringComparison.OrdinalIgnoreCase));
            if (move == null) return;

            Run(enemy, context =>
            {
                context.Target = target;
                var gameEvent = new GameEvent("move") { Source = enemy, Target = target };
                gameEvent.Data["move"] = Value.FromText(move.Name);
                Interpreter.Raise(gameEvent, context, () => Interpreter.Execute(move.Body, context));
            });
        }

        // Real time ----------------------------------------------------------------------------

        /// <summary>Advances a <see cref="TickClock"/>. Call from the engine's fixed timestep.</summary>
        public void Tick(int count = 1)
        {
            if (!(State.Clock is TickClock ticks)) throw new InvalidOperationException("Tick needs a TickClock; this runtime uses turns.");
            for (int i = 0; i < count; i++)
            {
                Interpreter.ResetSteps();
                ticks.Tick();
            }
        }

        public bool IsReady(Entity ability) => ability.GetBase("ready_at") <= Num.FromInt(State.Clock.Now);

        /// <summary>Uses an ability if it is off cooldown. Returns false if it was not ready.</summary>
        public bool UseAbility(Entity ability, Entity? target = null)
        {
            if (ability == null) throw new ArgumentNullException(nameof(ability));

            int abilityId = ability.Id;
            int targetId = target?.Id ?? 0;
            bool used = false;
            Attempt(
                () => { used = UseAbilityCore(ability, target); return PlayResult.Played; },
                () =>
                {
                    if (Live(abilityId) is Entity again) used = UseAbility(again, Live(targetId));
                    return Outcome();
                });
            return used;
        }

        private bool UseAbilityCore(Entity ability, Entity? target)
        {
            if (ability.IsRemoved || ability.Owner == null || !ability.Owner.IsAlive || !IsReady(ability)) return false;

            Entity owner = ability.Owner;
            bool used = false;

            Run(owner, context =>
            {
                context.Self = ability;
                context.Target = target;
                var gameEvent = new GameEvent("ability_used") { Source = owner, Target = target };
                gameEvent.Data["ability"] = Value.FromEntity(ability);
                used = true;
                Interpreter.Raise(gameEvent, context, () =>
                {
                    BlockNode? effect = ability.Definition?.Effect;
                    if (effect != null) Interpreter.Execute(effect, context);
                });
            });

            if (ability.Definition?.Property("cooldown")?.First is NumberExpr cooldown
                && State.Clock.TryConvert(cooldown.Value, cooldown.Unit, out long units))
            {
                ability.SetBase("ready_at", Num.FromInt(State.Clock.Now + CooldownOf(ability, owner, units)));
            }

            return used;
        }

        private const string CooldownChannel = "cooldown";

        /// <summary>
        /// How long an ability waits, in clock units, after the <c>cooldown</c> channel has had it.
        /// </summary>
        /// <remarks>
        /// Modifiers see the converted duration rather than the number content wrote, so <c>x0.75</c>
        /// means the same three quarters whether the ability was authored in seconds or in turns.
        /// The cost of that choice is that an additive amount is in clock units — ticks in real time,
        /// turns otherwise — so a multiplier is the spelling that travels. Rounding is up, matching
        /// how a duration converts in the first place rather than how damage rounds down, and a
        /// cooldown never falls below nothing. The text a card prints still shows the cooldown as
        /// written, the way a printed cost does.
        /// </remarks>
        private long CooldownOf(Entity ability, Entity owner, long units)
        {
            if (!State.Modifiers.HasChannel(CooldownChannel)) return units;

            var query = new ModifierQuery(CooldownChannel)
            {
                Subject = ability,
                Source = owner,
                Tags = ability.Tags.ToArray(),
            };
            return Math.Max(0, State.Modifiers.Compute(query, Num.FromInt(units)).Ceiling().ToInt());
        }

        private void OnClockAdvanced(long now)
        {
            foreach (ScheduledAction action in State.Scheduled.Where(s => s.Timing == ScheduleTiming.AtTime && s.DueAt <= now).ToArray())
            {
                State.Unschedule(action);
                Run(action.Owner, _ => Interpreter.RunScheduled(action));
            }

            Run(null, _ => Interpreter.RunDuePeriodic(now));
            Run(null, _ => Interpreter.ExpireTimedStatuses());
            if (State.InBattle) CheckBattleOver();
        }

        // Player choices ------------------------------------------------------------------------

        private Func<PlayResult>? _replay;
        private bool _deferring;

        /// <summary>
        /// The decision a UI still has to make, set when an action returned
        /// <see cref="PlayResult.ChoicePending"/>. Null when nothing is waiting.
        /// </summary>
        public PendingChoice? Pending { get; private set; }

        /// <summary>
        /// Answers <see cref="Pending"/> and replays the action that asked. Returns what the replayed
        /// action returned, which is <see cref="PlayResult.ChoicePending"/> again if it needs a
        /// further decision.
        /// </summary>
        public PlayResult Answer(params int[] entityIds) => Answer((IEnumerable<int>)entityIds);

        public PlayResult Answer(IEnumerable<Entity> entities) => Answer((entities ?? Enumerable.Empty<Entity>()).Select(e => e.Id));

        public PlayResult Answer(IEnumerable<int> entityIds)
        {
            if (Pending == null) throw new InvalidOperationException("No choice is pending.");
            if (Pending.IsOffer) throw new InvalidOperationException("This choice offers content, not entities: answer it with Answer(EntityDefinition).");
            return Replay(entityIds);
        }

        /// <summary>
        /// Answers a pending offer of content, as <c>discover</c> makes, with the candidate the
        /// player picked from <see cref="PendingChoice.Definitions"/>, and replays the action.
        /// </summary>
        public PlayResult Answer(EntityDefinition chosen)
        {
            if (chosen == null) throw new ArgumentNullException(nameof(chosen));
            if (Pending == null) throw new InvalidOperationException("No choice is pending.");
            if (!Pending.IsOffer) throw new InvalidOperationException("This choice is between entities: answer it with their ids.");

            // The offered instance, or one a reload put in its place: kind and name identify it.
            int index = -1;
            for (int i = 0; i < Pending.Definitions.Count; i++)
            {
                if (ReferenceEquals(Pending.Definitions[i], chosen)) { index = i; break; }
            }
            for (int i = 0; index < 0 && i < Pending.Definitions.Count; i++)
            {
                if (DeferredChooser.SameDefinition(Pending.Definitions[i], chosen)) index = i;
            }
            if (index < 0) throw new ArgumentException($"{chosen} was not one of the offers for \"{Pending.Prompt}\".", nameof(chosen));

            return Replay(new[] { index }, Pending.Definitions[index]);
        }

        private PlayResult Replay(IEnumerable<int> answer, EntityDefinition? pick = null)
        {
            if (!(Chooser is DeferredChooser deferred)) throw new InvalidOperationException("Answering a choice needs a DeferredChooser.");

            Func<PlayResult> replay = _replay ?? throw new InvalidOperationException("There is no action to replay.");
            deferred.Add(answer, pick);
            Pending = null;
            _replay = null;
            try
            {
                return replay();
            }
            finally
            {
                // Answers belong to the action they were given for. Unless it stopped to ask again,
                // that action is over, whether it finished, threw, or found its card gone.
                if (Pending == null) deferred.Clear();
            }
        }

        /// <summary>
        /// Abandons the pending choice. The game is already back where it was before the action that
        /// asked, so nothing else has to be undone.
        /// </summary>
        public void CancelPending()
        {
            Pending = null;
            _replay = null;
            (Chooser as DeferredChooser)?.Clear();
        }

        private PlayResult Outcome() => Pending == null ? PlayResult.Played : PlayResult.ChoicePending;

        private Entity? Live(int id) => id == 0 ? null : State.Find(id);

        /// <summary>
        /// Runs a top-level action so that a decision nobody has answered stops it cleanly: the game
        /// is restored to the snapshot the action started from, the choice is reported, and
        /// <see cref="Answer(int[])"/> replays it. Without a <see cref="DeferredChooser"/> this is
        /// nothing but a direct call.
        /// </summary>
        private PlayResult Attempt(Func<PlayResult> action, Func<PlayResult> replay)
        {
            if (_deferring || !(Chooser is DeferredChooser deferred)) return action();

            GameSnapshot before;
            try
            {
                before = Capture();
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException(
                    "Deferred choices need a game that can be snapshotted. " + error.Message, error);
            }

            // A new action abandons a choice left unanswered, and the answers collected for it, so they
            // cannot be read as answers to this action's questions. A replay never gets here with one
            // pending: Answer clears Pending before replaying.
            if (Pending != null) CancelPending();

            int traceMark = State.Trace.Entries.Count;
            _deferring = true;
            deferred.Rewind();
            deferred.Armed = true;
            Interpreter.BeginHostBuffer();

            try
            {
                PlayResult result = action();

                // Only a completed action is real: now the game may hear about its events.
                Interpreter.FlushHostBuffer();
                deferred.Clear();
                Pending = null;
                _replay = null;
                return result;
            }
            catch (ChoicePendingException pending)
            {
                Interpreter.AbandonPending();
                Interpreter.DiscardHostBuffer();

                // Options belong to the state being thrown away; ids survive the round trip.
                int[] optionIds = pending.Request.Options.Select(o => o.Id).ToArray();
                int chooserId = pending.Request.Chooser?.Id ?? 0;

                RestoreState(before);
                State.Trace.TruncateTo(traceMark);

                Pending = new PendingChoice(
                    pending.Request.Prompt,
                    optionIds.Select(Live).Where(e => e != null).ToList()!,
                    pending.Request.Min,
                    pending.Request.Max,
                    Live(chooserId),
                    pending.Request.Span);
                _replay = replay;
                return PlayResult.ChoicePending;
            }
            catch (OfferPendingException pending)
            {
                Interpreter.AbandonPending();
                Interpreter.DiscardHostBuffer();

                // Definitions are immutable content, so they survive the rollback as they are.
                int chooserId = pending.Offer.Chooser?.Id ?? 0;

                RestoreState(before);
                State.Trace.TruncateTo(traceMark);

                Pending = new PendingChoice(pending.Offer.Prompt, pending.Offer.Options, Live(chooserId), pending.Offer.Span);
                _replay = replay;
                return PlayResult.ChoicePending;
            }
            catch
            {
                Interpreter.DiscardHostBuffer();
                deferred.Clear();
                throw;
            }
            finally
            {
                deferred.Armed = false;
                _deferring = false;
            }
        }

        // Hot reload ----------------------------------------------------------------------------

        /// <summary>What <see cref="ApplyContentChanges"/> did, for tools and logs.</summary>
        public sealed class ReloadReport
        {
            /// <summary>Live entities pointed at a newly loaded definition.</summary>
            public int Rebound { get; internal set; }

            /// <summary>Definitions entities still use that the reloaded content no longer has.</summary>
            public List<string> Missing { get; } = new List<string>();

            /// <summary>The ruleset changed; a running game keeps the rules it started with.</summary>
            public bool RulesetChanged { get; internal set; }

            public override string ToString() =>
                $"{Rebound} rebound, {Missing.Count} missing" + (RulesetChanged ? ", ruleset changed (needs a new game)" : string.Empty);
        }

        /// <summary>
        /// Rebinds every live entity to the definition now loaded under its kind and name, so edits
        /// to content take effect in a running game. Call it after reloading files
        /// into <see cref="Content"/>.
        /// </summary>
        /// <remarks>
        /// Stats the game has changed keep their values; stats still at the old definition's number
        /// take the new one. An entity whose definition has disappeared keeps the one it has and is
        /// listed in the report.
        /// </remarks>
        public ReloadReport ApplyContentChanges()
        {
            if (Interpreter.HasPendingWork) throw new InvalidOperationException("Cannot reload content while effects are still resolving.");

            var report = new ReloadReport();

            foreach (Entity entity in State.Entities.ToArray())
            {
                EntityDefinition? old = entity.Definition;
                if (old == null || entity.IsRemoved) continue;

                EntityDefinition? current = Content.Find(old.Name, old.KindName);
                if (current == null)
                {
                    if (!report.Missing.Contains(old.ToString())) report.Missing.Add(old.ToString());
                    continue;
                }

                if (ReferenceEquals(current, old)) continue;

                State.Rebind(entity, current);
                report.Rebound++;
            }

            report.RulesetChanged = !State.Rules.SameAs(Content.BuildRuleset());
            return report;
        }

        // Save and load -------------------------------------------------------------------------

        private BlockAddressBook? _addresses;
        private int _addressGeneration = -1;

        private BlockAddressBook Addresses
        {
            get
            {
                if (_addresses == null || _addressGeneration != Content.Generation)
                {
                    _addresses = BlockAddressBook.Build(Content);
                    _addressGeneration = Content.Generation;
                }
                return _addresses;
            }
        }

        /// <summary>
        /// Captures the full rules state. Only valid between actions; the returned object is plain
        /// data that any serializer can store.
        /// </summary>
        /// <summary>
        /// Whether <see cref="Capture"/> would succeed right now. Snapshots are only valid between
        /// actions, so a UI can grey out its save button instead of catching an exception.
        /// </summary>
        public bool CanCapture => !Interpreter.HasPendingWork;

        public GameSnapshot Capture()
        {
            if (Interpreter.HasPendingWork) throw new InvalidOperationException("Cannot snapshot while effects are still resolving.");

            GameSnapshot snapshot = State.Capture(Addresses);
            snapshot.NextChainRoot = Interpreter.ChainCounter;
            snapshot.Won = Won;
            snapshot.SkipNextDraw = _skipNextDraw;
            return snapshot;
        }

        /// <summary>
        /// Replaces the current state with a snapshot. The runtime must have the same content
        /// loaded; the next inputs then play out exactly as they would have in the original game.
        /// A choice left pending is abandoned: it belonged to the game being replaced.
        /// </summary>
        public void Restore(GameSnapshot snapshot)
        {
            RestoreState(snapshot);
            CancelPending();
        }

        /// <summary>The state alone, for rolling back an action whose pending choice must survive.</summary>
        private void RestoreState(GameSnapshot snapshot)
        {
            if (Interpreter.HasPendingWork) throw new InvalidOperationException("Cannot restore while effects are still resolving.");

            State.Restore(snapshot, Addresses);
            Interpreter.ChainCounter = snapshot.NextChainRoot;
            Won = snapshot.Won;
            _skipNextDraw = snapshot.SkipNextDraw;
        }

        // Ad hoc execution ---------------------------------------------------------------------

        /// <summary>
        /// Runs DSL statements directly, as the REPL and tests do: <c>runtime.Execute("deal 5 to enemy")</c>.
        /// </summary>
        public void Execute(string statements, Entity? self = null, Entity? target = null)
        {
            int selfId = self?.Id ?? 0;
            int targetId = target?.Id ?? 0;
            Attempt(
                () => { ExecuteCore(statements, self, target); return PlayResult.Played; },
                () => { Execute(statements, Live(selfId), Live(targetId)); return Outcome(); });
        }

        private void ExecuteCore(string statements, Entity? self, Entity? target)
        {
            BlockNode block = ParseStatements(statements);
            Entity? actor = self == null ? State.Player : self.Kind == EntityKind.Actor ? self : self.Controller;

            Run(actor, context =>
            {
                context.Self = self ?? actor;
                context.Target = target;
                Interpreter.Execute(block, context);
            });
            if (State.InBattle) CheckBattleOver();
        }

        /// <summary>Parses free-standing statements by wrapping them in a synthetic block.</summary>
        public static BlockNode ParseStatements(string statements, string file = "<execute>")
        {
            string[] lines = statements.Replace("\r\n", "\n").Split('\n');
            string wrapped = "test \"execute\"\n" + string.Join("\n", lines.Select(l => "  " + l)) + "\n";

            var diagnostics = new DiagnosticBag();
            SourceFileNode file_ = Parser.Parse(wrapped, file, diagnostics);
            diagnostics.ThrowIfErrors();
            return file_.Declarations.OfType<TestDeclNode>().First().Body;
        }

        private int _runDepth;

        /// <summary>
        /// Every top-level operation runs through here: a fresh chain, a fresh step budget and a
        /// full drain. A nested operation (an enemy's <c>use</c> inside a move) shares the outer
        /// budget, so a loop cannot escape the sandbox by calling back into the runtime. If the
        /// operation fails, whatever it queued is dropped with it.
        /// </summary>
        private void Run(Entity? actor, Action<EvalContext> body)
        {
            if (_runDepth == 0) Interpreter.ResetSteps();

            _runDepth++;
            try
            {
                var context = new EvalContext(actor) { Source = actor, Chain = Interpreter.NewChain() };
                body(context);
                Interpreter.Drain();
            }
            catch
            {
                Interpreter.AbandonPending();
                throw;
            }
            finally
            {
                _runDepth--;
            }
        }

        // Runtime-level verbs -----------------------------------------------------------------

        private void RegisterRuntimeVerbs()
        {
            // `replay card on target`: resolve a card's effect again, for free (Burst, Echo Form).
            Interpreter.RegisterVerb("replay", call =>
            {
                ExprNode? node = call.ArgumentNode(0) ?? throw call.Error("expected a card.");
                Entity? target = call.Context.Target;
                if (node is BinaryExpr { Operator: BinaryOperator.On } on)
                {
                    node = on.Left;
                    target = Interpreter.Evaluate(on.Right, call.Context).AsEntities().FirstOrDefault();
                }

                Value value = Interpreter.Evaluate(node, call.Context);
                Entity? card = value.Kind == ValueKind.Entity ? value.Entity : null;
                EntityDefinition? definition = card?.Definition ?? value.Definition;
                if (definition?.Effect == null) throw call.Error($"{value} has no effect to replay.");

                EvalContext replay = call.Context.Derive();
                replay.Self = card ?? call.Context.Self;
                replay.Card = card ?? call.Context.Card;
                replay.Target = target;
                Interpreter.Execute(definition.Effect, replay);
            });

            // `use Bellow`: an enemy performs one of its own moves.
            Interpreter.RegisterVerb("use", call =>
            {
                Entity self = call.Context.Self ?? throw call.Error("only enemies can use moves.");
                string move = call.ArgumentNode(0) switch
                {
                    NameExpr n => n.Name,
                    StringExpr s => s.Value,
                    _ => throw call.Error("expected a move name."),
                };
                if (self.Definition?.Moves.Any(m => string.Equals(m.Name, move, StringComparison.OrdinalIgnoreCase)) != true)
                    throw call.Error($"`{self.Name}` has no move `{move}`.");
                UseMove(self, move, call.Context.Target ?? State.Player);
            });
        }
    }
}
