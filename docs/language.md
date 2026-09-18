# Language reference

This describes the DSL as implemented in `src/GameplayEffects.Core`. Where it differs from the design notes, the [last section](#differences-from-the-design-notes) says how.

- [Files](#files)
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
- [Determinism](#determinism)
- [Diagnostics](#diagnostics)
- [Differences from the design notes](#differences-from-the-design-notes)

## Files

Content lives in `.ge` files. A folder loads every `.ge` file under it, in path order.

- **Indentation** delimits blocks. Use spaces; a tab counts as up to the next multiple of four. A line that does not line up with an enclosing block is error GE0001.
- **Comments** start with `#` and run to the end of the line. Blank and comment-only lines are ignored entirely.
- **Names** of definitions are strings (`card "Fire Bolt"`) or bare words (`status Poison`). A name with spaces or hyphens can only be referred to as a string later, so single-word names are easier to use.
- **Keywords** are case-insensitive. Names are matched case-insensitively.
- A block can be written on the same line after its colon: `if target.dead: draw 1`, `effect: deal 6 to target`.

## Declarations

| Declaration | Purpose |
|---|---|
| `card "Name"` | A playable card |
| `status "Name"` | A status attached to an entity |
| `relic "Name"`, `item "Name"` | Passive effects held by the player |
| `keyword "Name"` | A status-like entity used for keywords such as Retain |
| `enemy "Name"` | An enemy actor with moves |
| `actor "Name"` | A generic actor; when created, it joins its creator's side |
| `ability "Name"` | An ability with a cooldown, for real-time play |
| `resource "stat"` | Bounds and reset rules for a stat |
| `verb name(params):` | A verb written in the DSL |
| `ruleset` | Rules the content is written against |
| `test "Name"` | A test run by `gedsl test` |

Inside a declaration:

- **Properties** are `name value...`. Values may be separated by commas or spaces: `tags attack, fire` and `pattern cycle Chomp, Bellow`. A property with a single number becomes a **stat** of every entity created from the definition (`cost 2`, `hp 40`, `mark 0`). `hp` also sets `max_hp` unless both are given.
- **Blocks** are `name:` followed by statements: `effect:`, `move "Chomp":`.
- **Listeners** are `on ...:` blocks. See [Listeners](#listeners).
- **Modifiers** are `modify ...:` lines. See [Modifiers](#modifiers).
- **Presentation** properties are `text`, `text_override`, `flavour` (or `flavor`) and `text_checked`. See [Descriptions](#descriptions).

## Cards

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
| `target enemy` | Needs a living enemy. With one enemy it is chosen automatically; with several, the chooser picks. |
| `target ally` | Needs a living ally; defaults to the player. |
| `target self` | Targets the player. |
| `target any` | Any living actor, or none. |
| (none) | No target. |

Tags with built-in behaviour:

| Tag | Behaviour |
|---|---|
| `exhaust` | Goes to the exhaust pile after it is played. |
| `power` | Goes to the powers zone after it is played. Its listeners and modifiers only work once played. |
| `retain` | Stays in hand at the end of the turn. |
| `ethereal` | Exhausted if still in hand at the end of the turn. |
| `unplayable` | `Play` refuses it. |
| `attack` | Counted by the `attacks` history counter. |

Other cards listen from hand, so a curse can hurt while it is held:

```
card "Ache"
  cost 0
  tags curse, unplayable
  on card_played:
    lose 1 hp
```

Playing a card checks energy and target, pays the cost, moves the card to the `play` zone, raises `card_played` around its effect, then moves it to its destination and resolves every queued trigger. A `before_card_played` listener that cancels refunds nothing because nothing was paid; an `instead_of_card_played` listener replaces the effect but the cost is still paid.

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

Reading `host.Weak` gives that counter: stacks, or the remaining duration for duration and refresh statuses. Writing `host.Weak -1` changes the same counter, applying the status if it was absent.

`apply X N for 3s` (or `for 2 turns`) also removes the status when the clock reaches that time.

## Relics, items and keywords

Relics and items are active while in the `relics` zone (where `AddRelic` puts them). Their listeners and modifiers treat the holder as "you".

```
relic "Tally"
  on card_played(tag:attack) once per turn:
    gain 1 gold
```

A `keyword` definition behaves like a status. Cards tagged `retain` or `exhaust` get those behaviours without any keyword definition; defining `keyword "Exhaust"` only adds a tooltip.

## Enemies

```
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

**Phases** gate which moves an enemy may choose from:

```
enemy "Slime King"
  hp 60
  phase Broken when hp <= max_hp / 2
  move "Chomp":
    deal 11 to player
  move "Split" phase Broken:
    deal 5 to player
  pattern cycle Chomp, Split
```

A move with no `phase` is available in every phase; a move with one only while that phase is active. Conditions are evaluated against the enemy each time its intent is rolled, and the **last** declared phase whose condition holds is the one that applies — so thresholds can be written in the order they are thought of (three quarters, then half, then a quarter) and the deepest one that is true wins. Entering a phase restarts the pattern, because the old position counted through a list of moves that is no longer the same one. The phase is readable as `enemy.phase`, and is part of the saved game.

The next move (the intent, readable as `enemy.intent`) is rolled when the battle starts, when an enemy spawns or is created mid-battle, and after each enemy turn. Inside a move, `self` is the enemy and `target` is the player. `use Chant` makes an enemy perform one of its moves.

## Abilities and real time

```
ability "Frost Nova"
  cooldown 8s
  effect:
    apply Chill 1 for 3s to enemies
```

Real-time games create the runtime with a `TickClock` and call `runtime.Tick()` from their fixed timestep. `GrantAbility` attaches an ability to an actor; `UseAbility` runs it if it is off cooldown and starts the cooldown. Durations with `s` or `ms` convert to ticks on a tick clock; `turns` convert on a turn clock. Using seconds on a turn clock is an error.

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

**Phases.** `on damaged` listens after the fact. `on before_damaged` runs before, and may change `event.amount` or `cancel`. `on instead_of_died` runs in place of the action: if any instead listener fires, the default action is skipped. The phase prefix may come before the scope (`before_owner.damaged`) or after it (`owner.before_damaged`).

**Scope.** `on owner.damaged` only hears `damaged` events whose target is the listener's owner. Scopes are `self`, `owner` (the host of a status, the holder of a relic), `controller`, `player`, `any`, or any name that resolves to an entity.

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

**Every.** `on every 1s:` fires on an interval rather than on an event. It is pumped by the clock instead of raised by anything, so only the listener whose interval has elapsed runs. The interval is written like any other duration (`1s`, `250ms`, `2 turns`), and the clock has to understand the unit: seconds mean nothing to a turn-based game, so `every 1s` there registers nothing at all rather than half-working. Filters and `once per ...` apply as they do to any listener. When the next firing is due is part of the saved game, so a reload resumes mid-interval instead of restarting it.

**Ordering.** Listeners for the same event run by priority (higher first), then play order (the order their entities became active), then the active side first, then registration order. The ruleset can reorder the first three.

**Loop protection.** A listener never re-triggers from its own consequences within one causal chain, and chains stop at depth 50. `once per turn`, `once per battle`, `once per run` and `once per chain` limit how often a listener fires.

## Built-in events

| Event | Fields |
|---|---|
| `damaged` | target took a hit from source (card if a card caused it). `amount` is hp actually lost after block. Data: `base`, `total`, `blocked`, `overkill`. Tags: the damage type. |
| `blocked` | block absorbed `amount` of a hit on target |
| `overkill` | a killing hit had `amount` to spare |
| `died` | target is dying. `instead_of_died` prevents it. The dying actor's own listeners still hear it. |
| `killed` | target died; source is the killer |
| `healed` | target regained `amount` hp |
| `gained_block` | target gained `amount` block |
| `drawn` | target (a card) was drawn |
| `shuffled` | the discard pile was shuffled into the draw pile |
| `discarded`, `exhausted`, `moved` | target (a card) changed zone. Data: `from`, `to`. A card drawn with a full hand is `discarded`. |
| `created` | target was created |
| `destroyed` | target was taken out of the game |
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
```

**Amounts** pick the layer: `+N` or `-N` (add), `xN` or `*N` (multiply; `x50%` is half), `clamp A..B` or `clamp N` (clamp; a single number is a ceiling), `=N` or `set N` (override). Values pass through the layers in ruleset order, add, multiply, clamp, override by default. Within the override layer the most recently created source wins. Damage, block and healing are rounded down after modifiers.

**Channels** are `damage`, `damage_taken`, `block`, `block_taken`, `heal`, `heal_taken`, `cost`, `draw`, or any stat name (`max_hp`, `armor`...).

**Default scope.** Without `of`, a modifier applies relative to its **anchor**: a status's host, a relic's holder, or the card itself for a modifier written on a card.

| Channel | Applies to |
|---|---|
| `damage`, `block`, `heal`, `draw` | what the anchor's controller deals, gains, heals or draws (or what the card itself does, when anchored to a card) |
| `damage_taken`, `block_taken`, `heal_taken` | what the anchor's controller receives |
| `cost` | the card itself, when anchored to a card; otherwise all of the controller's cards |
| any stat | that stat on the anchor |

**`of` scope** replaces the default: the modifier applies when the value being computed belongs to someone in the group. The group is read from the modifier owner's side, so `of enemies` on the player's relic always means the player's enemies. A `where` on the group reads stats from the candidate (`hp > 20`) but tests qualifiers such as `tag:` and `source:` against the value being computed, like a `where` on the modifier.

**Filters** (`where`) see the value being computed: `tag:fire` checks the damage's tags and the card's tags, `source:self` compares the source's controller with the modifier owner's. Roles and group names such as `source:enemies` and `allies.count` are read from the modifier owner's side, whoever is acting.

Stat reads are cached and the cache is invalidated by any change to the game state.

## Statements

**Commands** are a verb followed by arguments, named clauses and trailing flags:

```
deal 6 to target
apply Slow 40% for 3s to enemies
deal stacks to owner, ignore block
```

Clause keywords are `to`, `from`, `for`, `with`, `at`, `by`, `into`, `over`, `as`, `of`, `against`, `using`, `onto`. Flags after a comma are `ignore block`, `pierce`, `true damage`, `unblockable`, `silent`, `hidden`, `forced`, `optional`, `upgraded`, or a bare word.

**Assignments**:

```
stacks -1
energy += 1
hp = 10
target.Poison -1
event.amount = event.amount * 2
```

`name -N` and `name +N` are shorthand for `-=` and `+=` when nothing else follows on the line. A bare stat name refers to the nearest entity up the ownership chain that has the stat, else the controller: `stacks -1` in a status changes the status, `energy += 1` in a card changes the player, `block +6` in an enemy move changes the enemy. `host.Status` adjusts that status's counter. Only `event.amount` can be assigned on an event.

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

Labelled blocks such as `setup:` just run their statements; the label is for readers and tools.

## Expressions

**Literals**: numbers (`6`, `1.5`), units written against the number or after a space (`40%`, `3s`, `250ms`, `2 turns`, `3 seconds`), `x1.5` for multipliers, strings (`"text"`), and ranges (`3..6`), which roll a whole number when used as a number.

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

**Selectors.** `random N group` shuffles with the game's RNG and takes N. `lowest <stat> group` and `highest <stat> group` take the first by that stat, breaking ties by board position then id; with a single name (`lowest enemies`) the stat is `hp`. `other group` leaves out the target or the running entity's controller. A prefix word followed by nothing selectable (as in `pattern random`) is an ordinary name. `within` is passed to the host's `TryCall`; without a host that implements it, it is a runtime error.

**Functions**: `min(a, b, ...)`, `max(...)`, `abs(n)`, `floor(n)`, `ceil(n)`, `round(n)`, `clamp(n, lo, hi)`, `count(group)`, `random(lo, hi)`, `adjacent(who)`, `has(who, predicate)`, `stacks(Status[, who])`. Methods: `who.has(predicate)`, `who.stacks(Status)`.

`has` is true when the entity has the tag (directly or on an attached status), has a status of that name, or is that entity.

**Members of an entity**: `dead`, `alive`, `removed`, `name`, `id`, `owner`, `source`, `controller`, `team`, `zone`, `position`, `intent`, `statuses`, `kind`, a status name (its counter), a history name (`damage_taken_this_turn`), or any stat. Stats that nothing has set read as 0.

**Members of a group**: `count`, `size`, `length`, `first`, `last`, `empty`, `any`, or a stat, which sums it across the group (`enemies.hp`).

**Members of `event`**: `source`, `target`, `card`, `amount`, `name`, `cancelled`, and anything in its data. Using `event` outside a listener is an error.

### Qualifiers

`tag:x`, `keyword:x`, `status:x`, `source:role`, `target:role`, `name:x`, `card:x`, `zone:x`, `team:x`, `kind:x`, `type:x`, `rarity:x`, `id:x`. They are written with no spaces around the colon.

A qualifier tests whatever is in focus: the candidate inside `where`, the value being computed inside a modifier, or the event inside a listener filter. Roles for `source:` and `target:` are `self`, `owner`, `player`, `enemy`/`enemies`, `ally`/`allies`, `target`, `any`, or an entity name. Roles compare controllers, so `source:self` on a relic matches damage from its holder's cards. `enemy` and `ally` roles are relative to the side the effect runs for; inside a modifier, that is the modifier owner's side.

## Names

A bare name resolves in this order: local variables (`let` bindings, `for each` variables, verb parameters, `chosen`, `created`, `index`, `x`); the built-in names below; history counters; names the host supplies; inside `where`, the candidate's stat; the running entity's stat; a definition (statuses and keywords first, so `apply Burn` and `has(Burn)` find the status when a card has the same name); event data; the controller's stat; a stat declared anywhere in content, which reads 0. Anything else is a runtime error with a suggestion.

| Name | Meaning |
|---|---|
| `self` | the running entity |
| `owner` | the running entity's owner (a status's host), or itself |
| `source` | who is acting |
| `target` | the target |
| `card` | the card being played |
| `player`, `controller` | the player; the running entity's controller |
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
| `block` (`gain_block`) | `block N [to who]`. Defaults to yourself. |
| `heal` | `heal N [to who]`. Defaults to yourself. |
| `draw` | `draw [N]` for the controller. Reshuffles the discard pile when the draw pile runs out. |
| `discard`, `exhaust` | `discard N` asks the chooser to pick from hand; `discard who` and `exhaust self` name the cards. |
| `apply` | `apply Status [N] [for duration] [to who]`. Defaults to the target, or a status's host, or the controller. |
| `add` | `add tag:x [to who]` adds a tag; `add Status N` is `apply`. |
| `remove` | `remove Status [from who]`, `remove tag:x from who` (every status with the tag, and the tag), `remove who` (destroys it). |
| `gain`, `lose` | `gain N Status` adjusts a status; `gain N stat` changes a stat. `[to who]`, else yourself. |
| `change` | `change stat by N [to who]`, or `change hp -5 on target`. |
| `create` | `create Card [N] [into zone]` (default hand), `create Relic`, `create Enemy`. Binds `created`. |
| `shuffle` | `shuffle` (discard into draw), `shuffle Card [N]` (creates copies in the draw pile), `shuffle cards into draw`. |
| `move` | `move cards to zone [, top]` |
| `destroy` | `destroy [who]`. Defaults to itself. |
| `kill` | `kill [who]`. Defaults to the target. |
| `choose` | `choose N from group [as name]`. Binds the result to `chosen`, or to the name after `as`: one entity, or a group when more than one is chosen. |
| `emit` | `emit event [amount] [to who]` raises a custom event. |
| `cancel` | In a `before_` or `instead_of_` listener, cancels the event. |
| `replay` | `replay card [on target]` resolves a card's effect again, for free. |
| `use` | In an enemy, performs one of its moves. |
| `log` | `log values...` writes to the runtime's `Logged` event. |

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
- `in N turns:` (or `in 3s:` on a tick clock) runs when the clock reaches that time. The turn clock advances once per round, after the player's turn has started, so the block runs after turn-start resets.
- `until <event>:` runs now and undoes its changes when `<event>` next happens to its owner, after that event's listeners. It undoes statuses and stacks it applied (only those stacks), tags it added and changes to non-resource stats. Damage and other resource changes are not refunded. The end of a battle undoes every pending `until`.

## Rulesets

```
ruleset
  events: before, instead, after
  loops: once_per_chain, max_depth 50
  ordering: priority, play_order, active_player
  modifier_layers: add, multiply, clamp, override
  triggers: queued
  hand_size 5
```

| Setting | Default | Meaning |
|---|---|---|
| `events` | all three | which phases are dispatched |
| `loops` | `once_per_chain, max_depth 50` | `once_per_chain` stops self-retriggering; without it only the depth cap applies |
| `ordering` | `priority, play_order, active_player` | listener order; anything left out keeps its default position |
| `modifier_layers` | `add, multiply, clamp, override` | modifier layer order |
| `triggers` | `queued` | `immediate` runs after listeners inline |
| `hand_size` | 5 | cards drawn each turn |
| `max_hand_size` | 10 | cards drawn beyond this go to the discard pile |
| `max_steps` | 100000 | interpreter steps per top-level action before it is stopped |
| `max_call_depth` | 64 | content verb nesting |

If several rulesets are loaded, the last one loaded wins (GE0112).

## How a battle runs

1. **StartBattle** removes enemies that died in an earlier battle, shuffles the draw pile, rolls enemy intents, raises `battle_start` and starts the player's turn.
2. **A turn starts**: the turn number increases; each actor on the side raises `turn_start`, which resets resources such as energy and block; pending `next turn:` blocks run; the turn clock advances; the player draws `hand_size` cards.
3. **The player plays cards** with `Play`. Queued triggers resolve after each card.
4. **EndTurn**: the player raises `turn_end` (decay and `until` reverts follow its listeners); the hand is discarded except retained cards, and ethereal cards are exhausted; the enemies' turn starts; each living enemy uses its intent; enemies raise `turn_end`; intents are rolled; the player's next turn starts.
5. **The battle ends** when the player dies (lost) or no enemies are left alive (won). `battle_end` is raised, `until` blocks are undone, the player's statuses are removed unless flagged `persistent`, and every card returns to the draw pile.

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

**Drift protection.** `gedsl lint` reports a placeholder that matches nothing (GE401). Add `text_checked "<hash>"` once a text has been reviewed; when the effect later changes, lint reports GE402 with the new hash to paste after re-reading the text. Presentation properties do not affect the hash.

## Tests

```
test "Poison ticks and decays"
  enemy hp 20
  apply Poison 3 to enemy
  end turn
  expect enemy.hp == 17 and enemy.Poison == 2
```

Each test gets a fresh game with seed 1 and a player with 80 hp and 3 energy. Setup statements run first; the battle starts, without shuffling or drawing a hand, at the first other statement. Any DSL statement works in a test, plus:

| Verb | Meaning |
|---|---|
| `enemy [Name] [stat N]...` | Spawns an enemy: a defined one, or a plain 10 hp enemy (`enemy "Label" hp 20`). A status name applies that many stacks. The first is `enemy` and `enemy1`, then `enemy2`... |
| `player stat N...` | Sets player stats or applies statuses |
| `hand`, `deck`, `discard_pile` | Adds cards to that zone |
| `relic Name` | Gives the player a relic |
| `seed N` | Reseeds the game's RNG |
| `answer "A, B"` | Queues an answer for the next choice |
| `realtime N` | Uses a tick clock with N ticks per second for the whole test, wherever it is written |
| `play Card [on who]` | Plays a card, adding it to the hand if needed; fails the test if it cannot be played |
| `end turn` | Ends the turn |
| `tick N` | Advances the tick clock |
| `grant Ability`, `cast Ability [on who]` | Gives and uses abilities |
| `expect condition` | Fails the test, showing the values involved, if the condition is false |
| `setup:` | A block of setup statements |

## Determinism

The same content, seed and inputs produce the same game on every machine:

- All arithmetic uses a fixed-point number with six decimal places; no floating point.
- Randomness comes from a seeded xoshiro256** generator, never `System.Random`.
- Listener order, selector ties and resource resets use explicit orderings, never hash order.
- `runtime.State.ComputeHash()` fingerprints the rules state, including scheduled work and history, for replay checks and lockstep multiplayer.
- Snapshots restore exactly, including listener limit windows and the RNG.

Numbers range to about ±9.2 trillion when written, and stay exact in multiplication up to about ±1 million.

## Diagnostics

| Codes | Source |
|---|---|
| GE0001-GE0028 | lexer and parser (indentation, unexpected tokens, invalid numbers, nesting deeper than 256) |
| GE0101-GE0112 | loading content (duplicate definitions and verbs, unknown stacking modes or flags, pattern moves, several rulesets) |
| GE0201-GE0202 | ruleset settings |
| GE301 | unknown verb |
| GE302 | unknown name; an error where a definition is required |
| GE303 | a tag no definition declares |
| GE304 | a listener on an event nothing raises |
| GE305 | an emitted event nobody listens to (note) |
| GE306 | listeners that can re-trigger each other (note) |
| GE307 | `event` outside a listener |
| GE308 | `cancel` in an after listener |
| GE309 | a targeted card whose effect ignores its target |
| GE310 | an unused content verb (note) |
| GE311 | `stacks` outside a status |
| GE312 | an enemy using a move it does not have |
| GE401 | a description placeholder that matches nothing |
| GE402 | `text_checked` no longer matches the effect |
| GE403 | `text` shadowed by `text_override` (note) |

## Differences from the design notes

- **Integration.** Section 4.7 sketches an `IEffectHost` with `Damage` and `Draw` methods. The library owns those rules itself (as section 6 asks), so the host only supplies names, functions and presentation: `TryResolveName`, `TryCall` and `OnEvent`.
- **Spatial selectors** (`within`) parse, but their meaning comes from the host.
- **`deal 2 to adjacent(target)`** uses board positions: actors on the same side one slot apart.
- **Pending choices** are answered by an `IChoiceProvider`. A UI that cannot answer on the spot uses `DeferredChooser`: the action rolls back to a snapshot, reports the choice, and replays deterministically once answered, so a saved game is never mid-choice.
- **Backends.** Only the tree-walking interpreter exists; `ExecutionMode` is recorded but does not change pacing yet.
- **Descriptions** follow section 3.12. Automatic text is serviceable English, meant as a starting point that designers override with `text:`.
