using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Diagnostics;

namespace Cantrip.Runtime
{
    /// <summary>
    /// The causal chain an effect is running in: which listeners are its ancestors and how deep it
    /// is. Immutable, so a queued trigger can carry its chain without copying.
    /// </summary>
    public sealed class Chain
    {
        private Chain(Chain? parent, int listenerId, int depth, long rootId)
        {
            Parent = parent;
            ListenerId = listenerId;
            Depth = depth;
            RootId = rootId;
        }

        /// <summary>
        /// Starts a fresh chain for a top-level action such as playing a card. Internal: chain roots
        /// are handed out by the interpreter and are what <c>once per chain</c> counts, so game code
        /// that minted its own could give two actions the same root and quietly defeat the limit.
        /// </summary>
        internal static Chain NewRoot(long rootId) => new Chain(null, 0, 0, rootId);

        /// <summary>
        /// The link this one was extended from, or null at the root. Walking up it is how
        /// <see cref="Contains"/> and the depth limit are answered.
        /// </summary>
        public Chain? Parent { get; }

        /// <summary>The listener that extended the chain to this link; 0 for the root.</summary>
        public int ListenerId { get; }

        /// <summary>
        /// How many listeners deep this is. The ruleset's call-depth limit is checked against it, which
        /// is what stops a chain of reactions from running out of stack.
        /// </summary>
        public int Depth { get; }

        /// <summary>Identifies the whole chain, for <c>once per chain</c> limits.</summary>
        public long RootId { get; }

        /// <summary>
        /// Whether a listener is already somewhere up this chain — the check that keeps a listener from
        /// re-triggering itself through its own effect. A listener id of 0 is the root and is never
        /// contained.
        /// </summary>
        public bool Contains(int listenerId)
        {
            for (Chain? link = this; link != null; link = link.Parent)
            {
                if (link.ListenerId == listenerId && listenerId != 0) return true;
            }
            return false;
        }

        /// <summary>
        /// A new link for a listener about to run, one deeper and keeping the same
        /// <see cref="RootId"/> — so everything set off by one action shares a root, which is what
        /// <c>once per chain</c> counts.
        /// </summary>
        public Chain Extend(int listenerId) => new Chain(this, listenerId, Depth + 1, RootId);
    }

    /// <summary>
    /// Everything an expression or statement can see while it runs: who is acting, on whom, which
    /// event triggered it, and its local variables.
    /// </summary>
    public sealed class EvalContext
    {
        private readonly EvalContext? _parent;
        private Dictionary<string, Value>? _locals;

        /// <summary>
        /// A root context for running content outside a listener. It starts a fresh chain, so anything
        /// it sets off is counted as its own action for <c>once per chain</c> purposes.
        /// </summary>
        /// <param name="self">The entity whose content is about to run, or null for statements that belong to nothing.</param>
        public EvalContext(Entity? self)
        {
            Self = self;
            Chain = Chain.NewRoot(0);
        }

        private EvalContext(EvalContext parent)
        {
            _parent = parent;
            Self = parent.Self;
            Source = parent.Source;
            Target = parent.Target;
            Card = parent.Card;
            Event = parent.Event;
            It = parent.It;
            ItDefinition = parent.ItDefinition;
            ItIsFocus = parent.ItIsFocus;
            Focus = parent.Focus;
            Chain = parent.Chain;
            UndoScope = parent.UndoScope;
        }

        /// <summary>The entity whose content is running: the card, status or relic.</summary>
        public Entity? Self { get; set; }

        /// <summary>Who is acting: the card's player, the status's applier, the relic's holder.</summary>
        public Entity? Source { get; set; }

        /// <summary>
        /// What the effect is aimed at. Inside a listener this is the event's target unless the body
        /// changed it, and it is null for an effect that targets nothing.
        /// </summary>
        public Entity? Target { get; set; }

        /// <summary>The card being played, if any. Its tags become the tags of the damage it deals.</summary>
        public Entity? Card { get; set; }

        /// <summary>The triggering event, inside a listener.</summary>
        public GameEvent? Event { get; set; }

        /// <summary>The candidate being tested inside <c>where</c>, or the subject of a modifier.</summary>
        public Entity? It { get; set; }

        /// <summary>
        /// The candidate being tested inside a <c>where</c> over definitions, as <c>discover</c> uses:
        /// content that nothing has been made from yet. Never set at the same time as <see cref="It"/>,
        /// so only one kind of candidate is ever in focus.
        /// </summary>
        public Cantrip.Content.EntityDefinition? ItDefinition { get; set; }

