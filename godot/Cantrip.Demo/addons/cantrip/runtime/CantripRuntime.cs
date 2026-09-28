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
    /// <para>
    /// No helper method may be added to this class. Godot's source generator publishes every
    /// ordinary method of a <c>[GlobalClass]</c> to script whatever its C# accessibility says, so a
    /// private helper here is a method a game can call and one this addon has promised to keep.
    /// Helpers belong on <see cref="RunLoop"/>, <see cref="VariantMap"/> or
    /// <see cref="GodotContentLoader"/>, none of which the generator reads.
    /// </para>
    /// </remarks>
    [GlobalClass]
    public partial class CantripRuntime : Node
    {
        private readonly GodotEffectHost _host = new GodotEffectHost();
        private readonly ChoiceBridge _choices = new ChoiceBridge();
        private readonly VariantMap.Marshal _marshal = new VariantMap.Marshal();
        private readonly RunLoop _loop;

        private CardRuntime? _core;
        private CantripDebugAgent? _debug;
        private DescriptionBuilder? _describer;
        private string[] _trackedStats = { "hp", "block", "energy" };
        private int _describerGeneration = -1;
        private bool _busy;
        private bool _wasInBattle;

        public CantripRuntime() => _loop = new RunLoop(this);

        /// <summary>
        /// Where <c>.cantrip</c> files are discovered, as a <c>res://</c> path. Left empty, which is
        /// the default, the project setting <c>cantrip/content/folder</c> is read, and
        /// <c>res://content</c> when that is not set either — so the editor dock and the running
        /// game read one folder rather than two that can disagree without either saying so.
        /// </summary>
        [Export]
        public string ContentFolder { get; set; } = string.Empty;

        /// <summary>
        /// Loads content when the node enters the tree, reporting any errors and warnings in the
        /// Output panel. Turn it off to load by hand and receive them from <see cref="LoadContent"/>.
        /// </summary>
        [Export]
        public bool AutoLoad { get; set; } = true;

        /// <summary>
        /// The seed every roll comes from. The same seed and the same inputs replay exactly. Every
        /// value is a seed of its own, 0 and negative numbers included.
        /// </summary>
        [Export]
        public long Seed { get; set; } = 1;

        /// <summary>Records the causality trace. Off by default, because it is not free.</summary>
        [Export]
        public bool Trace { get; set; }

        /// <summary>Real time rather than turns: the clock advances by ticks from a TickDriver.</summary>
        [Export]
        public bool RealTime { get; set; }

        /// <summary>
        /// The clock rate <c>cooldown 8s</c> in content converts through. A <see cref="Driver"/> is
        /// put on this rate as the node enters the tree, so the two cannot mean different lengths.
        /// </summary>
        [Export]
        public int TicksPerSecond { get; set; } = 60;

        /// <summary>
        /// The stats each event carries for the entities it touched, and the stats a choice's
        /// options carry. An animation plays after the whole action resolved, so live stats would
        /// show the end of the story; these are the values as that event happened.
        /// </summary>
        /// <remarks>
        /// Read live rather than captured when the rules come into being. A choice's options always
        /// read it live, so setting it afterwards used to change one of the two dictionaries it
        /// names and not the other, which is the kind of difference nobody goes looking for.
        /// </remarks>
        [Export]
        public string[] TrackedStats
        {
            get => _trackedStats;
            set
            {
                _trackedStats = value ?? new string[0];
                _host.TrackedStats = _trackedStats;
            }
        }

        /// <summary>Optional: paces events one at a time instead of emitting them all at once.</summary>
        [Export]
        public BattlePresenter? Presenter { get; set; }

        /// <summary>Optional: drives <see cref="Tick"/> from the physics step in a real-time game.</summary>
        [Export]
        public TickDriver? Driver { get; set; }

        [Signal]
        public delegate void EffectEventEventHandler(Godot.Collections.Dictionary effect_event);

        [Signal]
        public delegate void BattleStartedEventHandler();

        [Signal]
        public delegate void BattleEndedEventHandler(bool won);

        [Signal]
        public delegate void ChoiceRequestedEventHandler(Godot.Collections.Dictionary request);

        /// <summary>Carries the whole report <see cref="ReloadContent"/> returns, not only its problems.</summary>
        [Signal]
        public delegate void ContentReloadedEventHandler(Godot.Collections.Dictionary report);

        /// <summary>The rules engine itself, for a game written in C#.</summary>
        public CardRuntime Core => EnsureRuntime();

        public ContentLibrary Content { get; private set; } = new ContentLibrary();

        internal EventBuffer Buffer => _host.Buffer;

        public override void _Ready()
        {
            if (Driver != null)
            {
                Driver.Drive = count => Tick(count);

                // One rate written in two places. A driver left at another rate makes `cooldown 8s`
                // mean two different lengths of time, and neither node would have said anything.
                if (Driver.TicksPerSecond != TicksPerSecond)
                {
                    GD.PushWarning(
                        "Cantrip: the TickDriver ticks at " + Driver.TicksPerSecond + " per second and this runtime at "
                        + TicksPerSecond + ". Content converts through the runtime's rate, so the driver has been put on it.");
                    Driver.TicksPerSecond = TicksPerSecond;
                }
            }

            // Nobody receives what an automatic load returns, so the Output panel is told instead. A
            // content error the editor dock alone shows is invisible to whoever is running the game.
            if (AutoLoad) Report(Load(string.Empty));
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
        /// Discovers and loads every <c>.cantrip</c> file under a folder, returning a report whose
        /// <c>ok</c> says whether the content came in clean. This goes through the engine's own file
        /// access, so it works the same in the editor and inside an exported game, where the
        /// project's files are not on disk at all.
        /// </summary>
        public Godot.Collections.Dictionary LoadContent(string folder = "") => VariantMap.ContentReport(Load(folder));

        /// <summary>
        /// Reloads content into a running game and rebinds everything live to it. Stats the game has
        /// changed keep their values; a card still at its printed cost takes the new one. The report
        /// is <see cref="LoadContent"/>'s, with what the rebind did added to it, and the
        /// <c>ContentReloaded</c> signal carries the same dictionary.
        /// </summary>
        public Godot.Collections.Dictionary ReloadContent(Godot.Collections.Array? paths = null)
        {
            CardRuntime core = EnsureRuntime();
            _loop.Guard();

            IReadOnlyList<string> files = paths == null || paths.Count == 0
                ? GodotContentLoader.Discover(GodotContentLoader.ResolveFolder(ContentFolder))
                : VariantMap.ToStrings(paths);

            DiagnosticBag problems = GodotContentLoader.LoadInto(Content, files);
            CardRuntime.ReloadReport rebind = core.ApplyContentChanges();
            _describerGeneration = -1;

            Godot.Collections.Dictionary report = VariantMap.ContentReport(problems);
            report["rebound"] = rebind.Rebound;
            report["missing"] = VariantMap.Strings(rebind.Missing);
            report["ruleset_changed"] = rebind.RulesetChanged;

            EmitSignal(SignalName.ContentReloaded, report);
            return report;
        }

        // Setup ----------------------------------------------------------------------------------
        //
        // These run no rules, so they do not go through Act, but they do change the game, so a host
        // callback may no more call them than play a card.

        public int CreatePlayer(string name = "Player", int hp = 80, int max_energy = 3)
        {
            CardRuntime core = EnsureRuntime();
            _loop.Guard();
            return core.CreatePlayer(name, hp, max_energy).Id;
        }

        public int AddCard(string name, string zone = Zones.Draw)
        {
            CardRuntime core = EnsureRuntime();
            _loop.Guard();

            VariantMap.WarnUnknownZone(zone, nameof(AddCard));
            return core.AddCard(name, zone).Id;
        }

        public Godot.Collections.Array AddDeck(Godot.Collections.Array names)
        {
            CardRuntime core = EnsureRuntime();
            _loop.Guard();

            var ids = new Godot.Collections.Array();
            foreach (string name in VariantMap.ToStrings(names)) ids.Add(core.AddCard(name).Id);
            return ids;
        }

        public int AddRelic(string name) => Act(() => EnsureRuntime().AddRelic(name).Id);

        /// <summary>
        /// Adds an enemy, with the health its content declares unless <paramref name="hp"/> is
        /// positive. A negative number, -1 by convention, means the content's own.
        /// </summary>
        /// <remarks>
        /// 0 used to mean the content's health, so a game that worked one out and arrived at 0
        /// spawned an enemy at full health instead. It is refused rather than read either way,
        /// because a game asking for an enemy with no health is asking for something definite and a
        /// silent substitution is the one answer that cannot be right.
        /// </remarks>
        public int SpawnEnemy(string name, int hp = -1)
        {
            CardRuntime core = EnsureRuntime();
            _loop.Guard();

            if (hp == 0)
                throw new ArgumentOutOfRangeException(
                    nameof(hp), hp, "An enemy cannot be spawned with 0 health. Pass -1 for the health its content declares.");

            return core.SpawnEnemy(name, hp < 0 ? (int?)null : hp).Id;
        }

        /// <summary>
        /// Applies a status to an entity, as the player. Returns the status's id, or 0 when the
        /// target is unknown or no status of that name is loaded.
        /// </summary>
        /// <remarks>
        /// Both failures answer 0. They used to differ — an unknown target gave 0 and an unknown
        /// name threw, which from GDScript is null — so a caller checking for 0, as this method's
        /// own documentation says to, was right only half the time.
        /// </remarks>
        public int ApplyStatus(string status, int target_id, int stacks = 1)
        {
            CardRuntime core = EnsureRuntime();
            Entity? target = core.State.Find(target_id);
            if (target == null) return VariantMap.NoEntity;
            if (Content.Find(status ?? string.Empty, "status") == null) return VariantMap.NoEntity;

            return Act(() => core.ApplyStatus(status!, target, stacks)?.Id ?? VariantMap.NoEntity);
        }

        /// <summary>
        /// Attaches an ability to an actor. Returns its id, or 0 when the owner is unknown or no
        /// ability of that name is loaded, as <see cref="ApplyStatus"/> answers.
        /// </summary>
        public int GrantAbility(string name, int owner_id)
        {
            CardRuntime core = EnsureRuntime();
            _loop.Guard();

            Entity? owner = core.State.Find(owner_id);
            if (owner == null) return VariantMap.NoEntity;
            if (Content.Find(name ?? string.Empty, "ability") == null) return VariantMap.NoEntity;

            return core.GrantAbility(name!, owner).Id;
        }

        /// <summary>
        /// Takes a card out of the game for good, as <c>destroy</c> does in content and as
        /// <c>Execute("destroy target", 0, cardId)</c> would: the way to remove a card from the deck
        /// between battles, or to swap one for its upgraded definition. Content hears it as
        /// <c>destroyed</c>. Returns whether the card is gone; false, having changed nothing, when
        /// the id is not a card still in the game.
        /// </summary>
        public bool RemoveCard(int card_id)
        {
            CardRuntime core = EnsureRuntime();
            _loop.Guard();

            Entity? card = core.State.Find(card_id);
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
            _loop.Guard();

            _debug?.Uninstall();
            _debug = null;

            // Lets the old runtime go of its clock, so it stops resolving effects on a game that has
            // been replaced. The node makes a clock per runtime, so nothing else changes here.
            _core?.Dispose();
            _core = null;

            _choices.Close();
            _loop.Forget();
            Buffer.Reset();
            if (Presenter != null && IsInstanceValid(Presenter)) Presenter.SkipAll();

            EnsureRuntime();
        }

        // Battle flow ----------------------------------------------------------------------------

        public void StartBattle(bool shuffle = true, bool draw_opening_hand = true)
        {
            CardRuntime core = EnsureRuntime();
            Act(() =>
            {
                core.StartBattle(shuffle, draw_opening_hand);
                return true;
            });

            if (core.State.InBattle) EmitSignal(SignalName.BattleStarted);
        }

        /// <summary>
        /// Plays a card. The answer is one of "played", "pending", "not_a_card", "not_in_hand",
        /// "unplayable", "not_enough_energy", "invalid_target" or "cancelled"; "pending" means the
        /// rules need a decision and a <c>choice_requested</c> signal is on its way.
        /// </summary>
        public string Play(int card_id, int target_id = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(card_id);
            if (card == null) return Words.ActionName(ActionResult.NotACard);

            Entity? target = target_id == VariantMap.NoEntity ? null : core.State.Find(target_id);
            return Act(() => Words.ActionName(core.Play(card, target)));
        }

        /// <summary>Plays the first card of that name in hand, for a game that thinks in names.</summary>
        public string PlayNamed(string card_name, int target_id = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? target = target_id == VariantMap.NoEntity ? null : core.State.Find(target_id);
            return Act(() => Words.ActionName(core.Play(card_name, target)));
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

        /// <summary>
        /// Uses an ability, answering with a word from the same table <see cref="Play"/> answers
        /// from: "played", "pending", "not_ready" for a cooldown, "not_enough_energy",
        /// "invalid_target", "cancelled", or "not_a_card" for an id that is not an ability that can
        /// be used at all.
        /// </summary>
        /// <remarks>
        /// It used to answer a bool, so a cooldown, a question the rules stopped to ask, and an id
        /// naming nothing were all one <c>false</c>, and a real-time game could not tell the player
        /// why the ability did not fire.
        /// </remarks>
        public string UseAbility(int ability_id, int target_id = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? ability = core.State.Find(ability_id);
            if (ability == null) return Words.ActionName(ActionResult.NotACard);

            Entity? target = target_id == VariantMap.NoEntity ? null : core.State.Find(target_id);
            return Act(() => Words.ActionName(core.UseAbility(ability, target)));
        }

        /// <summary>
        /// Runs statements as content would, as the player unless <paramref name="self_id"/> names
        /// someone else: a console line, a cheat key, or a small step a game takes between battles,
        /// such as a rest with <c>Execute("heal 12", 0, 0)</c>.
        /// </summary>
        /// <remarks>
        /// <paramref name="self_id"/> 0 means the player, as <see cref="GetZone"/>'s owner does;
        /// <paramref name="target_id"/> 0 means nobody, as it does everywhere else. The text is
        /// parsed on every call and the linter never sees it, so a mistake shows only when the line
        /// runs, as an error in the Output panel. Keep it to a line or two: anything longer,
        /// anything run often, and anything a card, relic or status should own belongs in content,
        /// where it is checked and tested. Like any other action it can stop for a choice.
        /// </remarks>
        public void Execute(string statements, int self_id = 0, int target_id = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? self = self_id == VariantMap.NoEntity ? null : core.State.Find(self_id);
            Entity? target = target_id == VariantMap.NoEntity ? null : core.State.Find(target_id);

            Act(() =>
            {
                core.Execute(statements, self, target);
                return true;
            });
        }

        // Queries --------------------------------------------------------------------------------

        /// <summary>
        /// The ids in one of an owner's zones, such as <c>GetZone(PlayerId(), "hand")</c>.
        /// </summary>
        /// <remarks>
        /// An <paramref name="owner_id"/> of 0 means the player, which is the one place besides
        /// <see cref="Execute"/>'s <c>self_id</c> where 0 does not mean nobody. Pass
        /// <see cref="PlayerId"/> wherever the id is worked out rather than written down, so that an
        /// id that happens to come out 0 does not read someone else's pile.
        /// </remarks>
        public Godot.Collections.Array GetZone(int owner_id, string zone)
        {
            CardRuntime core = EnsureRuntime();
            VariantMap.WarnUnknownZone(zone, nameof(GetZone));

            Entity? owner = owner_id == VariantMap.NoEntity ? core.Player : core.State.Find(owner_id);
            return VariantMap.Ids(core.State.ZoneOf(owner, zone));
        }

        public Godot.Collections.Array GetEnemies() => VariantMap.Ids(EnsureRuntime().State.Actors(Team.Enemy));

        public Godot.Collections.Array GetAllies() => VariantMap.Ids(EnsureRuntime().State.Actors(Team.Player));

        public Godot.Collections.Array GetActors() => VariantMap.Ids(EnsureRuntime().State.Actors());

        public int PlayerId() => EnsureRuntime().Player?.Id ?? VariantMap.NoEntity;

        /// <summary>Everything a UI shows about one entity. Empty when the id is unknown.</summary>
        public Godot.Collections.Dictionary GetEntity(int entity_id)
        {
            Entity? entity = EnsureRuntime().State.Find(entity_id);
            return entity == null ? new Godot.Collections.Dictionary() : VariantMap.Entity(EntityView.Of(entity));
        }

        /// <summary>One stat after modifiers, which is the number the rules would use now.</summary>
        public int GetStat(int entity_id, string stat)
        {
            Entity? entity = EnsureRuntime().State.Find(entity_id);
            return entity == null ? 0 : entity.GetInt(stat);
        }

        public int CostOf(int card_id)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(card_id);
            return card == null ? 0 : core.CostOf(card);
        }

        /// <summary>
        /// Whether <c>Play</c> would accept the card now: in hand, affordable in whatever it is
        /// priced in, and with a legal target if it needs one.
        /// </summary>
        public bool CanPlay(int card_id)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(card_id);
            return card != null && core.CanPlay(card);
        }

        /// <summary>
        /// What the card or ability asks to be aimed at: whatever word content wrote after <c>target</c>,
        /// usually "enemy", "ally", "self", "any" or "none". Empty, rather than "none", for an id
        /// that names nothing, so a UI can tell a stale id from a card that needs no target.
        /// </summary>
        public string GetTargetMode(int card_id)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(card_id);
            return card == null ? string.Empty : core.TargetMode(card);
        }

        /// <summary>
        /// The entities that card or ability may be aimed at, after its own <c>target … where</c>
        /// filter and content's <c>targetable</c> rules, so a UI highlights exactly what <c>Play</c>
        /// and <c>UseAbility</c> accept.
        /// </summary>
        public Godot.Collections.Array GetLegalTargets(int card_id)
        {
            CardRuntime core = EnsureRuntime();
            Entity? card = core.State.Find(card_id);
            return card == null ? new Godot.Collections.Array() : VariantMap.Ids(core.LegalTargets(card));
        }

        /// <summary>The rules text with live values, ready for a card frame.</summary>
        public Godot.Collections.Dictionary Describe(int entity_id, int target_id = 0)
        {
            CardRuntime core = EnsureRuntime();
            Entity? entity = core.State.Find(entity_id);
            if (entity == null) return new Godot.Collections.Dictionary();

            Entity? target = target_id == VariantMap.NoEntity ? null : core.State.Find(target_id);
            return VariantMap.Description(DescriptionView.Of(Describer().Describe(entity, core, target)));
        }

        /// <summary>What an enemy will do next. Empty until intents have been rolled.</summary>
        public Godot.Collections.Dictionary DescribeIntent(int enemy_id)
        {
            CardRuntime core = EnsureRuntime();
            Entity? enemy = core.State.Find(enemy_id);
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
        /// <remarks>
        /// The kind here is the keyword that declares the definition in content — "card", "relic",
        /// "enemy" — and not the <c>kind</c> an entity's dictionary carries, which says what it is
        /// in the rules: the Slime spawned from <c>enemy Slime</c> reads back as an "actor". It
        /// reads the content alone, so it does not bring the rules into being.
        /// </remarks>
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
        /// <remarks>
        /// The kind is the declaring keyword, as <see cref="DescribeDefinition"/>'s is, not an
        /// entity's <c>kind</c>. It reads the content alone, so it does not bring the rules into
        /// being.
        /// </remarks>
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
        /// to where that action started, so it replays from there with the answer in place. The
        /// answer always carries <c>result</c>, which is empty when the answer was refused.
        /// </summary>
        public Godot.Collections.Dictionary AnswerChoice(int request_id, Godot.Collections.Array chosen)
        {
            CardRuntime core = EnsureRuntime();
            ChoiceAnswer answer = _choices.Validate(request_id, VariantMap.ToIds(chosen));
            if (!answer.Accepted) return VariantMap.Refused(answer.ReasonName, answer.Message, "result", string.Empty);

            // An offer is answered with the number of the pick; the core wants the definition itself.
            PendingChoice pending = _choices.Current!;
            string result = pending.IsOffer
                ? Act(() => Words.ActionName(core.Answer(pending.Definitions[ChoiceBridge.OfferPosition(answer.EntityIds[0])])))
                : Act(() => Words.ActionName(core.Answer(answer.EntityIds)));
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
            _loop.Guard();

            core.CancelPending();
            _loop.SyncChoice();
        }

        // Save and load --------------------------------------------------------------------------

        /// <summary>
        /// Whether a save would succeed: not while effects are resolving, nor while a block that a
        /// reload has changed is still waiting to run. <see cref="Save"/> answers the same question
        /// and says which of the two it is, so this is for greying out a button, not for telling
        /// the player what happened.
        /// </summary>
        public bool CanSave() => !_busy && EnsureRuntime().CanCapture;

        /// <summary>
        /// The whole game as a string, in the same <c>accepted</c>, <c>reason</c>, <c>message</c>
        /// dictionary <see cref="LoadSave"/> answers with, with the string itself under <c>save</c>.
        /// The reason is "resolving" while effects are still running and "reload_pending" for a
        /// waiting block a reload has changed, whose message is the rules' own.
        /// </summary>
        /// <remarks>
        /// It used to throw, which from GDScript is a stack trace in the Output panel and a bare
        /// null. The message saying which of the two had stopped the save was the one thing a save
        /// button needed and the one thing thrown away.
        /// </remarks>
        public Godot.Collections.Dictionary Save()
        {
            CardRuntime core = EnsureRuntime();

            // The rules see resolving only while their queue runs. A callback in the middle of a
            // card's own effect finds the queue empty, and a snapshot taken there would hold half an
            // action, so the node, which knows an action is under way, refuses it itself.
            if (_busy)
            {
                return VariantMap.Refused(
                    SaveCheck.NameOf(SaveRejection.Resolving),
                    "Cannot save while effects are still resolving.",
                    "save",
                    string.Empty);
            }

            GameSnapshot snapshot;
            try
            {
                snapshot = core.Capture();
            }
            catch (InvalidOperationException error)
            {
                // Nothing is resolving, since the node is not busy, so this is the rules' other
                // refusal: a waiting block whose statements a reload has changed. Only their own
                // message names the definition that block belongs to.
                return VariantMap.Refused(SaveCheck.NameOf(SaveRejection.ReloadPending), error.Message, "save", string.Empty);
            }

            SaveEnvelope envelope = SaveEnvelope.Wrap(Content.Fingerprint, JsonSerializer.Serialize(snapshot));
            string saved = JsonSerializer.Serialize(new SaveFile
            {
                Format = envelope.Format,
                Fingerprint = envelope.Fingerprint,
                Snapshot = envelope.Payload,
            });

            return new Godot.Collections.Dictionary
            {
                ["accepted"] = true,
                ["reason"] = "none",
                ["message"] = string.Empty,
                ["save"] = saved,
            };
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
            _loop.Guard();

            SaveFile? file;
            try
            {
                file = JsonSerializer.Deserialize<SaveFile>(json, ReadOptions);
            }
            catch (JsonException error)
            {
                return VariantMap.Refused("wrong_format", "This does not look like a save file: " + error.Message);
            }

            if (file == null) return VariantMap.Refused("no_payload", "This save carries no game to restore.");

            SaveEnvelope envelope = new SaveEnvelope(file.Format, file.Fingerprint, file.Snapshot);
            SaveCheck check = envelope.Check(Content.Fingerprint);
            if (!check.Accepted && check.Reason != SaveRejection.ContentChanged) return VariantMap.Refused(check.ReasonName, check.Message);

            // An envelope from an older addon is read, not refused; only a newer one is refused,
            // above, because this addon cannot know what is in it.
            envelope = envelope.Upgraded();

            GameSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<GameSnapshot>(envelope.Payload, ReadOptions);
            }
            catch (JsonException error)
            {
                return VariantMap.Refused("wrong_format", "The game in this save cannot be read: " + error.Message);
            }

            if (snapshot == null) return VariantMap.Refused("no_payload", "This save carries no game to restore.");

            // The rules would refuse this too, but it is a save from another version of Cantrip.Core,
            // not one from other content, and a game may want to tell the player so. Only a save
            // needing a reader newer than this build is turned away: an older save is restored,
            // which is the whole of the promise in docs/stability.md.
            int needs = GameSnapshot.ReaderNeededBy(snapshot);
            if (needs > GameSnapshot.CurrentFormat)
            {
                return VariantMap.Refused("wrong_format",
                    "The game in this save is in format " + snapshot.FormatVersion + " and needs a Cantrip.Core that reads format "
                    + needs + "; this one reads up to format " + GameSnapshot.CurrentFormat + ".");
            }

            try
            {
                core.Restore(snapshot);
            }
            catch (InvalidOperationException error)
            {
                // The rules refuse before they change anything, so there is nothing to put back.
                return VariantMap.Refused("content_changed", error.Message);
            }

            _choices.Close();
            _loop.Forget();
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

        /// <summary>
        /// The node's loop: the re-entry guard, the event drain and the choice announcement.
        /// </summary>
        /// <remarks>
        /// A plain object the node holds, as <see cref="ChoiceBridge"/> and
        /// <see cref="GodotEffectHost"/> are, and for a reason beyond tidiness. Godot's source
        /// generator publishes every ordinary method of a <c>[GlobalClass]</c> to script whatever
        /// its C# accessibility says, so as members of the node these three were callable from
        /// GDScript, and at 1.0 that would be a promise to keep them: a script calling
        /// <c>AfterAction</c> re-enters the drain and tells the game every buffered event twice.
        /// </remarks>
        private sealed class RunLoop
        {
            private readonly CantripRuntime _node;
            private bool _draining;
            private int _announced;

            public RunLoop(CantripRuntime node) => _node = node;

            /// <summary>Forgets which question has been announced, for a game starting over.</summary>
            public void Forget() => _announced = 0;

            /// <summary>
            /// Refuses a call made while the rules are mid-effect. That can only happen from a host
            /// callback, which the interpreter invokes in the middle of resolving; such a callback
            /// must answer and return. Acting again from a signal handler is fine: by then the
            /// action is over.
            /// </summary>
            /// <remarks>
            /// A query such as <c>CanPlay</c> or <c>Describe</c> evaluates content too, and so can
            /// call a callback without any action running, which is why the host is asked as well.
            /// </remarks>
            public void Guard()
            {
                if (_node._busy || _node._host.InCallback)
                    throw new InvalidOperationException(
                        "The rules are resolving. A host callback must answer and return; it cannot set the game up, play a card, end a turn or load a save while the rules wait for its answer.");
            }

            /// <summary>Hands the game everything one finished action left behind.</summary>
            public void AfterAction()
            {
                // A handler that acts again arrives here a second time while the first drain is
                // still walking the buffer. The outer one tells this action's events once its own
                // batch is done, and then whether the battle ended, so the game hears them in the
                // order they happened. The handler may be waiting on a choice its call left, so
                // that is told now.
                if (_draining)
                {
                    SyncChoice();
                    return;
                }

                _draining = true;
                try
                {
                    // Until nothing is left: a batch holds only what was recorded before it began,
                    // and a handler that acts adds more. One that starts a new run ends this one:
                    // the rest of the batch is not told, since its ids would now name the new run's
                    // entities, while anything the handler then does in the new run is.
                    while (_node.Buffer.Count > 0)
                    {
                        CardRuntime? run = _node._core;
                        if (_node.Presenter != null && IsInstanceValid(_node.Presenter)) _node.Presenter.Drain(_node.Buffer);
                        else _node.Buffer.Drain(record =>
                        {
                            if (_node._core == run) _node.EmitSignal(SignalName.EffectEvent, VariantMap.Event(record));
                        });
                    }
                }
                finally
                {
                    _draining = false;
                }

                SyncChoice();

                bool inBattle = _node._core != null && _node._core.State.InBattle;
                if (_node._wasInBattle && !inBattle) _node.EmitSignal(SignalName.BattleEnded, _node._core?.Won ?? false);
                _node._wasInBattle = inBattle;
            }

            /// <summary>Tells the game about a decision the rules are waiting on, once per request.</summary>
            public void SyncChoice()
            {
                int id = _node._choices.Sync(_node._core?.Pending);
                if (id == 0)
                {
                    _announced = 0;
                    return;
                }

                if (id == _announced) return;
                _announced = id;

                PendingChoice? pending = _node._choices.Current;
                if (pending != null)
                {
                    _node.EmitSignal(
                        SignalName.ChoiceRequested,
                        VariantMap.Choice(id, pending, _node._trackedStats, _node.OfferText));
                }
            }
        }

        private DiagnosticBag Load(string folder)
        {
            if (_core != null) throw new InvalidOperationException("Content is already in play; use ReloadContent to change it.");

            Content = new ContentLibrary();
            DiagnosticBag problems = GodotContentLoader.LoadFolder(
                Content, GodotContentLoader.ResolveFolder(string.IsNullOrEmpty(folder) ? ContentFolder : folder));
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
                // Every value is its own seed. It used to be clamped up to 1, so 0, 1 and -5 all
                // played the same game and nothing said why.
                Seed = unchecked((ulong)Seed),
                Trace = Trace,
                Host = _host,
                Chooser = new DeferredChooser(),
            };
            if (RealTime) options.Clock = new TickClock(Math.Max(1, TicksPerSecond));

            _core = new CardRuntime(Content, options);
            _host.State = _core.State;
            _host.TrackedStats = _trackedStats;
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
            _loop.Guard();

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
            _loop.AfterAction();
            return result;
        }
    }
}
