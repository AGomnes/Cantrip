namespace Cantrip
{
    /// <summary>
    /// The three phases every verb emits, per section 3.3 of the design notes.
    /// </summary>
    public enum EventPhase
    {
        /// <summary>Runs before the action, and may cancel or alter its inputs.</summary>
        Before,

        /// <summary>Replaces the action entirely: prevention, redirection, conversion.</summary>
        Instead,

        /// <summary>Runs after the action has resolved. The common case.</summary>
        After,
    }

    /// <summary>
    /// Fixed layers of the modifier pipeline. Values pass through them in the order the ruleset
    /// declares (add, multiply, clamp, override by default), which is what keeps a stack of
    /// modifiers from depending on the order they happened to be applied in.
    /// </summary>
    public enum ModifierLayer
    {
        Add,
        Multiply,
        Clamp,
        Override,
    }

    /// <summary>How repeated applications of the same status combine, per section 3.9.</summary>
    public enum StackingMode
    {
        /// <summary>Stacks add up; there is no timer. Poison, Strength.</summary>
        Intensity,

        /// <summary>Duration extends; intensity is fixed. Most timed buffs.</summary>
        Duration,

        /// <summary>Both stacks and duration accumulate.</summary>
        Both,

        /// <summary>Reapplying does nothing while it is already present.</summary>
        None,

        /// <summary>Reapplying resets the duration to full rather than extending it.</summary>
        Refresh,

        /// <summary>Each application is tracked as its own instance, with its own source and timer.</summary>
        Separate,
    }

    /// <summary>Which side an actor is on. Kept deliberately simple; games can layer factions on top.</summary>
    public enum Team
    {
        Neutral = 0,
        Player = 1,
        Enemy = 2,
    }

    /// <summary>What an entity is. Everything in the game is an entity; this only affects defaults.</summary>
    public enum EntityKind
    {
        Actor,
        Card,
        Status,
        Relic,
        Ability,
        Keyword,
        Item,
        Global,
    }

    /// <summary>Pacing of the action queue, per the table in section 4.4.</summary>
    public enum ExecutionMode
    {
        /// <summary>Drain the queue as fast as possible. Used by simulations and tests.</summary>
        Headless,

        /// <summary>Yield between actions so presentation can keep up.</summary>
        Live,
    }
}