        /// <summary>
        /// True while evaluating a <c>where</c> predicate: qualifiers such as <c>tag:fire</c> then test
        /// <see cref="It"/> rather than the event or action being modified.
        /// </summary>
        public bool ItIsFocus { get; set; }

        /// <summary>The value being computed, while evaluating a modifier's filter.</summary>
        public ModifierQuery? Focus { get; set; }

        /// <summary>
        /// The causal chain this is running in. Replacing it rather than extending it starts a new
        /// chain, which resets what <c>once per chain</c> has counted.
        /// </summary>
        public Chain Chain { get; set; }

        /// <summary>Set inside <c>until</c> blocks: changes are recorded here so they can be undone.</summary>
        public ScheduledAction? UndoScope { get; set; }

        /// <summary>The actor responsible for this effect.</summary>
        public Entity? Controller => Source?.Controller ?? Self?.Controller;

        /// <summary>A child scope: same actors, fresh locals that shadow the parent's.</summary>
        public EvalContext Derive() => new EvalContext(this);

        /// <summary>
        /// Looks a local up through this scope and its parents, innermost first. False leaves
        /// <paramref name="value"/> at <see cref="Value.None"/>, which is also what a local explicitly
        /// set to none reads as — so the return value is the one to test.
        /// </summary>
        public bool TryGetLocal(string name, out Value value)
        {
            for (EvalContext? scope = this; scope != null; scope = scope._parent)
            {
                if (scope._locals != null && scope._locals.TryGetValue(name, out value)) return true;
            }
            value = Value.None;
            return false;
        }

        /// <summary>
        /// Binds a local in this scope, shadowing any of the same name in a parent. Names are compared
        /// without case, as everywhere else, and the binding lasts as long as this context does.
        /// </summary>
        public void SetLocal(string name, Value value)
        {
            _locals ??= new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            _locals[name] = value;
        }

        /// <summary>Names of every local visible from this scope, for "did you mean" suggestions.</summary>
        public IEnumerable<string> LocalNames()
        {
            for (EvalContext? scope = this; scope != null; scope = scope._parent)
            {
                if (scope._locals == null) continue;
                foreach (string name in scope._locals.Keys) yield return name;
            }
        }
    }

    /// <summary>
    /// The game's side of the integration. Everything is optional: a host only
    /// implements the parts the library cannot know, such as spatial queries or presentation.
    /// </summary>
    /// <remarks>
    /// Every member has a default that does nothing, so a host implements only the parts it cares
    /// about, and a member added in a later release does not break one written today. See
    /// <see href="https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md">Stability</see>.
    /// </remarks>
    public interface IEffectHost
    {
        /// <summary>Supplies custom names such as game-specific selectors. Return false to fall through.</summary>
        bool TryResolveName(string name, EvalContext context, out Value value)
        {
            value = Value.None;
            return false;
        }

        /// <summary>Supplies custom functions such as <c>within(...)</c> for spatial games.</summary>
        bool TryCall(string function, IReadOnlyList<Value> arguments, EvalContext context, out Value value)
        {
            value = Value.None;
            return false;
        }

        /// <summary>
        /// Called after each event's after phase resolves. VFX, audio and UI hang off this; the
        /// library never plays anything itself.
        /// </summary>
        void OnEvent(GameEvent gameEvent)
        {
        }
    }

    /// <summary>
    /// A host that adds nothing. The default, and a convenience: since every member of
    /// <see cref="IEffectHost"/> has a default of its own, a host can implement the interface
    /// directly and override only what it needs.
    /// </summary>
    public class EffectHostBase : IEffectHost
    {
        /// <summary>
        /// Answers nothing. Override to supply a name content uses that the engine does not know,
        /// returning true only for the names this host owns; returning true for a name the engine also
        /// resolves shadows it.
        /// </summary>
        public virtual bool TryResolveName(string name, EvalContext context, out Value value)
        {
            value = Value.None;
            return false;
        }

        /// <summary>
        /// Answers nothing. Override to supply a function content calls, such as a
        /// <c>within(target, 5m)</c> the world has to measure.
        /// </summary>
        public virtual bool TryCall(string function, IReadOnlyList<Value> arguments, EvalContext context, out Value value)
        {
            value = Value.None;
            return false;
        }

