using System;
using System.Collections.Generic;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using GameplayEffects.Syntax;

namespace GameplayEffects
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
    /// The entry point for games (section 4.7): load content, set up actors and decks, then drive
    /// battles through <see cref="StartBattle"/>, <see cref="Play"/> and <see cref="EndTurn"/>, or
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

        public Entity? ApplyStatus(string status, Entity target, int stacks = 1, Entity? source = null)
        {
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
        public void StartBattle(bool shuffle = true, bool drawOpeningHand = true)
        {
            Entity player = State.Player ?? throw new InvalidOperationException("Create a player before starting a battle.");

            State.ResetBattleHistory();
            State.BattleNumber++;
            State.Turn = 0;
            State.InBattle = true;
            Won = null;
            _skipNextDraw = !drawOpeningHand;

            if (!player.HasStat("block")) player.SetBase("block", 0);
            if (shuffle) Interpreter.ShuffleZone(player, Zones.Draw);
            foreach (Entity enemy in State.Actors(Team.Enemy)) RollIntent(enemy);

            Run(player, context => Interpreter.Raise(new GameEvent("battle_start") { Source = player }, context));
            if (CheckBattleOver()) return;

            StartTurn(Team.Player);
        }

        /// <summary>Ends the player's turn, runs the enemies' turn, and starts the next player turn.</summary>
        public void EndTurn()
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
                if (State.Turn > 1 && State.Clock is TurnClock turns) turns.AdvanceTurn();
            }

            foreach (Entity actor in State.Actors(team).ToArray())
            {
                if (!actor.IsAlive) continue;

                // Only work scheduled before this turn began runs now; `next turn:` written during
                // this turn start belongs to the following turn.
                var due = State.Scheduled.Where(s => s.Timing == ScheduleTiming.NextTurn && s.Owner == actor).ToList();

                Run(actor, context =>
                {
                    Interpreter.ResetResources(actor, "turn_start");
                    Interpreter.ProcessDecay(actor, "turn_start");
                    Interpreter.Raise(new GameEvent("turn_start") { Source = actor, Target = actor }, context);
                });

                foreach (ScheduledAction action in due)
                {
                    State.Unschedule(action);
                    Run(actor, _ => Interpreter.RunScheduled(action));
                }

                if (CheckBattleOver()) return;
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
                Run(actor, _ =>
                {
                    Interpreter.ProcessDecay(actor, "turn_end");
                    Interpreter.ResetResources(actor, "turn_end");
                });

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

            bool hadEnemies = State.Entities.Any(e => e.Kind == EntityKind.Actor && e.Team == Team.Enemy);
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

            // Temporary effects end with the battle; statuses flagged persistent carry over.
            foreach (ScheduledAction action in State.Scheduled.ToArray())
            {
                State.Unschedule(action);
                if (action.Timing == ScheduleTiming.Until) Interpreter.Revert(action);
            }

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

        /// <summary>The card's current cost after modifiers. X-cost cards cost all remaining energy.</summary>
        public int CostOf(Entity card)
        {
            if (IsXCost(card)) return card.Controller.GetInt("energy");

            var query = new ModifierQuery("cost") { Subject = card, Source = card.Controller, Card = card, Tags = card.Tags.ToArray() };
            Num cost = State.Modifiers.Compute(query, card.Get("cost"));
            return Math.Max(0, cost.Floor().ToInt());
        }

        /// <summary>Plays a card from hand: checks energy and target, pays, resolves, and drains triggers.</summary>
        public PlayResult Play(Entity card, Entity? target = null)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (card.Kind != EntityKind.Card || card.IsRemoved) return PlayResult.NotACard;
            if (card.Zone != Zones.Hand) return PlayResult.NotInHand;
            if (card.HasTag("unplayable")) return PlayResult.Unplayable;

            Entity player = card.Controller;
            int cost = CostOf(card);
            if (!IsXCost(card) && player.GetInt("energy") < cost) return PlayResult.NotEnoughEnergy;

            if (!TryResolveTarget(card, ref target)) return PlayResult.InvalidTarget;

            Interpreter.ResetSteps();
            var context = new EvalContext(card) { Source = player, Target = target, Card = card, Chain = Interpreter.NewChain() };
            if (IsXCost(card)) context.SetLocal("x", Value.FromNumber(Num.FromInt(cost)));

            var gameEvent = new GameEvent("card_played") { Source = player, Target = target, Card = card, Amount = cost };
            foreach (string tag in card.Tags) gameEvent.Tags.Add(tag);

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
                    if (cost > 0) Interpreter.ChangeStat(player, "energy", AssignOperator.Subtract, cost, context);
                    State.MoveTo(card, Zones.Play);
                    State.RecordHistory("cards_played", player, Num.One);
                    if (card.HasTag("attack")) State.RecordHistory("attacks", player, Num.One);
                });

            if (gameEvent.Cancelled && card.Zone == Zones.Hand) return PlayResult.Cancelled;

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
            CheckBattleOver();
            return PlayResult.Played;
        }

        public PlayResult Play(string cardName, Entity? target = null)
        {
            Entity player = State.Player ?? throw new InvalidOperationException("There is no player.");
            Entity? card = State.ZoneOf(player, Zones.Hand).FirstOrDefault(c => string.Equals(c.Name, cardName, StringComparison.OrdinalIgnoreCase));
            return card == null ? PlayResult.NotInHand : Play(card, target);
        }

        private bool TryResolveTarget(Entity card, ref Entity? target)
        {
            Entity player = card.Controller;
            Team opposing = player.Team == Team.Enemy ? Team.Player : Team.Enemy;

            switch (card.Definition?.Word("target") ?? "none")
            {
                case "enemy":
                {
                    if (target == null)
                    {
                        IReadOnlyList<Entity> enemies = State.Actors(opposing);
                        if (enemies.Count == 0) return false;
                        target = enemies.Count == 1
                            ? enemies[0]
                            : Chooser.Choose(new ChoiceRequest("choose a target", enemies, 1, 1, player, card.Definition!.Syntax.Span), State).FirstOrDefault(enemies.Contains) ?? enemies[0];
                    }
                    return target.Kind == EntityKind.Actor && target.IsAlive && target.Team == opposing;
                }

                case "ally":
                    target ??= player;
                    return target.Kind == EntityKind.Actor && target.IsAlive && target.Team == player.Team;

                case "self":
                    target = player;
                    return true;

                case "any":
                    return target == null || (target.Kind == EntityKind.Actor && target.IsAlive);

                default:
                    return true;
            }
        }

        // Enemies ------------------------------------------------------------------------------

        /// <summary>Picks each enemy's next move from its pattern, so the UI can show intents in advance.</summary>
        public void RollIntent(Entity enemy)
        {
            EntityDefinition? definition = enemy.Definition;
            if (definition == null || definition.Moves.Count == 0)
            {
                enemy.Intent = null;
                return;
            }

            List<string> names = definition.PatternMoves.Count > 0
                ? definition.PatternMoves.ToList()
                : definition.Moves.Select(m => m.Name).ToList();

            switch (definition.Pattern)
            {
                case EnemyPatternKind.Cycle:
                    enemy.Intent = names[enemy.PatternIndex % names.Count];
                    enemy.PatternIndex++;
                    break;

                case EnemyPatternKind.Random:
                case EnemyPatternKind.RandomNoRepeat:
                {
                    var candidates = names.ToList();
                    if (definition.Pattern == EnemyPatternKind.RandomNoRepeat && candidates.Count > 1 && enemy.LastMove != null)
                        candidates.RemoveAll(n => string.Equals(n, enemy.LastMove, StringComparison.OrdinalIgnoreCase));

                    var weights = candidates
                        .Select(n => definition.Moves.FirstOrDefault(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase))?.Weight ?? Num.One)
                        .ToList();
                    int index = State.Rng.PickWeighted(weights);
                    enemy.Intent = index < 0 ? null : candidates[index];
                    break;
                }
            }

            State.Touch();
        }

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
                ability.SetBase("ready_at", Num.FromInt(State.Clock.Now + units));
            }

            return used;
        }

        private void OnClockAdvanced(long now)
        {
            foreach (ScheduledAction action in State.Scheduled.Where(s => s.Timing == ScheduleTiming.AtTime && s.DueAt <= now).ToArray())
            {
                State.Unschedule(action);
                Run(action.Owner, _ => Interpreter.RunScheduled(action));
            }

            Run(null, _ => Interpreter.ExpireTimedStatuses());
            if (State.InBattle) CheckBattleOver();
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
        /// </summary>
        public void Restore(GameSnapshot snapshot)
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

        /// <summary>Every top-level operation runs through here: fresh chain, step budget, and a full drain.</summary>
        private void Run(Entity? actor, Action<EvalContext> body)
        {
            var context = new EvalContext(actor) { Source = actor, Chain = Interpreter.NewChain() };
            body(context);
            Interpreter.Drain();
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
