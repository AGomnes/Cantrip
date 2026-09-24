# Coverage corpus

To measure how much the language can express without new C#, the coverage corpus re-creates effects from existing games under our own names. The effects are in [`samples/corpus`](../samples/corpus), and each one the language can express has at least one test. Cantrip is not affiliated with these games or their publishers.

From a clone of the repository, run the tests and the linter over the corpus with:

```
dotnet run --project src/Cantrip.Cli -- test samples/corpus
dotnet run --project src/Cantrip.Cli -- lint samples/corpus
```

The tables say what each effect needs. [What cannot be expressed yet](#open-gaps-most-useful-first) lists what is missing, and [Sharp edges](#sharp-edges) lists the rules that most often catch authors out, with links to where the language reference states them. Missing an effect from your game? [Report it with the effect form](https://github.com/AGomnes/Cantrip/issues/new?template=effect.yml).

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
| Dota 2 (real time) | 8 | 6 | 0 | 2 |
| Magic | 10 | 7 | 1 | 2 |
| Inscryption | 8 | 6 | 1 | 1 |
| Dominion | 8 | 5 | 1 | 2 |
| Darkest Dungeon | 8 | 6 | 1 | 1 |
| **Total** | **93** | **71** | **9** | **13** |

Units and minions in Monster Train and Hearthstone are modelled as `actor` definitions on the player's side, attacking from their own `turn_end` listeners. Balatro scoring is modelled with `chips`, `mult` and `score` stats on the player. The Dota 2 effects run on the tick clock. Magic creatures are `actor` definitions too, spells are cards, and permanents that only sit there are relics. Inscryption models creatures the same way, with sigils as `keyword` definitions applied to them and bones as a declared `resource`. Dominion is treasure and victory cards as cards, with actions, buys and coins as declared resources that establish and refresh themselves each turn. Darkest Dungeon has heroes as actors, stress as a declared resource with bounds and no reset, and trinkets as `item` definitions.

## Slay the Spire

Content: [slay_the_spire.cantrip](../samples/corpus/slay_the_spire.cantrip). Tests: [slay_the_spire.tests.cantrip](../samples/corpus/slay_the_spire.tests.cantrip).

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
| 22 | Slime Boss split | Slime King | Works | A phase gates the Split, and `retelegraph` on that phase re-rolls the intent the moment the threshold is crossed, so the player is shown the Split before it lands rather than after. The King becomes one of the halves with `transform self into Slimeling` rather than `kill self`, so it keeps its slot and its id and nothing hears a death — which is what lets a boss both split and have a death rattle |
| 23 | Spore Cloud | Sporeling | Works | Debuffs the player when it dies |

## Monster Train

Content: [monster_train.cantrip](../samples/corpus/monster_train.cantrip). Tests: [monster_train.tests.cantrip](../samples/corpus/monster_train.tests.cantrip).

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

Content: [hearthstone.cantrip](../samples/corpus/hearthstone.cantrip). Tests: [hearthstone.tests.cantrip](../samples/corpus/hearthstone.tests.cantrip).

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

Content: [balatro.cantrip](../samples/corpus/balatro.cantrip). Tests: [balatro.tests.cantrip](../samples/corpus/balatro.tests.cantrip).

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Greedy Joker | Greedy Joker | Works | `on card_played(tag:diamond): gain 3 mult to player` |
| 2 | Abstract Joker | Abstract Joker | Workaround | After listeners resolve too late for the score computed on the next line, so the joker listens in the before phase |
| 3 | Cavendish | Cavendish | Works | `modify mult: x3` |
| 4 | Glass Card | Glass Queen | Works | `player.mult *= 2` and `chance 25%: destroy self` |
| 5 | Joker order | | Not expressible | Jokers apply left to right, so a ×Mult joker before a +Mult joker scores less. The modifier pipeline applies layers in a fixed order, not by source position |
| 6 | Blueprint | | Not expressible | Copying another joker's ability needs effects as first-class values |
| 7 | Poker hands | | Not expressible | Pairs, flushes and straights need grouping over the played cards (count by rank, by suit) |

A score that multiplies grows fast. Arithmetic is correct while values stay within ±1 million, and past that a result can be wrong with no error, so a game scored like this has to keep its numbers in range: see [Numbers](stability.md#numbers).

## Dota 2

Content: [dota2.cantrip](../samples/corpus/dota2.cantrip). Tests: [dota2.tests.cantrip](../samples/corpus/dota2.tests.cantrip).

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Lifesteal | Lifesteal | Works | `heal event.amount * stacks / 100 to owner` |
| 2 | Damage over time | Poison Sting | Works | The venom carries the ticking: a debuff applied `for 2s` with `on every 1s:` deals its damage once a second |
| 3 | Skull Basher | Skull Basher | Works | Stun cancels its host's move in the before phase — `on before_move(source:owner): cancel` — so a stunned unit really does lose its turn |
| 4 | Vladmir's Offering | War Banner | Works | `modify damage of everyone where source:allies: x1.25` |
| 5 | Cooldown reduction | Arcane Vestments | Works | `modify cooldown: x0.5` on a relic shortens every ability its holder has; on an ability it shortens only that one. Modifiers see the duration already in clock units, so a multiplier reads the same on either clock |
| 6 | Crystal Nova (area) | | Not expressible | `within` is parsed but its meaning must come from a host; DSL tests have none |
| 7 | Blink | | Not expressible | Actors have board slots, not positions in space |
| 8 | Mana regeneration | Mana Font | Works | `on every 1s: gain 1 mana to player` on a relic |

## Magic

Content: [magic.cantrip](../samples/corpus/magic.cantrip). Tests: [magic.tests.cantrip](../samples/corpus/magic.tests.cantrip).

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

Content: [inscryption.cantrip](../samples/corpus/inscryption.cantrip). Tests: [inscryption.tests.cantrip](../samples/corpus/inscryption.tests.cantrip).

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

Content: [dominion.cantrip](../samples/corpus/dominion.cantrip). Tests: [dominion.tests.cantrip](../samples/corpus/dominion.tests.cantrip).

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Village | Hamlet | Works | `draw 1` and `gain 2 actions`, on a card costing coins. The turn's one action comes from the `resource "actions"` declaration alone |
| 2 | Market | Bazaar | Works | Four resources moved at once: a card, an action, a buy and a coin |
| 3 | Cellar | Larder | Workaround | `discard N` takes a fixed count, so "discard any number, draw that many" becomes a particular number. Which cards go is still the player's choice; how many is not |
| 4 | Chapel | Shrine | Works | `exhaust 1` trashes a card out of the deck, resolved through the chooser |
| 5 | Moat | Bulwark | Works | A card in hand is active, so it reacts from there without being played: `on before_damaged(target:player) once per turn: cancel` |
| 6 | Throne Room | Regent | Works | `choose 1 from hand where tag:action as picked`, then `play picked, free` and `replay played`. The chosen card is really played — it leaves hand, raises `card_played` and is discarded — and then resolves a second time. Before `play` existed both halves were `replay`, so the card never left hand and nothing counted it as played |
| 7 | Buying from the supply | | Not expressible | Pools exist now (gap 7), but a supply is a pile that runs out and a definition cannot hold a count. `cost 5 coins` is already refused when unaffordable; what is missing is a buy phase — nothing in the repo ever spends `buys` — and a cost in two currencies at once (gap 15) |
| 8 | Militia | | Not expressible | There is one player, and enemies are actors without hands or decks, so an effect reaching into another player's hand has nobody to reach |

## Darkest Dungeon

Content: [darkest_dungeon.cantrip](../samples/corpus/darkest_dungeon.cantrip). Tests: [darkest_dungeon.tests.cantrip](../samples/corpus/darkest_dungeon.tests.cantrip).

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

## Open gaps, most useful first

What the language cannot say yet, ranked by how many rows of the tables above each one holds back. The numbers are the gaps' permanent names: comments in the corpus and elsewhere refer to them. An effect from your game that is not here, or that the language cannot write, is worth [reporting with the effect form](https://github.com/AGomnes/Cantrip/issues/new?template=effect.yml): it is how this list grows.

| Gap | What is missing | Rows | What works today |
|---|---|---|---|
| 12 | **Space and richer boards.** Actors have board slots, not positions in space, and each side has one row. | Dota 2 #6, #7; Monster Train #10 | Slots and `adjacent(target)`. `within` parses, and a game can give it a meaning through its host's `TryCall`. |
| 15 | **Costs in several currencies, or paid by a sacrifice.** A cost is one amount in one resource. | Magic #7; Inscryption #6; Dominion #7 | A cost in a named resource, `cost 2 bones`. A sacrifice written into the effect, which cannot refuse the play. |
| 5 | **Effects as values.** Nothing can copy another entity's effects, or switch them off. `copy` duplicates an entity's *state* — its live stats, tags and statuses — which is a different thing. | Balatro #6; Hearthstone #9 | `copy` for an entity's state. Nothing for its effects. |
| 17 | **A run above the battle.** A battle is the outermost thing content can see: nothing carries lives, candles or stress from one battle to the next, and `once per run` is the only nod to runs. | Inscryption #8; Darkest Dungeon #7 | The game carries hp, deck and relics between battles in its own code, as [Winning, losing and several battles](csharp.md#winning-losing-and-several-battles) shows. `cantrip sim` plays a gauntlet a `scenario` states; it does not generate one. |
| 3 | **Target rules beyond choosing a card's target.** There is no blocking step for a rule such as Flying, and no `card:self`. Enemy moves and the `attack` verb do not ask the `targetable` channel, so a taunt does not bind them. | Magic #8; Darkest Dungeon #6 | The `targetable` channel for cards. A card that limits its own reach names itself, as in `card:Pike`. |
| 6 | **Whether a listener should hear the event that brought it into play.** Today it does, so each listener of that shape must leave its own cause out. The sample roguelite's Chill relies on the current behaviour, so changing it is a decision rather than a fix. | Slay the Spire #10; Hearthstone #6 | A filter: `not target:self`, `not card:Reverb`. |
| 18 | **A priority window.** Nothing lets one side respond to a card while it is being played. | Magic #5 | A permanent that commits in advance to countering the next spell, which is still cast and paid for. |
| 8 | **Grouping over collections.** Counting by rank or suit, distinct values, runs. | Balatro #7 | `where` filters and `.count`. |
| 9 | **Modifiers in source order.** Layers apply in a fixed order, not by the position of their sources, as Balatro's jokers need. It would be a ruleset option. | Balatro #5 | The fixed layer order, which `modifier_layers` can rearrange. |
| 7 | **Pools as values, and piles that run out.** A pool cannot be named, counted or iterated, and a definition holds no count, so a supply pile cannot deplete. Dominion also needs a buy phase. | Dominion #7 | `discover` offers content by filter and weight. |
| 4 | **Verbs that return values.** A verb cannot hand a result to an expression, as `let x = shatter target` would need. | None | `into` binds what a damage verb landed. |

The other workarounds are explained in their rows: Heavy Blade (Slay the Spire #4), Multistrike (Monster Train #4), Abstract Joker (Balatro #2) and Cellar (Dominion #3). Militia (Dominion #8) needs a second player with a hand, which Cantrip does not have by design.

**`copy`, `play` and `transform` do not move this table.** They are three verbs content kept working around, and what they change is stated in the rows above: `play` makes Dominion #6 Throne Room actually play a card, and `transform` lets Slay the Spire #22's Slime King split without `kill self`. None of them closes gap 5: `copy` duplicates an entity's state, not another entity's effects, so Silence (Hearthstone #9) and Blueprint (Balatro #6) stay exactly where they are. No row moves from *Not expressible* or *Workaround* to *Works* because of them, and the summary counts are unchanged.

## Closed gaps

- **1. Time-based triggers.** `on every 1s:` fires on the clock (Dota 2 #2, #8).
- **2. Re-telegraphing when a phase changes.** `retelegraph` on a phase re-rolls the intent as the threshold is crossed (Slay the Spire #22). It is opt-in: see [Phases](language.md#phases).
- **3, in part. Target validity.** The `targetable` channel is asked before a card accepts a target, which is how a taunt and stealth are written (Hearthstone #8, #11). The rest is open above.
- **7, in part. Definition pools.** `discover` offers content that nothing has been made from yet (Hearthstone #10, Darkest Dungeon #8). Since 0.1.0-preview.2 the player can answer the offer in a game, as with any other choice. The rest is open above.
- **10. Cancellable resets.** `event.reset` marks a reset, so `if event.reset: cancel` keeps block across turns (Slay the Spire #9).
- **11. Cooldown as a modifier channel.** `modify cooldown: x0.5` (Dota 2 #5).
- **13. Not a gap: the modifier anchor.** A modifier without `of` applies to what it is written on, and naming a scope reaches past that (Slay the Spire #7, Hearthstone #4).
- **14. `draw` for someone else.** `draw 1 to player` lets a creature draw for the player (Inscryption #7).
- **15, in part. A cost in another resource.** `cost 2 bones` is refused when the bones are not there, as an energy cost is (Inscryption #2). The rest is open above.
- **16. A declared resource creates its own stat.** `resource "actions"` with `reset_to 1` gives every actor one action a turn with nothing else to grant it (Dominion #1).

## Sharp edges

Rules that are stated in the language reference but still catch authors out, most of them found while building this corpus. Each links to where the reference states it.

- **`copy` of an actor that has been killed but not yet cleared away brings it back at full health,** because `kill` marks it dead without changing its hp and a copy takes the stats as they stand. Selectors skip dead actors, so only a name you bound yourself reaches one: `on killed: copy event.target` is an enemy that never stops. See [Built-in verbs](language.md#built-in-verbs).
- **A listener hears the event that brought it into play.** A status applied by a card hears that card's `card_played`, and a minion hears its own creation. See Joining mid-event under [Listeners](language.md#listeners).
- **Block granted on `battle_start` is gone by the first turn,** because the first turn start resets block. Grant it `on turn_start once per battle`. See [Relics](language.md#relics-items-and-keywords).
- **A modifier without `of` applies to what it is written on:** on a card, to that card's own damage. See [Modifiers](language.md#modifiers).
- **In the `where` of a modifier's `of` group, a bare qualifier tests the value being computed.** Write `it.has(tag:goblin)` to test the candidate. See [Modifiers](language.md#modifiers).
- **`other` leaves out the entity the modifier is written on,** so `of other allies` on a goblin leader buffs every goblin but the leader. See [Expressions](language.md#expressions).
- **There is no `card:self`.** A card that needs a rule about itself names itself. See [Qualifiers](language.md#qualifiers).
- **Board positions count from 0,** so the front two slots are `position <= 1`. See [Expressions](language.md#expressions).
- **`draw` draws for the controller, and a creature controls itself,** so a creature draws for the player with `draw 1 to player`. See [Built-in verbs](language.md#built-in-verbs).
- **`discover` offers from everything loaded,** not only from the card's own file. Give a pool a tag of its own. See [Built-in verbs](language.md#built-in-verbs).
- **`player` is always the game's player,** whichever side is acting. See [Names](language.md#names).
- **In a test, block set in setup, and energy set below the maximum, are reset when the first turn starts;** winning the battle removes the player's statuses and returns every card to the draw pile; `play` cannot check that a card is refused; and an `item` is given with `relic`. See [Tests](language.md#tests).
- **A name with spaces, hyphens or other punctuation is written in quotes wherever it is used,** after `name:` or `card:` too, as in `card:"Fire Bolt"`. See [Files](language.md#files) and [Qualifiers](language.md#qualifiers).
- **A `once per` limit is spent when the listener fires,** even if an `if` in its body then does nothing. Put the condition in the filter: `on owner.damaged(owner.hp <= owner.max_hp / 2) once per battle:`. See [Listeners](language.md#listeners).
- **A listener's scope is matched against the event's target,** so `on owner.card_played` hears cards played at the holder, not by it. For the cards it plays, write `on card_played(source:owner)`. See [Listeners](language.md#listeners).
- **A tag works only on the `tags` line.** Under a card, `exhaust` on a line of its own is a property that nothing reads, and the card is discarded as usual; `lint` warns about it (CT316). See [Cards](language.md#cards).
- **`damage` is what an entity deals and `damage_taken` what it receives,** so a Vulnerable is `modify damage_taken: x1.5`; written with `damage`, it makes its host hit harder. See [Modifiers](language.md#modifiers).
- **A status is read through the entity that has it,** as in `owner.Weak` or `target.Weak`. There is no name `host`. See [Statuses](language.md#statuses).
- **`copy` fires `on created(self)` on the original,** because the event carries `copy_of` and that makes the original involved in it. A minion that reacts to being created also reacts to being copied. See [Built-in verbs](language.md#built-in-verbs).
- **A copied status raises no `status_applied` and skips `immune`,** because a copy is a snapshot of a state rather than a new application. A poison-immune thing copied while poisoned arrives poisoned. See [Built-in verbs](language.md#built-in-verbs).
- **A `play` in a test's own body is the test's verb, and a `play` anywhere else is the rules'.** The test's puts a card into hand by name and plays it from there; the rules' plays a card that is already in a pile. A content verb containing `play` is the rules' even when a test line calls it. See [Built-in verbs](language.md#built-in-verbs) and [Tests](language.md#tests).

The summary counts the rows of the per-game tables. If the two ever disagree, the rows are right.