        /// <summary>
        /// Does nothing. Override to drive presentation from what happened. <b>Never call back into the
        /// runtime from here</b>: this runs while the action is still resolving, and an action started
        /// inside it re-enters the one that is running.
        /// </summary>
        public virtual void OnEvent(GameEvent gameEvent)
        {
        }
    }

    /// <summary>A pending player decision: choose a target, choose N cards, discover.</summary>
    public sealed class ChoiceRequest
    {
        /// <summary>
        /// A question about entities in the game. Content builds these; a game builds one only when it
        /// drives the interpreter itself.
        /// </summary>
        /// <param name="prompt">What to ask, as content wrote it.</param>
        /// <param name="options">What may be picked. An answer outside this list is ignored.</param>
        /// <param name="min">The fewest that must be picked.</param>
        /// <param name="max">The most that may be.</param>
        /// <param name="chooser">The actor deciding, or null when nobody in the game is.</param>
        /// <param name="span">The line of content that asked.</param>
        public ChoiceRequest(string prompt, IReadOnlyList<Entity> options, int min, int max, Entity? chooser, SourceSpan span)
        {
            Prompt = prompt;
            Options = options;
            Min = min;
            Max = max;
            Chooser = chooser;
            Span = span;
        }

        /// <summary>What content wrote as the prompt. Not localized.</summary>
        public string Prompt { get; }

        /// <summary>
        /// What may be picked, in the order the selector produced them. A chooser that answers with
        /// anything not in here has that part of its answer dropped rather than refused.
        /// </summary>
        public IReadOnlyList<Entity> Options { get; }

        /// <summary>
        /// The fewest that must be picked. Content may ask for more than <see cref="Options"/> holds, in
        /// which case everything on offer is taken.
        /// </summary>
        public int Min { get; }

        /// <summary>The most that may be picked.</summary>
        public int Max { get; }

        /// <summary>The actor making the choice.</summary>
        public Entity? Chooser { get; }

        /// <summary>The line of content that asked, so a tool can jump to it.</summary>
        public SourceSpan Span { get; }
    }

    /// <summary>
    /// Pluggable decision maker: UI, AI, random or scripted. Answers must be a
    /// subset of <see cref="ChoiceRequest.Options"/>; anything else is ignored.
    /// </summary>
    public interface IChoiceProvider
    {
        /// <summary>
        /// Answers a question about entities. The default takes as many of the first options as the
        /// request needs, which keeps a partly written chooser deterministic rather than stuck.
        /// </summary>
        /// <remarks>
        /// Whatever is returned is filtered to <see cref="ChoiceRequest.Options"/>, so a chooser cannot
        /// smuggle in an entity content did not offer. A UI that cannot answer on the spot uses
        /// <see cref="DeferredChooser"/> rather than blocking here.
        /// </remarks>
        IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state)
        {
            return request.Options.Take(Math.Max(request.Min, Math.Min(request.Max, request.Options.Count))).ToList();
        }

