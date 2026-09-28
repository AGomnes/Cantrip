using System;
using System.Collections.Generic;
using Cantrip.Content;

namespace Cantrip.Descriptions
{
    /// <summary>
    /// Supplies descriptions in another language. The library owns the templates
    /// and placeholders; a game owns the translations. Return null from any member to fall back
    /// to the content's own text or the built-in English phrase.
    /// </summary>
    /// <remarks>
    /// Every member defaults to null, which is "I have no translation for this", so a localizer
    /// implements only the parts it has, and a member added in a later release does not break one
    /// written today. See
    /// <see href="https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md">Stability</see>.
    /// </remarks>
    public interface IDescriptionLocalizer
    {
        /// <summary>The display name of a definition.</summary>
        string? Name(EntityDefinition definition) => null;

        /// <summary>
        /// The definition's rules text in this language, using the same <c>{placeholders}</c> as its
        /// <c>text:</c>. When the definition uses <c>text_override:</c>, this replaces that instead.
        /// </summary>
        string? Text(EntityDefinition definition) => null;

        string? Flavour(EntityDefinition definition) => null;

        /// <summary>
        /// A phrase template used by generated text, such as <c>deal</c> =
        /// <c>"Deal {amount} {type}damage{target}{ignore}."</c>. See <see cref="EnglishDescriptions.Keys"/>.
        /// </summary>
        /// <remarks>
        /// The default answers null, which falls back to the built-in English phrase, so a localizer
        /// that translates only names and rules text still reads correctly.
        /// </remarks>
        string? Phrase(string key) => null;
    }

    /// <summary>
    /// The built-in English phrases. Subclass it to change a few phrases, or implement
    /// <see cref="IDescriptionLocalizer"/> directly for a full translation.
    /// </summary>
    public class EnglishDescriptions : IDescriptionLocalizer
    {
        public static readonly EnglishDescriptions Instance = new EnglishDescriptions();

        private static readonly Dictionary<string, string> Phrases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Verbs
            ["deal"] = "Deal {amount} {type}damage{target}{ignore}.",
            ["ignore_block"] = ", ignoring Block",
            ["block"] = "Gain {amount} Block.",
            ["block.target"] = "Give {target} {amount} Block.",
            ["heal"] = "Heal {amount} HP.",
            ["heal.target"] = "Heal {target} for {amount} HP.",
            ["draw.one"] = "Draw {amount} card.",
            ["draw.many"] = "Draw {amount} cards.",
            ["apply"] = "Apply {amount} {status}{target}{duration}.",
            ["for_duration"] = " for {amount}",
            ["gain"] = "Gain {amount} {thing}{target}.",
            ["lose"] = "Lose {amount} {thing}{target}.",
            ["remove"] = "Remove {thing}{from}.",
            ["remove.tag"] = "all {tag} effects",
            ["create.one"] = "Add {card} to your {zone}.",
            ["create.many"] = "Add {amount} {card} to your {zone}.",
            // Without a zone the builder cannot know whether {thing} is a card or a minion, so the
            // phrasing is "Make", never "Add ... to your hand", which would be a lie for a minion.
            ["copy.one"] = "Make a copy of {thing}.",
            ["copy.many"] = "Make {amount} copies of {thing}.",
            ["copy.into.one"] = "Add a copy of {thing} to your {zone}.",
            ["copy.into.many"] = "Add {amount} copies of {thing} to your {zone}.",
            ["transform"] = "Transform {thing} into {into}.",
            ["shuffle"] = "Shuffle your discard pile into your draw pile.",
            ["shuffle.cards"] = "Shuffle {amount} {card} into your draw pile.",
            ["shuffle.cards.zone"] = "Shuffle {amount} {card} into your {zone}.",
            ["discard.one"] = "Discard {amount} card.",
            ["discard.many"] = "Discard {amount} cards.",
            ["discard.thing"] = "Discard {thing}.",
            ["exhaust.one"] = "Exhaust {amount} card.",
            ["exhaust.many"] = "Exhaust {amount} cards.",
            ["exhaust.thing"] = "Exhaust {thing}.",
            ["exhaust.self"] = "Exhaust this card.",
            ["kill"] = "Kill {target}.",
            ["cancel"] = "Prevent it.",
            ["choose"] = "Choose {amount} from {from}.",
            ["emit"] = "Trigger {event}.",
            ["play"] = "Play {card}.",
            ["play.free"] = "Play {card} for free.",
            ["replay"] = "Play {card} again.",
            ["use"] = "Use {move}.",
            ["verb"] = "{verb}{args}.",
            ["stacks.lose.one"] = "Lose {amount} stack.",
            ["stacks.lose.many"] = "Lose {amount} stacks.",
            ["stat.set"] = "Set {thing} to {amount}.",
            ["stat.multiply"] = "Multiply {thing} by {amount}.",
            ["to"] = " to {who}",
            ["from"] = " from {who}",

