using System;
using System.Collections.Generic;
using GameplayEffects.Runtime;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>Why an answer was not passed on to the runtime.</summary>
    public enum ChoiceRejection
    {
        None,

        /// <summary>Nothing is waiting to be answered.</summary>
        NothingPending,

        /// <summary>The answer is to a question that has already been answered or cancelled.</summary>
        StaleRequest,

        /// <summary>An entity that was not among the options.</summary>
        UnknownOption,

        /// <summary>The same option twice.</summary>
        DuplicateOption,

        /// <summary>Fewer options than the content asked for.</summary>
        TooFew,

        /// <summary>More options than the content allows.</summary>
        TooMany,
    }

    /// <summary>The verdict on one answer from the UI, and the ids to pass on when it is good.</summary>
    public sealed class ChoiceAnswer
    {
        internal ChoiceAnswer(ChoiceRejection reason, string message, IReadOnlyList<int> entityIds)
        {
            Reason = reason;
            Message = message;
            EntityIds = entityIds;
        }

        public bool Accepted => Reason == ChoiceRejection.None;

        public ChoiceRejection Reason { get; }

        /// <summary>Why it was rejected, ready to show or log. Empty when accepted.</summary>
        public string Message { get; }

        /// <summary>The ids to hand to <c>CardRuntime.Answer</c>, in the order they were given.</summary>
        public IReadOnlyList<int> EntityIds { get; }

        public override string ToString() => Accepted ? "accepted" : Reason + ": " + Message;
    }

    /// <summary>
    /// Gives the core's <c>PendingChoice</c> an identity, so a late answer cannot be applied to the
    /// wrong question.
    /// </summary>
    /// <remarks>
    /// Answering a choice replays the action, which may immediately ask something else: a card that
    /// exhausts two cards asks twice, and both questions look alike from GDScript. A button press
    /// that arrives a frame after the first question was answered would otherwise be read as the
    /// answer to the second. Each question therefore gets a number that is never reused, and an
    /// answer carrying any other number is refused.
    /// </remarks>
    public sealed class ChoiceBridge
    {
        private static readonly IReadOnlyList<int> NoIds = new int[0];

        private readonly HashSet<int> _options = new HashSet<int>();
        private readonly List<int> _optionOrder = new List<int>();
        private int _lastId;

        /// <summary>The choice the runtime is waiting on, or null.</summary>
        public PendingChoice? Current { get; private set; }

        /// <summary>The id of <see cref="Current"/>; 0 when nothing is pending.</summary>
        public int CurrentId { get; private set; }

        public bool IsPending => Current != null;

        /// <summary>Ids of the options, snapshotted when the request opened.</summary>
        public IReadOnlyList<int> OptionIds => _optionOrder;

        /// <summary>
        /// Gives a pending choice its id, or returns the id it already has. Re-opening the same
        /// choice is deliberately free, so a node may call this after every action without churning
        /// ids the UI is holding.
        /// </summary>
        public int Open(PendingChoice choice)
        {
            if (choice == null) throw new ArgumentNullException(nameof(choice));
            if (ReferenceEquals(choice, Current)) return CurrentId;

            Current = choice;
            CurrentId = ++_lastId;

            _options.Clear();
            _optionOrder.Clear();
            for (int i = 0; i < choice.Options.Count; i++)
            {
                Entity option = choice.Options[i];
                if (option != null && _options.Add(option.Id)) _optionOrder.Add(option.Id);
            }

            return CurrentId;
        }

        /// <summary>
        /// Follows <c>CardRuntime.Pending</c>: opens a new request, keeps the current one, or closes
        /// it when the runtime has nothing to ask. Returns the current id, or 0.
        /// </summary>
        public int Sync(PendingChoice? pending)
        {
            if (pending == null)
            {
                Close();
                return 0;
            }
            return Open(pending);
        }

        /// <summary>
        /// Forgets the pending request, whether it was answered or abandoned. The next question gets
        /// a new id, so answers to this one stop being accepted from here on.
        /// </summary>
        public void Close()
        {
            Current = null;
            CurrentId = 0;
            _options.Clear();
            _optionOrder.Clear();
        }

        /// <summary>
        /// Checks an answer from the UI against the question actually pending. It changes nothing:
        /// the caller passes the accepted ids to the runtime and then syncs, so a runtime that
        /// throws does not leave the bridge believing the choice is gone.
        /// </summary>
        public ChoiceAnswer Validate(int requestId, IReadOnlyList<int>? entityIds)
        {
            PendingChoice? choice = Current;
            if (choice == null)
            {
                return Reject(ChoiceRejection.NothingPending, "No choice is pending.");
            }

            if (requestId != CurrentId)
            {
                return Reject(
                    ChoiceRejection.StaleRequest,
                    "Choice " + requestId + " has already been dealt with; the game is waiting for choice " + CurrentId + ".");
            }

            var chosen = new List<int>();
            var seen = new HashSet<int>();
            if (entityIds != null)
            {
                for (int i = 0; i < entityIds.Count; i++)
                {
                    int id = entityIds[i];
                    if (!_options.Contains(id))
                    {
                        return Reject(ChoiceRejection.UnknownOption, "Entity " + id + " is not one of the options for \"" + choice.Prompt + "\".");
                    }
                    if (!seen.Add(id))
                    {
                        return Reject(ChoiceRejection.DuplicateOption, "Entity " + id + " was chosen twice.");
                    }
                    chosen.Add(id);
                }
            }

            if (chosen.Count < choice.Min)
            {
                return Reject(ChoiceRejection.TooFew, "\"" + choice.Prompt + "\" needs at least " + choice.Min + "; got " + chosen.Count + ".");
            }
            if (chosen.Count > choice.Max)
            {
                return Reject(ChoiceRejection.TooMany, "\"" + choice.Prompt + "\" allows at most " + choice.Max + "; got " + chosen.Count + ".");
            }

            return new ChoiceAnswer(ChoiceRejection.None, string.Empty, chosen);
        }

        private static ChoiceAnswer Reject(ChoiceRejection reason, string message) =>
            new ChoiceAnswer(reason, message, NoIds);
    }
}
