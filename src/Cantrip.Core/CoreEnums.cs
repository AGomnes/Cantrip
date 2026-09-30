namespace Cantrip
{
    /// <summary>
    /// The three phases every verb emits: before, instead and after.
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
        /// <summary>Flat addition: <c>+2</c>. Everything on this layer sums, in no particular order.</summary>
        Add,

        /// <summary>Scaling: <c>x150%</c>. Applied to whatever the add layer left, so a flat bonus is scaled too.</summary>
        Multiply,

        /// <summary>A floor or a ceiling. It runs after the arithmetic, so it is the last word on the number — unless something overrides it.</summary>
        Clamp,

        /// <summary>
        /// A fixed result that replaces everything before it, clamp included. Last in the default order
        /// and therefore the strongest thing a modifier can say; two overrides on one value is a
        /// content bug the linter cannot see, and the later one wins.
        /// </summary>
        Override,
    }

    /// <summary>How repeated applications of the same status combine.</summary>
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
        /// <summary>
        /// On nobody's side. It is the default for cards, statuses and relics, which take their side
        /// from whoever holds them rather than carrying one; an actor on this team is fought by neither
        /// side and ends no battle by dying.
        /// </summary>
        Neutral = 0,

        /// <summary>The party's side: the leader, the heroes beside it, and anything they summon that fights for them.</summary>
        Player = 1,

        /// <summary>The other side. A battle ends when this side has no living actor left on the board.</summary>
        Enemy = 2,
    }

    /// <summary>What an entity is. Everything in the game is an entity; this only affects defaults.</summary>
    public enum EntityKind
    {
        // Saved games store these as numbers: never renumber or reuse one, only add.
        /// <summary>
        /// Something that takes part in the fight and has hp: the leader, a <c>hero</c>, an
        /// <c>enemy</c>, a summon. Both <c>hero</c> and <c>enemy</c> declarations land here — the side
        /// is <see cref="Team"/>, not the kind.
        /// </summary>
        Actor = 0,

        /// <summary>Something played from a hand and paid for. The only kind <c>CardRuntime.Play(Entity, Entity, Entity)</c> accepts.</summary>
        Card = 1,

        /// <summary>A timed or stacking effect attached to an actor. Its count is the <c>stacks</c> stat, not a separate number.</summary>
        Status = 2,

        /// <summary>A permanent held by an actor, live from the moment it is obtained until the run ends.</summary>
        Relic = 3,

        /// <summary>
        /// Something an actor uses directly rather than playing from hand: the real-time verb. It has a
        /// cooldown instead of a cost, which is why <c>ActionResult.CannotAfford</c> never comes back
        /// from using one.
        /// </summary>
        Ability = 4,

        /// <summary>A named rule with a tooltip, attached like a status but with no stacks or duration of its own.</summary>
        Keyword = 5,

        /// <summary>A consumable. It behaves as a relic does in every way the engine cares about; the distinction is the game's.</summary>
        Item = 6,

        /// <summary>
        /// Anything else, and what an unrecognised declaration becomes rather than failing to load.
        /// A definition that ended up here when it should not have is usually a misspelled kind word.
        /// </summary>
        Global = 7,
    }

}
