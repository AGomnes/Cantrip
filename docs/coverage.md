# Coverage corpus

Section 7 of the design notes asks for about 150 reference effects from existing games, to measure how much the language can express without new C#. This page tracks that corpus.

Effects are reproduced under our own names in [`samples/corpus`](../samples/corpus), each expressible one with at least one DSL test. Run them with:

```
gedsl test samples/corpus
gedsl lint samples/corpus
```

**Status**

| Status | Meaning |
|---|---|
| Works | Expressed directly, the way a designer would write it |
| Workaround | Expressible, but needs a trick worth removing |
| Not expressible | Needs new language or runtime features; no test |

## Summary

| Game | Effects | Works | Workaround | Not expressible |
|---|---|---|---|---|
| Slay the Spire | 23 | 15 | 8 | 0 |
| Monster Train | 10 | 7 | 2 | 1 |
| Hearthstone | 10 | 4 | 3 | 3 |
| Balatro | 7 | 3 | 1 | 3 |
| Dota 2 (real time) | 8 | 2 | 2 | 4 |
| **Total** | **58** | **31** | **16** | **11** |

Units and minions in Monster Train and Hearthstone are modelled as `actor` definitions on the player's side, attacking from their own `turn_end` listeners. Balatro scoring is modelled with `chips`, `mult` and `score` stats on the player. The Dota 2 effects run on the tick clock.

## Slay the Spire

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Bash | Crushing Blow | Works | Damage plus a duration debuff |
| 2 | Whirlwind | Cyclone | Works | X-cost, hits every enemy X times |
| 3 | Body Slam | Shield Slam | Works | Damage equal to current block |
| 4 | Heavy Blade | Titan Blade | Workaround | Strength must count three times; the extra two are written as `2 * player.Strength` because the Strength modifier already adds one |
| 5 | Demon Form | Fiend Form | Works | A power with a turn-start listener |
| 6 | Blade Dance | Knife Flurry | Works | Creates exhausting Shivs in hand |
| 7 | Accuracy | Honed Edge | Workaround | A modifier written on a power card is anchored to that card, so the power applies a status that carries `modify damage where card:Shiv` |
| 8 | Corruption | Rot Pact | Works | Skills cost 0 and exhaust |
| 9 | Barricade | Bastion | Workaround | Cancels the turn-start block reset by listening for `before_block_changed` with `new == 0` |
| 10 | Burst | Reverb | Workaround | The next skill is replayed. A listener that becomes active during an event also hears that event's after phase, so Echo needs `not card:Reverb` |
| 11 | Reaper | Soul Reap | Workaround | Heals for unblocked damage dealt. There are no local variables, so the card stores the counter in a scratch stat |
| 12 | Envenom | Venom Coat | Works | Unblocked attack damage applies Poison |
| 13 | Pain | Ache | Works | A curse that hurts while held |
| 14 | Pen Nib | Quill Nib | Works | Every tenth attack deals double, with a counter stat |
| 15 | Anchor | Heavy Anchor | Workaround | Block granted on `battle_start` is wiped by the first turn's reset, so it is granted on the first `turn_start` instead |
| 16 | Snecko Eye | Serpent Eye | Works | Drawn cards get a random cost |
| 17 | Artifact | Nullify | Works | Cancels the next debuff |
| 18 | Intangible | Phased | Works | Damage taken is clamped to 1 |
| 19 | Cultist and Ritual | Chanter | Workaround | Patterns cannot say "this move first, then cycle", so the opening is a branch inside one move; Ritual skips its first turn with a scratch stat |
| 20 | Louse Curl Up | Rolling Louse | Works | Blocks the first time it is attacked |
| 21 | Gremlin Nob Enrage | Fury | Works | Gains Strength when the player uses a skill |
| 22 | Slime Boss split | Slime King | Workaround | Splits immediately at half hp instead of telegraphing a Split intent |
| 23 | Spore Cloud | Sporeling | Works | Debuffs the player when it dies |

## Monster Train

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Rage | Rage | Works | `modify attack: +2 * stacks` with `decay 1` |
| 2 | Armor | Armor | Workaround | A before listener reduces the hit and spends stacks; the absorbed amount goes through a scratch stat because there are no local variables |
| 3 | Spikes | Spikes | Works | `on owner.damaged(source:enemies): deal stacks to event.source` |
| 4 | Multistrike | Multistrike | Workaround | Combat is written in content, so every unit's attack must read Multistrike itself |
| 5 | Slay | Headsman | Works | `on killed(source:self)` |
| 6 | Revenge | Grudgeholder | Works | `on self.damaged` |
| 7 | Summon | Recruiter's Bell | Works | `on created(kind:actor)` |
| 8 | Incant | Acolyte | Works | `on card_played(tag:spell)` |
| 9 | Burnout | Burnout | Works | Decays each turn and kills its host after the last one |
| 10 | Floors and capacity | | Not expressible | There is one board row per side. Multiple floors, per-floor capacity and moving units between floors need a richer board model |

