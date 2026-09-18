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
| Slay the Spire | 23 | 16 | 7 | 0 |
| Monster Train | 10 | 8 | 1 | 1 |
| Hearthstone | 10 | 5 | 2 | 3 |
| Balatro | 7 | 3 | 1 | 3 |
| Dota 2 (real time) | 8 | 4 | 1 | 3 |
| Magic | 10 | 7 | 1 | 2 |
| Inscryption | 8 | 6 | 1 | 1 |
| **Total** | **76** | **49** | **14** | **13** |

Units and minions in Monster Train and Hearthstone are modelled as `actor` definitions on the player's side, attacking from their own `turn_end` listeners. Balatro scoring is modelled with `chips`, `mult` and `score` stats on the player. The Dota 2 effects run on the tick clock. Magic creatures are `actor` definitions too, spells are cards, and permanents that only sit there are relics. Inscryption models creatures the same way, with sigils as `keyword` definitions applied to them and bones as a declared `resource`.

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
| 11 | Reaper | Soul Reap | Workaround | Heals for unblocked damage dealt. Verbs return nothing, so the card differences the history counter around the attack, holding it in a `let` |
| 12 | Envenom | Venom Coat | Works | Unblocked attack damage applies Poison |
| 13 | Pain | Ache | Works | A curse that hurts while held |
| 14 | Pen Nib | Quill Nib | Works | Every tenth attack deals double, with a counter stat |
| 15 | Anchor | Heavy Anchor | Workaround | Block granted on `battle_start` is wiped by the first turn's reset, so it is granted on the first `turn_start` instead |
| 16 | Snecko Eye | Serpent Eye | Works | Drawn cards get a random cost |
| 17 | Artifact | Nullify | Works | Cancels the next debuff |
| 18 | Intangible | Phased | Works | Damage taken is clamped to 1 |
| 19 | Cultist and Ritual | Chanter | Works | A phase for the opening and one for afterwards, so the chant is its own move and the telegraphed intent is the real one. Ritual still skips its first turn with a counter stat, which is cross-turn state rather than a temporary |
| 20 | Louse Curl Up | Rolling Louse | Works | Blocks the first time it is attacked |
| 21 | Gremlin Nob Enrage | Fury | Works | Gains Strength when the player uses a skill |
| 22 | Slime Boss split | Slime King | Workaround | Splits immediately at half hp instead of telegraphing a Split intent. A phase can gate the move, but intents are rolled at battle start and after each enemy turn, so crossing the threshold during the player's turn does not re-telegraph |
| 23 | Spore Cloud | Sporeling | Works | Debuffs the player when it dies |

## Monster Train

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Rage | Rage | Works | `modify attack: +2 * stacks` with `decay 1` |
| 2 | Armor | Armor | Works | A before listener reduces the hit and spends stacks, holding the absorbed amount in a `let` |
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
| 6 | Knife Juggler | Knife Juggler | Workaround | A listener that becomes active during an event hears it, so the Juggler must leave its own summon out. `not target:self` says it as a filter rather than a guard in the body, but it still has to be said |
| 7 | Minion combat | `trade` verb | Works | The `attack` verb makes each creature the source of its own hit. A content verb still spells the trade out, which is where combat rules belong |
| 8 | Taunt | | Not expressible | Target selection has no rules content can add to, such as "must target a Taunt minion" |
| 9 | Silence | | Not expressible | Nothing can switch off an entity's own listeners and modifiers |
| 10 | Discover | | Not expressible | No way to pick random definitions from a pool, such as "three random spells" |