            // Control flow
            ["if"] = "If {condition}, {body}",
            ["else"] = "Otherwise, {body}",
            ["repeat"] = "{amount} times: {body}",
            ["foreach"] = "For each of {group}: {body}",
            ["chance"] = "{amount} chance: {body}",
            ["next_turn"] = "Next turn: {body}",
            ["in_turns"] = "In {amount} turns: {body}",
            ["until"] = "Until {deadline}: {body}",
            ["until.turn_end"] = "Until the end of the turn: {body}",
            ["cond.dies"] = "{who} dies",
            ["cond.alive"] = "{who} is alive",
            ["cond.has"] = "{who} has {thing}",
            ["cond.not"] = "not {condition}",

            // Listeners
            ["on.turn_start"] = "At the start of your turn{limit}: {body}",
            ["on.turn_start.status"] = "At the start of its holder's turn{limit}: {body}",
            ["on.turn_end"] = "At the end of your turn{limit}: {body}",
            ["on.turn_end.status"] = "At the end of its holder's turn{limit}: {body}",
            ["on.battle_start"] = "At the start of combat{limit}: {body}",
            ["on.battle_end"] = "At the end of combat{limit}: {body}",
            ["on.after"] = "When {event}{limit}: {body}",
            ["on.before"] = "Before {event}{limit}: {body}",
            ["on.instead"] = "If {event}, instead{limit}: {body}",
            ["limit.turn"] = " (once per turn)",
            ["limit.battle"] = " (once per combat)",
            ["limit.run"] = " (once per run)",

            // Events, as the subject of "When ..."
            ["event.damaged"] = "{who} takes {tags}damage",
            ["event.damaged.you"] = "you take {tags}damage",
            ["event.healed"] = "{who} is healed",
            ["event.healed.you"] = "you are healed",
            ["event.died"] = "{who} dies",
            ["event.died.you"] = "you die",
            ["event.killed"] = "{who} is killed",
            ["event.killed.you"] = "you are killed",
            ["event.gained_block"] = "{who} gains Block",
            ["event.gained_block.you"] = "you gain Block",
            ["event.blocked"] = "Block absorbs damage",
            ["event.overkill"] = "a hit deals more damage than needed",
            ["event.card_played"] = "you play {a} {tags}card",
            ["event.drawn"] = "you draw {a} {tags}card",
            ["event.discarded"] = "you discard {a} {tags}card",
            ["event.exhausted"] = "{a} {tags}card is exhausted",
            ["event.shuffled"] = "you shuffle your draw pile",
            ["event.created"] = "something is created",
            ["event.destroyed"] = "something is destroyed",
            ["event.transformed"] = "{who} is transformed",
            ["event.transformed.you"] = "you are transformed",
            ["event.status_applied"] = "{a} {tags}status is applied",
            ["event.status_removed"] = "{a} {tags}status is removed",
            ["event.status_resisted"] = "a status is resisted",
            ["event.move"] = "{who} acts",
            ["event.ability_used"] = "an ability is used",
            ["event.obtained"] = "you obtain this",

            // Who
            ["who.you"] = "you",
            ["who.anyone"] = "anyone",
            ["who.holder"] = "its holder",
            ["who.target"] = "the target",
            ["who.itself"] = "itself",
            ["who.this_card"] = "this card",
            ["who.player"] = "the player",
            ["who.attacker"] = "the attacker",
            ["who.it"] = "it",
            ["who.that_card"] = "that card",
            ["who.all_enemies"] = "ALL enemies",
            ["who.its_enemies"] = "its enemies",
            ["who.all_allies"] = "ALL allies",
            ["who.everyone"] = "everyone",
            ["who.an_enemy"] = "an enemy",
            ["who.an_ally"] = "an ally",
            ["who.each_enemy"] = "every enemy",
            ["who.each_ally"] = "every ally",

            // The board. `adjacent` is one step away *on the anchor's own side*, so which side that
            // is depends on the anchor and cannot be printed into the phrase: "adjacent enemies"
            // described `modify attack of adjacent(self): +1` as a buff for the enemy, which is the
            // wrong side of the board and the wrong card.
            ["who.adjacent.allies"] = "the allies beside {who}",
            ["who.adjacent.enemies"] = "the enemies beside {who}",
            ["who.adjacent"] = "the ones beside {who}",
            ["who.lane"] = "{who}'s {lane_word}",
            ["who.rank"] = "{who}'s {rank_word}",
            ["who.within.one"] = "everything within 1 step of {who}",
            ["who.within.many"] = "everything within {amount} steps of {who}",
            ["who.within.group.one"] = "{group} within 1 step of {who}",
            ["who.within.group.many"] = "{group} within {amount} steps of {who}",
            ["who.distance"] = "the steps between {who} and {other}",
            ["who.random"] = "{amount} random {group}",
            ["who.lowest"] = "the one of {group} with the lowest {stat}",
            ["who.highest"] = "the one of {group} with the highest {stat}",
            ["who.hand.first"] = "the first card in your hand",
            ["who.hand.last"] = "the last card in your hand",
            ["who.pile.top"] = "the top card of your {zone}",
            ["who.pile.bottom"] = "the bottom card of your {zone}",
            ["zone.hand"] = "hand",
            ["zone.draw"] = "draw pile",
            ["zone.discard"] = "discard pile",
            ["zone.exhaust"] = "exhaust pile",

