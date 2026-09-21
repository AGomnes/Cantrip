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

        /// <summary>Starts a fresh chain for a top-level action such as playing a card.</summary>
        public static Chain NewRoot(long rootId) => new Chain(null, 0, 0, rootId);

        public Chain? Parent { get; }

        /// <summary>The listener that extended the chain to this link; 0 for the root.</summary>
        public int ListenerId { get; }

        public int Depth { get; }

        /// <summary>Identifies the whole chain, for <c>once per chain</c> limits.</summary>
        public long RootId { get; }

        public bool Contains(int listenerId)
        {
            for (Chain? link = this; link != null; link = link.Parent)
            {
                if (link.ListenerId == listenerId && listenerId != 0) return true;
            }
            return false;
        }

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

        public Chain Chain { get; set; }

        /// <summary>Set inside <c>until</c> blocks: changes are recorded here so they can be undone.</summary>
        public ScheduledAction? UndoScope { get; set; }

        /// <summary>The actor responsible for this effect.</summary>
        public Entity? Controller => Source?.Controller ?? Self?.Controller;

        /// <summary>A child scope: same actors, fresh locals that shadow the parent's.</summary>
        public EvalContext Derive() => new EvalContext(this);

        public bool TryGetLocal(string name, out Value value)
        {
            for (EvalContext? scope = this; scope != null; scope = scope._parent)
            {
                if (scope._locals != null && scope._locals.TryGetValue(name, out value)) return true;
            }
            value = Value.None;
            return false;
        }

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
    public interface IEffectHost
    {
        /// <summary>Supplies custom names such as game-specific selectors. Return false to fall through.</summary>
        bool TryResolveName(string name, EvalContext context, out Value value);

        /// <summary>Supplies custom functions such as <c>within(...)</c> for spatial games.</summary>
        bool TryCall(string function, IReadOnlyList<Value> arguments, EvalContext context, out Value value);

        /// <summary>
        /// Called after each event's after phase resolves. VFX, audio and UI hang off this; the
        /// library never plays anything itself.
        /// </summary>
        void OnEvent(GameEvent gameEvent);
    }

    /// <summary>A host that adds nothing. The default.</summary>
    public class EffectHostBase : IEffectHost
    {
        public virtual bool TryResolveName(string name, EvalContext context, out Value value)
        {
            value = Value.None;
            return false;
        }

        public virtual bool TryCall(string function, IReadOnlyList<Value> arguments, EvalContext context, out Value value)
        {
            value = Value.None;
            return false;
        }

        public virtual void OnEvent(GameEvent gameEvent)
        {
        }
    }

    /// <summary>A pending player decision: choose a target, choose N cards, discover.</summary>
    public sealed class ChoiceRequest
    {
        public ChoiceRequest(string prompt, IReadOnlyList<Entity> options, int min, int max, Entity? chooser, SourceSpan span)
        {
            Prompt = prompt;
            Options = options;
            Min = min;
            Max = max;
            Chooser = chooser;
            Span = span;
        }

        public string Prompt { get; }
        public IReadOnlyList<Entity> Options { get; }
        public int Min { get; }
        public int Max { get; }

        /// <summary>The actor making the choice.</summary>
        public Entity? Chooser { get; }

        public SourceSpan Span { get; }
    }

    /// <summary>
    /// Pluggable decision maker: UI, AI, random or scripted. Answers must be a
    /// subset of <see cref="ChoiceRequest.Options"/>; anything else is ignored.
    /// </summary>
    public interface IChoiceProvider
    {
        IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state);
    }

    /// <summary>
    /// An offer of content that does not exist yet: the three cards a Discover shows before one of
    /// them is made. Kept apart from <see cref="ChoiceRequest"/>, whose options are live entities
    /// addressed by id all the way out to the editor bridge.
    /// </summary>
    public sealed class DefinitionChoice
    {
        public DefinitionChoice(string prompt, IReadOnlyList<Cantrip.Content.EntityDefinition> options, Entity? chooser, SourceSpan span)
        {
            Prompt = prompt;
            Options = options;
            Chooser = chooser;
            Span = span;
        }

        public string Prompt { get; }
        public IReadOnlyList<Cantrip.Content.EntityDefinition> Options { get; }

        /// <summary>The actor making the choice.</summary>
        public Entity? Chooser { get; }

        public SourceSpan Span { get; }
    }

    /// <summary>
    /// Implemented by a chooser that can answer an offer of definitions. Optional: a provider that
    /// does not implement it is given the first candidate, the way <see cref="IChoiceProvider"/>
    /// answers that fall short are topped up from the front.
    /// </summary>
    public interface IDefinitionChooser
    {
        Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state);
    }

    /// <summary>Always takes the first options offered. Deterministic, and the default.</summary>
    public sealed class FirstOptionChooser : IChoiceProvider, IDefinitionChooser
    {
        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state) =>
            request.Options.Take(Math.Max(request.Min, Math.Min(request.Max, request.Options.Count))).ToList();

        public Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state) =>
            request.Options.Count == 0 ? null : request.Options[0];
    }

    /// <summary>Picks uniformly at random from its own forked RNG stream, so it never disturbs game rolls.</summary>
    public sealed class RandomChooser : IChoiceProvider, IDefinitionChooser
    {
        private readonly Rng _rng;

        public RandomChooser(ulong seed) => _rng = new Rng(seed);

        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state)
        {
            var options = request.Options.ToList();
            _rng.Shuffle(options);
            int count = request.Min >= request.Max ? request.Max : _rng.NextInt(request.Min, request.Max);
            return options.Take(Math.Min(count, options.Count)).ToList();
        }

        public Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state) =>
            request.Options.Count == 0 ? null : request.Options[_rng.NextInt(0, request.Options.Count - 1)];
    }

    /// <summary>
    /// Answers from a queue of names, for tests and replays. Each answer is a comma-separated list
    /// of entity names; when the queue runs dry it falls back to the first options.
    /// </summary>
    public sealed class ScriptedChooser : IChoiceProvider, IDefinitionChooser
    {
        private readonly Queue<string> _answers = new Queue<string>();

        public ScriptedChooser(params string[] answers)
        {
            foreach (string answer in answers) _answers.Enqueue(answer);
        }

        public void Enqueue(string answer) => _answers.Enqueue(answer);

        public int Remaining => _answers.Count;

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