        /// <summary>
        /// Answers an offer of content that does not exist yet, as <c>discover</c> makes. The default
        /// takes the first candidate, the way an answer to <see cref="Choose"/> that falls short is
        /// topped up from the front, so a chooser that only decides between live entities needs
        /// nothing here.
        /// </summary>
        Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state)
        {
            return request.Options.Count == 0 ? null : request.Options[0];
        }
    }

    /// <summary>
    /// An offer of content that does not exist yet: the three cards a Discover shows before one of
    /// them is made. Kept apart from <see cref="ChoiceRequest"/>, whose options are live entities
    /// addressed by id all the way out to the editor bridge.
    /// </summary>
    public sealed class DefinitionChoice
    {
        /// <summary>
        /// An offer of content that does not exist yet. Always one pick from the list, which is why it
        /// carries no min or max.
        /// </summary>
        /// <param name="prompt">What to ask, as content wrote it.</param>
        /// <param name="options">The candidates. They are definitions: nothing has been made from any of them.</param>
        /// <param name="chooser">The actor deciding, or null when nobody in the game is.</param>
        /// <param name="span">The line of content that asked.</param>
        public DefinitionChoice(string prompt, IReadOnlyList<Cantrip.Content.EntityDefinition> options, Entity? chooser, SourceSpan span)
        {
            Prompt = prompt;
            Options = options;
            Chooser = chooser;
            Span = span;
        }

        /// <summary>What content wrote as the prompt. Not localized.</summary>
        public string Prompt { get; }

        /// <summary>
        /// The candidates on offer, drawn from content when the offer was made. A replay draws them
        /// again, so a reload between asking and answering can change this list.
        /// </summary>
        public IReadOnlyList<Cantrip.Content.EntityDefinition> Options { get; }

        /// <summary>The actor making the choice.</summary>
        public Entity? Chooser { get; }

        /// <summary>The line of content that asked, so a tool can jump to it.</summary>
        public SourceSpan Span { get; }
    }

    /// <summary>Always takes the first options offered. Deterministic, and the default.</summary>
    public sealed class FirstOptionChooser : IChoiceProvider
    {
        /// <summary>Takes the first options, as many as the request needs. Deterministic, which is why it is the default and what tests rely on.</summary>
        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state) =>
            request.Options.Take(Math.Max(request.Min, Math.Min(request.Max, request.Options.Count))).ToList();

        /// <summary>Takes the first candidate, or null when nothing is on offer.</summary>
        public Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state) =>
            request.Options.Count == 0 ? null : request.Options[0];
    }

    /// <summary>Picks uniformly at random from its own forked RNG stream, so it never disturbs game rolls.</summary>
    public sealed class RandomChooser : IChoiceProvider
    {
        private readonly Rng _rng;

        /// <summary>
        /// A chooser with a stream of its own, so its rolls never move the game's. A simulation seeds
        /// this separately from the run, which is what lets the same game be replayed against different
        /// decisions.
        /// </summary>
        public RandomChooser(ulong seed) => _rng = new Rng(seed);

        /// <summary>Shuffles the options and takes a count somewhere between the request's bounds — so it varies how many it picks, not only which.</summary>
        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state)
        {
            var options = request.Options.ToList();
            _rng.Shuffle(options);
            int count = request.Min >= request.Max ? request.Max : _rng.NextInt(request.Min, request.Max);
            return options.Take(Math.Min(count, options.Count)).ToList();
        }

        /// <summary>One candidate, uniformly, or null when nothing is on offer.</summary>
        public Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state) =>
            request.Options.Count == 0 ? null : request.Options[_rng.NextInt(0, request.Options.Count - 1)];
    }

    /// <summary>
    /// Answers from a queue of names, for tests and replays. Each answer is a comma-separated list
    /// of entity names; when the queue runs dry it falls back to the first options.
    /// </summary>
    public sealed class ScriptedChooser : IChoiceProvider
    {
        private readonly Queue<string> _answers = new Queue<string>();

        /// <summary>
        /// A chooser that reads from a queue of answers. Each answer is a comma-separated list of entity
        /// <b>names</b>, not ids, which is what lets a test written against content survive the entities
        /// being renumbered.
        /// </summary>
        /// <param name="answers">The answers, in the order the questions will be asked.</param>
        public ScriptedChooser(params string[] answers)
        {
            foreach (string answer in answers) _answers.Enqueue(answer);
        }

        /// <summary>Adds one more answer to the back of the queue.</summary>
        public void Enqueue(string answer) => _answers.Enqueue(answer);

        /// <summary>
        /// How many answers are left. Zero does not mean the script is finished: the chooser then
        /// quietly takes the first options, so a test that asks more questions than it answered still
        /// passes or fails on something.
        /// </summary>
        public int Remaining => _answers.Count;

        /// <summary>
        /// Takes the next answer and matches its names against the options, ignoring case. A name that
        /// matches nothing is skipped in silence, so a misspelled answer reads as "picked fewer" rather
        /// than as an error.
        /// </summary>
        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state)
        {
            if (_answers.Count == 0) return new FirstOptionChooser().Choose(request, state);

            var chosen = new List<Entity>();
            foreach (string name in _answers.Dequeue().Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                Entity? match = request.Options.FirstOrDefault(o =>
                    !chosen.Contains(o) && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) chosen.Add(match);
            }
            return chosen;
        }

        /// <summary>
        /// Answers a definition offer from the same queue, so a test says <c>answer Fireball</c>
        /// whether the options are live cards or cards that do not exist yet.
        /// </summary>
        public Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state)
        {
            if (request.Options.Count == 0) return null;
            if (_answers.Count == 0) return request.Options[0];

            foreach (string name in _answers.Dequeue().Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                Cantrip.Content.EntityDefinition? match = request.Options
                    .FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return request.Options[0];
        }
    }
}