A Deathrattle that draws a card needs to name who draws — `draw 1 to player` — because `draw` otherwise draws for the running entity's controller, and a minion controls itself.

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
| 2 | Damage over time | Poison Sting | Works | The venom carries the ticking: a debuff applied `for 2s` with `on every 1s:` deals its damage once a second |
| 3 | Skull Basher | Skull Basher | Workaround | Stun is only a marker; nothing stops a stunned unit from acting |
| 4 | Vladmir's Offering | War Banner | Works | `modify damage of everyone where source:allies: x1.25` |
| 5 | Cooldown reduction | | Not expressible | Cooldowns come straight from the `cooldown` property, not through a modifier channel |
| 6 | Crystal Nova (area) | | Not expressible | `within` is parsed but its meaning must come from a host; DSL tests have none |
| 7 | Blink | | Not expressible | Actors have board slots, not positions in space |
| 8 | Mana regeneration | Mana Font | Works | `on every 1s: gain 1 mana to player` on a relic |

## Magic

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Lightning Bolt | Spark Bolt | Works | Direct damage from a spell card |
| 2 | Raise the Alarm | Muster | Works | `create Militia 2` makes two token creatures |
| 3 | Goblin King | Goblin Chief | Works | `modify attack of other allies where tag:goblin`. `other` leaves out the entity the modifier is written on, so the chief carries the goblin tag itself and still buffs only the rest |
| 4 | Regenerate | Regrowth | Works | `on instead_of_died(target:owner) once per turn` survives one death a turn |
| 5 | Counterspell | Denial | Workaround | A counterspell is an instant played into a priority window, and there is no such window here. It becomes a permanent that commits in advance to countering the next spell, which is still cast and still paid for |
| 6 | Blood Artist | Blood Tithe | Works | `on killed(kind:actor): deal 1 to enemies` |
| 7 | Coloured mana costs | | Not expressible | `cost` is one number through one channel, so a cost cannot be "two red and one of any colour" |
| 8 | Flying | | Not expressible | There is no blocking step, and target validity has no rules content can add to. The same gap as Taunt |
| 9 | Deathtouch | Venomstrike | Works | `on damaged(source:owner): kill event.target`, which only works because the creature is the source of its own hit |
| 10 | Lifelink | Soulbond | Works | `heal event.amount to player` on the same trigger. A minion controls itself here, so the life goes to `player` |

## Inscryption

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Bones from every death | Bone Collector | Works | `resource "bones"` with a floor, and `on killed(kind:actor): gain 1 bones to player` |
| 2 | Bone Lord's Horn | Bone Bargain | Works | `cost 2 bones`: the bones are the cost, so the card is unplayable without them rather than merely ineffective |
| 3 | Sharp Quills | Quills | Works | A sigil as a `keyword` definition, applied like a status: `on owner.damaged(source:enemies): deal 1 to event.source` |
| 4 | Fledgling | Fledgling | Works | `on any.turn_end once per battle: attack += 1` |
| 5 | Ant Swarm | Ant Worker | Works | A modifier amount may be a group count: `modify attack: +(allies where tag:ant).count` |
| 6 | Blood cost | Blood Offering | Workaround | The sacrifice happens in the effect, so the card summons whether or not anything was actually sacrificed. A real blood cost would refuse |
| 7 | Leshy's Fecundity | Sporebearer | Works | `draw 1 to player` on `self.died`. Naming who draws is what lets a creature draw for you, since it controls itself |
| 8 | Candles and lives | | Not expressible | Run structure above a battle: nothing models a run of battles with lives carried between them |

## Gaps, most useful first

