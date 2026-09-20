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
| Slay the Spire | 23 | 21 | 2 | 0 |
| Monster Train | 10 | 8 | 1 | 1 |
| Hearthstone | 11 | 9 | 1 | 1 |
| Balatro | 7 | 3 | 1 | 3 |
| Dota 2 (real time) | 8 | 5 | 0 | 3 |
| Magic | 10 | 7 | 1 | 2 |
| Inscryption | 8 | 6 | 1 | 1 |
| Dominion | 8 | 5 | 1 | 2 |
| Darkest Dungeon | 8 | 6 | 1 | 1 |
| **Total** | **93** | **70** | **9** | **14** |

Units and minions in Monster Train and Hearthstone are modelled as `actor` definitions on the player's side, attacking from their own `turn_end` listeners. Balatro scoring is modelled with `chips`, `mult` and `score` stats on the player. The Dota 2 effects run on the tick clock. Magic creatures are `actor` definitions too, spells are cards, and permanents that only sit there are relics. Inscryption models creatures the same way, with sigils as `keyword` definitions applied to them and bones as a declared `resource`. Dominion is treasure and victory cards as cards, with actions, buys and coins as declared resources that establish and refresh themselves each turn. Darkest Dungeon has heroes as actors, stress as a declared resource with bounds and no reset, and trinkets as `item` definitions.

