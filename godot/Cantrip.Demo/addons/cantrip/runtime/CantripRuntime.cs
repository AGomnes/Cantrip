#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Godot;

namespace Cantrip.GodotAdapter
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
    /// is not re-entrant. Every entry point that changes the game, setting it up included, refuses
    /// a call from inside a host callback for the same reason; only the queries answer there.
    /// </para>
    /// </remarks>
    [GlobalClass]
    public partial class CantripRuntime : Node
    {
        private readonly GodotEffectHost _host = new GodotEffectHost();
        private readonly ChoiceBridge _choices = new ChoiceBridge();
        private readonly VariantMap.Marshal _marshal = new VariantMap.Marshal();

        private CardRuntime? _core;
        private CantripDebugAgent? _debug;
        private DescriptionBuilder? _describer;
        private int _describerGeneration = -1;
        private bool _busy;
        private bool _drainingEvents;
        private int _announcedChoice;
        private bool _wasInBattle;

        /// <summary>Where <c>.cantrip</c> files are discovered, as a <c>res://</c> path.</summary>
        [Export]
        public string ContentFolder { get; set; } = "res://content";

        /// <summary>
        /// Loads content when the node enters the tree, reporting any errors and warnings in the
        /// Output panel. Turn it off to load by hand and receive them from <see cref="LoadContent"/>.
        /// </summary>
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

            // Nobody receives what an automatic load returns, so the Output panel is told instead. A
            // content error the editor dock alone shows is invisible to whoever is running the game.
            if (AutoLoad) Report(Load(ContentFolder));
        }

        public override void _ExitTree()
        {
            _debug?.Uninstall();
            _debug = null;
        }

        public override void _Notification(int what)
        {
            // Let go of the registered callables while GDScript is still there to free them. Held
            // until Godot shuts down, a lambda is released after GDScript has gone, and the game
            // crashes on exit. Not on leaving the tree, which a node that is only moving also does.
            if (what == NotificationPredelete) _host.ClearCallbacks();
        }

        // Content --------------------------------------------------------------------------------

        /// <summary>
        /// Discovers and loads every <c>.cantrip</c> file under a folder, returning what the parser said.
        /// This goes through the engine's own file access, so it works the same in the editor and
        /// inside an exported game, where the project's files are not on disk at all.
        /// </summary>
        public Godot.Collections.Array LoadContent(string folder = "") => VariantMap.Diagnostics(Load(folder));

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
        //
        // These run no rules, so they do not go through Act, but they do change the game, so a host
        // callback may no more call them than play a card.

        public int CreatePlayer(string name = "Player", int hp = 80, int maxEnergy = 3)
        {
            CardRuntime core = EnsureRuntime();
            Guard();
            return core.CreatePlayer(name, hp, maxEnergy).Id;
        }

        public int AddCard(string name, string zone = Zones.Draw)
        {
            CardRuntime core = EnsureRuntime();
            Guard();
            return core.AddCard(name, zone).Id;
        }

        public Godot.Collections.Array AddDeck(Godot.Collections.Array names)
        {
            CardRuntime core = EnsureRuntime();
            Guard();

            var ids = new Godot.Collections.Array();
            foreach (string name in VariantMap.ToStrings(names)) ids.Add(core.AddCard(name).Id);
            return ids;
        }

        public int AddRelic(string name) => Act(() => EnsureRuntime().AddRelic(name).Id);

        public int SpawnEnemy(string name, int hp = 0)
        {
            CardRuntime core = EnsureRuntime();
            Guard();
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
            Guard();

            Entity? owner = core.State.Find(ownerId);
            return owner == null ? VariantMap.NoEntity : core.GrantAbility(name, owner).Id;
        }

        /// <summary>
        /// Takes a card out of the game for good, as <c>destroy</c> does in content and as
        /// <c>Execute("destroy target", 0, cardId)</c> would: the way to remove a card from the deck
        /// between battles, or to swap one for its upgraded definition. Content hears it as
        /// <c>destroyed</c>. Returns whether the card is gone; false, having changed nothing, when
        /// the id is not a card still in the game.
        /// </summary>
        public bool RemoveCard(int cardId)
        {
            CardRuntime core = EnsureRuntime();
            Guard();

            Entity? card = core.State.Find(cardId);
            if (card == null || card.Kind != EntityKind.Card || card.IsRemoved) return false;

            return Act(() =>
            {
                core.Execute("destroy target", null, card);
                return card.IsRemoved;
            });
        }

        /// <summary>
        /// Starts a new run on this node: the rules begin again from the loaded content, with no
        /// player, cards or enemies. <see cref="Seed"/> and the other exports are read again, so set
        /// a new seed first.
        /// </summary>
        /// <remarks>
        /// What belonged to the old run goes with it: a choice waiting for an answer, events not yet
        /// delivered, including the rest of a batch being delivered when this is called from an
        /// <c>EffectEvent</c> handler, and whatever the <see cref="Presenter"/> has queued. No
        /// signal says the old battle ended. The content stays as it is, reloads included, so a
        /// ruleset a reload changed takes effect now. The callables registered with
        /// <see cref="RegisterName"/> and <see cref="RegisterFunction"/> stay registered.
        /// <see cref="Core"/> is a new object afterwards.
        /// </remarks>
        public void NewRun()
        {
            Guard();

            _debug?.Uninstall();
            _debug = null;
            _core = null;

            _choices.Close();
            _announcedChoice = 0;
            Buffer.Reset();
            if (Presenter != null && IsInstanceValid(Presenter)) Presenter.SkipAll();

            EnsureRuntime();
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

        /// <summary>
        /// Runs statements as content would, as the player unless <paramref name="selfId"/> names
        /// someone else: a console line, a cheat key, or a small step a game takes between battles,
        /// such as a rest with <c>Execute("heal 12", 0, 0)</c>.
        /// </summary>
        /// <remarks>
        /// The text is parsed on every call and the linter never sees it, so a mistake shows only
        /// when the line runs, as an error in the Output panel. Keep it to a line or two: anything
        /// longer, anything run often, and anything a card, relic or status should own belongs in
        /// content, where it is checked and tested. Like any other action it can stop for a choice.
        /// </remarks>
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

        /// <summary>
        /// Whether <c>Play</c> would accept the card now: in hand, affordable in whatever it is
        /// priced in, and with a legal target if it needs one.
        /// </summary>
        public bool CanPlay(int cardId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(cardId);
            return card != null && core.CanPlay(card);
        }

        /// <summary>"enemy", "ally", "self", "any" or "none": what the card asks to be aimed at.</summary>
        public string GetTargetMode(int cardId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(cardId);
            return card == null ? "none" : core.TargetMode(card);
        }

        /// <summary>
        /// The entities that card may be aimed at after content's <c>targetable</c> rules, so a UI
        /// highlights exactly what <c>Play</c> accepts.
        /// </summary>
        public Godot.Collections.Array GetLegalTargets(int cardId)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(cardId);
            return card == null ? new Godot.Collections.Array() : VariantMap.Ids(core.LegalTargets(card));
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

        /// <summary>
        /// The rules text of a definition that need not be in play, such as a reward, a shop's stock
        /// or a page of a card library, with its printed values: the same dictionary as
        /// <see cref="Describe"/>. An empty <paramref name="kind"/> takes the first definition of
        /// that name. Empty when nothing of that name is loaded. Name and kind are matched without
        /// regard to case, as <see cref="GetDefinitions"/> matches the kind.
        /// </summary>
        /// <remarks>It reads the content alone, so it does not bring the rules into being.</remarks>
        public Godot.Collections.Dictionary DescribeDefinition(string name, string kind = "")
        {
            // Kinds are stored as the keyword that declares them, which is lower case.
            EntityDefinition? definition = Content.Find(name ?? string.Empty, string.IsNullOrEmpty(kind) ? null : kind.ToLowerInvariant());
            return definition == null
                ? new Godot.Collections.Dictionary()
                : VariantMap.Description(DescriptionView.Of(Describer().Describe(definition)));
        }

        /// <summary>
        /// The names of every loaded definition of one kind, such as "card" or "relic", with
        /// <paramref name="tag"/> among its tags unless that is empty: the pool a reward screen or a
        /// shop picks from. Sorted by name, which a reload does not disturb, so a pick made from it
        /// with the game's own seeded random numbers replays.
        /// </summary>
        /// <remarks>It reads the content alone, so it does not bring the rules into being.</remarks>
        public Godot.Collections.Array GetDefinitions(string kind, string tag = "")
        {
            var names = new Godot.Collections.Array();
            foreach (EntityDefinition definition in Content.Pool(kind ?? string.Empty))
            {
                if (string.IsNullOrEmpty(tag) || definition.HasTag(tag)) names.Add(definition.Name);
            }
            return names;
        }

        public bool IsInBattle() => EnsureRuntime().State.InBattle;

        public int GetTurn() => EnsureRuntime().State.Turn;

        /// <summary>
        /// Whether the player won the battle that ended last: null while a battle is running and
        /// before the first one has ended.
        /// </summary>
        public Variant GetWon()
        {
            // Spelled out: in a conditional against a bool, a bare default is false, not null.
            bool? won = EnsureRuntime().Won;
            if (won == null) return default(Variant);
            return won.Value;
        }

        /// <summary>The rules state as one number, for checking that two runs agree, as replay tests do.</summary>
        public string StateHash() => EnsureRuntime().State.ComputeHash().ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

        // Choices --------------------------------------------------------------------------------

        public bool HasPendingChoice() => _choices.IsPending;

        /// <summary>The decision the rules are waiting on, or empty when there is none.</summary>
        public Godot.Collections.Dictionary GetPendingChoice()
        {
            PendingChoice? pending = _choices.Current;
            return pending == null
                ? new Godot.Collections.Dictionary()
                : VariantMap.Choice(_choices.CurrentId, pending, TrackedStats, OfferText);
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
                    ["reason"] = answer.ReasonName,
                    ["message"] = answer.Message,
                };
            }

            // An offer is answered with the position of the pick; the core wants the definition itself.
            PendingChoice pending = _choices.Current!;
            string result = pending.IsOffer
                ? Act(() => Word(core.Answer(pending.Definitions[answer.EntityIds[0]])))
                : Act(() => Word(core.Answer(answer.EntityIds)));
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
            CardRuntime core = EnsureRuntime();
            Guard();

            core.CancelPending();
            SyncChoice();
        }

        // Save and load --------------------------------------------------------------------------

        /// <summary>
        /// Whether a save would succeed: not while effects are resolving, nor while a block that a
        /// reload has changed is still waiting to run.
        /// </summary>
        public bool CanSave() => !_busy && EnsureRuntime().CanCapture;

        /// <summary>
        /// The whole game as a string, with the fingerprint of the content it was taken against.
        /// When no save can be made it throws an <see cref="InvalidOperationException"/> that says
        /// why: effects still resolving, or, in the rules' own words, a waiting block a reload has
        /// changed.
        /// </summary>
        public string Save()
        {
            CardRuntime core = EnsureRuntime();

            // The rules see resolving only while their queue runs. A callback in the middle of a
            // card's own effect finds the queue empty, and a snapshot taken there would hold half an
            // action, so the node, which knows an action is under way, refuses it itself.
            if (_busy) throw new InvalidOperationException("Cannot save while effects are still resolving.");

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
        /// a refused save leaves the game, and any choice it is waiting on, exactly as they were.
        /// </summary>
        /// <remarks>
        /// A fingerprint that differs says only that a definition has been added, renamed or removed
        /// since the save was made, and a patch that only adds a card leaves every older save
        /// loadable. So it is not a refusal by itself: the rules look up everything the save needs
        /// as they restore, a definition it names and a waiting <c>next turn:</c> or
        /// <c>in N turns:</c> block included, and refuse before changing anything. That refusal,
        /// like any other snapshot the rules turn down, comes back as "content_changed" with their
        /// message.
        /// </remarks>
        public Godot.Collections.Dictionary LoadSave(string json)
        {
            CardRuntime core = EnsureRuntime();
            Guard();

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
            if (!check.Accepted && check.Reason != SaveRejection.ContentChanged) return Refused(check.ReasonName, check.Message);

            GameSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<GameSnapshot>(envelope.Payload, ReadOptions);
            }
            catch (JsonException error)
            {
                return Refused("wrong_format", "The game in this save cannot be read: " + error.Message);
            }

            if (snapshot == null) return Refused("no_payload", "This save carries no game to restore.");

            // The rules would refuse this too, but it is a save from another version of Cantrip.Core,
            // not one from other content, and a game may want to tell the player so.
            if (snapshot.FormatVersion != GameSnapshot.CurrentFormat)
            {
                return Refused("wrong_format",
                    "The game in this save is in format " + snapshot.FormatVersion + "; this version of Cantrip.Core reads format " + GameSnapshot.CurrentFormat + ".");
            }

            try
            {
                core.Restore(snapshot);
            }
            catch (InvalidOperationException error)
            {
                // The rules refuse before they change anything, so there is nothing to put back.
                return Refused("content_changed", error.Message);
            }

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
        /// <remarks>
        /// Any Callable will do: a method, a lambda, or either with <c>.bind()</c>. The parameter is
        /// a Variant rather than a <see cref="Callable"/> because C#'s Callable holds only an object
        /// and a method name, or a C# delegate, so a GDScript lambda or bound Callable converted to
        /// one arrives empty. A Variant keeps it whole; see <see cref="GodotEffectHost"/>. The node
        /// keeps it until the name is registered again or the node is freed, then disposes it, and
        /// refuses, with an <see cref="ArgumentException"/>, anything that cannot be called. From
        /// C#, pass a <see cref="Callable"/>, which becomes a new Variant for the call, rather than
        /// a Variant you go on using or register twice.
        /// </remarks>
        public void RegisterName(string name, Variant callable) => _host.RegisterName(name, callable);

        /// <summary>
        /// Answers a function the rules do not know, such as <c>within(5)</c> in a spatial game. Any
        /// Callable will do, as for <see cref="RegisterName"/>.
        /// </summary>
        public void RegisterFunction(string name, Variant callable) => _host.RegisterFunction(name, callable);

        // Internals ------------------------------------------------------------------------------

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>The save file's own shape. The snapshot inside it stays a string: this layer never reads it.</summary>
        private sealed class SaveFile
        {
            public int Format { get; set; }

            public string Fingerprint { get; set; } = string.Empty;

            public string Snapshot { get; set; } = string.Empty;
        }

        private DiagnosticBag Load(string folder)
        {
            if (_core != null) throw new InvalidOperationException("Content is already in play; use ReloadContent to change it.");

            Content = new ContentLibrary();
            DiagnosticBag problems = GodotContentLoader.LoadFolder(Content, string.IsNullOrEmpty(folder) ? ContentFolder : folder);
            _describerGeneration = -1;
            return problems;
        }

        /// <summary>
        /// Puts each error and warning in the Output panel on one line, worded as the importer and
        /// the command-line tool word it: <c>file:line:column: error CODE: message</c>. Notes stay
        /// out of a running game's output.
        /// </summary>
        private static void Report(IEnumerable<Diagnostic> problems)
        {
            foreach (Diagnostic problem in problems)
            {
                if (problem.Severity == DiagnosticSeverity.Error) GD.PushError(problem.ToString());
                else if (problem.Severity == DiagnosticSeverity.Warning) GD.PushWarning(problem.ToString());
            }
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
            _debug = new CantripDebugAgent(new CantripDebugService(_core));
            _debug.Install();

            return _core;
        }

        /// <summary>The rules text of an offered candidate, as a reward or discover screen shows it.</summary>
        private string OfferText(Cantrip.Content.EntityDefinition offered) => Describer().Describe(offered).ToPlainText();

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
        /// <remarks>
        /// A query such as <see cref="CanPlay"/> or <see cref="Describe"/> evaluates content too, and
        /// so can call a callback without any action running, which is why the host is asked as well.
        /// </remarks>
        private void Guard()
        {
            if (_busy || _host.InCallback)
                throw new InvalidOperationException(
                    "The rules are resolving. A host callback must answer and return; it cannot set the game up, play a card, end a turn or load a save while the rules wait for its answer.");
        }

        private void AfterAction()
        {
            // A handler that acts again arrives here a second time while the first drain is still
            // walking the buffer. The outer one tells this action's events once its own batch is
            // done, and then whether the battle ended, so the game hears them in the order they
            // happened. The handler may be waiting on a choice its call left, so that is told now.
            if (_drainingEvents)
            {
                SyncChoice();
                return;
            }

            _drainingEvents = true;
            try
            {
                // Until nothing is left: a batch holds only what was recorded before it began, and
                // a handler that acts adds more. One that starts a new run ends this one: the rest
                // of the batch is not told, since its ids would now name the new run's entities,
                // while anything the handler then does in the new run is.
                while (Buffer.Count > 0)
                {
                    CardRuntime? run = _core;
                    if (Presenter != null && IsInstanceValid(Presenter)) Presenter.Drain(Buffer);
                    else Buffer.Drain(record =>
                    {
                        if (_core == run) EmitSignal(SignalName.EffectEvent, VariantMap.Event(record));
                    });
                }
            }
            finally
            {
                _drainingEvents = false;
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
            if (pending != null) EmitSignal(SignalName.ChoiceRequested, VariantMap.Choice(id, pending, TrackedStats, OfferText));
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