## Hearthstone

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Divine Shield | Aegis | Works | A before listener cancels the next hit and removes the shield |
| 2 | Leper Gnome (Deathrattle) | Plague Gnome | Works | `on self.died: deal 2 to all enemies` |
| 3 | Elven Archer (Battlecry) | Elven Archer | Works | The card's effect summons the minion and pings |
| 4 | Kobold Geomancer (Spell Damage) | Kobold Geomancer | Workaround | A modifier on a minion is anchored to that minion, so "your spells" needs `of enemies where tag:spell, source:player` |
| 5 | Dire Wolf Alpha (adjacency aura) | Dire Wolf | Works | `modify attack of adjacent(self): +1` |
| 6 | Knife Juggler | Knife Juggler | Workaround | A listener that becomes active during an event hears it, so the Juggler must ignore its own summon with `event.target != self` |
| 7 | Minion combat | `trade` verb | Workaround | No built-in attack; a content verb trades damage, and its damage comes from whoever called the verb rather than the minion |
| 8 | Taunt | | Not expressible | Target selection has no rules content can add to, such as "must target a Taunt minion" |
| 9 | Silence | | Not expressible | Nothing can switch off an entity's own listeners and modifiers |
| 10 | Discover | | Not expressible | No way to pick random definitions from a pool, such as "three random spells" |

A Deathrattle that draws a card is also not directly expressible: `draw` always draws for the running entity's controller, and a minion controls itself.

## Balatro

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Greedy Joker | Greedy Joker | Works | `on card_played(tag:diamond): gain 3 mult to player` |
| 2 | Abstract Joker | Abstract Joker | Workaround | After listeners resolve too late for the score computed on the next line, so the joker listens in the before phase |
| 3 | Cavendish | Cavendish | Works | `modify mult: x3` |
| 4 | Glass Card | Glass Queen | Works | `player.mult *= 2` and `chance 25%: destroy self` |
| 5 | Joker order | | Not expressible | Jokers apply left to right, so a ×Mult joker before a +Mult joker scores less. The modifier pipeline applies layers in a fixed order, not by source position |
| 6 | Blueprint | | Not expressible | Copying another joker's ability needs effects as first-class values |
| 7 | Poker hands | | Not expressible | Pairs, flushes and straights need grouping over the played cards (count by rank, by suit) |

## Dota 2

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Lifesteal | Lifesteal | Works | `heal event.amount * stacks / 100 to owner` |
| 2 | Damage over time | Poison Sting | Workaround | There is no `every 1s` trigger, so each tick is a nested `in 1s:` block |
| 3 | Skull Basher | Skull Basher | Workaround | Stun is only a marker; nothing stops a stunned unit from acting |
| 4 | Vladmir's Offering | War Banner | Works | `modify damage of everyone where source:allies: x1.25` |
| 5 | Cooldown reduction | | Not expressible | Cooldowns come straight from the `cooldown` property, not through a modifier channel |
| 6 | Crystal Nova (area) | | Not expressible | `within` is parsed but its meaning must come from a host; DSL tests have none |
| 7 | Blink | | Not expressible | Actors have board slots, not positions in space |
| 8 | Mana regeneration | | Not expressible | Nothing triggers per second or per tick |

## Gaps, most useful first

1. **Time-based triggers** (Dota 2 #2, #8). `on every 1s` was in the design notes; real-time content needs it for regeneration, damage over time and auras.
2. **Enemy AI phases and sequences** (Slay the Spire #19, #22). An opening move, HP-threshold phases and telegraphed intents would cover most bosses.
3. **Combat and targeting rules** (Hearthstone #7, #8; Monster Train #4; Dota 2 #3). A built-in attack verb with the attacker as source, target validity rules (Taunt) and action blocking (Stun).
4. **Local variables, or verbs that return values** (Slay the Spire #11, Monster Train #2).
5. **Effects as values** (Balatro #6, Hearthstone #9). Copying and disabling another entity's effects; section 3.11 already lists this as a stress test.
6. **Listeners registered during an event hear that event** (Slay the Spire #10, Hearthstone #6). Either skip it, or offer a clean "not myself" filter.
7. **Definition pools** (Hearthstone #10). "A random spell", "three random cards with tag X".
8. **Grouping over collections** (Balatro #7). Count by rank or suit, distinct values, runs.
9. **Ordered modifier resolution** (Balatro #5). Resolve by source position instead of fixed layers, as an option.
10. **Cancellable resets** (Slay the Spire #9, #15).
11. **Cooldown as a modifier channel** (Dota 2 #5).
12. **Space and richer boards** (Dota 2 #6, #7; Monster Train #10). Built-in geometry, or a documented host contract for it.
13. **Outgoing modifier scopes for other cards and "your" effects** (Slay the Spire #7, Hearthstone #4).
14. **`draw` for someone else** (Hearthstone note above).

## Found while building the corpus

- **A defect, fixed:** inside modifiers, roles such as `source:enemies` and group names such as `allies` were read from the attacker's side instead of the modifier owner's. War Banner depends on the fix.
- Names with hyphens or spaces (`Anti-Magic`) cannot be written as bare words in expressions or test setup; use single-word names or strings.
- Winning a battle clears the player's non-persistent statuses, so a test that checks a status applied by the last enemy on death needs a second enemy.
