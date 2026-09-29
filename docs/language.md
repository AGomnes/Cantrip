# Language reference

> These docs describe the `main` branch, which can be ahead of the latest release. The changelog's [Unreleased](../CHANGELOG.md#unreleased) section lists what that release lacks, and each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

This page sets out the declarations, statements and rules of the `.cantrip` language, as the rules engine in `src/Cantrip.Core` runs them. It is a reference: to learn the language by writing a first card, status, relic and enemy, start with [Writing content](writing-content.md), and see [the samples](../samples/README.md) for worked examples with tests. The [last section](#implementation-notes) notes a few implementation choices worth knowing.

- [Files](#files)
- [Terms](#terms)
- [Declarations](#declarations)
- [Cards](#cards)
- [Statuses](#statuses)
- [Relics, items and keywords](#relics-items-and-keywords)
- [Enemies](#enemies)
- [Abilities and real time](#abilities-and-real-time)
- [Resources](#resources)
- [Listeners](#listeners)
- [Built-in events](#built-in-events)
- [Modifiers](#modifiers)
- [Statements](#statements)
- [Expressions](#expressions)
- [Names](#names)
- [Built-in verbs](#built-in-verbs)
- [Content-defined verbs](#content-defined-verbs)
- [Scheduling](#scheduling)
- [Rulesets](#rulesets)
- [How a battle runs](#how-a-battle-runs)
- [Descriptions](#descriptions)
- [Tests](#tests)
- [Scenarios](#scenarios)
- [Determinism](#determinism)
- [Diagnostics](#diagnostics)
- [Implementation notes](#implementation-notes)

## Files

Content lives in `.cantrip` files. A folder loads every `.cantrip` file under it, in path order.

- **Indentation** delimits blocks. Use spaces; a tab counts as up to the next multiple of four. A line that does not line up with an enclosing block is error CT0001.
- **Comments** start with `#` and run to the end of the line. Blank and comment-only lines are ignored entirely.
- **Names** of definitions are strings (`card "Fire Bolt"`) or bare words (`status Poison`). A name with spaces, hyphens or other punctuation, such as `"Strike+"`, is written in quotes wherever it is used, after a qualifier too, as in `name:"Strike+"` (see [Qualifiers](#qualifiers)), so single-word names are easier to type.
- **Keywords** are case-insensitive. Names are matched case-insensitively.
- A block can be written on the same line after its colon: `if target.dead: draw 1`, `effect: deal 6 to target`.
- **Editors.** In Godot, the addon's editor dock writes, lints and tests content without leaving the editor, highlighted and checked as you type; see [godot.md](godot.md). Outside Godot the files are written in any text editor, and there is no syntax-highlighting package for one yet.

## Terms

| Term | Meaning |
|---|---|
| definition | What one declaration describes, such as `card Strike`. It is loaded once. |
| entity | One thing in play made from a definition: each Strike in the deck, each Poison on an enemy. |
| actor | An entity that takes turns and has hp: the player, an enemy, or an `actor` such as a summoned minion. |
| stat | A number on an entity: `hp`, `block`, `cost`, or any name content gives one. |
| host, owner | The entity a status is attached to is its host; inside the status, `owner` is the host. A card or relic's owner is the actor holding it. |
| controller | The actor at the top of the ownership chain: the player for their cards, relics and the statuses on them; an actor controls itself. |
| source, target | Who is acting, and who it is aimed at. |
| anchor | What a modifier written without `of` applies to: a status's host, a relic's holder, or the card itself. See [Modifiers](#modifiers). |
| scope | Whose events a listener hears, as in `on owner.damaged`, or whose values a modifier changes, as in `of enemies`. See [Listeners](#listeners) and [Modifiers](#modifiers). |
| channel | A value that modifiers can change: `damage`, `block`, `cost` and the others under [Modifiers](#modifiers), or any stat. |
| layer | The stage a modifier works in: add, multiply, clamp or override. |
| qualifier | A `word:value` test such as `tag:fire` or `source:self`. See [Qualifiers](#qualifiers). |
| chooser | Whatever answers a choice: the player through the game's interface, or the `answer` queue in a test. |
| causal chain | Everything one action sets off. Loop protection and `once per chain` count within it. |
| timing | Whether a listener runs before, instead of, or after its event. Not the same as an enemy's **phase**, which is a stage of its behaviour, such as below half health. |

## Declarations

| Declaration | Purpose |
|---|---|
| `card "Name"` | A playable card |
| `status "Name"` | A status attached to an entity |
| `relic "Name"`, `item "Name"` | Passive effects held by the player |
| `keyword "Name"` | A status-like entity used for keywords such as Retain |
| `enemy "Name"` | An enemy actor with moves |
| `actor "Name"` | A generic actor; when created, it joins its creator's side |
| `hero "Name"` | A party member: an actor on the player's side that the game asks for input |
| `ability "Name"` | An ability with a cooldown, for real-time play |
| `resource "stat"` | Bounds and reset rules for a stat |
| `board "Name"` | The shape of the board a battle is fought on |
| `verb name(params):` | A verb written in the DSL |
| `ruleset` | Rules the content is written against |
| `test "Name"` | A test run by `cantrip test` |
| `scenario "Name"` | A gauntlet played many times by `cantrip sim` |

Inside a declaration:

- **Properties** are `name value...`. Values may be separated by commas or spaces: `tags attack, fire` and `pattern cycle Chomp, Bellow`. A property with a single number becomes a **stat** of every entity created from the definition (`cost 2`, `hp 40`, `mark 0`). `hp` also sets `max_hp` unless both are given.
- **Blocks** are `name:` followed by statements: `effect:`, `move "Chomp":`.
- **Listeners** are `on ...:` blocks. See [Listeners](#listeners).
- **Modifiers** are `modify ...:` lines. See [Modifiers](#modifiers).
- **Presentation** properties are `text`, `text_override`, `flavour` (or `flavor`) and `text_checked`. See [Descriptions](#descriptions).

What each declaration reads, beyond the listeners, modifiers, `tags` and presentation properties that any of them can have:

| Declaration | Properties it reads | Blocks it runs |
|---|---|---|
| `card` | `cost`, `target` | `effect:` |
| `status`, `keyword` | `stacking`, `max_stacks`, `decay`, `flags`, `immune` | none |
| `relic`, `item` | none | none |
| `enemy`, `actor` | `hp`, `phase`, `pattern`, `immune` | `move "Name":` |
| `hero` | `hp`, `abilities`, `immune` | none |
| `ability` | `cooldown` | `effect:` |
| `resource` | `min`, `max`, `reset_to`, `reset_on` | none |
| `board` | `lanes`, `ranks`, `facing`/`shared`, `metric`, `on_vacated`, `lane_word`, `rank_word` | none |

`rarity` and `weight` are read by [`discover`](#built-in-verbs) on any kind of definition.

**Anything else is only a stat, or a label.** A property the engine does not read is accepted silently and becomes a stat, or nothing at all: `max_stack 3`, with the `s` missing, leaves a status with no cap. `duration 3` on a status does not make it last three turns either (see [Statuses](#statuses)), and `exhaust` on a line of its own does not exhaust a card: a tag works only on the `tags` line, and `lint` warns about a tag written this way (CT316). Any other line ending in a colon is taken as a labelled block, and a labelled block in a declaration never runs. A listener written the wrong way round, such as `when card_played:` or `once per battle on card_played:`, is one of these: it loads, `describe` still prints rules text for it, and it never fires. `lint` warns about every such block (CT313) and suggests the listener it looks like. A listener always starts with `on`. A game that runs a block of its own from C#, reading it through `EntityDefinition.Blocks`, names it in `LintOptions.HostBlocks` so the linter knows it runs.

## Cards

A card, an ability and an enemy's move are the same thing: an action in combat, with an effect and a
target. They differ only in what decides when it can be used.

| | Available when | Lives as |
|---|---|---|
| `card` | it is in hand and its cost is paid | an entity in a zone, so it can be drawn, discarded, copied or changed on its own |
| [`ability`](#abilities-and-real-time) | its cooldown has run out | part of the actor it was granted to |
| [`move`](#enemies) | its enemy's pattern picks it, and its phase allows it | part of the enemy that has it |

The effect body is the same in all three, and so are the events it raises and the rules text it
generates. A game with no cards at all is the same engine with the piles left out, as
[samples/abilities](../samples/abilities) shows.

```
card "Strike"
  cost 1
  target enemy
  tags attack
  effect:
    deal 6 to target

card "Whirlwind"
  cost x
  tags attack
  effect:
    repeat x:
      deal 5 to all enemies
```

| Property | Meaning |
|---|---|
| `cost N` | Energy to play. Goes through the `cost` modifier channel. |
| `cost x` | Spends all energy; the effect sees the amount as `x`. |
| `cost N <resource>` | Paid in that resource instead of energy: `cost 2 bones`. The card is refused when the payer has too little, exactly as it would be for energy, and `cost x bones` spends all of it. Declare the resource with `resource` so its bounds are known. |
| `target enemy` | Needs a living enemy. With one candidate it is chosen automatically; with several, the chooser picks. |
| `target ally` | Needs a living ally. With one candidate — a party of one, so usually the player — it is chosen automatically; with several, the chooser picks. |
| `target self` | Targets the player. Not a choice, so nothing is asked about it. |
| `target any` | Any living actor, or none. |
| `target <side> where <filter>` | The same, narrowed to the candidates the filter accepts. See [Targets](#targets). |
| `range N` | How far it reaches, in slots on the [board](#boards): `range 1` is melee. Goes through the `range` modifier channel. |
| `range A..B` | A span, for something that cannot be used point blank: `range 2..3`. |
| (none) | No target. |

### Targets

Everything that is pointed at somebody settles who that is the same way: a card, an [ability](#abilities-and-real-time), an enemy's move, and the [`attack`](#built-in-verbs) verb. A candidate has to pass all four of these, in this order:

1. **Side, and alive.** What the `target` word names: `enemy`, `ally`, `self` or `any`.
2. **Reach.** The action's `range`, through the `range` modifier channel, measured against the [distance](#boards) between whoever is using it and the candidate. An action that prints no `range`, in a game with nothing on the channel, asks nothing and reaches everybody.
3. **The action's own `where` filter**, if it has one.
4. **The [`targetable`](#modifiers) channel**, which is how content adds rules from somewhere other than the action itself: a taunt, a stealth.

```
card "Pike"
  cost 1
  target enemy where it.rank <= 1
  tags attack
  effect:
    deal 5 to target
```

`it` is the candidate, so `it.rank`, `it.hp` and `it.has(Burn)` all read the one being considered. It is an ordinary [`where`](#expressions), so a bare qualifier such as `tag:undead` tests the candidate too — unlike the same words inside a `targetable` modifier, where a bare qualifier tests the card being played. `self` is the card or ability; `source` is whoever is using it.

This is how a card states a rule about its own reach, and it is deliberately **not** a modifier channel: a card's printed reach is its own rule, and a stranger's modifier should not rewrite it. The alternative was a `targetable` modifier that had to name its own card, because a modifier written on a card in hand otherwise binds every card played while it sits there, and there is no `card:self`.

The generated rules text says the filter out loud — "Targets an enemy in the front 2 ranks." — in the board's own [`rank_word`](#boards), so a train reads "in the front 2 slots" without writing `text_override` on every card.

**`range` is a channel**, with the printed range as its base, so a status shortens everything its holder points at, including a card that printed no range at all:

```
status "Crippled"
  stacking none
  modify range: set 1
```

The channel computes the far end of the reach and the near end follows it down, so a longbow told to be melee becomes a melee weapon rather than something that can no longer reach anything. An enemy's move takes a reach in its header the same way: `move "Swing" range 1:`.

An action whose filter leaves nobody has no legal target, which means what it always meant: the card cannot be played, and the ability is refused.

Area and random effects are not target selection and ask none of this. `deal 3 to enemies` reaches a stealthed minion, and one a reach limit could not be pointed at, exactly as it always has.

Tags with built-in behaviour:

| Tag | Behaviour |
|---|---|
| `exhaust` | Goes to the exhaust pile after it is played. |
| `power` | Goes to the powers zone after it is played. Its listeners and modifiers only work once played. |
| `retain` | Stays in hand at the end of the turn. |
| `ethereal` | Exhausted if still in hand at the end of the turn. |
| `unplayable` | `Play` refuses it. |
| `attack` | Counted by the `attacks` history counter. |

These work only on the `tags` line. Written on a line of its own, as in `exhaust`, the word is a property that nothing reads, so the card is discarded as usual; `lint` warns about it (CT316).

Other cards listen from hand, so a curse can hurt while it is held (`on ...:` blocks are covered under [Listeners](#listeners)):

```
card "Ache"
  cost 0
  tags curse, unplayable
  on card_played:
    lose 1 hp
```

Playing a card checks energy and target, pays the cost, moves the card to the `play` zone, raises `card_played` around its effect, then moves it to its destination and resolves every queued trigger. A `before_card_played` listener that cancels refunds nothing because nothing was paid; an `instead_of_card_played` listener replaces the effect but the cost is still paid.

**Upgrades.** There is no upgrade mechanism. `upgraded` is one of the words the parser accepts as a flag after a comma, but nothing in the engine reads it (see [Statements](#statements)). Write an upgraded card as a definition of its own, and have the game put it in the deck in place of the original between battles:

```
card "Strike+"
  cost 1
  target enemy
  tags attack
  effect:
    deal 9 to target
```

A card can make the swap during a battle with `destroy` and `create`. The new card stays in the deck when the battle ends, like any other card, and the old one is gone:

```
card Hone
  cost 1
  effect:
    choose 1 from hand where name:Strike as picked
    if picked:
      destroy picked
      create "Strike+" into hand
```

A tag such as `upgraded` on the new card lets reward pools leave it out: see [Card upgrades](writing-content.md#card-upgrades), which has the recipe with its tests.

## Statuses

```
status "Poison"
  tags dot, poison, debuff
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block
    stacks -1

status "Weak"
  tags debuff
  stacking duration
  modify damage: x0.75

status "Plating"
  tags buff
  stacking intensity
  decay 2 on turn_start
  modify block: +stacks
```

A status is an entity attached to its host. Inside it, `owner` is the host and `stacks` is its own stack count.

| Property | Meaning |
|---|---|
| `stacking intensity` | Stacks add up. The default. |
| `stacking duration` | A single stack; applying adds to `duration`, which decays by 1 at its host's turn end by default. |
| `stacking refresh` | Like duration, but applying resets `duration` to the larger value instead of adding. |
| `stacking both` | Stacks and duration both accumulate. |
| `stacking none` | Applying again does nothing. |
| `stacking separate` | Every application is its own instance, with its own source and timer. |
| `max_stacks N` | Caps stacks. |
| `decay N` | Loses N at its host's turn end (stacks, or duration for duration-like modes). |
| `decay N on <event>` | Loses N when `<event>` happens to its host (the host is the event's target or source), after the event's listeners. |
| `flags ...` | `buff`, `debuff`, `dispellable`, `persistent` (survives the end of a battle), `hidden`, `unique` (one instance per source). The tags `buff` and `debuff` set those flags too. |
| `immune tag:x`, `immune Name` | On a status or enemy: blocks statuses with that tag or name from being applied to the host. `status_resisted` is raised instead. |

A status is removed when its counter reaches zero: `stacks` for intensity-like modes, `duration` for duration-like ones.

A status is read by its name on the entity that has it: `owner.Weak` inside a status or relic, `target.Weak` in a card aimed at an enemy. That gives its counter: stacks, or the remaining duration for duration and refresh statuses, and 0 when the entity does not have it. Writing `target.Weak -1` changes the same counter, and `target.Weak +2` applies the status if it was absent. There is no name `host`: inside a status, its host is `owner`.

A status has no length of its own. The number comes from whoever applies it: `apply Weak 2` is a duration of 2. A `duration 2` line in the status's declaration does not change that. On a `duration`, `refresh` or `both` status the duration always comes from the application, so the line does nothing and `lint` warns about it (CT315); on any other status it is only a stat.

### How long a duration status lasts

A `duration` or `refresh` status loses 1 at the end of each of its **host's** turns, and goes when it reaches 0. The player's turn ends before the enemies act, so the same number buys a different number of enemy turns depending on who has the status. Applied during the player's first turn:

| Moment | `apply Warded 2 to player` | `apply Weak 2 to target` (an enemy) |
|---|---|---|
| Just applied | 2 | 2 |
| The player's turn ends | 1 | 2 |
| The enemy's turn: it attacks | 1: the hit is halved | 2: the hit is weakened |
| The enemy's turn ends | 1 | 1 |
| The player's second turn ends | 0: removed | 1 |
| The enemy's turn: it attacks | full damage | 1: the hit is weakened |
| The enemy's turn ends | | 0: removed |

Here `Warded` is a `stacking duration` status with `modify damage_taken: x0.5`, and `Weak` is the one above. In short:

- **On an enemy**, N covers the enemy's next N turns. A status that changes what the enemy takes, such as a Vulnerable with `modify damage_taken: x1.5`, covers the rest of the player's current turn and the player's next N − 1 turns.
- **On the player, applied by the player's own card**, N covers only N − 1 enemy turns, because the player's turn end ticks it first.
- **On the player, applied by an enemy's move**, N covers the player's next N turns.

To protect the player for the next N enemy turns, either apply N + 1, or declare the status with `decay 1 on turn_start`, so that it ticks at the start of the player's turn instead of the end:

```
status Guarded
  tags buff
  stacking duration
  decay 1 on turn_start
  modify damage_taken: x0.5
```

`apply Guarded 2 to player` then covers the next two enemy turns.

**`for`** sets a deadline on the clock: `apply Chill 1 for 3s`, or `apply Shield for 2 turns`. The status is removed when the clock reaches it, whatever its counter says. The turn clock moves once per round, at the start of each player turn after the first, once that turn's `turn_start` event and `next turn:` blocks have run, so `for 2 turns` applied during the player's turn covers two enemy turns.

`for` never lengthens a `duration` or `refresh` status: whichever runs out first removes it. `apply Weak for 2 turns` is a duration of 1, the default amount, with a two-turn deadline, so it covers one enemy turn; write `apply Weak 2`. `lint` warns when `for` gives more turns than the amount applied to such a status (CT314). It says nothing where the deadline can matter: on a status that decays on some other event or not at all, and where the status may land on a card, which has no turns to tick it down. Use `for` with `intensity`, `none` and `separate` statuses, which have no duration of their own. On a `stacking both` status the `for` length becomes the duration; `both` has no decay unless it declares one.

The recipes [A debuff that lasts N enemy turns](writing-content.md#a-debuff-that-lasts-n-enemy-turns) and [A buff that lasts N enemy attacks](writing-content.md#a-buff-that-lasts-n-enemy-attacks) show these with tests.

## Relics, items and keywords

Relics and items are active while the player holds them: the game gives them with `AddRelic`, and a test with `relic Name`. Their listeners and modifiers treat the holder as "you".

```
relic "Tally"
  on card_played(tag:attack) once per turn:
    gain 1 gold

# At the start of each battle. Block gained on battle_start would be reset by the
# first turn start, so this grants it on the first turn instead.
relic "Anchor"
  on turn_start once per battle:
    block 10

# Every third card played draws a card. The count is a stat on the relic.
relic "Metronome"
  beats 0
  on card_played:
    beats += 1
    if beats >= 3:
      beats = 0
      draw 1

# Always on: every block the holder gains is 1 higher.
relic "Bracer"
  modify block: +1
```

`once per battle` starts again with each new battle, and `once per turn` with each turn. A stat such as `beats` keeps its value between battles, for as long as the game keeps the relic.

A `keyword` definition behaves like a status. Cards tagged `retain` or `exhaust` get those behaviours without any keyword definition; defining `keyword "Exhaust"` only adds a tooltip.

## Enemies

```
# Uses the Strength status defined under Modifiers. Without it, Chant still runs but only
# sets a stat that nothing reads; `cantrip lint` reports the missing status.
enemy "Cultist"
  hp 48
  move "Chant":
    gain 3 Strength
  move "Slash":
    deal 6 to player
  pattern cycle Chant, Slash

enemy "Gremlin"
  hp 20
  move "Scratch" weight 3:
    deal 4 to player
  move "Hide" weight 1:
    block 5
  pattern random_no_repeat
```

| Pattern | Meaning |
|---|---|
| `pattern cycle A, B` | Moves in order, then loop. Without a pattern, moves cycle in declaration order. |
| `pattern random` | A weighted random move each turn (`weight N` on the move, default 1). |
| `pattern random_no_repeat` | Weighted random, never the same move twice in a row (unless it is the only move). |

The next move (the intent, readable as `enemy.intent`) is rolled when the battle starts, when an enemy spawns or is created mid-battle, and after each enemy turn. Inside a move, `self` is the enemy and `target` is the player. `use Chant` makes an enemy perform one of its moves.

### Telegraphed targets

A move says who it is going to hit with `at`, and the answer is readable before the blow lands:

```
enemy "Brigand"
  hp 46
  move "Cutthroat" at lowest hp enemies:
    deal 8 to target
  move "Volley" at random enemies:
    deal 4 to target
  pattern cycle Cutthroat, Volley
```

`at` takes an ordinary selector, so `at lowest hp enemies`, `at highest hp enemies`, `at lowest rank enemies` and `at random enemies` all work with no words of their own. Inside an enemy, `enemies` is the player's side.

**With no `at`, the move aims at a living party member, drawn uniformly.** For a party of one that is that one member, and nothing is drawn — which is why adding this moved no existing game's results.

`enemy.intent` is still the move's name. `enemy.intent_target` is who it is aimed at, and it is **recomputed every time it is read**, not stored: a taunt applied after the intent was rolled changes the answer with no event for a UI to have missed, and so does a death or a swap. What it answers is exactly what the move itself decides when it runs, because both ask one question of one rule. When every option is hidden the enemy still swings, at the one it was going to hit.

### Phases

**Phases** gate which moves an enemy may choose from. This boss chomps until it drops to half health, then alternates splitting and chomping:

```
enemy "Slime King"
  hp 60
  phase Broken when hp <= max_hp / 2, retelegraph
  move "Chomp":
    deal 11 to player
  move "Split" phase Broken:
    deal 5 to player
  pattern cycle Split, Chomp
```

The rules that decide what the player sees:

- **A move with no `phase` is available in every phase**; a move with one only while that phase is active. Before any phase applies, `enemy.phase` is `none`, which is compared without quotes: `enemy.phase == none`.
- **A cycle skips the moves the current phase does not allow.** Before Broken, `cycle Split, Chomp` is Chomp every turn.
- **Entering a phase restarts the pattern** from its first allowed move, because the old position counted through a list of moves that is no longer the same one. That is why Split is listed first: listed second, it would come a turn later.
- **The phase changes as soon as a hit takes the enemy across the threshold**, and otherwise when its next intent is rolled. The **last** declared phase whose condition holds is the one that applies, so thresholds can be written in the order they are thought of (three quarters, then half, then a quarter) and the deepest one that is true wins.
- **An enemy has one pattern**, written at the top level of the enemy, not inside a phase. Phases change which of its moves it can use.

Generated rules text names the phase a move is limited to. `cantrip describe` gives the Slime King as "Chomp: Deal 11 damage to the player. Split (Broken phase only): Deal 5 damage to the player."

**`retelegraph`** on a phase re-rolls the intent the moment the enemy enters that phase, so the player is shown the new phase's first move straight away. Without it, the intent already on show stays, and the new pattern starts on the enemy's next roll. For the Slime King, crossing half health during the player's turn gives:

| Pattern | With `retelegraph` | Without |
|---|---|---|
| `cycle Split, Chomp` | Split, then Chomp | Chomp (already shown), then Split |
| `cycle Chomp, Split` | Chomp, then Split | Chomp (already shown), Chomp, then Split |

It is opt-in because it gives up a guarantee that is otherwise worth having: ordinarily the intent shown during your turn is exactly the move that follows, and a re-telegraphing boss can change its mind after you have committed. A phase crossed by a killing blow re-telegraphs nothing, and a hit absorbed entirely by block cannot cross a threshold at all.

For a boss whose whole pattern changes, give it two phases that between them cover every hp value, and gate every move. With no `pattern` line, moves cycle in the order they are written, skipping those the phase does not allow:

```
enemy "Archmage"
  hp 120
  phase Calm when hp > max_hp / 2
  phase Unbound when hp <= max_hp / 2, retelegraph
  move "Bolt" phase Calm:
    deal 11 to player
  move "Barrier" phase Calm:
    block 12
  move "Meteor" phase Unbound:
    deal 16 to player
  move "Drain" phase Unbound:
    deal 8 to player
    heal 8
```

It alternates Bolt and Barrier, and from half health Meteor and Drain, starting with Meteor. Tests compare the intent and the phase with quoted strings, since they are names rather than definitions:

```
test "The Archmage opens its Unbound phase with Meteor"
  enemy "Archmage"
  expect enemy.phase == "Calm"
  expect enemy.intent == "Bolt"
  deal 60 to enemy
  expect enemy.phase == "Unbound"
  expect enemy.intent == "Meteor"
```

Written without quotes, `enemy.intent == Meteor` is a runtime error: `Unknown name`. The phase is part of the saved game. The sample roguelite's Archmage, in [samples/slice/enemies.cantrip](../samples/slice/enemies.cantrip), is this boss with Strength added.

## The party

A **`hero`** is an actor on the player's side that the game asks for input. It is an ordinary actor in every other way: `EntityKind.Actor`, `Team.Player`, on the board with a place, in `allies`, a legal target for a taunt.

```
hero "Crusader"
  hp 38
  abilities Smite, Bulwark

hero "Vestal"
  hp 28
  abilities Mend
```

`abilities` hands those abilities over when the member is made. Content adds a member with `create Vestal`, which is how a mid-run recruit or a mid-battle summon that *acts* is written; a game adds one with [`AddHero`](csharp.md#the-party).

**The leader is already a member.** `CreatePlayer` makes a party of one, and that member is the leader, so a party of four is one `CreatePlayer` and three `AddHero`. Everything below reads the same for a party of one as it did before a party existed, which is the whole reason it could be added at 1.0.

**A summoned `actor` is not a member.** That is deliberate: a Monster Train or Hearthstone minion attacks from its own `turn_end` and must not acquire an input step nobody asked for. `allies` is everyone on the side, members and summons both; `party` is the members.

### What the words mean

| Name | Meaning |
|---|---|
| `player`, `leader` | one entity: the party's leader, the one that holds the run's relics and gold |
| `party` | the living members, in the order they take their steps |
| `allies` | everyone on the side, members and summons both |
| `fallen` | the dead of the side, in the order they fell â the group the other three leave out |

`player` keeps meaning the leader and always will. In content that declares a `hero`, writing it inside an enemy's move or a card's or ability's effect is error **CT326**, because `deal 5 to player` in an enemy move would hit one hero however carefully the enemy telegraphed somebody else — quietly, which is the one class of wrong answer this project refuses to ship. Write `target` for whoever the line is aimed at, `leader` where the run's own actor really is meant, or `party` for all of them. Everywhere else — a relic's listener, a run's gold, a test's own lines — `player` is still the right word and is left alone.

### The turn

**One round is one turn.** `turn` is the round number in **both** modes, because `on every N turns`, `once per turn`, the history counters, the saved turn number and `sim`'s stall limit all key off it. Four members must not make it mean four things.

There are two modes, set with [`turns:`](#rulesets):

| `turns:` | The round |
|---|---|
| `sides` | the default: the party acts, then the enemies. Every member's `turn_start` fires at the party's turn start, and the game acts with them in whatever order it likes. |
| `initiative` | one order over both sides, so a hero acts between two enemies. Each combatant's `turn_start` and `turn_end` fire at **its own step**, and the round ends when every living one has taken one. |

Under `sides`, a game asks whether a member still has its step with [`CanAct`](csharp.md#the-party) and says it is done with `Pass`; when the last one that could act has passed, the enemies take their turn. `ActiveMember` is the one the engine would offer next — a suggestion for a UI, not a rule, because any waiting member may act.

Under `initiative`, `ActiveMember` **is** the rule: that member and no other may act, `Pass` on anyone else is refused, and passing runs the round on through the enemy steps until one of ours is up again. `EndTurn` is still "pass everyone of ours who has not acted", so the rest of the round happens around them. For a party of one against one enemy the two modes play the same round, to the turn number and the hp.

`order:` says who goes first. `position` is the default — where they stand, `(lane, rank)`, with the party's side before the enemies' — and `speed` sorts by the `speed` stat descending, ties broken by where they stand and then by id. `speed` orders the party under `sides` too, where it decides which member the engine offers first and the order they draw in. An actor with no `speed` reads 0.

**The clock moves once a round in both modes.** Under `sides` it moves after the side's turn has started, so `in 1 turn:` work lands after the turn-start resets. Under `initiative` the combatants start their turns one at a time and there is no moment at which they all have, so it moves at the **start** of the round, before anybody's step: `on every 2 turns` fires for everyone before anyone acts, and work hung on `in N turns:` arrives ahead of the turn-start resets. Content that wants its own reset first writes `next turn:`, which fires at that actor's own step.

The order is read off the living every time it is asked, never stored. So a member that falls mid-round is skipped with no bookkeeping, one revived after its step does not take a second one — who has acted is remembered by id — and one revived or summoned before its place still takes the step it had not taken.

A summoned `actor` on your side is in the order but is not a member, so under `initiative` its step runs straight through: its `turn_start` and `turn_end` fire, which is how a minion that attacks from its own turn end attacks, and nobody is asked what it does. That is the same division `sides` makes, where a summon's turn events fire with the side's and only members are passed.

**`once per turn` is once per round, per listening entity.** A status on each of four members fires four times a round — once each, because each is a different listener. A relic on the leader fires once. That was always true and is worth saying twice.

### Resources, cards and abilities

Each member has its own `energy`, its own hand and its own piles, because the engine was already per-actor: `max_energy` on a hero gives it a pool that resets at its own turn start, and `draw 2 to allies.last` draws from that member's own pile.

Two shapes fall out of that, and the engine tells them apart with no setting at all:

- **A deck each.** A member with a draw pile of its own draws `hand_size` from it at the party's turn start, plays from its own hand and pays its own `energy`.
- **One hand for the party.** The cards belong to the leader, so the leader draws, pays and files them. A play then names the member *performing* it: the cost comes out of the leader's pool, and everything else is the performer's — `card_played`'s source, the damage, `source:` filters, the performer's own statuses and modifiers. In a listener, `event.source` is that member (`source` on its own is the listening entity, as it is for every listener).

**A member with no pile of its own draws nothing and plays from the party's hand.** That needs no code and no setting: drawing from an empty pile with an empty discard simply stops.

Cooldowns are per ability entity, so per member for free: two members with the same ability have two cooldowns. On a turn clock the clock advances once a round, so `cooldown 2 turns` is two rounds however many members act.

Restricting who may perform a card needs no new syntax — `on before_card_played(card:Smite): if not event.source.has(tag:holy): cancel` refuses the play before it commits, so the card never leaves the hand and nothing is paid.

### Death and revival

**The battle is lost when no member is alive**, not when the leader dies. A surviving summon does not keep the fight going, and a party that has lost its leader fights on. Won is unchanged: no enemy alive.

A fallen member is buried in the `dead` zone like a dead enemy and cleared at the next battle. It leaves `party`, `allies` and the turn order at once, because all three are derived from where the living actors stand and none of them is stored.

`heal` refuses a dead target, deliberately and permanently, so bringing one back is its own verb: `revive <who> [N]`, which un-buries the actor at N hp (1 by default) and raises `revived`. It does nothing to somebody who was never dead, so a card that reads "bring back a fallen ally" cannot quietly become a heal.

**`fallen` is how content names the argument.** `party` and `allies` hold the living by design, `target ally` and `target any` both want somebody alive, and `everyone where zone:dead` finds nobody, because `everyone` is the living too. `fallen` is the dead of the running side, in the order they fell:

```
card "Last Rites"
  cost 2
  effect:
    if fallen.any:
      revive fallen.first 8

ability "Cull"
  effect:
    choose 1 from fallen as who
    revive who 5
```

It reads like any other group: `fallen.count`, `fallen.first`, `fallen.last`, `for each one in fallen`. From C# it is [`runtime.Fallen`](csharp.md#the-party) and `State.Fallen(team)`, and from GDScript [`GetFallen()`](godot.md#methods).

At the end of a battle **every** member is tidied up — statuses that are not `persistent` removed, cards returned to that member's draw pile — and at the end of a turn **every** member's hand is discarded. Before 1.0 both touched the leader only.

## Abilities and real time

```
ability "Frost Nova"
  cooldown 8s
  effect:
    apply Chill 1 for 3s to enemies

ability "Smite"
  cooldown 2 turns
  target enemy
  effect:
    deal 8 to target
```

Real-time games create the runtime with a `TickClock` and call `runtime.Tick()` from their fixed timestep. `GrantAbility` attaches an ability to an actor; `UseAbility` runs it if it is off cooldown and starts the cooldown. Durations with `s` or `ms` convert to ticks on a tick clock; `turns` convert on a turn clock. Using seconds on a turn clock is an error when the line runs, and error CT325 at lint for content that states its [`clock`](#rulesets). A cooldown goes through the [`cooldown` modifier channel](#modifiers), so content can shorten it.

An ability takes a `target` line and reads it exactly as a card does — the same four filters, in [Targets](#targets). `UseAbility` with no target settles one: with a single candidate it takes it, and with several it asks the chooser. An ability with nothing legal to aim at is refused with `InvalidTarget` and does not start its cooldown, rather than running at nobody. `cast Smite` in a [test](#tests) does the same.

## Boards

Every battle is fought on a board: a rectangle of **lanes** across and **ranks** along the facing axis, both counting from 0. An actor stands on one slot, and no two actors share one.

```
board "Train"
  lanes 3
  ranks 3
  facing
  metric manhattan
  on_vacated gap
  lane_word "floor"
  rank_word "slot"
```

| Property | Values | Default |
|---|---|---|
| `lanes` | a whole number of 1 or more | `1` |
| `ranks` | a whole number of 1 or more, or `unbounded` | `unbounded` |
| `facing` / `shared` | written on its own, with nothing after it | `facing` |
| `metric` | `manhattan`, `chebyshev` | `manhattan` |
| `on_vacated` | `gap`, `close_ranks` | `gap` |
| `lane_word`, `rank_word` | one word, for rules text | `lane`, `rank` |

**Content that declares no board** is played on `lanes 1, ranks unbounded, facing, manhattan, gap`, which is the board every game had before boards existed. Nothing written against it moves.

**`facing` or `shared`.** On a `facing` board each side has its own grid, mirrored, so rank 0 is the front for both and a rank never means the same place on both sides. Two actors in the same lane at rank 0, one on each side, are **one** step apart: across the sides the rank term is `a.rank + b.rank + 1`. On a `shared` board there is one grid for everyone, a rank is the same place whoever stands on it, and an actor on either side can take any free slot.

**`metric`** decides how the lane term and the rank term combine: `manhattan` adds them, so a diagonal step is two; `chebyshev` takes the larger, so a diagonal step is one.

**`on_vacated`** decides what the survivors do when a slot's occupant leaves. `gap` is the default: nobody moves, and the hole waits for the next thing put in it. `close_ranks` steps everyone behind it in that lane forward one rank.

**Where a new actor lands.** `create` puts it in its creator's lane, on its own side, at the lowest free rank. If that lane is full, **nothing is made**: `created` binds empty, the trace says which lane was full, and the statements after it still run. That is a refusal, the way `play` on an empty pile is, rather than an error — and it is Monster Train's floor capacity with no new concept. A game that calls `SpawnEnemy` or `Instantiate` from C# takes the first lane with room.

**A freed slot is reusable.** A dead actor leaves the board for the `dead` zone, so its slot is free and a later summon fills the hole rather than landing past it. That is what keeps an adjacency aura working across a death. Survivors still never shift unless the board says `close_ranks`.

**Reading a place.** `it.lane` and `it.rank` are ordinary members, so `target enemy where it.rank <= 1` is a card whose reach is the front two ranks. `lowest rank enemies` and `highest rank enemies` sort by rank. A group of one answers `.lane` and `.rank` like the one it holds. `position` is what `rank` was called before a board had two axes: it reads the same number and keeps working for the whole of the 1.x line, with a note (CT329) suggesting the newer word. It cannot be *written*, because it names one axis of a place that has two (CT328).

**Selectors over the board.** Five, and nothing else:

| Written | Means |
|---|---|
| `adjacent(who)` | live actors **on who's own side** at distance 1. Deliberately not cross-side: on a facing board the two front ranks are one step apart, so a cross-side `adjacent` would turn an adjacency aura into a gift to the enemy |
| `within(who, N)` | live actors on **either** side at distance N or less, including `who` |
| `within(who, 5m)` | unchanged: a length with a unit is a question about a world the engine knows nothing about, and goes to the game's `IEffectHost.TryCall` |
| `lane(who)`, `rank(who)` | live actors sharing who's lane or rank, on who's side, including `who` |
| `distance(a, b)` | how many steps apart they are, by the board's metric. Anything not on the board is further away than any board is wide |

`group in group` intersects, so `enemies in within(target, 2)` is the splash of a blast. Everything else is an ordinary `where` over `it.lane` and `it.rank`: a spear that hits the one behind is `enemies where it.lane == target.lane and it.rank == target.rank + 1`. There is no cone, no beam and no template, because a lane *is* the beam on a facing board.

**Reach.** `range 1` on a card, an ability or an enemy's move (`move "Swing" range 1:`) is melee; `range 2..3` is a bow that cannot shoot point blank. It is measured with `distance` between whoever is using the action and the candidate, and it is decided in exactly one place, so a reach rule means the same thing for a card and for an enemy's move. See [Targets](#targets).

**Moving.** Movement is a member write, and there is no movement verb:

```
target.rank = 0            # pull to the front
target.rank -1             # one step forward
self.lane += 1             # up a floor
a.rank = b.rank            # swap them, because the slot is taken
```

Assigning a slot somebody else is standing on **swaps** the two, which is total, deterministic and its own inverse. A step that would leave the board stops at the edge of it, so a shove against the back wall leaves the unit against the back wall; CT327 says so when the number written could never have been a slot. A move along one axis keeps the other, so `self.lane += 1` carries the unit to the same rank of the next lane.

Any living actor on the board may be moved, by its own effect, a move, a listener or a card aimed at it. There is no permission system: a unit that must not be moved cancels [`before_moved`](#built-in-events).

```
status "Braced"
  stacking none
  on before_moved(target:owner):
    cancel
```

A move raises [`moved`](#built-in-events) with `kind` `"actor"`, `from` and `to` in words, and `from_lane`, `from_rank`, `to_lane`, `to_rank` as numbers. It reverts inside `until`, because a place is two integers and its inverse is exact — which is where a slot differs from a `transform`, refused inside `until` (CT321) because nothing remembers an old form. `on_vacated close_ranks` raises `moved` for each survivor that steps forward, after the row has closed: `before_moved` is not asked there, because refusing half a closed row would put two actors on one slot.

**Picking one.** Content may declare any number of boards by name, and a game picks one per battle with `StartBattle(board: "Train")`; a `test` picks one with `board "Train"` in its setup, and with no board named a battle is fought on the first one declared. A name no `board` declares is refused rather than invented, because the linter has to know how deep a board is to say that `it.rank <= 3` on a two-rank board reaches everybody (CT327). Changing board mid-run keeps everyone who still fits exactly where they are and gives the rest the lowest free slot.

See [`samples/board`](../samples/board) for a worked one.

## Resources

A resource is a stat with bounds and reset rules. These are built in, and content can redeclare any of them:

| Stat | Rule |
|---|---|
| `hp` | At least 0, at most `max_hp` |
| `block` | At least 0; resets to 0 on `turn_start` |
| `energy` | At least 0; resets to `max_energy` on `turn_start` |
| `stacks` | At least 0 |
| `gold` | At least 0 |

```
resource "mana"
  min 0
  max 10
  reset_to 3
  reset_on turn_start
```

A bound that names another stat (`max max_hp`) only applies to entities that have that stat. A reset happens as part of its event, to the entities the event concerns (its target and source): `before_` listeners see the old value, ordinary listeners see the new one. So "gain 1 energy at the start of your turn" works as expected.

A reset raises the ordinary `<stat>_changed` event, and marks it: `event.reset` is true only when a `reset_on` rule is doing the writing. That is how content refuses a reset without having to infer one from the value — `on before_block_changed: if event.reset: cancel` keeps block across turns, while an effect that means to set block to 0 still does.

A reset also **establishes** the stat. An entity that did not have it gets it, so declaring `resource "actions"` with `reset_to 1` gives every actor whose turn begins one action, without anything having to grant it first. A reset whose value names another stat is the exception and is still skipped for an entity without that stat — which is why an enemy never acquires `energy` from `reset_to max_energy`.

## Listeners

```
on [phase_][scope.][phase_]event[(filter)] [once per turn|battle|run|chain] [priority N]:
  statements

on every <interval>[(filter)] [once per turn|battle|run|chain] [priority N]:
  statements
```

```
relic "Thorns Ring"
  on owner.damaged(source:enemies):
    deal 3 to event.source

relic "Lizard Tail"
  on instead_of_died(target:owner) once per battle:
    heal 40

relic "Bloodlust"
  on before_damaged(source:owner):
    event.amount += 2
```

The line always starts with `on`, and `once per ...` and `priority` come after the event and its filter. A line written another way, such as `once per battle on card_played:`, is not a listener at all but a label that never runs (see [Declarations](#declarations)); `lint` warns about it (CT313) and suggests `on card_played once per battle:`.

| To run... | Write |
|---|---|
| when the holder plays an attack | `on card_played(tag:attack):` |
| the first time that happens in each battle | `on card_played(tag:attack) once per battle:` |
| at the start of the holder's turn | `on turn_start:` |
| once, on the first turn of each battle | `on turn_start once per battle:` |
| when the holder is hit by an enemy | `on owner.damaged(source:enemies):` |
| before the holder takes damage, to change or stop it | `on before_damaged(target:owner):`, then `event.amount -= 1` or `cancel` |
| when an enemy dies | `on killed(target:enemies):` |
| when a Weak status leaves anyone | `on status_removed(Weak):` |

**Timing.** `on damaged` listens after the fact. `on before_damaged` runs before, and may change `event.amount` or `cancel`. `on instead_of_died` runs in place of the action: if any instead listener fires, the default action is skipped. The timing prefix may come before the scope (`before_owner.damaged`) or after it (`owner.before_damaged`). The grammar above calls it `phase_`; it has nothing to do with an enemy's [phases](#phases).

**Scope.** `on owner.damaged` only hears `damaged` events whose target is the listener's owner. Scopes are `self`, `owner` (the host of a status, the holder of a relic), `controller`, `player`, `any`, or any name that resolves to an entity.

The scope is matched against the event's target, whatever the event. So `on owner.card_played` hears cards played *at* the holder, such as a `target self` card, and not the cards the holder plays. For those, write `on card_played(source:owner)`. In a game where only the player plays cards, plain `on card_played` on the player's relic hears the same ones.

**Your turn.** An unscoped `on turn_start` or `on turn_end` on a status, card or relic only hears its own controller's turn. Use `on any.turn_end` to hear everyone's.

**Filters.** Clauses in parentheses are joined with `and` (a comma also means `and`); `or` and `not` work too. Each clause is tested on its own:

| Clause | Matches when |
|---|---|
| an entity or group (`self`, `enemies`) | one of them is the event's target, source, card, or an entity in its data |
| a definition (`Poison`) | something from that definition is involved, or it is the status being applied |
| `tag:fire` | the event or its card has the tag |
| `source:self`, `target:owner`, ... | the event's source or target fills that role (see [Qualifiers](#qualifiers)) |
| anything else | the expression is true |

**Inside a listener**, `self` is the listening entity, `source` is also the listening entity (so a status's damage comes from the status, and its controller is the host), `target` is `event.target`, and `event` is the event. `card` is not set: the card that caused the event is `event.card`. This keeps a status's retaliation from counting as part of the card that triggered it.

**Resolution.** Before and instead listeners run immediately. After listeners are queued and resolve in order once the current action finishes; work they raise joins the back of the queue. With `triggers: immediate` in the ruleset they run immediately instead.

**Every.** `on every 1s:` fires on an interval rather than on an event. It is pumped by the clock instead of raised by anything, so only the listener whose interval has elapsed runs. The interval is written like any other duration (`1s`, `250ms`, `2 turns`), and the clock has to understand the unit: seconds mean nothing to a turn-based game, so `every 1s` there registers nothing at all rather than half-working. Say [`clock turns` or `clock ticks`](#rulesets) in the ruleset and that becomes error CT325 instead, before anything runs. Filters and `once per ...` apply as they do to any listener. When the next firing is due is part of the game's state, so a restored save resumes mid-interval instead of restarting it, and so does a hot reload. A content change can make a listener start afresh, as [Save and load](csharp.md#save-and-load) describes.

**Ordering.** Listeners for the same event run by priority (higher first), then play order (the order their entities became active), then the active side first, then registration order. The ruleset can reorder the first three.

**One is a group of one.** `.count`, `.size`, `.length`, `.first`, `.last`, `.empty` and `.any` work on a single entity as they do on a group: the count is 1, `.first` and `.last` are the entity itself. It reads the other way too: a group answers `.lane` and `.rank` for the first of them, so a group of one says where its one actor stands. That is what makes `choose` safe to read either way — `choose 1 from hand as picked` binds an entity while `choose 2` binds a group, and `picked.first` used to fall through to "a stat nothing has" and read 0 on the one-card path.

**Joining mid-event.** A listener that becomes active while an event is being handled hears that event's after timing. A status applied by a card's effect hears the `card_played` of that same card, and a minion listening `on created(kind:actor)` hears its own creation. Where that is not wanted, leave the listener's own cause out with a filter: `on created(kind:actor, not target:self):`, or `not card:Reverb` for the card that applied the status. A `power` card is the exception, since it only becomes active after its `card_played` has finished.

**Loop protection.** A listener never re-triggers from its own consequences within one causal chain, and chains stop at depth 50. `once per turn`, `once per battle`, `once per run` and `once per chain` limit how often a listener fires; `once per battle` starts again when the next battle starts.

**`once per run` has no boundary of its own.** Cantrip has no idea what a run is — a battle is the outermost thing content can see ([coverage gap 17](coverage.md#open-gaps-most-useful-first)) — so the window is *one `CardRuntime`*, and nothing in content can reset it. What that means in practice depends on what the listener is written on. On a relic held for the whole game it fires once and never again, and it survives a save and a restore, because a spent limit is part of the saved state. On a card it is reset whenever the card's listeners are registered again, which is every time the card comes back to hand, so it is closer to "once per visit to hand" than to "once per run". A game that carries a run across several `CardRuntime`s in its own code gets one use per runtime, not one per run. Where the window has to be exact, count it in a stat the game controls instead.

**A limit is spent when the listener fires,** whatever its body then does. An `if` in the body that does nothing still uses it up, so a relic that heals "the first time you fall to half health" with the condition in an `if` spends its one use on the first hit. Put the condition in the filter, which is checked before the limit: `on owner.damaged(owner.hp <= owner.max_hp / 2) once per battle:`.

## Built-in events

| Event | Fields |
|---|---|
| `damaged` | target took a hit from source (card if a card caused it). `amount` is hp actually lost after block. Data: `base`, `total`, `blocked`, `overkill`. Tags: the damage type. |
| `blocked` | block absorbed `amount` of a hit on target |
| `overkill` | a killing hit had `amount` to spare |
| `died` | target is dying. `instead_of_died` prevents it. The dying actor's own listeners still hear it. |
| `killed` | target died; source is the killer |
| `revived` | target is being brought back from the dead by source; `amount` is the hp it comes back at. Raised only for an actor that really is dead. |
| `healed` | target regained `amount` hp |
| `gained_block` | target gained `amount` block |
| `drawn` | target (a card) was drawn |
| `shuffled` | the discard pile was shuffled into the draw pile |
| `discarded`, `exhausted` | target (a card) changed zone. Data: `kind` `"card"`, `from`, `to`. A card drawn with a full hand is `discarded`. |
| `moved` | target changed where it is. A card changed zone: data `kind` `"card"`, `from`, `to` (zone names). An actor changed slot: data `kind` `"actor"`, `from`, `to` (places in words), `from_lane`, `from_rank`, `to_lane`, `to_rank`. `before_moved` refuses an actor's move; a row closing under `on_vacated close_ranks` is reported once it has closed. |
| `created` | target was created by `create`, `copy` or `shuffle <card>`. Data: `copy_of`, the original, when `copy` made it. |
| `destroyed` | target was taken out of the game |
| `transformed` | target is becoming something else and keeps its id, owner, side and place. Data: `was`, `into` (both definitions). Tags: the tags it had before. Raised once; the statuses it sheds raise nothing. |
| `card_played` | source played card on target; `amount` is the energy paid. Tags: the card's tags. |
| `status_applied` | a status is applied to target; `amount` is stacks. Data: `status` (the definition before it exists, the status entity afterwards), `status_name`. Tags: the status's tags. |
| `status_resisted` | target was immune to a status |
| `status_removed` | a status left target; `amount` is its last stacks. Data: `status`, `status_name`. |
| `turn_start`, `turn_end` | target's turn began or is ending |
| `battle_start`, `battle_end` | a battle began or ended. Data on `battle_end`: `won`. |
| `move` | an enemy (source) performs a move against target. Data: `move`. |
| `ability_used` | source used an ability. Data: `ability`. |
| `obtained` | source obtained a relic (target) |
| `<stat>_changed` | a stat changed through a verb, assignment or reset. Data: `stat`, `old`, `new`. Only raised when something listens. |

Content raises its own events with `emit`.

## Modifiers

```
modify <channel> [of <group> [where <filter>]] [where <filter>]: <amount>
```

```
status "Strength"
  stacking intensity
  modify damage: +stacks

relic "Pyromancer's Codex"
  modify damage where tag:fire, source:self: x1.5
  modify cost of cards where tag:fire: -1

relic "Siege Engine"
  modify damage_taken of enemies where hp > 20: +2

relic "Choirmaster's Baton"
  modify damage of party where tag:holy: +2
```

**Amounts** pick the layer: `+N` or `-N` (add), `xN` or `*N` (multiply; `x50%` is half), `clamp A..B` or `clamp N` (clamp; a single number is a ceiling), `=N` or `set N` (override). Values pass through the layers in ruleset order, add, multiply, clamp, override by default. Within the override layer the most recently created source wins. Damage, block and healing are rounded down after modifiers.

**Channels** are `damage`, `damage_taken`, `block`, `block_taken`, `heal`, `heal_taken`, `cost`, `draw`, `targetable`, `range`, `cooldown`, or any stat name (`max_hp`, `armor`...).

**Default scope.** Without `of`, a modifier applies relative to its **anchor**: a status's host, a relic's holder, or the card itself for a modifier written on a card.

| Channel | Applies to |
|---|---|
| `damage`, `block`, `heal`, `draw` | what the anchor's controller deals, gains, heals or draws (or what the card itself does, when anchored to a card) |
| `damage_taken`, `block_taken`, `heal_taken` | what the anchor's controller receives |
| `cost` | the card itself, when anchored to a card; otherwise all of the controller's cards |
| `range` | the card or ability itself, when written on one; otherwise everything its controller points at |
| any stat | that stat on the anchor |

**`of` scope** replaces the default: the modifier applies when the value being computed **belongs to** someone in the group. Which end of an action that is, the channel decides â and it is **the same end the bare form uses**, so `of party` is a widening of `modify damage` and never its opposite.

| Channel | `of <group>` names |
|---|---|
| `damage`, `block`, `heal`, `draw` | whoever deals, gains, heals or draws it |
| `damage_taken`, `block_taken`, `heal_taken` | whoever receives it |
| `targetable` | whoever is being aimed at |
| `cost` | whose card it is â or the card itself, so `of cards` and `of hand` both work |
| `cooldown` | whose ability it is â or the ability itself |
| `range` | whoever is reaching â or the card or ability doing the reaching |
| any stat | whoever holds the stat |

So `modify damage of party: +2` is "the party deals 2 more" and `modify damage_taken of party: +2` is "the party takes 2 more"; `modify damage of enemies: +2` is "the enemies hit 2 harder" and `modify damage_taken of enemies: +2` is "the enemies take 2 more". The channel says which end, the group says who, and the two are never the same question.

The group is read from the modifier owner's side, so `of enemies` on the player's relic always means the player's enemies, whoever is acting. A `where` on the group reads stats from the one that matched (`hp > 20`, `it.has(tag:devout)`) but tests qualifiers such as `tag:` and `source:` against the value being computed, like a `where` on the modifier.

A group that can never hold what the channel names is **CT336**, a warning: `modify damage of hand` names a pile of cards on a channel whose value belongs to an actor, so it would apply to nothing and say nothing about it.

**Filters** (`where`) see the value being computed: `tag:fire` checks the damage's tags and the card's tags, `source:self` compares the source's controller with the modifier owner's. Roles and group names such as `source:enemies` and `allies.count` are read from the modifier owner's side, whoever is acting.

**Target validity.** The `targetable` channel is asked before anything is pointed at somebody, over a base of 1: zero or less means it may not be pointed at that entity. With no scope it anchors to its owner like any other modifier, so `modify targetable: set 0` on a status hides its host. A scope is how one entity speaks for others, which is what a taunt is:

```
status "Taunt"
  stacking none
  modify targetable of allies where source:enemies, not it.has(Taunt): set 0
```

While that is attached, the other side's `target enemy` cards may only be pointed at something that also has Taunt, and so may the other side's `attack` and its enemies' moves. Write the predicate as `not it.has(Taunt)` rather than a comparison with the owner, so that two taunting entities leave each other available instead of cancelling out. `source:enemies` is what keeps a taunt from constraining its own side's cards.

Four things ask, and they ask through the one function described under [Targets](#targets).

- **A card's target.** Only the `target` words that name someone: `enemy`, `ally` and `any`. `target self` is not a choice, so nothing is asked of it. The chooser is offered only the candidates that pass, so a rule narrows what a player may pick rather than making the play fail, and a card left with nothing to point at is refused as `InvalidTarget`.
- **An ability's target.** The same, for the same reasons. An ability with nothing legal to aim at is refused and its cooldown does not start.
- **The `attack` verb.** `attack enemy` swings only at what the attacker may be pointed at. Everything named being untouchable is a rules outcome and not a mistake: the swing lands nowhere, and `into` binds 0.
- **An enemy's move.** The move keeps the target it was handed whenever that target is still legal, and otherwise goes to the first entity on that side which is — which is what a taunt is. An enemy whose every option is hidden still takes its turn, against the one it was going to hit.

Area and random effects use the selectors under [Expressions](#expressions) and are not filtered, so a blast still reaches what a card may not single out — which is the rule these games actually have. Inside a move, likewise, only the target the move is *handed* moves; a `deal 5 to all enemies` in its body reaches whoever it reaches.

Because the query carries the card being played, a `where` on the group must write `it.` to mean the candidate: a bare `tag:` there tests the card, not the entity being considered. An enemy's move carries no card, so a rule scoped with `card:` never binds one. A card that wants a rule about *itself* writes it on its own `target` line instead, where there is nothing to name: see [Targets](#targets).

**Cooldowns.** `cooldown` is a channel as well, so content can shorten what an ability waits:

```
relic "Arcane Vestments"
  modify cooldown: x0.5
```

On a relic or a status that shortens every ability its holder has; written on the ability itself it shortens only that one — the same division `cost` makes between a card and its owner's cards. A relic belongs to the leader and a `hero` is its own controller, so "your abilities recharge faster" for a whole party is the `of` form: `modify cooldown of party: x50%`. Modifiers see the cooldown already converted into clock units, so a multiplier means the same thing whether the ability was written `8s` or `2 turns`. An additive amount is therefore in clock units, ticks in real time and turns otherwise, which is why a multiplier is the spelling that travels between clocks. The result rounds up, the way a duration rounds up when it converts, and never falls below nothing. The text a card prints still shows the cooldown as written, as a printed cost does.

Stat reads are cached and the cache is invalidated by any change to the game state.

## Statements

**Commands** are a verb followed by arguments, named clauses and trailing flags:

```
deal 6 to target
apply Slow 2 for 3s to enemies
deal stacks to owner, ignore block
```

Clause keywords are `to`, `from`, `for`, `with`, `at`, `by`, `into`, `over`, `as`, `of`, `against`, `using`, `onto`. The grammar knows all thirteen and gives none of them a meaning; which verb reads which is [in the verb reference](#built-in-verbs), and a clause a built-in verb does not read is error CT323 rather than a word that quietly does nothing.

In a command, a comma introduces a flag word rather than another argument, so arguments are separated by spaces: `log "hp is" enemy.hp`, not `log "hp is", enemy.hp` (error CT0023). The built-in verbs read these flags:

| Flag | Read by |
|---|---|
| `ignore block`, `pierce`, `unblockable`, `true damage` | `deal` and `attack`: the damage skips block |
| `top` | `move`: the cards go on top of the zone |
| `weighted` | `discover`: candidates are drawn by their `weight` |

Any other word after a comma, including `silent`, `hidden`, `forced`, `optional` and `upgraded`, is accepted and ignored by the built-in verbs. It is there for verbs a game registers in C#, which can read it with `call.Flag("name")`.

**Assignments**:

```
stacks -1
energy += 1
hp = 10
target.Poison -1
event.amount = event.amount * 2
```

`name -N` and `name +N` are shorthand for `-=` and `+=` when nothing else follows on the line. A bare stat name refers to the nearest entity up the ownership chain that has the stat, else the controller: `stacks -1` in a status changes the status, `energy += 1` in a card changes the player, `block +6` in an enemy move changes the enemy. A status written after an entity, as in `target.Weak -1` or `owner.Poison += 2`, adjusts that status's counter (see [Statuses](#statuses)). Only `event.amount` can be assigned on an event.

**Locals** bind a value for the rest of the body with `let`:

```
let bonus = 2 + stacks
gain bonus energy
deal bonus to target
```

A local shadows any stat of the same name, and assigning to a name that is already a local changes the local rather than a stat. That is the difference worth having: without `let`, a scratch value had to be declared as a stat, and `scratch += 1` quietly wrote one. A local holds anything an expression yields, entities included (`let foe = enemy`), and lives as long as the body that bound it — still visible after the `if` that bound it, and gone once the effect, move or listener returns.

**Control flow**:

```
if target.dead:
  draw 2
else if target.hp < 5:
  draw 1
else:
  apply Weak 1 to target

repeat 3:
  deal 2 to random enemies

for each foe in enemies:
  deal 2 to foe

chance 30%:
  gain 1 energy
else:
  lose 1 hp
```

`repeat` binds `index` (from 0). `for each` iterates over a snapshot of the group, skipping entities removed meanwhile. `chance` rolls against a percentage.

Inside a body, labelled blocks such as `setup:` or `first:` just run their statements; the label is for readers and tools. A labelled block directly inside a declaration is different: only `effect:` and `move ...:` run, and any other label is kept but never runs, which `lint` warns about (CT313; see [Declarations](#declarations)).

## Expressions

**Literals**: numbers (`6`, `1.5`), units written against the number or after a space (`40%`, `3s`, `250ms`, `2 turns`, `3 seconds`), `x1.5` for multipliers, strings (`"text"`), and ranges (`3..6`), which roll a whole number when used as a number.

A percentage is a **fraction to multiply by**, not a number of its own: `target.max_hp * 40%` is two fifths of the target's maximum health, and `modify damage: x150%` is half again. A bare one where a built-in verb counts whole things is error **CT324**, at lint and at run: `apply Slow 40%` applied forty stacks and printed "Apply 40% Slow", so the card's own text stated a percentage nothing implements.

**Operators**, loosest first:

| Operators | Example |
|---|---|
| `or` | `a or b` |
| `and` | `a and b` |
| `not` | `not target.dead` |
| `==` `=` `!=` `<` `<=` `>` `>=` `is` `has` | `target.hp <= 5`, `target has tag:ice` |
| `per` | `2 per Poison on target` (left side times the count on the right) |
| `on`, `in` | `Poison on target` (stacks), `cards in hand` (intersection) |
| `..` | `3..6` |
| `+` `-` | `3 + damage_taken_this_turn` |
| `*` `/` `%` | `2 * player.Strength` (`%` with spaces is modulo; `x` before a number is `*`) |
| unary `-`, `+` | `-stacks` |
| selectors | `all enemies`, `random 2 enemies`, `lowest hp enemies`, `highest block allies`, `other allies` |
| postfix | `target.hp`, `t.has(tag:ice)`, `cards where cost > 1`, `enemies within 5m` |

Percent values multiply as fractions: `10 * 50%` is 5. Division by zero gives 0.

**Selectors.** `random N group` shuffles with the game's RNG and takes N. `lowest <stat> group` and `highest <stat> group` take the first by that stat, breaking ties by where an actor stands — lane, then rank — and then by id; with a single name (`lowest enemies`) the stat is `hp`. `other group` leaves out the running entity itself, and also the target when the target is in the group, or else the running entity's controller. In a modifier's `of` scope the running entity is the one the modifier is written on, so `modify attack of other allies where it.has(tag:goblin): +1` on a goblin leader buffs every other goblin but not the leader. A prefix word followed by nothing selectable (as in `pattern random`) is an ordinary name. `within` with a plain number counts slots on the [board](#boards); with a unit (`5m`, `250px`) it is passed to the host's `TryCall`, and without a host that implements it, that is a runtime error. CT330 says which of the two a line is asking for.

**Functions**: `min(a, b, ...)`, `max(...)`, `abs(n)`, `floor(n)`, `ceil(n)`, `round(n)`, `clamp(n, lo, hi)`, `count(group)`, `random(lo, hi)`, `adjacent(who)`, `within(who, n)`, `lane(who)`, `rank(who)`, `distance(a, b)`, `has(who, predicate)`, `stacks(Status[, who])`. Methods: `who.has(predicate)`, `who.stacks(Status)`. The five board ones are described under [Boards](#boards).

`has` is true when the entity has the tag (directly or on an attached status), has a status of that name, or is that entity.

**Members of an entity**: `dead`, `alive`, `removed`, `name`, `id`, `owner`, `source`, `controller`, `team`, `zone`, `lane`, `rank`, `position`, `intent`, `phase`, `statuses`, `kind`, a status name (its counter), a history name (`damage_taken_this_turn`), or any stat. Stats that nothing has set read as 0. `lane` and `rank` are where the entity stands on the [board](#boards), both counting from 0, so the front two ranks are `rank <= 1`; `position` is the older name for `rank` and reads the same number. `intent` and `phase` are text, or `none`; compare them with a quoted string, as in `enemy.intent == "Chomp"`, or with `none` unquoted. `enemy.phase == "none"` is never true.

**Members of a group**: `count`, `size`, `length`, `first`, `last`, `empty`, `any`, `lane` and `rank` — where the first of them stands, so a group of one answers like the one it holds — or a stat, which sums it across the group (`enemies.hp`).

**Members of `event`**: `source`, `target`, `card`, `amount`, `name`, `cancelled`, and anything in its data. Using `event` outside a listener is an error.

### Qualifiers

`tag:x`, `keyword:x`, `status:x`, `source:role`, `target:role`, `name:x`, `card:x`, `zone:x`, `team:x`, `kind:x`, `type:x`, `rarity:x`, `id:x`. They are written with no spaces around the colon. The value is one word of letters, digits and `_`, or, for a name that is not one word, text in quotes: `card:"Fire Bolt"`, `hand where name:"Strike+"`.

A qualifier tests whatever is in focus: the candidate inside `where`, the value being computed inside a modifier, or the event inside a listener filter. Roles for `source:` and `target:` are `self`, `owner`, `player`, `enemy`/`enemies`, `ally`/`allies`, `target`, `any`, or an entity name. Roles compare controllers, so `source:self` on a relic matches damage from its holder's cards. `enemy` and `ally` roles are relative to the side the effect runs for; inside a modifier, that is the modifier owner's side.

`name:` and `card:` compare names, and there is no `card:self`: a card that needs a rule about itself names itself, as in `card:Pike`.

## Names

A bare name resolves in this order: local variables (`let` bindings, `for each` variables, verb parameters, `chosen`, `created`, `index`, `x`); the built-in names below; history counters; names the host supplies; inside `where`, the candidate's stat; the running entity's stat; a definition (statuses and keywords first, so `apply Burn` and `has(Burn)` find the status when a card has the same name); event data; the controller's stat; a stat declared anywhere in content, which reads 0. Anything else is a runtime error with a suggestion.

| Name | Meaning |
|---|---|
| `self` | the running entity |
| `owner` | the running entity's owner (a status's host), or itself |
| `source` | who is acting |
| `target` | the target |
| `card` | the card being played |
| `player`, `leader`, `controller` | the party's leader (two names for one entity); the running entity's controller |
| `party` | the living [party members](#the-party), in the order they take their steps |
| `enemy`, `enemies`, `allies`, `everyone` | relative to the side the effect runs for |
| `hand`, `draw`, `discard`, `exhaust`, `powers`, `relics`, `deck`, `cards` | the controller's zones (`deck` is draw, hand and discard) |
| `statuses` | statuses on the candidate or controller |
| `stacks` | inside a status, its stacks |
| `turn`, `now` | the turn number, the clock time |
| `true`, `false`, `none` | literals |

**History counters** are read as `<key>_this_turn` or `<key>_this_battle`, for the controller or as a member of any entity: `damage_taken`, `damage_dealt`, `healed`, `kills`, `cards_played`, `attacks`, `cards_drawn`, `cards_discarded`, `cards_exhausted`.

## Built-in verbs

| Verb | Usage |
|---|---|
| `deal` (`damage`) | `deal N [to who] [as tag] [, ignore block]`. Without `to`, hits the effect's target. The damage carries the tags of the running card, status or relic. |
| `attack` | `attack [who] [with attacker] [as tag] [, ignore block]`. Deals the attacker's `attack` stat to the target, with the attacker as the damage source, so the attacker's own listeners and modifiers see it as its damage. The attacker defaults to the running entity when that is an actor, otherwise to its controller, so a card or relic swings with the player; the target defaults to the effect's target. |
| `block` (`gain_block`) | `block N [to who] [into name]`. Defaults to yourself. Block is not timed: it lasts until it is spent or the holder's turn starts, so there is no `for` on it. |
| `heal` | `heal N [to who] [into name]`. Defaults to yourself. Refuses a dead target; see `revive`. |
| `revive` | `revive who [N]`. Brings a fallen actor back at N hp (1 by default) and raises `revived`. Does nothing to somebody living, so it cannot become a heal. |
| `grant` | `grant Ability[, Ability] [to who]`. Gives an actor an ability. Defaults to whoever is running the line. Granting one somebody already has does nothing. |
| `draw` | `draw [N] [to who]` for the controller, or for whoever `to` names. Reshuffles the discard pile when the draw pile runs out. A creature controls itself, so `draw 1 to player` is how a creature draws for you. |
| `discard`, `exhaust` | `discard N` asks the chooser to pick from hand; `discard who` and `exhaust self` name the cards. |
| `apply` | `apply Status [N] [for duration] [to who]`. Defaults to the target, or a status's host, or the controller. |
| `add` | `add tag:x [to who]` adds a tag; `add Status N` is `apply`. |
| `remove` | `remove Status [from who]`, `remove tag:x from who` (every status with the tag, and the tag), `remove who` (destroys it). |
| `gain`, `lose` | `gain N Status` adjusts a status; `gain N stat` changes a stat. `[to who]`, else yourself. |
| `change` | `change stat by N [to who]`, or `change hp -5 on target`. |
| `create` | `create Card [N] [into zone] [to who]` (default hand, and whoever is running the line), `create Relic`, `create Enemy`. Makes a fresh one from the definition, so it arrives with its printed stats. Binds `created`. A status, keyword or ability is refused (CT320): those belong to whoever has them, and `apply` is the verb that gives one. |
| `copy` | `copy [who] [N] [into zone] [to who]` duplicates something that is **in the game**, as it stands now — an upgraded card, a wounded minion, a discounted power. Defaults to itself. Binds `copied`, always a list. See below. |
| `shuffle` | `shuffle` (discard into draw), `shuffle Card [N] [into zone]` (creates copies), `shuffle cards into zone`. `into` says which pile the cards land in and which pile is then shuffled; without it, the draw pile. |
| `move` | `move cards to zone [, top]` |
| `transform` | `transform [who] into Definition` replaces what something is while it keeps its place, its id and everything holding it. Defaults to the effect's target. Binds nothing. See below. |
| `destroy` | `destroy [who]`. Defaults to itself. |
| `kill` | `kill [who]`. Defaults to the target. |
| `choose` | `choose N from group [as name]`. Binds the result to `chosen`, or to the name after `as`: one entity, or a group when more than one is chosen. Either answers `.first`, `.last` and `.count`, so a line written for one works for the other. |
| `discover` | `discover N <kind> [where filter] [, weighted] [as name]`. Offers N pieces of content and binds the one chosen to `discovered`, or to the name after `as`. See below. |
| `emit` | `emit event [amount] [to who]` raises a **custom** event. A built-in event name is refused (CT322): the engine raises those itself, and emitting one would run its listeners while the history counters stayed where they were. |
| `cancel` | In a `before_` or `instead_of_` listener, cancels the event. |
| `play` | `play card [on target] [, free]` plays a card out of a pile its controller owns, with its cost, its `card_played` and its triggers. Binds `played` to the card, or to `none` when it was not played. See below. |
| `replay` | `replay card [on target]` resolves a card's effect again, for free. The card stays where it is, nothing is paid, and no `card_played` is raised: it is not a play. The card must be one that is in the game: `replay Strike` is refused (CT320). |
| `use` | In an enemy, performs one of its moves. |
| `log` | `log "hp is" target.hp`, with the values separated by spaces, writes them to the runtime's `Logged` event. `cantrip repl` prints it. In a test it goes into the trace, as a `[log]` line under the `log` statement, which `cantrip test --trace` prints for a failing test. A passing test shows nothing, so to see a value there, use `expect`, whose failure shows it. |

**Which clauses a verb reads.** A clause a verb does not read is error **CT323** — at lint and at run — because a dropped clause is a card that reads as one thing and does another. `block 8 for 2 turns` used to give ordinary block that vanished at the next turn start, and `deal 5 against enemy2` hit whatever the card was aimed at.

| Verb | Clauses it reads |
|---|---|
| `deal`, `damage` | `to`, `as`, `into` |
| `attack` | `to`, `with`, `as`, `into` |
| `heal` | `to`, `into` |
| `block`, `gain_block` | `to`, `into` |
| `draw` | `to` |
| `apply` | `to`, `for` |
| `add` | `to` |
| `remove` | `from` |
| `gain`, `lose` | `to` |
| `change` | `to`, `of`, `by` |
| `create`, `copy` | `into`, `to`, `onto` |
| `move` | `to`, `into`, `onto` |
| `shuffle` | `into`, `to` |
| `transform` | `into`, `to` |
| `choose` | `from`, `as` |
| `discover` | `as` |
| `emit` | `to` |
| `kill` | `to` |
| `destroy`, `discard`, `exhaust`, `cancel`, `log`, `play`, `replay`, `use` | none |

The grammar knows thirteen clause words, four of which — `at`, `over`, `against` and `using` — no built-in verb reads. They stay reserved so that a verb a game registers in C# can read them, and so that a built-in verb handed one can name the word that works: `apply Poison 3 at target` says to write `to`. Three spellings of one clause would be language to learn and content to keep consistent, for nothing, so they are not synonyms.

**A flag after a comma is not a clause** and is never checked: `, ignore block`, `, free`, `, top`, `, weighted`, and whatever a game's own verb reads. Only the verbs above are held to the table — a verb content declares, or one a game registers, reads what it likes.

**`into`** binds what a verb actually achieved, so an effect can act on it: `deal 4 to all enemies into dealt`, then `heal dealt`. The number is what landed, summed across the targets — after modifiers changed the amount, after block absorbed what it could, and counting only what a dying target could still take, which is rarely the number the line asked for. Available on `deal`, `damage`, `attack`, `heal` and `block`: a heal stops at full health and block goes through its own modifiers, so those two are worth asking about for the same reason. It binds a **new name**, so do not reuse the name of a stat: `heal 5 into hp` binds a local called `hp` that shadows the stat for the rest of the effect. On `create`, `copy`, `move` and `shuffle`, `into` names a **zone** instead.

**`into` is the pile and `to` is whose.** `create Brine into discard to leader` makes the curse in the *leader's* discard pile, whoever ran the line â which is how an enemy puts a Wound in the player's deck, and it is the same `to` every other verb uses for the one on the receiving end. Without `to`, whoever is running the line keeps what it makes, so an enemy's `create Brine into discard` fills its own pile. `to` naming a pile (`create Shiv 2 to hand`) is refused and says to write `into`; `onto` is a second spelling of `into`. The same division holds for `copy`.

**`copy`** is the other half of `create`. `create Strike` makes a Strike as it is printed; `copy picked` makes one as it *is* — with the buff it was given this battle, the cost it was discounted to, the wound it is carrying. That is the only difference between the two verbs, and it is the whole point of this one:

```
card "Dual Wield"
  cost 1
  effect:
    choose 1 from hand where tag:attack or tag:power as picked
    copy picked into hand
```

Two rules decide what a copy is:

- **Its side and owner come from the original** unless `to` says otherwise, never from whoever is copying. A player's card that copies an enemy's minion gives the *enemy* a second minion. Without this, an `actor` declaration summoned by an enemy would change sides the moment anything copied it.
- **It is placed by kind, where a new one would go**: an actor on the board in the next free slot, a relic or item into `relics`, anything else into hand, and `into <zone>` overrides. A copy never inherits the original's zone — copying an active power would otherwise put a second live power into `powers` that nobody played, and copying an exhausted card would put it where nothing can reach it.

Everything the original has now comes across: its live stats, its runtime tags, and a new instance of every status and keyword on it with the same `stacks`, `duration` and `expires_at`. Those arrive silently — no `status_applied` is raised and `immune` is never asked — because a copy is a snapshot of a state, not a new application.

Nothing is restored. A wounded minion is copied wounded; content that wants a fresh one says so in a line of its own, which is also why there is no flag for it:

```
    copy target
    copied.hp = copied.max_hp
```

A copy raises `created` through all three phases, with `event.copy_of` set to the original. That makes the original *involved* in the event, so `on created(self)` on the original hears its own copying. The copy is a new combatant otherwise: its `once per battle` and `once per turn` windows are unused, and an actor rolls a fresh intent.

`copy` is refused, with an error that names the verb to use instead, for a definition (`copy Strike` → `create Strike`, also CT320 at lint), for a status, keyword or ability (`apply` gives another one), and for the player, which nothing in content describes. A group that happens to be empty copies nothing and binds an empty `copied`, the way `for each` over an empty group does nothing. An actor that has died is skipped in the same way, even when you name it yourself: `kill` leaves its hp as it was, so a copy would arrive alive and whole, and `on killed: copy event.target` would never run out of enemies.

**`transform`** replaces what something *is* while keeping who it is. `to` is a synonym for `into`.

```
card "Polymorph"
  cost 4
  target enemy
  effect:
    transform target into Sheepling
```

**What survives** is the point: the `Id`, `Owner`, `Source`, `Team`, `Sequence`, `Zone` **and the index within that zone**, the board `Position`, and the entity's history counters. Everything holding it — `event.target`, a `let`, a scheduled binding, a test's `enemy2`, a C# reference a game has cached — still holds the same object, now wearing the new name.

**What goes** is everything the old definition brought, and everything the game gave it while it was that thing: its stats, its runtime tags, every status and keyword on it, its spent `once per ...` windows, its intent, its phase, and its own `next turn:` plans. The new definition's stats and tags take their place, so the new thing arrives whole. Content that wants a wound carried says so on the card, which also generalises to any other stat:

```
    let wounds = target.max_hp - target.hp
    transform target into Beast
    target.hp = target.max_hp - wounds
```

**Statuses go silently.** Nothing raises `status_removed` for them: one verb raising a variable number of cancellable events, each able to destroy the host half way through, is not something content could reason about. `destroy` already sheds its attachments the same way. The one event is `transformed`, raised once through all three phases, carrying the tags the entity had **before**, and `event.was` and `event.into`, the two definitions. It is **not** a `created`, a `died`, a `killed` or a `destroyed`: a Polymorph never fires a death rattle and never fires a Knife Juggler.

An actor rolls a fresh intent at once, so the player is never shown a move the thing no longer has. An `enemy` may become an `actor` declaration and back — both are actors, and the side is kept, so one `actor "Sheep"` serves both sides where `create` would need two declarations.

Transforming something into what it already is is not a no-op but a **reset**: printed stats back, statuses gone, limits fresh.

`transform` is refused, by name, for a definition where the thing to change is meant (`transform Strike into Wound` reads as if it changed every Strike and would otherwise do nothing at all; CT320 at lint), for an entity in the `into` clause (`transform a into b` would otherwise give `b`'s *printed* stats, and mean two different things depending on whether a word happens to be a local), for a definition of a different kind, for the player, and **inside an `until` block** — `until` puts back what it did, and none of this can be put back (CT321 at lint, a runtime error at run, which is what catches a content verb holding one). Nothing remembers the old form, so content that wants a round trip stores `event.was`; a timed form change is a status with modifiers and a duration.

**`play`** is "play the top card of your draw pile", and everything shaped like it. The card leaves its pile, pays, raises `card_played` with its tags, counts towards `cards_played` and `attacks`, runs its effect, and is filed afterwards by the tags it has *then* — exhaust pile, `powers`, or discard pile. It never passes through hand, so nothing is `drawn` and the hand limit is not involved.

```
card "Havoc"
  cost 1
  tags skill, exhaust
  effect:
    play draw.first, free
    if played == none:
      discard draw.first
    else:
      exhaust played
```

`, free` plays it without paying. It changes what is **paid**, never what the card **sees**: a `cost 2` card played free still has `cost 2`, and an X-cost card played free binds `x` to what the payer has and spends nothing. The `card_played` event's `amount` is what was actually paid, which is 0 for a free play, so "gain 1 hp per energy spent" stays honest. Paid is the default, because the free version already exists — `replay` — and a `play` that were always free would leave "play it and make them pay for it" unwritable.

**A refusal the rules allow is not an error.** An empty pile, a Curse, a cost the payer cannot afford, no legal target, a card already in `play` or `powers`: nothing happens, `played` is `none`, and the trace says so. That is what lets "play the top card of your draw pile" be written once and not crash the first time the top card is a Curse. `played` is bound before anything else, so the name always exists.

**Targeting never prompts.** `on <target>` if written, exactly as written; otherwise the effect's own target, but only where the card could legally be pointed at it — a `play` inside a listener inherits whatever that event happened to be about, which is usually the actor whose turn started or the card that was drawn, and a hint the card cannot take is dropped rather than allowed to refuse the play; otherwise, for a `target enemy` or `target ally` card, one **at random** from the legal targets, rolled from the game's own RNG — so the roll replays and saves like any other, and one candidate costs no roll. The roll picks from [the same legal targets](#targets) a player would be offered, so a Taunt, or the card's own `target … where`, binds an automatic play exactly as it binds a hand-played one. A `target self` card is aimed at the controller, and a card with no legal target is not played.

A card played by another card's effect **extends that effect's chain**, so a `once per chain` limit counts one chain across the whole cascade rather than restarting at every play. A card that plays a card that plays a card is fine; direct recursion stops at `max_call_depth` (64) with an error that says what happened. A nested play does not drain the outer effect's queued triggers — they resolve after the outer effect, as they would without it.

`play` is a test verb as well as a rule verb, and which one a line means is decided by **where it is written**: a `play` in a test's own body is the test's, and a `play` anywhere else — in a card's effect, or in a content verb a test calls — is the rules'. Inside a `scenario` it is still CT301: a scenario states the fight, and a bot plays it.

**`discover`** picks from content itself rather than from what is on the board, which is what "a random spell" or "one of these afflictions" needs:

```
card "Discovery"
  cost 1
  effect:
    discover 3 cards where tag:spell as found
    create found into hand

card "Breaking Point"
  cost 0
  target ally
  effect:
    discover 1 statuses where tag:affliction or tag:virtue, weighted as mood
    apply mood 1 to target
```

The kind is one of `cards`, `statuses`, `relics`, `items`, `keywords`, `abilities`, `enemies` or `actors` (singular reads the same). It is taken as the word that was written rather than evaluated, so it names a kind of content here and cannot be confused with the live group the same word means everywhere else.

What is bound is a definition, not an entity, and `create`, `apply`, `shuffle` and the rest already accept one — nothing exists until a verb makes it, so the candidates nobody chose raise no `created` event and leave no trace.

**Offering one candidate is a random pick with no decision in it**, so the chooser is never asked and no prompt appears: `discover 1` is how content rolls on a table, and `discover 3` is how it asks. With `, weighted`, each candidate's `weight` property decides its share (default 1, and a weight of 0 is never drawn) — the same property an enemy's moves are weighted by, so "rarely a virtue" is a 1 against three 5s. Without it every candidate is equally likely.

Filters see a definition, so they test what is printed on it rather than anything about being in play: `tag:`, `kind:`, `name:`, `rarity:`, and properties through `it` (`it.cost <= 2`) or bare (`cost <= 2`). A qualifier that only makes sense for something in play, such as `zone:`, is an error rather than a quiet no-match — as is discovering from a filter nothing matches.

The pool is every definition loaded, not only those in the card's own file. When several sets of content are loaded together, give a pool a tag of its own, as in `where tag:arcane`, so that it offers what was meant.

Picks come from the game's own seeded RNG and cost one roll per candidate taken, never a shuffle of everything loaded, so the same seed discovers the same content on a replay and adding content to a pool does not disturb any other roll. Candidates are ordered by kind and name before anything is drawn, never by the order content happened to load in.

Games add verbs with `runtime.RegisterVerb`.

## Content-defined verbs

```
verb shatter(t):
  if t.has(tag:ice):
    remove tag:ice from t
    deal 10 to t
```

Call a content verb like a built-in one: `shatter target`. Arguments bind the parameters in order; a `to` clause fills the next missing parameter. A content verb with the same name as a built-in verb replaces it. Calls may nest 64 deep (`max_call_depth`).

## Scheduling

```
card "Flex"
  cost 0
  effect:
    until turn_end:
      gain 2 Strength

card "Prepare"
  cost 1
  effect:
    next turn:
      draw 2

card "Fuse"
  cost 0
  effect:
    in 2 turns:
      deal 10 to all enemies
```

- `next turn:` runs at the start of the controller's next turn, after its turn-start event.
- `in N turns:` (or `in 3s:` on a tick clock) runs when the clock reaches that time. The turn clock advances once per round, after the player's turn has started, so the block runs after turn-start resets. Under [`turns: initiative`](#the-turn) the clock moves at the start of the round instead, before anybody's step, so the block runs *before* them; hang work that has to survive a reset on `next turn:`.
- `until <event>:` runs now and undoes its changes when `<event>` next happens to its owner, after that event's listeners. It undoes statuses and stacks it applied (only those stacks), tags it added and changes to non-resource stats. Damage and other resource changes are not refunded. The end of a battle undoes every pending `until`.

## Rulesets

```
ruleset
  clock turns
  events: before, instead, after
  loops: once_per_chain, max_depth 50
  ordering: priority, play_order, active_player
  modifier_layers: add, multiply, clamp, override
  triggers: queued
  turns: sides
  order: position
  hand_size 5
```

| Setting | Default | Meaning |
|---|---|---|
| `clock` | unstated | `turns` or `ticks`: which clock the content is written for. See below. |
| `events` | all three | which phases are dispatched |
| `loops` | `once_per_chain, max_depth 50` | `once_per_chain` stops self-retriggering; without it only the depth cap applies |
| `ordering` | `priority, play_order, active_player` | listener order; anything left out keeps its default position |
| `modifier_layers` | `add, multiply, clamp, override` | modifier layer order |
| `triggers` | `queued` | `immediate` runs after listeners inline |
| `new_listeners` | `hear_the_event` | whether a listener that comes into play during an event hears that event. See below. |
| `turns` | `sides` | `sides` or `initiative`: whether [the party](#the-party) acts as a side or every combatant takes its own step in one interleaved order. |
| `order` | `position` | `position` or `speed`: the order combatants are offered in — where they stand, `(lane, rank)`, or by the `speed` stat descending with ties broken by where they stand. |
| `hand_size` | 5 | cards drawn each turn |
| `max_hand_size` | 10 | cards drawn beyond this go to the discard pile |
| `max_steps` | 100000 | interpreter steps per top-level action before it is stopped with a runtime error (see [When content fails at runtime](csharp.md#when-content-fails-at-runtime)) |
| `max_call_depth` | 64 | content verb nesting |

If several rulesets are loaded, the last one loaded wins (CT0112).

**`clock`** says which clock the content is written for, because the two sets of units do not convert into each other: a turn clock measures `2 turns`, a tick clock measures `3s`, `250ms` and ticks. Content that says nothing is unchanged — nothing is checked, and a runtime given no clock of its own still uses turns.

Content that does say gets two things. A length in the wrong units is error **CT325** at lint: a cooldown, a `for`, an `in N ...:` delay or an `on every ...:` interval. And a runtime built with the other clock throws as it is built, rather than later. Both halves used to fail differently and neither said so: `on every 1s:` in a turn game registered nothing at all, in silence, while `apply Weak 1 for 3s` in the same game threw only when the line ran.

```
ruleset
  clock ticks
```

A runtime with no `RuntimeOptions.Clock` of its own then starts a `TickClock`, so the content runs on the clock it asked for.

**`new_listeners`** says whether a listener that comes into play *during* an event hears that event. The default, `hear_the_event`, is what it has always done: a minion summoned by "whenever you summon a minion" hears its own summoning, so a listener of that shape has to leave its own cause out —

```
actor "Knife Juggler"
  on created(kind:actor, not target:self):
    deal 1 to random enemies
```

— and the sample roguelite's Chill is written against exactly that. `miss_the_event` is the other answer: what was already here hears the event, and what arrived during it starts listening from the next one. The filter above then does nothing and can be dropped, and "when this enters play it also triggers on what put it there" becomes unwritable. Both are real rules in real games, which is why this is a setting.

```
ruleset
  new_listeners: miss_the_event
```

Only a listener's own arrival counts, by registration order: a status applied during an event, a minion created by it, a relic obtained in it. Nothing else changes — filters, `once per ...`, ordering and loop protection are the same either way.

## How a battle runs

1. **StartBattle** removes enemies that died in an earlier battle, shuffles the draw pile, rolls enemy intents, raises `battle_start` and starts the player's turn.
2. **A turn starts**: the turn number increases; each actor on the side raises `turn_start`, which resets resources such as energy and block; pending `next turn:` blocks run; the turn clock advances; each [party member](#the-party) draws `hand_size` cards from its own pile.
3. **The party plays cards** with `Play`, and uses abilities with `UseAbility`. Queued triggers resolve after each. A member that is done passes; when the last one has, the turn ends.
4. **EndTurn**: the party raises `turn_end` (decay and `until` reverts follow its listeners); every member's hand is discarded except retained cards, and ethereal cards are exhausted; the enemies' turn starts; each living enemy uses the move it telegraphed, against the member it telegraphed it at; enemies raise `turn_end`; intents are rolled; the party's next turn starts.
5. **The battle ends** when no party member is alive (lost) or no enemies are left alive (won). `battle_end` is raised, `until` blocks are undone, every member's statuses are removed unless flagged `persistent`, and every card returns to its owner's draw pile.

Dead enemies stay on the `dead` zone until the next battle starts.

## Descriptions

Every definition gets rules text at one of three levels:

1. **Automatic**, generated from its effects, listeners, modifiers, moves, cooldown, decay and card keywords.
2. **Custom**: `text: "..."` with `{placeholders}` linked to the effect.
3. **Override**: `text_override: "..."`, shown verbatim.

```
card "Firebolt"
  cost 1
  target enemy
  tags attack, fire
  effect:
    deal 7 to target
  text: "Scorch an enemy for {damage} damage."
  flavour: "Mind the eyebrows."
```

Placeholders are named after the values in the effect, in order: `{damage}`, `{damage2}`, `{block}`, `{heal}`, `{draw}`, `{Poison}` (the amount applied or gained), `{energy}` (an amount gained or lost), `{bonus}` (a modifier amount), `{cost}`, `{stacks}`, and any numeric property. Described against a live game, values go through the modifiers that would apply now, so a UI can show "~~7~~ 10".

A computed amount, such as `deal 2 * target.Poison to target`, has no number until there is a game to read. Described against a live game with a target, it shows the number; described on its own, as `cantrip describe` and a deck list do, both the automatic text and its placeholder show the expression as written ("Deal 2 * target.Poison damage."). Ranges and `random` never show a rolled number: `deal 3..6` reads "Deal 3–6 damage." For such cards, write text that needs no number, such as `text_override: "Deal damage equal to twice the target's Poison."`.

Descriptions show what was parsed, not what will run: a mistyped listener line that never fires (see [Declarations](#declarations)) is still described, though `lint` warns about it (CT313). Check behaviour with a [test](#tests).

**Drift protection.** `cantrip lint` reports a placeholder that matches nothing (CT401). Add `text_checked "<hash>"` once a text has been reviewed; when the effect later changes, lint reports CT402 with the new hash to paste after re-reading the text. Presentation properties do not affect the hash.

**Other languages.** The automatic text is English. A game translates its text in C#, with an `IDescriptionLocalizer` that can replace each definition's name, its `text` or `text_override`, its flavour, and the phrases the automatic text is built from. A translated `text` uses the same `{placeholders}`, so its numbers stay live. See [Rules text with live values](csharp.md#rules-text-with-live-values).

## Tests

```
test "Poison ticks and decays"
  enemy hp 20
  apply Poison 3 to enemy
  end turn
  expect enemy.hp == 17
  expect enemy.Poison == 2
```

`dotnet cantrip test <folder>` runs every test in the content (from a clone of the repository, `dotnet run --project src/Cantrip.Cli -- test <folder>`). Each test gets a fresh game with seed 1 and a player with 80 hp and 3 energy. Setup statements run first; the battle starts, without shuffling or drawing a hand, at the first statement that is not setup. Any DSL statement works in a test, plus these verbs:

| Verb | Setup | Meaning |
|---|---|---|
| `enemy [Name] [stat N]...` | yes | Spawns an enemy: a defined one, or a plain 10 hp enemy (`enemy "Label" hp 20`). A status name applies that status, as `apply Name N` would. The first is `enemy` and `enemy1`, then `enemy2`... |
| `player stat N...` | yes | Sets the leader's stats or applies statuses: `player hp 40 Strength 2` |
| `hero Name [stat N]...` | yes | Adds a [party member](#the-party) from a `hero` declaration, with its abilities: `hero Vestal hp 20`. The first is `hero` and `hero1`, then `hero2`… and each is also bound to **its own name**, so `expect Vestal.hp == 20` reads the live member rather than the printed stat on its definition |
| `hand`, `deck`, `discard_pile` | yes | Adds cards to the hand, the draw pile or the discard pile, in that order: `deck Strike, Strike, Defend`, or `deck 4 Strike, 2 Defend`. As a test verb, `deck` is the draw pile only; the name `deck` in an expression is draw, hand and discard together. |
| `relic Name` | yes | Gives the player a relic or an item |
| `grant Ability` | yes | Gives an ability to whoever is running the line — the leader, in a test's setup. It is a [rule verb](#built-in-verbs), not a test verb, so `grant Smite to target` works in content too |
| `seed N` | yes | Reseeds the game's RNG |
| `answer "A, B"` | yes | Queues the answer to the next choice, by name; `"A, B"` picks both |
| `realtime N` | yes | Uses a tick clock with N ticks per second for the whole test, wherever it is written |
| `setup:` | yes | A block of setup statements |
| `play Card [by who] [on who]` | no | Plays a card, adding it to the hand if needed; fails the test if it cannot be played. `by` names the member performing it, and may be written either side of `on`. See the note below: `play` is a rule verb too |
| `end turn` | no | Ends the turn, runs the enemies' turn and starts the next one |
| `tick N` | no | Advances the tick clock |
| `cast Ability [by who] [on who]` | no | Uses an ability. `by` names the member whose ability it is |
| `pass who` | no | That member is done this turn. When the last one has passed, the turn ends |
| `expect condition` | no | Fails the test if the condition is false |

**`play` means the test's verb only where the test wrote it.** It is also a [rule verb](#built-in-verbs), and the two are told apart by where the line is: a `play` in a test's own body puts a card into hand by name and plays it from there, and a `play` written anywhere else — in a card's effect, or in a content verb the test calls — plays a card that is already in a pile. So `verb cascade(): play draw.first` does the same thing whether a card calls it or a test line does.

**Setup runs before the battle starts, and the start of the first turn resets resources.** `player block 5` is wiped to 0, and `player energy 1` comes back as 3, the player's `max_energy`. A higher energy survives, because setting it also raises `max_energy`. To start a test with block or less energy, write a statement after setup: `block 5`, `player.energy = 1`. The same goes for any stat with a `reset_on turn_start` rule.

**One comparison per `expect`.** A failing comparison shows the value it found: `expected enemy.Poison == 3, but enemy.Poison was 2`. Joined with `and`, it shows only the condition, and a test stops at its first failing `expect`, so the lines after it are not checked. To look further into a failure, `--filter <text>` runs only the tests whose names contain the text, and `--trace` prints what happened, step by step, in each failing test, with what any `log` statement wrote.

**Setup does not check stat names.** In an `enemy` or `player` line, a word that is not a status becomes a stat, so `enemy hp 20 Weak 2` with no `Weak` defined sets a stat called `Weak` and the test carries on. `cantrip lint` says nothing about the setup line itself; it reports CT302 only where the test reads the name as a status, as in `expect enemy.Weak == 2`.

**Names of definitions are checked.** A line that names a card, relic, item, ability or enemy that is not defined, such as `hand Strik`, `relic Anchr`, `grant Zapp`, `play Strik on enemy` or `enemy Ghol hp 12`, fails the test with a message that names the nearest definition, and `cantrip lint` reports it as error CT302.

**A count before a name repeats it.** `deck 4 Strike, 2 Defend` puts six cards in the draw pile, and the same reading applies to `hand`, `discard_pile`, `relic`, `grant` and `answer`. The count is a whole number from 1 to 1000; anything else, or a count with no name after it, fails the test.

**Choices** are answered from the `answer` queue. With nothing queued, the first option is taken, which is enough for `discard 1` when the candidates are identical or there is only one.

**What a test cannot do:**

- span two battles, so it cannot show that something resets between battles;
- check that a play was refused, since `play` fails the test when a card cannot be played. For a targeting rule, show it the other way round: play the card with no target and check what it picked;
- show `log` output when it passes. Only `--trace` shows it, and only for a failing test;
- in the `cantrip` tool, use a verb, name or function that your game supplies in C#. Run such a test from the game's own test suite, where `DslTestRunner` can register them: see [Verbs written in C#](csharp.md#verbs-written-in-c).

**When the last enemy dies, the battle is won at once**, and winning removes the player's statuses that are not `persistent` and returns every card to the draw pile. A test that checks a status or a drawn card after a killing blow needs a second enemy to keep the battle going.

To try statements one at a time, `dotnet cantrip repl <folder>` loads the content and runs each line you type as the player, with a 100 hp enemy called Dummy as the target, and prints the state after each. It prints `log` output, but has none of the test verbs above — except that the rules' `play` is a verb of its own, so `play hand.first on target` works there. [Trying lines in the REPL](writing-content.md#6-trying-lines-in-the-repl) shows a session and its limits.

## Scenarios

```
scenario "The tower, starter deck"
  runs 500

  player hp 60 energy 3
  deck 4 Zap, 4 Ward, Kindle, Rime

  battle "Cinder Imp"
  battle "Frost Wisp", "Cinder Imp"
  heal 12                       # the rest between fights, stated rather than chosen
  battle "Tower Guard", "Frost Wisp"
  battle Archmage

  expect no stalls
  expect no errors
```

A test plays one fight the way you tell it to. A scenario states a whole run — a deck, some fights in order, and whatever happens between them — and `dotnet cantrip sim <folder>` plays it many times with two bots.

**Nothing above the fights you name is simulated.** There is no map, no reward offer, no shop and no gold. A rest, a relic picked up or a curse taken is written as a statement where it happens, so a scenario measures only what it says.

The body is statements, as a test's is, and most of it is ordinary DSL: `heal 12`, `apply Curse 1 to player`, `relic "Ember Charm"`, `grant Cleave`. Three lines are the scenario's own:

| Verb | Meaning |
|---|---|
| `battle Name[, Name...]` | Spawns those enemies and lets the bot fight them to the end. Each name must be an `enemy` declaration, and a count before one repeats it: `battle 2 "Cinder Imp"`. |
| `runs N` | How many times to play the scenario |
| `expect <measurement> <op> <number>` | Fails the scenario if it does not hold. `expect no stalls` is the same as `expect stalls == 0`. |

Setup is shared with a test, and each verb means the same thing in both: `player`, `hero`, `deck`, `hand`, `discard_pile`, `relic`, `grant`, `seed` and `answer`. See [Tests](#tests) for what each one does. A `setup:` block groups them here too, though nothing turns on where setup ends: a scenario starts a battle where it says `battle`, and nowhere else.

**A scenario never plays a card itself.** `play`, `cast`, `end turn` and `tick` belong to a test: a bot plays a scenario, and a line choosing a card by hand would fight it. `realtime` is out for the same reason — when to act in continuous time is the game's own frame loop, not a bot's.

**`expect` measures every run, not one game.** A scenario plays hundreds of games, so there is no single `enemy.hp` to compare. What it can measure:

| Measurement | Meaning |
|---|---|
| `stalls` | Battles that reached the turn limit without ending |
| `errors` | Runs that threw |
| `wins` | The share of runs won |
| `hp_left` | The player's hp at the end |
| `turns` | Turns per battle |

Anything else is error CT319.

**A win rate is a fact about the bot, not about your content.** Two bots given the same content reach two numbers: on `samples/slice`, over the same 500 seeds, the cautious bot finished 70.8% of the runs and the patient bot 74.0%. So `wins`, `hp_left` and `turns` are reported unchecked, with each bot's number quoted beside the other's; `stalls` and `errors` are checked, because they hold whoever plays. [Simulating](simulating.md#the-bots) has the bots and what each is for.

`cantrip validate` counts the scenarios in a folder, and `cantrip lint` checks them as it checks anything else: a misspelt card, relic, ability or enemy is error CT302, the same code as in a test, so a scenario that names nothing real fails in milliseconds rather than after a hundred runs.

[Simulating](simulating.md) has the command, its options and exit codes, what the report says and what it refuses to say.

## Determinism

Within one version of Cantrip, the same content, seed and inputs produce the same game:

- All arithmetic uses a fixed-point number with six decimal places; no floating point.
- Randomness comes from a seeded xoshiro256** generator, never `System.Random`.
- Listener order, selector ties and resource resets use explicit orderings, never hash order.
- `runtime.State.ComputeHash()` fingerprints the rules state, including scheduled work and history. Comparing hashes checks that a replay, or a second machine playing the same inputs, has not drifted. Cantrip has no networking of its own.
- Snapshots restore exactly, including listener limit windows and the RNG.

Which platforms this has been checked on, and what C# code of the game's own must avoid to keep it, are in [stability.md](stability.md#determinism).

**Numbers** can be written up to about ±9.2 trillion; a larger one is error CT0004. Arithmetic is correct while values stay within ±1 million. Past that nothing checks it, and a large enough result is wrong with no error, so a score that multiplies has to be kept in range by the content. [Numbers](stability.md#numbers) gives the limits.

## Diagnostics

Every message starts with the file, line and column, then its level and code:

```
game.cantrip:26:11: error CT302: Nothing called `Posion` is defined. Did you mean `Poison`?
```

Codes with four digits come from reading and loading the files. An error among them stops `cantrip test`, `describe` and `repl` before they start. Codes with three digits come from the linter and the description checks, which `cantrip lint` runs; their errors do not stop a test, so a misspelt verb fails a test only when the test runs it. `cantrip validate` reports every error, and `cantrip lint` also reports warnings and notes, which it prints as `info`. `lint` fails only on errors, unless it is given `--warnings-as-errors`, which makes a warning fail it too, though never a note: that is how a build server keeps a folder free of warnings. `--suppress CT301,CT306` leaves those codes out of either command, whether the linter or loading reported them, except that an error from reading or loading the files always shows. A failure while content runs has no code: a test prints `runtime error:` and the message.

**Reading the files**

| Code | Level | Meaning | Typical fix |
|---|---|---|---|
| CT0001 | error | A line's indentation does not line up with any block it could belong to. | Indent it to the same depth as the lines of its block. Use spaces. |
| CT0002 | error | A `!` on its own. | Write `!=`, or `not` to negate. |
| CT0003 | error | A character the language does not use. | Remove it. A curly quote pasted from a document is a common cause. |
| CT0004 | error | A number that is malformed or larger than about ±9.2 trillion. | Write a smaller number. |
| CT0005 | error | A string with no closing quote. | Close it on the same line. |
| CT0010 | error | Something other than what must come next, such as a missing `:` at the end of `if target.dead`. | The message says what was expected. |
| CT0011 | error | A top-level line that does not start with a word. | Start it with a declaration, or indent it under one. |
| CT0012 | error | A top-level word that is not a declaration. | Use one from [Declarations](#declarations); the message suggests the closest. |
| CT0013 | error | A declaration with no name. | `card "Name"`. |
| CT0014 | error | Something other than a name in a verb's parameter list. | `verb shatter(t):`. |
| CT0015 | error | A line in a `ruleset` that is not a setting. | Use a setting from [Rulesets](#rulesets). |
| CT0016 | error | A line in a declaration that does not start with a word. | Start it with a property, `on`, `modify` or a block name. |
| CT0017 | error | `on` with no event after it. | `on damaged:`. |
| CT0018 | error | `once` not followed by `per`. | `once per battle`. |
| CT0019 | error | `priority` with no number. | `priority 10`. |
| CT0020 | error | `once per` followed by something other than `turn`, `battle`, `run` or `chain`. | Use one of those four. |
| CT0021 | error | `modify` with no channel. | `modify damage: +2`. See [Modifiers](#modifiers). |
| CT0022 | error | `until` with no event. | `until turn_end:`. |
| CT0023 | error | Something unexpected in a statement, often a comma between arguments. | Separate arguments with spaces; a comma starts a flag (see [Statements](#statements)). |
| CT0024 | error | A value is missing, as after an operator. | Finish the expression. |
| CT0025 | error | Something unexpected in the first line of a block, before its `:`. | Check the words before the `:`. |
| CT0026 | error | Something unexpected in a property's values. | Check the line against the property's form. |
| CT0027 | error | Something unexpected in a ruleset setting. | Check the line against [Rulesets](#rulesets). |
| CT0028 | error | Blocks nested more than 256 deep. | Move some of the work into a [content verb](#content-defined-verbs). |
| CT0029 | error | A name with spaces not in quotes, or a verb name with spaces. | `card "Fire Bolt"`; a verb name is one word, such as `fire_bolt`. |
| CT0030 | error | A line indented further than the line above it, which does not open a block. | Line it up with the line above, or end that line with `:` if it should open a block. |

**Loading content**

| Code | Level | Meaning | Typical fix |
|---|---|---|---|
| CT0101 | warning | A declaration has the same block twice, such as two `effect:` blocks. The last one is used. | Merge them into one. |
| CT0102 | error | A `move` with no name. | `move "Chomp":`. |
| CT0103 | error | An unknown `stacking` mode. | `intensity`, `duration`, `refresh`, `both`, `none` or `separate`. |
| CT0104 | error | `decay` not followed by a number. | `decay 1`, or `decay 1 on turn_start`. |
| CT0105 | warning | An unknown status flag. The flag is ignored. | `buff`, `debuff`, `dispellable`, `persistent`, `hidden` or `unique`. |
| CT0106 | error | A `pattern` names a move the enemy does not have. | Fix the name, or add the move. |
| CT0107 | error | A `phase` without a name and a `when` condition. | `phase Broken when hp <= max_hp / 2`. |
| CT0108 | error | A move names a phase the enemy does not declare. | Add the `phase` line, or fix the name. |
| CT0109 | error | A `cost` with more than an amount and one resource. | `cost 2`, or `cost 2 bones`. |
| CT0110 | error | Two definitions of the same kind with the same name, ignoring case, perhaps in different files. | Rename one, or delete the copy. |
| CT0111 | error | Two content verbs with the same name. | Rename one. |
| CT0112 | warning | More than one `ruleset` is loaded. The last one loaded is used. | Keep one. |
| CT0113 | error | An `event` or `encounter` declaration. Both words are reserved and do nothing yet. | Remove it. Encounters are the game's code for now. |
| CT0114 | error | A `board` line the declaration cannot mean: a lane count of 0, an unknown `metric` or `on_vacated`, a word after `facing`, or a property no board reads. | Use a property from [Boards](#boards); the message says what that one takes and suggests the closest name. |
| CT0115 | error | A `range` line that is not a whole number of slots or a span of them, such as `range 5m`. | `range 1`, or `range 2..3`. A length with a unit is a question about the world and belongs to the game, not to the board. |
| CT0201 | warning | An unknown ruleset setting. It is ignored. | Use a setting from [Rulesets](#rulesets); the message suggests the closest. |
| CT0202 | error | An unknown value in `ordering` or `modifier_layers`. | Use the values listed under [Rulesets](#rulesets). |

**Linting**

| Code | Level | Meaning | Typical fix |
|---|---|---|---|
| CT301 | error | An unknown verb, or one written in the wrong kind of block: a test verb such as `cast` outside a test, `play` inside a scenario, or a scenario verb such as `battle` outside a scenario. | Fix the spelling, or move the line. For a verb the game registers in C#, run with `--suppress CT301`. |
| CT302 | error or warning | An unknown name. It is an error where a definition must be named (`apply Posion`, `create Wond`, `transform target into Sheeplng`, `card:Strke`, or a test or scenario line such as `hand Strik` or `battle Ghol`) or a status is read (`target.Posion`), and a warning for other names, which the game may supply at runtime. | Fix the spelling, or define it. |
| CT303 | warning | A `tag:` test for a tag no definition has, so it never matches. | Fix the spelling, or give the tag to what should match. |
| CT304 | warning | A listener on an event that nothing raises: it is not [built in](#built-in-events) and no content emits it, or it is `<stat>_changed` for a stat nothing in the content has. | Fix the event or stat name. The message suggests the nearest event, such as `turn_start` for `start_of_turn`. |
| CT305 | note | An event is emitted but no content listens for it. | Nothing, if the game listens in its own code. Otherwise check the name. |
| CT306 | note | Listeners that can set each other off. Loop protection stops each after one pass. | Check that this is what you want. |
| CT307 | error | `event` used outside an `on ...:` listener. | Move the line into a listener. |
| CT308 | error | `cancel` in a listener that runs after its event. | Listen to `before_<event>` instead. |
| CT309 | warning | A card asks for a target but its effect never uses `target`. | Use `target` in the effect, or remove the `target` line. |
| CT310 | note | A content verb that nothing calls. | Call it, or remove it. |
| CT311 | warning | `stacks` outside a status, where it reads a stat that is probably never set. | Read the status by name, as in `target.Poison`. |
| CT312 | error | `use` names a move the enemy does not have, or appears in something with no moves. | Fix the move's name. |
| CT313 | warning | A line in a declaration that ends in `:` but is not `effect:`, `move ...:` or a listener, such as `when card_played:` or `once per battle on card_played:`. It is only a label, so it never runs. | Start a listener with `on`, with `once per ...` after the event: `on card_played once per battle:`. The message suggests the form the line looks like, keeping a timing such as `before_` and a filter. For a block the game runs from C#, add its name to `LintOptions.HostBlocks`, or run with `--suppress CT313`. |
| CT314 | warning | `for N turns` on a `duration` or `refresh` status that ticks down on its host's turns, with N more than the amount applied. The status lasts as many turns as the amount, and `for` can only end it sooner. | Give the turns as the amount: `apply Weak 2`, not `apply Weak for 2 turns`. |
| CT315 | warning | A `duration` line on a `duration`, `refresh` or `both` status. The status takes its duration from whoever applies it, so the line does nothing. | Remove the line, and give the length where the status is applied: `apply Weak 2`. |
| CT316 | warning | A tag with behaviour of its own written on a line of its own: `exhaust`, `retain`, `ethereal`, `unplayable`, `power` or `attack` under a card, or `buff` or `debuff` under a status. The line is a property that nothing reads, so the behaviour never happens. | Put the word on the `tags` line: `tags exhaust`. The message suggests the whole line. |
| CT317 | warning | A [scenario](#scenarios) with no `battle` line, or a `battle` that names no enemy. There is nothing to play and nothing to measure. | Name the enemies to fight: `battle "Cinder Imp"`. |
| CT318 | note or error | A scenario's `runs` count: a note below 100, where the same content answers differently each time, and an error when it is not a whole number of one or more. | `runs 500`. |
| CT319 | error | An `expect` a scenario cannot check: a measurement it does not take, or a condition about one game, such as `expect enemy.hp == 3`. A scenario plays hundreds of games and measures `stalls`, `errors`, `wins`, `hp_left` and `turns` over all of them. | Compare a measurement with a number: `expect wins >= 55%`, `expect no stalls`. |
| CT320 | error | A verb handed a name it cannot act on. Either a verb that acts on something already in the game, given a name that is content — `copy Strike`, `transform Strike into Wound`, `replay Strike`, or `play Strike` outside a test — or `create` given a status, keyword or ability, which belong to whoever has them rather than to a zone. All of them are runtime errors. A game whose own verb has one of those names is left alone. | Use the verb that makes one (`create Strike`, or `create Strike into hand` and then `play created.first`), name something in the game in the slot (`transform target into Wound`), or hand the status out instead of making one (`apply Poison 2 to target`). `--suppress CT320` for content that reaches a verb of that name another way. |
| CT321 | error | A `transform` inside an `until` block. `until` puts back what it did, and the stats, statuses and used-up limits a transform replaces are gone. A game whose own `transform` verb runs instead is left alone. | Transform it outside the block, or apply a status instead. A `next turn:` block inside the `until` runs later on its own and is fine. `--suppress CT321` where a game's own verb is reached another way. |
| CT322 | error | `emit` handed the name of a built-in event, such as `emit damaged 99 to player`. `emit` raises a custom event; a built-in one belongs to the engine. Emitting one runs every listener of it while nothing happened and no history counter moves, so the event is forged and the record is not. It is a runtime error too. | Use the verb that really does it (`deal 99 to player`), or pick a name the engine does not use for an event of your own: `emit my_damaged 99 to player`. A game whose own `emit` verb runs instead is left alone; `--suppress CT322` where one is reached another way. |
| CT323 | error | A named clause a built-in verb does not read, such as `block 8 for 2 turns`, `apply Poison 3 at target` or `deal 5 against enemy2`. The clause was dropped in silence, so the line read as one thing and did another. It is a runtime error too. A flag after a comma is not a clause and is never reported, and neither is a verb content declares or a game registers. | Write the clause the verb reads — the message names it, and [Built-in verbs](#built-in-verbs) has the table — or drop the clause. Some of them are not a spelling at all: block is not timed, and a heal happens once. `--suppress CT323`, or `LintOptions.HostVerbs`, for content that reaches a verb of that name another way. |
| CT324 | error | A bare percentage where a built-in verb counts whole things, such as `apply Slow 40%`. The unit was dropped, so forty stacks were applied while the card's generated text said "Apply 40% Slow". It is a runtime error too. | Write the number (`apply Slow 40`), or a share of something (`deal target.max_hp * 40% to target`), which is what a percentage is for. |
| CT325 | error | A length in units the game's clock cannot measure: `on every 1s:` or `for 3s` where the ruleset says `clock turns`, or `2 turns` where it says `clock ticks`. Only content that states its clock is checked. | Write the length in the units that clock measures, or change the `clock` setting. The message says which units the stated clock takes. |
| CT326 | error | `player` written inside an enemy's move or a card's or ability's effect, in content that declares a [`hero`](#the-party). `player` is one entity — the party's leader — so the line acts on that one member however carefully the rules settled on another, and it does it quietly. Content with no `hero` is never reported, and `player` elsewhere is never reported. | Write `target` for whoever the line is aimed at, `leader` where the run's own actor really is meant, or `party` for all of them. |
| CT327 | warning | A lane or rank no [board](#boards) this game declares can hold. Compared against one, the comparison is the same for every actor before the game runs: `it.lane == 4` on a three-lane board matches nobody, and `it.rank <= 3` on a three-rank board matches everybody and limits nothing. Moved to one, the move stops at the edge of the board instead. | Use a place the board has, counting from 0, or declare the board the rule is written for. |
| CT328 | error | `position` assigned. It reads a rank and always will, but it names one axis of a place that has two, so a move written with it would have to guess which. | Write `rank`. `who.rank = 0` and `who.lane += 1` are moves; see [Boards](#boards). |
| CT329 | note | `position` read, which is the older name for `rank`. | Nothing is wrong: it reads the same number and keeps working for the whole 1.x line. Write `rank` when you next touch the line. |
| CT330 | warning / note | A `within` the engine will not answer the way it reads: a plain number, which counts slots, in a game that declares no [board](#boards) (warning); or a length with a unit, which is a question about the world and goes to the game's `IEffectHost.TryCall` (note). | Declare a `board` for slots, or keep the unit and implement `TryCall`. Both spellings are legal; the unit is what tells them apart. |
| CT331 | warning | `range` on something that points at nobody, so nothing ever reads it. | Add a `target` line, or take the `range` off. |
| CT332 | warning | A `range` that decides nothing: as wide as the widest board this game declares, written backwards (`range 3..1`), or `range 0` at an enemy, which on a facing board is a slot no enemy ever stands on. | Give it a reach the board can narrow. `range 1` is what melee is written as. |
| CT333 | warning | `lane(...)` on a board one rank deep, or `rank(...)` on a board one lane wide. One actor stands on a slot, so the row is that actor and nobody else. | Write the actor itself, or give the board a second rank or lane. |
| CT335 | error | `turns:` or `order:` in a game that says `clock ticks`. Both say how a turn is shared out, and a real-time game has no turns: `turn_start` and `turn_end` never fire there and the engine ignores the setting, so `turns: initiative` asks for an order that will never run. | Drop the line, or say `clock turns` if this game does take turns. |
| CT336 | warning | An `of` group that can never hold what the channel names: a pile of cards (`hand`, `discard`, `deck`, `cards`) on a channel whose value belongs to an actor, such as `damage` or `heal_taken`. The modifier would apply to nothing, whatever the game does. | Name a group of actors — `party`, `allies`, `enemies`, `everyone` — or move the rule to a channel a card carries, such as `cost`. |

**Descriptions**

| Code | Level | Meaning | Typical fix |
|---|---|---|---|
| CT401 | warning | A `{placeholder}` in `text` that matches nothing in the effect. | Use a name from [Descriptions](#descriptions). |
| CT402 | warning | The effect has changed since `text_checked` was recorded. | Re-read the text, then paste the new hash from the message. |
| CT403 | note | `text` is never shown, because `text_override` is set. | Remove one of the two. |

## Implementation notes

- **Integration.** The library owns the rules for damage, drawing and the rest itself, so a game's host only supplies names, functions and presentation: `TryResolveName`, `TryCall` and `OnEvent`.
- **Spatial selectors** with a unit (`within(x, 5m)`) parse, and their meaning comes from the host. With a plain number they count slots on the board and the engine answers them.
- **`deal 2 to adjacent(target)`** uses the board: actors on the same side one step apart.
- **Pending choices** are answered by an `IChoiceProvider`. A UI that cannot answer on the spot uses `DeferredChooser`: the action rolls back to a snapshot, reports the choice, and replays deterministically once answered, so a saved game is never mid-choice.
- **Backends.** Only the tree-walking interpreter exists; `ExecutionMode` is recorded but does not change pacing yet.
- **Descriptions** come at three levels, automatic, custom and override (see [Descriptions](#descriptions)). Automatic text is serviceable English, meant as a starting point that designers override with `text:`.