1. **Delivered: time-based triggers.** `on every 1s:` fires on the clock, and both Poison Sting (#2) and Mana Font (#8) are written with it. Nothing is left in this area that the language cannot say.
2. **Re-telegraphing when a phase changes** (Slay the Spire #22). Phases now gate which moves an enemy may choose, which covers opening moves and hp thresholds; what is left is re-rolling an intent when a phase changes during the player's turn, so a boss can telegraph the move its new phase has just unlocked.
3. **Targeting rules and action blocking** (Hearthstone #8; Magic #8; Dota 2 #3; Monster Train #4). The `attack` verb has landed, so a creature is the source of its own hit and deathtouch, lifelink and "whenever this deals damage" all work. What is left is target validity content can add to (Taunt, Flying), stopping a stunned unit from acting, and attack counts such as Multistrike.
4. **Verbs that return values** (Slay the Spire #11). `let` has covered the local-variable half of this gap; what remains is getting a result back out of a verb, so that an attack's damage need not be recovered by differencing a counter around it.
5. **Effects as values** (Balatro #6, Hearthstone #9). Copying and disabling another entity's effects; section 3.11 already lists this as a stress test.
6. **Listeners registered during an event hear that event** (Slay the Spire #10, Hearthstone #6). The clean "not myself" filter already exists — `not target:self`, `not card:Reverb` — so what is left to decide is whether the default should change, since every listener of this shape has to remember to exclude itself.
7. **Definition pools** (Hearthstone #10). "A random spell", "three random cards with tag X".
8. **Grouping over collections** (Balatro #7). Count by rank or suit, distinct values, runs.
9. **Ordered modifier resolution** (Balatro #5). Resolve by source position instead of fixed layers, as an option.
10. **Cancellable resets** (Slay the Spire #9, #15).
11. **Cooldown as a modifier channel** (Dota 2 #5).
12. **Space and richer boards** (Dota 2 #6, #7; Monster Train #10). Built-in geometry, or a documented host contract for it.
13. **Outgoing modifier scopes for other cards and "your" effects** (Slay the Spire #7, Hearthstone #4). Excluding yourself is already covered — `of other allies` leaves out the modifier's own owner — so what is left is reaching outward: "your spells", or a modifier written on one card that applies to another.
14. **Delivered: `draw` for someone else.** `draw N to who` names who draws, so a creature can draw for you (Inscryption #7) despite controlling itself. The Hearthstone Deathrattle note above is answered by the same clause.
15. **Multi-currency and sacrifice costs** (Magic #7; Inscryption #6). A cost may now name the resource it is paid in — `cost 2 bones`, and `cost x bones` spends all of it — and such a card is refused exactly as an unaffordable energy card is. What is left is a cost in *several* currencies at once ("two red and one of any colour"), and a cost paid by destroying something you own, which is a payment step rather than a number.

## Found while building the corpus

- **A defect, fixed:** inside modifiers, roles such as `source:enemies` and group names such as `allies` were read from the attacker's side instead of the modifier owner's. War Banner depends on the fix.
- Names with hyphens or spaces (`Anti-Magic`) cannot be written as bare words in expressions or test setup; use single-word names or strings.
- Winning a battle clears the player's non-persistent statuses, so a test that checks a status applied by the last enemy on death needs a second enemy.
- Until Inscryption was added, nothing in the repository declared a `resource` or a `keyword` — not the samples, not the corpus. Both behave as the language reference says, but neither had been exercised anywhere outside it.
- Knife Juggler's `if event.target != self` guard is expressible as a filter clause, `on created(kind:actor, not target:self)`. Two of the workarounds in this table turned out to be partly self-inflicted once they were re-tested rather than trusted, which is an argument for re-testing the rest of them.
- `other` excludes the entity a modifier is written on, not merely the target: a modifier's scope is evaluated with the owner as `self`, and `other` drops `self` as well as the target or controller. So "other goblins" is `of other allies where tag:goblin`, with no need to tag the lord separately. The language reference describes `other` in terms of the target and the running entity's controller, which does not make this obvious, and the Magic entry carried a needless workaround until it was checked.
- Winning a battle returns the player's hand, discard, exhaust, play and powers piles to the draw pile (`EndBattle`), and `Execute` checks whether the battle is over when it finishes. So a test that draws a card by killing the last enemy finds that card back in the draw pile afterwards, which looks precisely like the draw never happening. It is worth ruling this out before suspecting the effect. Relatedly, `player` in content is always the game's player, never relative to whichever side is acting.
