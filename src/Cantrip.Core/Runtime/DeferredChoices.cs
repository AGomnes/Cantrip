using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Diagnostics;

namespace Cantrip.Runtime
{
    /// <summary>
    /// Raised by <see cref="DeferredChooser"/> when content asks for a decision the game has not
    /// answered yet. <see cref="CardRuntime"/> catches it, rolls the action back and reports the
    /// choice; it only escapes to game code if content is driven without the runtime.
    /// </summary>
    public sealed class ChoicePendingException : Exception
    {
        /// <summary>
        /// Signals that content has asked a question the game has not answered. It is the engine's own
        /// control flow: catching it outside <c>CardRuntime</c> means the action was not rolled back and
        /// the game is mid-effect, which nothing should try to carry on from.
        /// </summary>
        public ChoicePendingException(ChoiceRequest request)
            : base("A player choice is pending.")
        {
            Request = request;
        }

        /// <summary>The question, so whoever catches this can ask it. Its options are entities in the game as it was before the action started.</summary>
        public ChoiceRequest Request { get; }
    }

    /// <summary>
    /// Raised by <see cref="DeferredChooser"/> when content offers a choice of content that does not
    /// exist yet, as <c>discover</c> does, and the game has not answered it. Handled exactly like
    /// <see cref="ChoicePendingException"/>.
    /// </summary>
    public sealed class OfferPendingException : Exception
    {
        /// <summary>Signals an unanswered offer of content, as <c>discover</c> makes. The same control flow as <see cref="ChoicePendingException"/>.</summary>
        public OfferPendingException(DefinitionChoice offer)
            : base("A player choice between offered content is pending.")
        {
            Offer = offer;
        }

        /// <summary>The offer, whose options are definitions rather than entities: nothing has been made yet.</summary>
        public DefinitionChoice Offer { get; }
    }

    /// <summary>
    /// A choice a UI still has to make, reported by <see cref="CardRuntime.Pending"/>. Most choices
    /// are between live entities in the rolled-back game, in <see cref="Options"/>, so a UI can
    /// highlight them straight away. An offer of content that does not exist yet, as <c>discover</c>
    /// makes, lists its candidates in <see cref="Definitions"/> instead, and is answered with
    /// <see cref="CardRuntime.Answer(Cantrip.Content.EntityDefinition)"/>.
    /// </summary>
    public sealed class PendingChoice
    {
        private static readonly IReadOnlyList<Entity> NoEntities = new Entity[0];
        private static readonly IReadOnlyList<Cantrip.Content.EntityDefinition> NoDefinitions = new Cantrip.Content.EntityDefinition[0];

        internal PendingChoice(string prompt, IReadOnlyList<Entity> options, int min, int max, Entity? chooser, SourceSpan span)
        {
            Prompt = prompt;
            Options = options;
            Definitions = NoDefinitions;
            Min = min;
            Max = max;
            Chooser = chooser;
            Span = span;
        }

        internal PendingChoice(string prompt, IReadOnlyList<Cantrip.Content.EntityDefinition> offered, Entity? chooser, SourceSpan span)
        {
            Prompt = prompt;
            Options = NoEntities;
            Definitions = offered;
            Min = 1;
            Max = 1;
            Chooser = chooser;
            Span = span;
        }

        /// <summary>
        /// What content wrote as the prompt, or the engine's own wording when it wrote none. It is not
        /// localized: a game that translates its UI translates this itself.
        /// </summary>
        public string Prompt { get; }

        /// <summary>The entities to choose between. Empty for an offer, which uses <see cref="Definitions"/>.</summary>
        public IReadOnlyList<Entity> Options { get; }

        /// <summary>The content offered, when this is a choice of something that does not exist yet.</summary>
        public IReadOnlyList<Cantrip.Content.EntityDefinition> Definitions { get; }

        /// <summary>True when this offers content (<see cref="Definitions"/>) rather than entities.</summary>
        public bool IsOffer => Definitions.Count > 0;

        /// <summary>How many options must be picked, and how many may be.</summary>
        public int Min { get; }

        /// <summary>
        /// The most that may be picked. It is capped at what is on offer, so it is safe to compare
        /// against a selection count; <see cref="Min"/> may still exceed <see cref="Options"/> when
        /// content asked for more than exists, and the action then takes what there is.
        /// </summary>
        public int Max { get; }

        /// <summary>The actor making the choice.</summary>
        public Entity? Chooser { get; }

        /// <summary>The content that asked, for tools that jump to the line.</summary>
        public SourceSpan Span { get; }