            // Modifiers
            ["modify"] = "{channel} {amount}{scope}.",
            ["modify.clamp"] = "is at most {amount}",
            ["modify.override"] = "is {amount}",
            ["modify.for"] = " for {who}",
            ["channel.damage"] = "Damage dealt",
            ["channel.damage_taken"] = "Damage taken",
            ["channel.block"] = "Block gained",
            ["channel.block_taken"] = "Block received",
            ["channel.heal"] = "Healing done",
            ["channel.heal_taken"] = "Healing received",
            ["channel.cost"] = "Cost",
            ["channel.draw"] = "Cards drawn",
            ["channel.range"] = "Range",

            // Reach, and what an action's own `target` line says it may be pointed at. A card's
            // printed reach is its own rule and not a channel, so it is described on the card.
            ["range.melee"] = "Melee.",
            ["range.at_most"] = "Range {amount}.",
            ["range.span"] = "Range {low}–{high}.",
            ["target.rule"] = "Targets {who}{where}.",
            ["target.where"] = " {where}",
            ["target.and"] = "{first}, {second}",
            ["target.front.one"] = "in the front {rank_word}",
            ["target.front.many"] = "in the front {amount} {rank_word}s",
            ["target.behind"] = "behind the front {amount} {rank_word}s",
            ["target.at_rank"] = "in {rank_word} {amount}",
            ["target.at_lane"] = "in {lane_word} {amount}",
            ["target.same_lane"] = "on this {lane_word}",
            ["target.other"] = "where {condition}",

            // Movement, which is a member write: `target.rank = 0`, `self.lane += 1`.
            ["place.front"] = "Pull {who} to the front.",
            ["place.to_rank"] = "Move {who} to {rank_word} {amount}.",
            ["place.to_lane"] = "Move {who} to {lane_word} {amount}.",
            ["place.forward.one"] = "Move {who} forward one {rank_word}.",
            ["place.forward.many"] = "Move {who} forward {amount} {rank_word}s.",
            ["place.back.one"] = "Move {who} back one {rank_word}.",
            ["place.back.many"] = "Move {who} back {amount} {rank_word}s.",
            ["place.up.one"] = "Move {who} up one {lane_word}.",
            ["place.up.many"] = "Move {who} up {amount} {lane_word}s.",
            ["place.down.one"] = "Move {who} down one {lane_word}.",
            ["place.down.many"] = "Move {who} down {amount} {lane_word}s.",
            ["place.match_rank"] = "Move {who} to {other}'s {rank_word}.",
            ["place.match_lane"] = "Move {who} to {other}'s {lane_word}.",

            // The same, for whatever the text is written on. "Move forward one slot", not "Move
            // you forward one slot", which is what naming the mover reads like when it is you.
            ["place.self.front"] = "Move to the front.",
            ["place.self.to_rank"] = "Move to {rank_word} {amount}.",
            ["place.self.to_lane"] = "Move to {lane_word} {amount}.",
            ["place.self.forward.one"] = "Move forward one {rank_word}.",
            ["place.self.forward.many"] = "Move forward {amount} {rank_word}s.",
            ["place.self.back.one"] = "Move back one {rank_word}.",
            ["place.self.back.many"] = "Move back {amount} {rank_word}s.",
            ["place.self.up.one"] = "Move up one {lane_word}.",
            ["place.self.up.many"] = "Move up {amount} {lane_word}s.",
            ["place.self.down.one"] = "Move down one {lane_word}.",
            ["place.self.down.many"] = "Move down {amount} {lane_word}s.",

            // Definitions
            ["decay"] = "Loses {amount} at the end of its holder's turn.",
            ["decay.turn_start"] = "Loses {amount} at the start of its holder's turn.",
            ["move.enemy"] = "{move}: {body}",
            ["move.enemy.phase"] = "{move} ({phase} phase only): {body}",
            ["cooldown"] = "Cooldown {amount}.",
            ["keyword.exhaust"] = "Exhaust.",
            ["keyword.retain"] = "Retain.",
            ["keyword.ethereal"] = "Ethereal.",
            ["keyword.unplayable"] = "Unplayable.",
        };

        /// <summary>Every phrase key, for translators.</summary>
        public static IEnumerable<string> Keys => Phrases.Keys;

        internal static string? Default(string key) => Phrases.TryGetValue(key, out string? phrase) ? phrase : null;

        public virtual string? Name(EntityDefinition definition) => null;

        public virtual string? Text(EntityDefinition definition) => null;

        public virtual string? Flavour(EntityDefinition definition) => null;

        public virtual string? Phrase(string key) => Default(key);
    }
}
