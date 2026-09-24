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
    public interface IDescriptionLocalizer
    {
        /// <summary>The display name of a definition.</summary>
        string? Name(EntityDefinition definition);

        /// <summary>
        /// The definition's rules text in this language, using the same <c>{placeholders}</c> as its
        /// <c>text:</c>. When the definition uses <c>text_override:</c>, this replaces that instead.
        /// </summary>
        string? Text(EntityDefinition definition);

        string? Flavour(EntityDefinition definition);

        /// <summary>
        /// A phrase template used by generated text, such as <c>deal</c> =
        /// <c>"Deal {amount} {type}damage{target}{ignore}."</c>. See <see cref="EnglishDescriptions.Keys"/>.
        /// </summary>
        string? Phrase(string key);
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
            ["who.adjacent"] = "adjacent enemies",
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
