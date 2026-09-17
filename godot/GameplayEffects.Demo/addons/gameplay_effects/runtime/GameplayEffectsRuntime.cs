using System;
using System.Collections.Generic;
using System.Text.Json;
using GameplayEffects.Content;
using GameplayEffects.Descriptions;
using GameplayEffects.Diagnostics;
using GameplayEffects.Runtime;
using Godot;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// The node a game drops into a scene, and the only surface script touches. It owns the rules
    /// engine, loads content the way an exported game must, and turns everything crossing the
    /// boundary into ids and dictionaries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here decides anything about the rules: every method is a call into
    /// <see cref="CardRuntime"/> plus a conversion. A C# game can skip the conversion entirely and
    /// use <see cref="Core"/>, which is the same object this node drives.
    /// </para>
    /// <para>
    /// Events do not reach the game while the rules are resolving. The host appends each resolved
    /// event to a buffer and this node drains it once the action has finished, because a script
    /// handler that called back into the engine mid-resolution would re-enter an interpreter that
    /// is not re-entrant. Every public entry point refuses a nested call for the same reason.
    /// </para>
    /// </remarks>
    [GlobalClass]
    public partial class GameplayEffectsRuntime : Node
    {
        private readonly GodotEffectHost _host = new GodotEffectHost();
        private readonly ChoiceBridge _choices = new ChoiceBridge();
        private readonly VariantMap.Marshal _marshal = new VariantMap.Marshal();

        private CardRuntime? _core;
        private GeDebugAgent? _debug;
        private DescriptionBuilder? _describer;
        private int _describerGeneration = -1;
        private bool _busy;
        private bool _drainingEvents;
        private int _announcedChoice;
        private bool _wasInBattle;

        /// <summary>Where <c>.ge</c> files are discovered, as a <c>res://</c> path.</summary>
        [Export]
        public string ContentFolder { get; set; } = "res://content";

        /// <summary>Loads content when the node enters the tree. Turn it off to load by hand.</summary>
        [Export]
        public bool AutoLoad { get; set; } = true;

        /// <summary>The seed every roll comes from. The same seed and the same inputs replay exactly.</summary>
        [Export]
        public int Seed { get; set; } = 1;

        /// <summary>Records the causality trace. Off by default, because it is not free.</summary>
        [Export]
        public bool Trace { get; set; }

        /// <summary>Real time rather than turns: the clock advances by ticks from a TickDriver.</summary>
        [Export]
        public bool RealTime { get; set; }

        [Export]
        public int TicksPerSecond { get; set; } = 60;

        /// <summary>
        /// The stats each event carries for the entities it touched. An animation plays after the
        /// whole action resolved, so live stats would show the end of the story; these are the
        /// values as that event happened.
        /// </summary>
        [Export]
        public string[] TrackedStats { get; set; } = { "hp", "block", "energy" };

        /// <summary>Optional: paces events one at a time instead of emitting them all at once.</summary>
        [Export]
        public BattlePresenter? Presenter { get; set; }

        /// <summary>Optional: drives <see cref="Tick"/> from the physics step in a real-time game.</summary>
        [Export]
        public TickDriver? Driver { get; set; }

        [Signal]
        public delegate void EffectEventEventHandler(Godot.Collections.Dictionary effectEvent);

        [Signal]
        public delegate void BattleStartedEventHandler();

        [Signal]
        public delegate void BattleEndedEventHandler(bool won);

        [Signal]
        public delegate void ChoiceRequestedEventHandler(Godot.Collections.Dictionary request);

        [Signal]
        public delegate void ContentReloadedEventHandler(Godot.Collections.Array diagnostics);

        /// <summary>The rules engine itself, for a game written in C#.</summary>
        public CardRuntime Core => EnsureRuntime();

        public ContentLibrary Content { get; private set; } = new ContentLibrary();

        internal EventBuffer Buffer => _host.Buffer;

        public override void _Ready()
        {
            if (Driver != null) Driver.Drive = count => Tick(count);
            if (AutoLoad) LoadContent(ContentFolder);
        }

        public override void _ExitTree()
        {
            _debug?.Uninstall();
            _debug = null;
        }

        // Content --------------------------------------------------------------------------------

        /// <summary>
        /// Discovers and loads every <c>.ge</c> file under a folder, returning what the parser said.
        /// This goes through the engine's own file access, so it works the same in the editor and
        /// inside an exported game, where the project's files are not on disk at all.
        /// </summary>
        public Godot.Collections.Array LoadContent(string folder = "")
        {
            if (_core != null) throw new InvalidOperationException("Content is already in play; use ReloadContent to change it.");

            Content = new ContentLibrary();
            DiagnosticBag problems = GodotContentLoader.LoadFolder(Content, string.IsNullOrEmpty(folder) ? ContentFolder : folder);
            _describerGeneration = -1;
            return VariantMap.Diagnostics(problems);
        }

        /// <summary>
        /// Reloads content into a running game and rebinds everything live to it. Stats the game has
        /// changed keep their values; a card still at its printed cost takes the new one.
        /// </summary>
        public Godot.Collections.Dictionary ReloadContent(Godot.Collections.Array? paths = null)
        {
            CardRuntime core = EnsureRuntime();
            Guard();

            IReadOnlyList<string> files = paths == null || paths.Count == 0
                ? GodotContentLoader.Discover(ContentFolder)
                : VariantMap.ToStrings(paths);

            DiagnosticBag problems = GodotContentLoader.LoadInto(Content, files);
            CardRuntime.ReloadReport report = core.ApplyContentChanges();
            _describerGeneration = -1;

            Godot.Collections.Array diagnostics = VariantMap.Diagnostics(problems);
            EmitSignal(SignalName.ContentReloaded, diagnostics);

            return new Godot.Collections.Dictionary
            {
                ["rebound"] = report.Rebound,
                ["missing"] = VariantMap.Strings(report.Missing),
                ["ruleset_changed"] = report.RulesetChanged,
                ["diagnostics"] = diagnostics,
            };
        }

        // Setup ----------------------------------------------------------------------------------

        public int CreatePlayer(string name = "Player", int hp = 80, int maxEnergy = 3) =>
            EnsureRuntime().CreatePlayer(name, hp, maxEnergy).Id;

        public int AddCard(string name, string zone = Zones.Draw) => EnsureRuntime().AddCard(name, zone).Id;

        public Godot.Collections.Array AddDeck(Godot.Collections.Array names)
        {
            CardRuntime core = EnsureRuntime();
            var ids = new Godot.Collections.Array();
            foreach (string name in VariantMap.ToStrings(names)) ids.Add(core.AddCard(name).Id);
            return ids;
        }

        public int AddRelic(string name) => Act(() => EnsureRuntime().AddRelic(name).Id);

        public int SpawnEnemy(string name, int hp = 0)
        {
            CardRuntime core = EnsureRuntime();
            return core.SpawnEnemy(name, hp <= 0 ? (int?)null : hp).Id;
        }

        public int ApplyStatus(string status, int targetId, int stacks = 1)
        {
            CardRuntime core = EnsureRuntime();
            Entity? target = core.State.Find(targetId);
            if (target == null) return VariantMap.NoEntity;

            return Act(() => core.ApplyStatus(status, target, stacks)?.Id ?? VariantMap.NoEntity);
        }

        public int GrantAbility(string name, int ownerId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? owner = core.State.Find(ownerId);
            return owner == null ? VariantMap.NoEntity : core.GrantAbility(name, owner).Id;
        }

        // Battle flow ----------------------------------------------------------------------------

        public void StartBattle(bool shuffle = true, bool drawOpeningHand = true)
        {
            CardRuntime core = EnsureRuntime();
            Act(() =>
            {
                core.StartBattle(shuffle, drawOpeningHand);
                return true;
            });

            if (core.State.InBattle) EmitSignal(SignalName.BattleStarted);
        }

        /// <summary>
        /// Plays a card. The answer is one of "played", "pending", "not_a_card", "not_in_hand",
        /// "unplayable", "not_enough_energy", "invalid_target" or "cancelled"; "pending" means the
        /// rules need a decision and a <c>choice_requested</c> signal is on its way.
        /// </summary>
        public string Play(int cardId, int targetId = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(cardId);
            if (card == null) return Word(PlayResult.NotACard);

            Entity? target = targetId == VariantMap.NoEntity ? null : core.State.Find(targetId);
            return Act(() => Word(core.Play(card, target)));
        }

        /// <summary>Plays the first card of that name in hand, for a game that thinks in names.</summary>
        public string PlayNamed(string cardName, int targetId = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? target = targetId == VariantMap.NoEntity ? null : core.State.Find(targetId);
            return Act(() => Word(core.Play(cardName, target)));
        }

        public void EndTurn() => Act(() =>
        {
            EnsureRuntime().EndTurn();
            return true;
        });

        public void Tick(int count = 1) => Act(() =>
        {
            EnsureRuntime().Tick(count);
            return true;
        });

        public bool UseAbility(int abilityId, int targetId = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? ability = core.State.Find(abilityId);
            if (ability == null) return false;

            Entity? target = targetId == VariantMap.NoEntity ? null : core.State.Find(targetId);
            return Act(() => core.UseAbility(ability, target));
        }

        /// <summary>Runs DSL statements, as a developer console would. Not for shipping game code.</summary>
        public void Execute(string statements, int selfId = 0, int targetId = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? self = selfId == VariantMap.NoEntity ? null : core.State.Find(selfId);
            Entity? target = targetId == VariantMap.NoEntity ? null : core.State.Find(targetId);

            Act(() =>
            {
                core.Execute(statements, self, target);
                return true;
            });
        }

        // Queries --------------------------------------------------------------------------------

        public Godot.Collections.Array GetHand() => GetZone(PlayerId(), Zones.Hand);

        public Godot.Collections.Array GetZone(int ownerId, string zone)
        {
            CardRuntime core = EnsureRuntime();
            Entity? owner = ownerId == VariantMap.NoEntity ? core.Player : core.State.Find(ownerId);
            return VariantMap.Ids(core.State.ZoneOf(owner, zone));
        }

        public Godot.Collections.Array GetEnemies() => VariantMap.Ids(EnsureRuntime().State.Actors(Team.Enemy));

        public Godot.Collections.Array GetAllies() => VariantMap.Ids(EnsureRuntime().State.Actors(Team.Player));

        public Godot.Collections.Array GetActors() => VariantMap.Ids(EnsureRuntime().State.Actors());

        public int PlayerId() => EnsureRuntime().Player?.Id ?? VariantMap.NoEntity;

        /// <summary>Everything a UI shows about one entity. Empty when the id is unknown.</summary>
        public Godot.Collections.Dictionary GetEntity(int entityId)
        {
            Entity? entity = EnsureRuntime().State.Find(entityId);
            return entity == null ? new Godot.Collections.Dictionary() : VariantMap.Entity(EntityView.Of(entity));
        }

        /// <summary>One stat after modifiers, which is the number the rules would use now.</summary>
        public int GetStat(int entityId, string stat)
        {
            Entity? entity = EnsureRuntime().State.Find(entityId);
            return entity == null ? 0 : entity.GetInt(stat);
        }

        public int CostOf(int cardId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(cardId);
            return card == null ? 0 : core.CostOf(card);
        }

        public bool CanPlay(int cardId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(cardId);
            if (card == null || card.Kind != EntityKind.Card || card.Zone != Zones.Hand) return false;
            if (card.HasTag("unplayable")) return false;

            return core.IsXCost(card) || card.Controller.GetInt("energy") >= core.CostOf(card);
        }

        /// <summary>"enemy", "ally", "self", "any" or "none": what the card asks to be aimed at.</summary>
        public string GetTargetMode(int cardId)
        {
            Entity? card = EnsureRuntime().State.Find(cardId);
            return card?.Definition?.Word("target") ?? "none";
        }

        /// <summary>The entities that card may be aimed at, so a UI can highlight them.</summary>
        public Godot.Collections.Array GetLegalTargets(int cardId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(cardId);
            if (card == null) return new Godot.Collections.Array();

            Team side = card.Controller.Team;
            Team opposing = side == Team.Enemy ? Team.Player : Team.Enemy;

            switch (GetTargetMode(cardId))
            {
                case "enemy": return VariantMap.Ids(core.State.Actors(opposing));
                case "ally": return VariantMap.Ids(core.State.Actors(side));
                case "self": return new Godot.Collections.Array { card.Controller.Id };
                case "any": return VariantMap.Ids(core.State.Actors());
                default: return new Godot.Collections.Array();
            }
        }

        /// <summary>The rules text with live values, ready for a card frame.</summary>
        public Godot.Collections.Dictionary Describe(int entityId, int targetId = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? entity = core.State.Find(entityId);
            if (entity == null) return new Godot.Collections.Dictionary();

            Entity? target = targetId == VariantMap.NoEntity ? null : core.State.Find(targetId);
            return VariantMap.Description(DescriptionView.Of(Describer().Describe(entity, core, target)));
        }

        /// <summary>What an enemy will do next. Empty until intents have been rolled.</summary>
        public Godot.Collections.Dictionary DescribeIntent(int enemyId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? enemy = core.State.Find(enemyId);
            if (enemy == null) return new Godot.Collections.Dictionary();

            // Read by the player the move is aimed at, so a bigger number is worse news, not better.
            return VariantMap.Description(DescriptionView.Of(Describer().DescribeIntent(enemy, core), forOpponent: true));
        }

        public bool IsInBattle() => EnsureRuntime().State.InBattle;

        public int GetTurn() => EnsureRuntime().State.Turn;

        /// <summary>True, false, or null while a battle is still running.</summary>
        public Variant GetWon()
        {
            bool? won = EnsureRuntime().Won;
            return won.HasValue ? won.Value : default;
        }

        /// <summary>The rules state as one number, for desync checks and replay tests.</summary>
        public string StateHash() => EnsureRuntime().State.ComputeHash().ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

        // Choices --------------------------------------------------------------------------------

        public bool HasPendingChoice() => _choices.IsPending;

        /// <summary>The decision the rules are waiting on, or empty when there is none.</summary>
        public Godot.Collections.Dictionary GetPendingChoice()
        {
            PendingChoice? pending = _choices.Current;
            return pending == null
                ? new Godot.Collections.Dictionary()
                : VariantMap.Choice(_choices.CurrentId, pending, TrackedStats);
        }

        /// <summary>
        /// Answers a pending choice and lets the interrupted action finish. The game was rolled back
        /// to where that action started, so it replays from there with the answer in place.
        /// </summary>
        public Godot.Collections.Dictionary AnswerChoice(int requestId, Godot.Collections.Array chosen)
        {
            CardRuntime core = EnsureRuntime();
            ChoiceAnswer answer = _choices.Validate(requestId, VariantMap.ToIds(chosen));
            if (!answer.Accepted)
            {
                return new Godot.Collections.Dictionary
                {
                    ["accepted"] = false,
                    ["reason"] = answer.Reason.ToString().ToLowerInvariant(),
                    ["message"] = answer.Message,
                };
            }

            string result = Act(() => Word(core.Answer(answer.EntityIds)));
            return new Godot.Collections.Dictionary
            {
                ["accepted"] = true,
                ["reason"] = "none",
                ["message"] = string.Empty,
                ["result"] = result,
            };
        }

        /// <summary>Abandons the pending choice. The interrupted action never happened.</summary>
        public void CancelChoice()
        {
            EnsureRuntime().CancelPending();
            SyncChoice();
        }

        // Save and load --------------------------------------------------------------------------

        /// <summary>Whether a save would succeed: snapshots are only valid between actions.</summary>
        public bool CanSave() => !_busy && EnsureRuntime().CanCapture;

        /// <summary>
        /// The whole game as a string, with the fingerprint of the content it was taken against so a
        /// save from before a patch can be refused with a message rather than a crash.
        /// </summary>
        public string Save()
        {
            CardRuntime core = EnsureRuntime();
            if (!core.CanCapture) throw new InvalidOperationException("Cannot save while effects are still resolving.");

            SaveEnvelope envelope = SaveEnvelope.Wrap(Content.Fingerprint, JsonSerializer.Serialize(core.Capture()));
            return JsonSerializer.Serialize(new SaveFile
            {
                Format = envelope.Format,
                Fingerprint = envelope.Fingerprint,
                Snapshot = envelope.Payload,
            });
        }

        /// <summary>
        /// Restores a saved game. Returns whether it was accepted, and why not when it was refused;
        /// nothing is touched unless the save is going to be applied.
        /// </summary>
        public Godot.Collections.Dictionary LoadSave(string json)
        {
            CardRuntime core = EnsureRuntime();
            SaveFile? file;
            try
            {
                file = JsonSerializer.Deserialize<SaveFile>(json, ReadOptions);
            }
            catch (JsonException error)
            {
                return Refused("wrong_format", "This does not look like a save file: " + error.Message);
            }

            if (file == null) return Refused("no_payload", "This save carries no game to restore.");

            var envelope = new SaveEnvelope(file.Format, file.Fingerprint, file.Snapshot);
            SaveCheck check = envelope.Check(Content.Fingerprint);
            if (!check.Accepted) return Refused(check.ReasonName, check.Message);

            GameSnapshot? snapshot = JsonSerializer.Deserialize<GameSnapshot>(envelope.Payload, ReadOptions);
            if (snapshot == null) return Refused("no_payload", "This save carries no game to restore.");

            core.Restore(snapshot);
            _choices.Close();
            _announcedChoice = 0;
            Buffer.Reset();
            _wasInBattle = core.State.InBattle;

            return new Godot.Collections.Dictionary
            {
                ["accepted"] = true,
                ["reason"] = "none",
                ["message"] = string.Empty,
            };
        }

        // Host extensions ------------------------------------------------------------------------

        /// <summary>
        /// Answers a name the rules do not know, such as a game-specific selector. The callable is
        /// asked for a value and must not change anything: it runs inside rules resolution.
        /// </summary>
        public void RegisterName(string name, Callable callable) => _host.RegisterName(name, callable);

        /// <summary>Answers a function the rules do not know, such as <c>within(5)</c> in a spatial game.</summary>
        public void RegisterFunction(string name, Callable callable) => _host.RegisterFunction(name, callable);

        // Internals ------------------------------------------------------------------------------

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>The save file's own shape. The snapshot inside it stays a string: this layer never reads it.</summary>
        private sealed class SaveFile
        {
            public int Format { get; set; }

            public string Fingerprint { get; set; } = string.Empty;

            public string Snapshot { get; set; } = string.Empty;
        }

        private CardRuntime EnsureRuntime()
        {
            if (_core != null) return _core;

            var options = new RuntimeOptions
            {
                Seed = (ulong)Math.Max(1, Seed),
                Trace = Trace,
                Host = _host,
                Chooser = new DeferredChooser(),
            };
            if (RealTime) options.Clock = new TickClock(Math.Max(1, TicksPerSecond));

            _core = new CardRuntime(Content, options);
            _host.State = _core.State;
            _host.TrackedStats = TrackedStats;
            _marshal.State = _core.State;
            _host.Marshal = _marshal;
            _wasInBattle = _core.State.InBattle;

            if (Presenter != null && IsInstanceValid(Presenter)) Presenter.Formatter = VariantMap.Event;

            // Only ever talks while a debugger is attached, so an exported game pays nothing for it.
            _debug = new GeDebugAgent(new GeDebugService(_core));
            _debug.Install();

            return _core;
        }

        private DescriptionBuilder Describer()
        {
            if (_describer == null || _describerGeneration != Content.Generation)
            {
                _describer = new DescriptionBuilder(Content);
                _describerGeneration = Content.Generation;
            }
            return _describer;
        }

        /// <summary>
        /// Runs one top-level action: refuse a nested call, then hand the game what happened. A
        /// failed action drops whatever it had buffered, so a half-told story is never animated.
        /// </summary>
        private T Act<T>(Func<T> action)
        {
            Guard();

            T result;
            _busy = true;
            try
            {
                result = action();
            }
            catch
            {
                Buffer.Discard();
                throw;
            }
            finally
            {
                _busy = false;
            }

            // Deliberately outside the guard. Telling the game what happened is not resolving, and
            // the obvious thing to do when asked for a decision is to answer it there and then.
            AfterAction();
            return result;
        }

        /// <summary>
        /// Refuses a call made while the rules are mid-effect. That can only happen from a host
        /// callback, which the interpreter invokes in the middle of resolving; such a callback must
        /// answer and return. Acting again from a signal handler is fine: by then the action is over.
        /// </summary>
        private void Guard()
        {
            if (_busy)
                throw new InvalidOperationException(
                    "The rules are resolving. A host callback must answer and return; it cannot play a card, end a turn or save from inside an effect.");
        }

        private void AfterAction()
        {
            // A handler that acts again arrives here a second time while the first drain is still
            // walking the buffer. Let the outer one finish rather than deliver the same events twice.
            if (!_drainingEvents)
            {
                _drainingEvents = true;
                try
                {
                    if (Presenter != null && IsInstanceValid(Presenter)) Presenter.Drain(Buffer);
                    else Buffer.Drain(record => EmitSignal(SignalName.EffectEvent, VariantMap.Event(record)));
                }
                finally
                {
                    _drainingEvents = false;
                }
            }

            SyncChoice();

            bool inBattle = _core != null && _core.State.InBattle;
            if (_wasInBattle && !inBattle) EmitSignal(SignalName.BattleEnded, _core?.Won ?? false);
            _wasInBattle = inBattle;
        }

        /// <summary>Tells the game about a decision the rules are waiting on, once per request.</summary>
        private void SyncChoice()
        {
            int id = _choices.Sync(_core?.Pending);
            if (id == 0)
            {
                _announcedChoice = 0;
                return;
            }

            if (id == _announcedChoice) return;
            _announcedChoice = id;

            PendingChoice? pending = _choices.Current;
            if (pending != null) EmitSignal(SignalName.ChoiceRequested, VariantMap.Choice(id, pending, TrackedStats));
        }

        private static Godot.Collections.Dictionary Refused(string reason, string message) =>
            new Godot.Collections.Dictionary
            {
                ["accepted"] = false,
                ["reason"] = reason,
                ["message"] = message,
            };

        /// <summary>The result as script reads it: lower case, and "pending" for a choice.</summary>
        private static string Word(PlayResult result) => result switch
        {
            PlayResult.Played => "played",
            PlayResult.ChoicePending => "pending",
            PlayResult.NotACard => "not_a_card",
            PlayResult.NotInHand => "not_in_hand",
            PlayResult.Unplayable => "unplayable",
            PlayResult.NotEnoughEnergy => "not_enough_energy",
            PlayResult.InvalidTarget => "invalid_target",
            PlayResult.Cancelled => "cancelled",
            _ => result.ToString().ToLowerInvariant(),
        };
    }
}