        /// <summary>The prompt and the shape of the choice, for a log or a test failure.</summary>
        public override string ToString() => $"{Prompt} ({Min}-{Max} of {(IsOffer ? Definitions.Count : Options.Count)})";
    }

    /// <summary>
    /// A chooser for games whose decisions come from a player, where an answer cannot be produced
    /// on the spot. Instead of guessing, it stops the action; the runtime rolls the
    /// game back, the UI asks, and the action is replayed with the answer. Because the rollback is
    /// an exact snapshot, the replay is deterministic.
    /// </summary>
    /// <remarks>
    /// Outside an action the runtime can roll back, which means a setup call or a choice raised
    /// while nothing is being attempted, it behaves like <see cref="FirstOptionChooser"/> rather than throwing
    /// at game code that has nothing to answer with.
    /// </remarks>
    public sealed class DeferredChooser : IChoiceProvider
    {
        private readonly List<int[]> _answers = new List<int[]>();

        // Beside each answer to an offer, the definition that was picked; null for entity answers.
        private readonly List<Cantrip.Content.EntityDefinition?> _picks = new List<Cantrip.Content.EntityDefinition?>();
        private int _next;

        /// <summary>True while an action that can be rolled back is running.</summary>
        internal bool Armed { get; set; }

        /// <summary>Answers already collected for the action being replayed.</summary>
        public int AnswerCount => _answers.Count;

        internal void Rewind() => _next = 0;

        internal void Clear()
        {
            _answers.Clear();
            _picks.Clear();
            _next = 0;
        }

        internal void Add(IEnumerable<int> entityIds, Cantrip.Content.EntityDefinition? pick = null)
        {
            _answers.Add((entityIds ?? Enumerable.Empty<int>()).ToArray());
            _picks.Add(pick);
        }

        /// <summary>
        /// Replays an answer already given, or stops the action so the game can ask. Outside an action
        /// the runtime can roll back it takes the first options instead, since there is nothing to
        /// replay into.
        /// </summary>
        /// <exception cref="ChoicePendingException">
        /// No answer for this question yet, and an action is running that can be rolled back. It is
        /// caught by <c>CardRuntime</c>, not by the game.
        /// </exception>
        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state)
        {
            if (_next < _answers.Count)
            {
                int[] ids = _answers[_next++];
                var chosen = new List<Entity>();
                foreach (int id in ids)
                {
                    Entity? option = request.Options.FirstOrDefault(o => o.Id == id && !chosen.Contains(o));
                    if (option != null) chosen.Add(option);
                }
                return chosen;
            }

            if (!Armed) return new FirstOptionChooser().Choose(request, state);
            throw new ChoicePendingException(request);
        }

        /// <summary>
        /// An offer of content is answered in the same sequence as any other choice, but by what was
        /// picked rather than where it stood: the replay draws its candidates again from content, and
        /// a reload in between can change them. The pick is matched by identity, or by kind and name
        /// once a reload has replaced the definition. When the replay no longer offers it at all, the
        /// player is asked again, with this answer and any after it dropped, since they answered a
        /// question that no longer exists.
        /// </summary>
        public Cantrip.Content.EntityDefinition? ChooseDefinition(DefinitionChoice request, GameState state)
        {
            if (_next < _answers.Count)
            {
                int slot = _next++;
                Cantrip.Content.EntityDefinition? pick = _picks[slot];
                if (pick != null)
                {
                    Cantrip.Content.EntityDefinition? match =
                        request.Options.FirstOrDefault(o => ReferenceEquals(o, pick)) ??
                        request.Options.FirstOrDefault(o => SameDefinition(o, pick));
                    if (match != null) return match;
                    if (!Armed) return null;

                    _answers.RemoveRange(slot, _answers.Count - slot);
                    _picks.RemoveRange(slot, _picks.Count - slot);
                    _next = slot;
                    throw new OfferPendingException(request);
                }

                // An answer given as a position alone.
                int[] answer = _answers[slot];
                int index = answer.Length > 0 ? answer[0] : -1;
                return index >= 0 && index < request.Options.Count ? request.Options[index] : null;
            }

            if (!Armed) return request.Options.Count == 0 ? null : request.Options[0];
            throw new OfferPendingException(request);
        }

        internal static bool SameDefinition(Cantrip.Content.EntityDefinition a, Cantrip.Content.EntityDefinition b) =>
            ReferenceEquals(a, b) ||
            (string.Equals(a.KindName, b.KindName, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }
}