## Slay the Spire

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Bash | Crushing Blow | Works | Damage plus a duration debuff |
| 2 | Whirlwind | Cyclone | Works | X-cost, hits every enemy X times |
| 3 | Body Slam | Shield Slam | Works | Damage equal to current block |
| 4 | Heavy Blade | Titan Blade | Workaround | Strength must count three times; the extra two are written as `2 * player.Strength` because the Strength modifier already adds one |
| 5 | Demon Form | Fiend Form | Works | A power with a turn-start listener |
| 6 | Blade Dance | Knife Flurry | Works | Creates exhausting Shivs in hand |
| 7 | Accuracy | Honed Edge | Works | `modify damage of enemies where card:Shiv` on the power card itself. A modifier with no scope is anchored to what it is written on — for a card, its own damage — and naming a scope reaches past that |
| 8 | Corruption | Rot Pact | Works | Skills cost 0 and exhaust |
| 9 | Barricade | Bastion | Works | `on before_block_changed(target:owner): if event.reset: cancel`. The reset marks the change it raises, so this refuses that and nothing else — an effect that means to strip block still strips it |
| 10 | Burst | Reverb | Workaround | The next skill is replayed. A listener that becomes active during an event also hears that event's after phase, so Echo needs `not card:Reverb` |
| 11 | Reaper | Soul Reap | Works | `deal 4 to all enemies into dealt`, then `heal dealt`. The clause binds what actually landed — past block, and only what a dying enemy could still take |
| 12 | Envenom | Venom Coat | Works | Unblocked attack damage applies Poison |
| 13 | Pain | Ache | Works | A curse that hurts while held |
| 14 | Pen Nib | Quill Nib | Works | Every tenth attack deals double, with a counter stat |
| 15 | Anchor | Heavy Anchor | Works | `on turn_start once per battle: block 10`. The battle sequence is documented — `battle_start`, then the turn start that resets block — so granting it on the first turn is the ordinary way to write this, and the `Naive Anchor` test beside it shows what the intuitive version does |
| 16 | Snecko Eye | Serpent Eye | Works | Drawn cards get a random cost |
| 17 | Artifact | Nullify | Works | Cancels the next debuff |
| 18 | Intangible | Phased | Works | Damage taken is clamped to 1 |
| 19 | Cultist and Ritual | Chanter | Works | A phase for the opening and one for afterwards, so the chant is its own move and the telegraphed intent is the real one. Ritual still skips its first turn with a counter stat, which is cross-turn state rather than a temporary |
| 20 | Louse Curl Up | Rolling Louse | Works | Blocks the first time it is attacked |
| 21 | Gremlin Nob Enrage | Fury | Works | Gains Strength when the player uses a skill |
| 22 | Slime Boss split | Slime King | Works | A phase gates the Split, and `retelegraph` on that phase re-rolls the intent the moment the threshold is crossed, so the player is shown the Split before it lands rather than after |
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
| 4 | Kobold Geomancer (Spell Damage) | Kobold Geomancer | Works | `modify damage of enemies where tag:spell, source:player`. Naming a scope is how an outward modifier is written here, as War Banner does; without one it would be anchored to the minion itself |
| 5 | Dire Wolf Alpha (adjacency aura) | Dire Wolf | Works | `modify attack of adjacent(self): +1` |
| 6 | Knife Juggler | Knife Juggler | Workaround | A listener that becomes active during an event hears it, so the Juggler must leave its own summon out. `not target:self` says it as a filter rather than a guard in the body, but it still has to be said |
| 7 | Minion combat | `trade` verb | Works | The `attack` verb makes each creature the source of its own hit. A content verb still spells the trade out, which is where combat rules belong |
| 8 | Taunt | Taunt | Works | `modify targetable of allies where source:enemies, not it.has(Taunt): set 0`. The `targetable` channel is asked before a card accepts a target, and the chooser is only offered what passes |
| 9 | Silence | | Not expressible | Nothing can switch off an entity's own listeners and modifiers |
| 10 | Discover | Discovery | Works | `discover 3 cards where tag:arcane as found`, then `create found into hand`. The two candidates nobody picks never become cards at all, so no `created` listener hears about them |
| 11 | Stealth | Stealth | Works | The same channel with no scope, so it anchors to its host: `modify targetable: set 0`. Only pointing a card at it is refused; a blast still reaches it |

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
| 3 | Skull Basher | Skull Basher | Works | Stun cancels its host's move in the before phase — `on before_move(source:owner): cancel` — so a stunned unit really does lose its turn |
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
| 8 | Flying | | Not expressible | Target validity is expressible now (Hearthstone #8), but Flying decides who may *block* an attacker, and there is no blocking step for a rule to attach to |
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

## Dominion

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Village | Hamlet | Works | `draw 1` and `gain 2 actions`, on a card costing coins. The turn's one action comes from the `resource "actions"` declaration alone |
| 2 | Market | Bazaar | Works | Four resources moved at once: a card, an action, a buy and a coin |
| 3 | Cellar | Larder | Workaround | `discard N` takes a fixed count, so "discard any number, draw that many" becomes a particular number. Which cards go is still the player's choice; how many is not |
| 4 | Chapel | Shrine | Works | `exhaust 1` trashes a card out of the deck, resolved through the chooser |
| 5 | Moat | Bulwark | Works | A card in hand is active, so it reacts from there without being played: `on before_damaged(target:player) once per turn: cancel` |
| 6 | Throne Room | Regent | Works | `choose 1 from hand where tag:action as picked`, then `repeat 2: replay picked` |
| 7 | Buying from the supply | | Not expressible | Pools exist now (gap 7), but a supply is a pile that runs out and a definition cannot hold a count. `cost 5 coins` is already refused when unaffordable; what is missing is a buy phase — nothing in the repo ever spends `buys` — and a cost in two currencies at once (gap 15) |
| 8 | Militia | | Not expressible | There is one player, and enemies are actors without hands or decks, so an effect reaching into another player's hand has nobody to reach |

## Darkest Dungeon

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Stress | Dread | Works | `resource "stress"` with `min 0 max 200` and no reset, so it accumulates across turns instead of refreshing. Both bounds are tested |
| 2 | Affliction at 100 stress | Focus Ring | Works | `on any.stress_changed: if event.new >= 100` — a threshold on a custom stat's own change event, which is how content notices a resource crossing a line rather than polling it. Declared as an `item` |
| 3 | Rally | Rally | Works | `lose 25 stress`, which the resource's floor stops below zero |
| 4 | Bleed | Bleeding, Lash | Works | `decay 1 on turn_end` beside a `turn_end` listener; the listener sees the stacks before they tick down |
| 5 | Death's Door | Faltering | Works | `on instead_of_died(target:owner) once per battle: heal 1 to owner` |
| 6 | Rank-limited skills | Pike | Workaround | `modify targetable of enemies where card:Pike, it.position > 1: set 0` — reach as a targeting rule, positions counting from zero. The wart is that the card must name itself: the qualifier compares names and there is no `card:self`, so without it the limit would bind every card played while this one sat in hand |
| 7 | Camping between fights | | Not expressible | Camping happens between battles, and nothing models a run of battles with stress and health carried across them. The same shape as Inscryption's candles |
| 8 | Virtue or affliction | Breaking Point | Works | `discover 1 statuses where tag:affliction or tag:virtue, weighted`. Offering one candidate is a pick with nothing to decide, so nobody is asked; "rarely a virtue" is a weight of 1 against three 5s |

## Gaps, most useful first

1. **Delivered: time-based triggers.** `on every 1s:` fires on the clock, and both Poison Sting (#2) and Mana Font (#8) are written with it. Nothing is left in this area that the language cannot say.
2. **Delivered: re-telegraphing when a phase changes.** `phase Broken when hp <= max_hp / 2, retelegraph` re-rolls the enemy's intent the moment a hit moves it into that phase, so a boss shows the move the phase has just unlocked (Slay the Spire #22). It is opt-in on purpose: ordinarily the intent shown during the player's turn is exactly the move that follows, and only a phase that asks gives that up. A killing blow re-telegraphs nothing, and a hit absorbed entirely by block cannot cross a threshold.
3. **Delivered in part: target validity.** Content adds rules to target selection through the `targetable` channel, asked over a base of 1 before a card accepts a target. A taunt is a scope speaking for others — `modify targetable of allies where source:enemies, not it.has(Taunt): set 0` (Hearthstone #8) — and the same channel with no scope anchors to its own host, which is stealth (Hearthstone #11). The chooser is only offered candidates that pass, so a rule narrows what a player may pick rather than making the play fail, and a card left with nothing to point at is refused. It was a query channel rather than a cancellable event precisely because candidates must be filtered *before* the chooser is asked, and an event that can deal damage cannot honestly be used as a question. What is left: Magic's Flying (#8) needs a blocking step, which is a phase of combat rather than a targeting rule. Darkest Dungeon's ranks (#6) have left this gap as a workaround — a skill can limit its reach with `it.position`, but only by naming itself, because `card:` compares names and there is no `card:self`. A `card:self` qualifier, or an anchor-aware `card:` inside a card's own modifier, would make that one clean. Enemy moves and the `attack` verb do not go through card target selection at all, so a taunt does not constrain them either. Two things once filed here had already left: action blocking is expressible — a stunned unit loses its move by cancelling `move` in the before phase (Dota 2 #3) — and Multistrike (Monster Train #4) is a duplication problem rather than a targeting one.
4. **Verbs that return values** (no corpus citation). The case that prompted this is gone: `into` binds what a damage verb landed, so Soul Reap no longer differences a counter (#11). What is still missing is a verb *returning* anything — `VerbHandler` is `delegate void`, a content verb's body is a statement block with no result channel, and `let x = shatter target` would need a command to be usable as an expression, which is a parser change reaching the walker, the canonical printer and the linter. Worth doing when something needs it; nothing in the corpus does yet.
5. **Effects as values** (Balatro #6, Hearthstone #9). Copying and disabling another entity's effects; section 3.11 already lists this as a stress test.
6. **Listeners registered during an event hear that event** (Slay the Spire #10, Hearthstone #6). The clean "not myself" filter already exists — `not target:self`, `not card:Reverb` — so what is left to decide is whether the default should change, since every listener of this shape has to remember to exclude itself.
7. **Delivered in part: definition pools.** `discover N <kind> [where ...] [, weighted] as name` offers content that nothing has been made from yet and binds what was chosen, ready for `create` or `apply` (Hearthstone #10, Darkest Dungeon #8). Offering a single candidate is a pick with nothing to decide, so the chooser is never asked and one verb serves both a player discovering a spell and a hero rolling on a table of afflictions; `weight` carries "rarely a virtue", reusing the property an enemy's moves are already weighted by. It is a verb rather than a `pool` declaration because a verb can read the kind as the word that was written instead of evaluating it, which needs no new AST node and so no trip down the checklist in [architecture.md](architecture.md) — and a declaration can still be added later as another source for the same verb. What is left: a pool is not a value, so it cannot be named, counted or iterated; and a pile cannot deplete, because definitions are immutable objects shared by every instance and nothing holds a per-definition count. So Dominion #7 stays open on a buy phase and multi-currency costs (gap 15) rather than on pools. Nor can a host UI answer a discovery yet — the editor's choice bridge validates answers against entity ids, so the core and the tests work while a Godot prompt would not.
8. **Grouping over collections** (Balatro #7). Count by rank or suit, distinct values, runs.
9. **Ordered modifier resolution** (Balatro #5). Resolve by source position instead of fixed layers, as an option.
10. **Delivered: cancellable resets.** A reset marks the `<stat>_changed` event it raises, so `if event.reset: cancel` refuses a reset and nothing else — Bastion keeps block across turns while an effect that means to strip block still strips it (Slay the Spire #9). The other row filed here was never a gap: the battle sequence is documented, `battle_start` before the turn start that resets block, so Heavy Anchor granting on the first turn is the ordinary spelling rather than a trick (#15).
11. **Cooldown as a modifier channel** (Dota 2 #5).
12. **Space and richer boards** (Dota 2 #6, #7; Monster Train #10). Built-in geometry, or a documented host contract for it.
13. **Not a gap: the modifier anchor.** A modifier with no `of` scope is anchored to the entity it is written on — a card to its own damage, a status to its host, a relic to its controller — and naming a scope reaches past that. Both rows once filed here (Slay the Spire #7, Hearthstone #4) were expressible that way all along, as Dota 2 #4 already was, and excluding yourself is covered by `of other allies`. The rule is set out under Modifiers in the language reference, both the default anchor and what `of` replaces it with; these corpus entries had simply not followed it.
14. **Delivered: `draw` for someone else.** `draw N to who` names who draws, so a creature can draw for you (Inscryption #7) despite controlling itself. The Hearthstone Deathrattle note above is answered by the same clause.
15. **Multi-currency and sacrifice costs** (Magic #7; Inscryption #6). A cost may now name the resource it is paid in — `cost 2 bones`, and `cost x bones` spends all of it — and such a card is refused exactly as an unaffordable energy card is. What is left is a cost in *several* currencies at once ("two red and one of any colour"), and a cost paid by destroying something you own, which is a payment step rather than a number.
16. **Delivered: a declared resource establishes its own stat.** `resource "actions"` with `reset_to 1` now gives every actor whose turn begins one action, with nothing having to grant it first — the reset creates the stat as well as refreshing it. Dominion's entries needed a Turn Order relic to bring the stat into being before this, and that relic is gone. The one exception is a reset whose value names another stat: `reset_to max_energy` is still skipped for an entity without `max_energy`, which is what keeps an enemy from acquiring energy.

17. **Run structure above a battle** (Inscryption #8; Darkest Dungeon #7). Nothing models a run of battles with hp, stress, lives or candles carried between them: a battle is the outermost unit here, `battle_end` is the last thing that happens, and `once per run` is the only acknowledgement that runs exist at all. Both rows were written as "not expressible" in their own files and cited nothing, because this had never been listed — found by counting corpus citations against this list rather than by reading either game.

## Found while building the corpus

- **A defect, fixed:** inside modifiers, roles such as `source:enemies` and group names such as `allies` were read from the attacker's side instead of the modifier owner's. War Banner depends on the fix.
- Names with hyphens or spaces (`Anti-Magic`) cannot be written as bare words in expressions or test setup; use single-word names or strings.
- Winning a battle clears the player's non-persistent statuses, so a test that checks a status applied by the last enemy on death needs a second enemy.
- Until Inscryption was added, nothing in the repository declared a `resource` or a `keyword` — not the samples, not the corpus. Both behave as the language reference says, but neither had been exercised anywhere outside it.
- Knife Juggler's `if event.target != self` guard is expressible as a filter clause, `on created(kind:actor, not target:self)`.
- The other four held up, and each fails by a measurable amount when the trick is taken out. Kobold Geomancer's Arcane Shot lands 2 instead of 3 without its `of` scope; a Shiv lands 4 instead of 8 without one; Titan Blade deals 26 instead of 23 if the doubled Strength is written as tripled, because the modifier applies its own multiple on top; and Abstract Joker scores 33 instead of 231 if it listens on the after phase rather than the before. Heavy Anchor needed no probe at all: the corpus already ships a `Naive Anchor` relic and a test asserting that block granted on `battle_start` is wiped to 0. A deliberate counter-example beside the entry is the best evidence in this table, and more of these notes could carry one.
- Dominion settled four things nothing had tested. `discard N` and `exhaust N` resolve through the chooser inside a DSL test with no `answer` needed, as long as the candidates are identical or there is only one. A card in hand is active, so it can react from there without ever being played. `choose N from group as name` binds something `replay` accepts. And a trap: a stat set in setup is wiped by its own `reset_on turn_start`, because setup runs before the battle starts — grant it with a statement instead.
- The summary table is bookkeeping over the sections below it, and nothing checks the two against each other. One figure went stale unnoticed: a row moved from workaround to works, the README was updated to match, and the total was not, so the table and the README disagreed for a commit. Every summary row and the total have since been counted against the section rows, and all nine games agree. The section rows are the authority — count them rather than adjusting the total by hand. This note deliberately carries no figure of its own: quoting one here would be the same mistake a level up, and it would go stale the next time a row moved. Counting the status cells mechanically is the quickest way to check, with one trap worth knowing before it alarms you: `| Works |` also matches the status key above and the summary table's own header row, so a raw count comes out three high in every column. Subtract those two rows, then compare — and count the sections you think you already know, because trusting the summary for two games is how this went wrong once already.
- `item` works as a declaration and behaves like a relic, but there is no `item` setup verb in the test language: a test grants one with `relic`. Darkest Dungeon is the first content anywhere to declare an `item` at all.
- Listening on a custom stat's change event works, and needs nothing declared for it: `on any.stress_changed` fires for a `resource` the content declared, and the linter does not complain that nothing raises it, because its stat inventory counts resource declarations. I expected a warning there and was wrong, which is worth recording in the direction it fell.
- The checklist in architecture.md earned its keep on the first new binding form since it was written. `into` binds a name, the linter did not know that, and every use of the bound name warned GE302 — exactly the failure the list predicts for a form the linter has not been taught. The corpus lint caught it, because that folder has been clean all along and two warnings stood out. Lint tests now pin both halves: a name bound by a verb that honours the clause is known, and one written on a verb that ignores it is still a real mistake.
- Twice now a row has looked like a workaround only because a documented ordering rule was not being followed — the modifier anchor, and the battle sequence that puts `battle_start` before the turn start which resets block. Both times the instinct to reason by analogy with another row pointed the wrong way, and both times the language reference settled it in a sentence. Check the reference before filing a gap.
- Re-telegraphing turned out to be a conflict between two guarantees rather than a missing hook. "The telegraph never lies" and "a boss re-telegraphs when it transforms" cannot both hold unconditionally, and an existing test pins the first across twelve turns — the intent shown is asserted to be the move used. That is what made the feature opt-in per phase instead of a change to how intents work. Replacing Slime King's `on self.damaged` listener with a phase-gated move also took four GE306 self-retrigger notes out of the corpus, which is a fair sign the listener was doing more than it looked like.
- Declaring a resource used to establish the stat on nobody, so its reset was silently inert and Dominion carried a relic whose only job was to bring `actions` into being. A reset now creates the stat it resets, that relic is deleted, and the four action counts it had been inflating came back to what the tests always asserted.
- Eight of these workarounds have been examined rather than trusted, and four were self-inflicted: the Goblin Chief's separate tag, the Juggler's body guard, Honed Edge's extra status, and Stun — which really can stop a unit from acting, by cancelling `move` in the before phase. What is real is narrower, and it is the anchoring rule: drop the `of` scope and Kobold Geomancer's Arcane Shot lands 2 instead of 3, and a Shiv lands 4 instead of 8. Naming a scope is the ordinary way to write an outward modifier, though, not a trick — so the lesson is not "the notes were wrong" but that the anchor catches people out even though the language reference states it plainly under Modifiers. Every note in this table is worth testing before believing.
- `other` excludes the entity a modifier is written on, not merely the target: a modifier's scope is evaluated with the owner as `self`, and `other` drops `self` as well as the target or controller. So "other goblins" is `of other allies where tag:goblin`, with no need to tag the lord separately. The language reference describes `other` in terms of the target and the running entity's controller, which does not make this obvious, and the Magic entry carried a needless workaround until it was checked.
- Winning a battle returns the player's hand, discard, exhaust, play and powers piles to the draw pile (`EndBattle`), and `Execute` checks whether the battle is over when it finishes. So a test that draws a card by killing the last enemy finds that card back in the draw pile afterwards, which looks precisely like the draw never happening. It is worth ruling this out before suspecting the effect. Relatedly, `player` in content is always the game's player, never relative to whichever side is acting.
- A DSL test cannot assert that a play was *refused*: `DslTestRunner.Play` throws when the result is anything but `Played`, so `play X on Y` either succeeds or fails the test outright. A rule that forbids something therefore has to be shown the other way round — play the card with no target and assert it picked the only thing it was allowed to pick, which has the merit of exercising candidate filtering rather than the final validity check. Where the refusal itself is the point, assert `PlayResult.InvalidTarget` from a core test instead.
- Three targeting details, each of which would have shipped as a plausible bug if it had been reasoned about instead of read. Inside a modifier's `of ... where ...`, `it` is the candidate but a bare qualifier such as `tag:` tests the value being computed, so a candidate's own tags must be written `it.` — `magic.ge`'s bare `tag:goblin` works only because a stat query carries no card to test instead. `card:` compares names and there is no `card:self`, so a card wanting a rule about itself must name itself, which is the whole wart in Darkest Dungeon #6. And positions count from zero, so ranks one and two are `position > 1`; that one is asserted outright in a core test rather than left to inference, because it was inferred from a default value rather than from the line that assigns it.
- A pool is drawn from every definition the library has loaded, not from the file the card is written in — obvious once said, and not obvious while writing a corpus test. This folder loads all nine games together, so `discover 3 cards where tag:spell` offers Magic's and Inscryption's spells beside Hearthstone's: eight of them across four files, which is why a test asserting *which* card was discovered failed while the count was right. A corpus discovery needs a pool tag of its own to be deterministic, and that is the more faithful model anyway, since the real card offers spells of your class rather than every spell ever printed.
