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
        public ChoicePendingException(ChoiceRequest request)
            : base("A player choice is pending.")
        {
            Request = request;
        }

        public ChoiceRequest Request { get; }
    }

    /// <summary>
    /// A choice a UI still has to make, reported by <see cref="CardRuntime.Pending"/>. Its options
    /// are live entities in the rolled-back game, so a UI can highlight them straight away.
    /// </summary>
    public sealed class PendingChoice
    {
        internal PendingChoice(string prompt, IReadOnlyList<Entity> options, int min, int max, Entity? chooser, SourceSpan span)
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

        /// <summary>How many options must be picked, and how many may be.</summary>
        public int Min { get; }

        public int Max { get; }

        /// <summary>The actor making the choice.</summary>
        public Entity? Chooser { get; }

        /// <summary>The content that asked, for tools that jump to the line.</summary>
        public SourceSpan Span { get; }

        public override string ToString() => $"{Prompt} ({Min}-{Max} of {Options.Count})";
    }

    /// <summary>
    /// A chooser for games whose decisions come from a player, where an answer cannot be produced
    /// on the spot (section 4.3). Instead of guessing, it stops the action; the runtime rolls the
    /// game back, the UI asks, and the action is replayed with the answer. Because the rollback is
    /// an exact snapshot, the replay is deterministic.
    /// </summary>
    /// <remarks>
    /// Outside an action the runtime can roll back — setup calls, or a choice raised while nothing
    /// is being attempted — it behaves like <see cref="FirstOptionChooser"/> rather than throwing
    /// at game code that has nothing to answer with.
    /// </remarks>
    public sealed class DeferredChooser : IChoiceProvider
    {
        private readonly List<int[]> _answers = new List<int[]>();
        private int _next;

        /// <summary>True while an action that can be rolled back is running.</summary>
        internal bool Armed { get; set; }

        /// <summary>Answers already collected for the action being replayed.</summary>
        public int AnswerCount => _answers.Count;

        internal void Rewind() => _next = 0;

        internal void Clear()
        {
            _answers.Clear();
            _next = 0;
        }

        internal void Add(IEnumerable<int> entityIds) => _answers.Add((entityIds ?? Enumerable.Empty<int>()).ToArray());

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
    }
}
