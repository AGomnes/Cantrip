# Building a real-time game on Cantrip: what hurt

Notes kept while building `realtime/` -- *Emberline* -- from the docs alone, as a user would:
`README.md`, `docs/language.md`, `docs/csharp.md`, `docs/godot.md`, `docs/writing-content.md`,
`docs/simulating.md`, `docs/coverage.md`, `docs/stability.md`, the `samples/` folder and
`reference/`. Engine or addon source was read only where noted, and **each time that is a finding
of its own**.

Real time has existed since early on, is marked experimental in
[stability.md](../docs/stability.md#known-limitations), and had never had a game built on it. The
only real-time content in the repository was `samples/corpus/dota2.cantrip`, eight effects in a
coverage corpus. Emberline is the first game.

Ranked by how much each would hurt a real developer. The verdict on 1.0 is #18 at the bottom.

**The headline: real time is not a mode. It is a second clock bolted to a turn engine, and every
turn-shaped call is still there, still callable, and still does turn things.** Nine of the
findings below are that one fact seen from nine directions.

---

## 1. The C# guide does not document real time at all

**Trying to do.** Build the runtime. Step one of a real-time game in C#.

**Expected from the docs.** [docs/csharp.md](../docs/csharp.md) is "for a game that calls the
library itself, whether a console app, a MonoGame or FNA game, or an engine loop of your own", and
walks through "the parts such a game uses".

**What happened.** It never mentions real time. In the whole 689-line guide:

| word | where it appears in csharp.md |
|---|---|
| `TickClock` | once, in a parenthesis inside a paragraph about restoring a save |
| `RuntimeOptions.Clock` | once, in a paragraph about `Dispose` |
| `Tick` | twice, as a row in two chooser tables |
| `GrantAbility` | never |
| `clock ticks` | never |
| a real-time example | never |

There is no section, no snippet, no statement of what the `TickClock` constructor's argument
means, and no mention that `clock ticks` in a ruleset makes a runtime that was given no clock
start one. The entire published C# documentation of real time is one paragraph in
[language.md](../docs/language.md#abilities-and-real-time): "Real-time games create the runtime
with a `TickClock` and call `runtime.Tick()` from their fixed timestep."

From that I had to guess a namespace and a constructor:

```csharp
var clock = new TickClock(20);
var runtime = new CardRuntime(content, new RuntimeOptions { Clock = clock, Seed = seed });
```

**To the engine's great credit, that compiled first time** -- `TickClock` is in `Cantrip.Runtime`,
which is one of the three namespaces csharp.md's own `using` block already lists, and its one
constructor argument is the tick rate. The design is guessable. It is just not written down.

What is *not* guessable is what the argument means for content. `cooldown 6s` converts through the
runtime's rate, so `new TickClock(20)` and `new TickClock(60)` are two different games from the
same content -- and the only place that is said is docs/godot.md's table entry for the node's
`TicksPerSecond` export, which a non-Godot game never reads.

**Workaround shipped.** `realtime/host/Emberline.cs` carries a comment saying what the docs do
not, and `TicksPerSecond` is a constant beside the content that depends on it.

**How bad.** Worst finding of the round, because it is the front door. Every other finding here is
something a developer hits after they have got a game running; this one is what stops them getting
one running at all. A developer who is not willing to read the engine's source has one paragraph
and a guess.

---

## 2. Every turn-shaped call still works on a tick runtime, and `EndTurn` really does run a turn

**Trying to do.** Find out what a party costs in real time -- which of the party calls a
real-time game may use and which it must stay away from.

**Expected from the docs.** language.md's CT325/CT335 entries are the only statement anywhere
about how turns and ticks relate, and CT335 says it plainly:

> `turns:` or `order:` in a game that says `clock ticks`. Both say how a turn is shared out, and a
> **real-time game has no turns: `turn_start` and `turn_end` never fire there** and the engine
> ignores the setting.

**What happened.** All of that is false. In a game whose ruleset says `clock ticks`, with a
`TickClock` runtime:

| called | what happens |
|---|---|
| `StartBattle()` | raises `battle_start` **and `turn_start`**, sets `State.Turn` to 1, draws a hand |
| `CanAct(member)` | answers `true` |
| `ActiveMember` | answers the leader |
| `Pass(member)` | accepted, no error |
| `EndTurn()` | raises `turn_end`, `turn_start`, `turn_end`, `turn_start` -- the party's turn *and the enemies' whole turn* -- and moves `State.Turn` from 1 to 2 |
| `State.Turn` | counts turns that nothing in a real-time game ever takes |

None of these refuses, warns or logs. `EndTurn` on a tick runtime runs a complete turn cycle,
including every enemy's telegraphed move, inside a game that has no turns. A front end that leaves
an **End turn** button wired up -- which is the default in every sample, every doc snippet and the
addon's own demo -- gives the player a button that fires every enemy's move at once for free.

This also means `turn_start` fires exactly once in a real-time battle, at `StartBattle` and never
again, which is the mechanism behind #14.

**Verified both ways.** In C#, `realtime/host/Program.cs` asserts `CanAct` and `ActiveMember`
answer rather than throw. Through the Godot node, `realtime/godot/game/checks.gd` asserts the same
and that `EndTurn()` moves `GetTurn()` from 1 to 2.

**Workaround shipped.** Emberline never calls `EndTurn`, `Pass` or `CanAct`, and the C# host
asserts `State.Turn == 1` after the whole 45-second hold as a regression guard.

**How bad.** Severe, and the finding that everything else in this document hangs off. The docs
state a rule the engine does not implement, in the one diagnostic whose whole job is to explain
how real time differs from turns. A developer reading CT335 would reasonably conclude that the
turn API is inert under ticks and leave it wired up.

**The good news buried in it:** a party *works* under ticks. `AddHero` returns a member, the
member stands on the board, its `abilities` line grants abilities with their own cooldowns, it
takes damage, `party` and `allies` answer, `Party` lists it, and every ability I gave a hero
behaved exactly as one on the leader did. The party design's worry -- that `Pass`, `CanAct` and
`ActiveMember` would have to throw on a tick runtime -- turns out not to bite, because a real-time
game simply never calls them. What it needs instead is for those calls to *say no*.

---

## 3. An enemy's whole vocabulary is turn-shaped, so real-time enemies are written from scratch

**Trying to do.** Write three enemies that walk in from the back of the board and attack.

**Expected from the docs.** language.md gives enemies `move`, `pattern`, `phase`, `at` targeting
and intents, and both csharp.md and godot.md give a front end `DescribeIntent`. Nothing anywhere
says any of it is turn-only.

**What happened.** All of it is dead in real time, because every piece of it is driven by the
enemies' turn:

| written | what it does under `clock ticks` |
|---|---|
| `move "Beam": deal 6 to leader` | never runs |
| `pattern cycle Beam, Sweep` | never advances |
| `phase Wounded when hp <= max_hp / 2` | gates moves, so it gates nothing |
| `move "Clutch" at lowest hp enemies:` | never aims at anybody |
| `entity.Intent` / `DescribeIntent` | **rolls once at `StartBattle` and then reads for ever**, telegraphing a move that will never happen |

The last row is the nasty one. `Intent` is not empty in a real-time game -- it is populated,
plausible and permanently wrong. A UI that draws the telegraph, which is the single most
recommended thing in the enemy documentation, shows the player a lie for the whole fight.

And **lint says nothing.** Content whose ruleset says `clock ticks` and which declares `move`,
`pattern` and `phase` blocks that can never run lints with zero errors, zero warnings and zero
notes. A `2 turns` in the same file is error CT325 -- so the language checks the *unit* a designer
writes and not the *machinery* they used.

The only thing left is `on every <n>s:` on the enemy declaration, which does work, and works well.
Emberline's enemies are:

```
enemy "Hollow"
  hp 16
  on every 2s:
    if not self.has(Reeling):
      if self.rank > 0:
        self.rank -1
      else:
        deal 5 to random party
```

That is a readable enemy. What it costs, next to the turn-based one it replaces, is: the
telegraph, the pattern, the phase, `at` targeting, and the separation between "what it does" and
"when it does it".

### 3a. CT326 -- the party's safety net -- does not look inside a listener

This is the sharp edge. CT326 exists to stop an enemy writing `deal 5 to player` in a party game,
because that would quietly hit the leader however carefully the rules had settled on somebody
else. language.md calls it "the one class of wrong answer this project refuses to ship". It fires
for `move` blocks and for card and ability effects.

It does not fire for a listener. In content that declares a `hero`:

```
enemy "Sniper"
  hp 10
  on every 2s:
    deal 5 to player      # accepted; 0 errors, 0 warnings, 0 notes

enemy "Mover"
  hp 10
  move "Shot":
    deal 5 to player      # error CT326
```

A real-time enemy has no `move`. **Every line of enemy behaviour in a real-time game is written in
the one place CT326 does not look**, so the guard 1.0 added for parties is absent from the entire
surface where a real-time party game lives.

**Workaround shipped.** Every enemy writes `deal <n> to random party`, and `enemies.cantrip`
carries a comment saying why, because nothing would have told me.

**How bad.** Severe. It is a silent wrong answer, of exactly the kind the project says it refuses
to ship, in exactly the shape 1.0 is promoting.

---

## 4. `cantrip sim` cannot play a real-time game, does not say so, and reports confidently wrong numbers

**Trying to do.** Write a `scenario` for Emberline, as `reference/` and every sample has.

**Expected from the docs.** simulating.md says a scenario is "a fuzzer and a coverage tool for your
own content", and the only mention of real time is that the `realtime` *test verb* is not allowed
in a scenario "because when to act in continuous time is the game's own frame loop, not a bot's".
That is about one verb. Nothing says a scenario cannot simulate a real-time game, and `sim` is
named in stability.md as part of how Cantrip is checked.

**What happened.** `sim` loads `clock ticks` content without a murmur and then plays it by calling
`EndTurn` fifty times. The clock is a `TickClock` and nothing ever ticks it, so `Now` stays at 0
for the whole run. Every `on every` listener is silent, every ability that is used once never
comes back, and no battle can ever end:

```
The hold, as a scenario
  20 run(s) by each of 2 bots, seeds 1-20, turn limit 50, cautious and patient bots

  What the content allows, whatever the bot did
    nothing threw
    40 battle(s) reached the turn limit of 50 and never ended
    every run came out the same way, so 20 runs said no more than 1 would

  What the cautious bot did with it, in 0.2s
    battle                       fought     won   turns  hp lost
    Hollow + Hollow + Breaker        20    0.0%    50.0      0.0
```

Read that table as a developer would. `hp lost 0.0` against three enemies that between them deal
seventeen damage every three seconds. `50.0 turns` in a game with no turns. And "40 battles
reached the turn limit and never ended" presented as a **finding about the content** -- under the
heading "What the content allows, whatever the bot did", which is the one part of the report that
is meant to hold whoever plays.

The exit code is 0, because `expect no errors` passed. Add the `expect no stalls` that every sample
scenario has and it fails for ever, and the developer goes hunting for an endless-fight bug that
does not exist.

**Workaround shipped.** There is no scenario in `realtime/content` and no `sim` step in CI for it.
`realtime/README.md` and the CI file both say why, in case somebody adds one later.

**How bad.** Severe. A tool that quietly gets the answer wrong is worse than one that refuses, and
`sim` is the tool the project points at for exactly the failures a real-time game is most prone
to: the fight that never ends, the ability that is never usable, the content that throws. A
real-time game gets none of that and is told everything is fine.

---

## 5. Nothing can hear an enemy arrive, and nothing can place one

**Trying to do.** Spawn a wave every two seconds, walking in at the back rank.

**Expected from the docs.** `created` is documented as "target was created by `create`, `copy` or
`shuffle <card>`", so I expected `SpawnEnemy` to raise it too -- a spawn is the most obviously
"created" thing there is. And the board documentation says an actor is moved by writing
`target.rank = 0`.

**What happened.** Neither.

- **`SpawnEnemy` raises nothing.** `on created(target:self): self.rank = 3` on the enemy
  declaration never fires; a `--trace` of the test shows no `created` event at all. So content
  cannot react to a spawn: no arrival effect, no self-placement, no "when something enters the
  fight" relic. In a turn game that never comes up, because enemies arrive once, before the
  battle. In a real-time game a spawn is the most frequent event there is.
- **No call places an actor.** `CardRuntime` has no method for it. godot.md says so outright:
  "Content moves an actor by writing `target.rank = 0`; nothing on the node does, because where
  somebody stands is a rule and not a view." That is a good rule for a turn game, where the engine
  lays out a fight once. It means a real-time host places every spawn by executing a **string**:

  ```csharp
  Runtime.Execute("arrive self " + lane, e, null);
  ```

  There is a `State.Assign(actor, lane, rank)` on the public surface, but it is in no document, and
  it throws an `ArgumentOutOfRangeException` rather than refusing, so it is not a seam a host
  should lean on. (Found by reflecting over the assembly -- see #9.)

**Workaround shipped.** `realtime/content/places.cantrip` declares three verbs -- `hold`, `arrive`
and `loom` -- and the host, the Godot front end and all sixteen tests call them through `Execute`
or as ordinary statements. It is the least bad shape: the rule stays in content, and there is one
definition of "walks in from the back".

**How bad.** High. Two independent gaps that meet on the most common action in the genre. The
string-executing workaround also means a typo in a lane number is a `DslException` at run time
rather than a compile error.

---

## 6. The board fills in single file, and writing a taken slot swaps

**Trying to do.** Stand three keepers abreast on the front rank.

**Expected from the docs.** Boards describes lanes and ranks and says content declares the shape,
and godot.md says "lay the party out by `(lane, rank)`". Nothing says how the engine chooses the
slots.

**What happened.** On a 3x5 board, three party members are placed at `(0,0)`, `(0,1)` and `(0,2)`
-- one lane, stacked in depth -- and so is a wave of three enemies. The party stands in a queue.
For a turn game on a two-rank board that is fine; for anything where the lanes mean something it
is never what the game wants, and there is no setting for it.

Writing a place that is taken **swaps** the two actors rather than refusing. That is documented
between the lines (`reference/FINDINGS.md` calls a swap "its own inverse") and it is a reasonable
rule, but it produces a surprise in a lane-defence: a Hollow at rank 1 advancing into an occupied
rank 0 pushes the one already there backwards, so a queue of enemies in a lane shuffles instead of
queueing, for ever. Nothing on `moved` distinguishes "I moved" from "I swapped", and there is no
"blocked" outcome; `before_moved` can refuse a move, so content *can* implement a queue, but it
has to know to.

**Workaround shipped.** The host places everyone explicitly at the start and on every spawn, and
Emberline's lanes are wide enough that the shuffling reads as a scrum rather than a bug.

**How bad.** Moderate. Nothing is wrong; it is undocumented behaviour that every real-time game
will meet in its first hour.

---

## 7. A battle ends the instant the board is empty, so a wave game needs a decoy to exist at all

**Trying to do.** Hold a line for forty-five seconds against waves on a timer.

**Expected from the docs.** "A battle ends when no party member is alive or no enemy is left
alive". Reasonable, and correct for a turn game.

**What happened.** In a wave game the board is empty every few seconds by design -- the keepers
clear one wave and the next is two seconds away. The moment the last one dies `Won` turns true,
`battle_end` is raised, every status that is not `persistent` is stripped, every card goes back to
its draw pile and `InBattle` is false. The first run of Emberline ended at **two seconds**, having
been won.

There is no setting for it, no "endless" flag, no way to suppress the end-of-battle check, and
nothing in `StartBattle` that keeps a battle open. Restarting the battle is not a workaround: it
wipes the statuses and cooldown state the hold is made of.

**Workaround shipped.** An enemy that is always there and can never be pointed at:

```
enemy "The Dark"
  hp 9999
  modify targetable: set 0
```

spawned before `StartBattle`, parked on rank 4 where no wave ever walks, and filtered out of
`Enemies` everywhere in the host and the front end so it never appears in a UI or an autopilot's
reckoning. It works, and `modify targetable: set 0` on an actor declaration is a nice piece of
language -- but it is a decoy propping the fight open, and every count, every `deal to enemies`
and every "is the board clear" question in the game has to remember it is there.

**How bad.** Severe, and the closest thing in this round to "this makes the game impossible". A
wave defence, a survival mode, a horde mode and an endless arena -- most of what people build on a
tick clock -- all need this or something like it, and nothing in the documentation hints that the
problem exists.

---

## 8. A test cannot say that an ability is not ready, or cannot reach

**Trying to do.** Test the two things Emberline is made of: that an ability is still on cooldown,
and that something is out of range.

**Expected from the docs.** language.md lists what a test cannot do, and one entry is "check that
a play was refused, since `play` fails the test when a card cannot be played". It names `play`.
`cast` has the same problem and is not mentioned.

**What happened.** `cast` fails the test when the ability is on cooldown (`could not be used:
NotReady`) or has nothing legal to aim at (`InvalidTarget`), so a cooldown and a reach rule can
only be tested from the side that succeeds. There is no `ready` predicate in content either:
`leader.is_ready(Bulwark)` is `runtime error: Unknown method 'is_ready'`.

`samples/corpus/dota2.tests.cantrip` already carries a comment about this ("`cast` fails a test
when an ability is not ready, so the reduction is shown the other way round"), so it is known; it
is just not in the list of what a test cannot do.

**Workaround shipped.** Three of Emberline's sixteen tests are written inside out -- two enemies,
one reachable and one not, `cast` with no target, then check which one it picked:

```
test "Backdraft reaches two ranks and no further"
  ...
  cast "Backdraft"
  expect enemy2.hp == 9
  expect enemy1.hp == 16
```

It works and it reads worse, and it cannot express "this ability is not ready" at all -- only
"three seconds later it is".

**How bad.** High, and higher in real time than in turns. A turn game's tests are mostly about what
an effect *does*; a real-time game's design is almost entirely about what you *may not do yet*,
and that half cannot be tested.

---

## 9. A real-time UI needs two numbers, and neither is documented

**Trying to do.** Draw a row of ability buttons with cooldown sweeps -- the entire interface of a
real-time game.

**What happened.** Two separate holes.

**Listing an actor's abilities.** From C# there is no documented way. csharp.md lists the zones as
"`Hand`, and likewise `Draw`, `Discard`, `Exhaust`, `Powers` and `Relics`"; none holds an ability.
`GrantAbility` is not in csharp.md at all, and the package's shipped XML documentation -- what an
IDE shows -- names only `GrantAbility`, `UseAbility` and `EntityDefinition.Abilities`.

I read the engine's public surface with reflection to find the answer, which is that an ability is
an entity in `Zones.Attached` with `Kind == EntityKind.Ability`:

```csharp
runtime.State.ZoneOf(who, Zones.Attached).Where(e => e.Kind == EntityKind.Ability)
```

`Zones.Attached` is not in csharp.md's list either. The Godot node does the right thing here --
`GetEntity(id)["abilities"]` is documented and returns exactly the ids a button row needs -- so
this is a gap in the C# guide rather than in the engine.

**Seconds left on a cooldown.** Neither surface has a documented answer. `IsReady` and `CanUse`
answer yes or no; nothing answers "1.4 seconds". The number does exist: once an ability has been
used it carries a stat called `ready_at`, the absolute tick it comes back at, and
`(ready_at - clock.Now) / ticksPerSecond` is the sweep. It is in no document, in C# or in
GDScript, and it **does not exist before the first use** -- `StatNames` on a fresh ability is
`cooldown` alone -- so a UI must treat "absent" as "ready" and not as "0 seconds left".

**Workaround shipped.** `Emberline.CooldownLeft` in C# and `_cooldown_text` in GDScript, both with
a comment saying the stat is undocumented.

**How bad.** High. A cooldown sweep is not a nicety; it is how a real-time game communicates at
all, and the only way to build one today is to find an undocumented stat.

---

## 10. The Godot real-time path has no caller anywhere in the repository

**Trying to do.** Drive the clock from Godot's frame loop.

**Expected from the docs.** godot.md's Real time section is thirteen lines: set `RealTime`, add a
`TickDriver` as `Driver` before the node enters the tree, both have a `TicksPerSecond`, `Running`
pauses, `MaxCatchUp` caps the catch-up, and a game may call `Tick(count)` itself.

**What happened.** It worked, first try, and it is well built -- see #17. But:

**Nothing else in the repository uses it.** `TickDriver` and `RealTime` appear in exactly four
files: the addon's own `CantripRuntime.cs` and `TickDriver.cs`, and two core unit test files about
periodic triggers. No demo scene, no headless Godot test, no GDScript smoke test, no sample. The
one path a Godot real-time game must take has, before this round, been run by nobody.

**Half the node's real-time surface is undocumented.** I had to read
`addons/cantrip/runtime/TickDriver.cs` to find:

| member | what it does | in godot.md? |
|---|---|---|
| `Ticked(count)` | a signal, emitted after each frame's ticks have run | **no** |
| `TotalTicks()` | ticks since the last reset | **no** |
| `DroppedTicks()` | ticks abandoned to `MaxCatchUp` -- time the game skipped | **no** |
| `Reset()` | clears the carried remainder and counters, "as after loading a save" | **no** |
| `Running`, `MaxCatchUp`, `TicksPerSecond` | | yes |

`Ticked` is the signal every real-time front end needs, because it is the only place a game may
act between ticks without polling in `_process`. `DroppedTicks` matters more: after a stall the
driver **abandons** ticks rather than replaying them, so the game's clock silently falls behind
wall time and a player on a slow machine plays a shorter fight. That is a defensible design and a
surprise to find in the source.

**Workaround shipped.** `realtime/godot/game/emberline.gd` uses `Ticked`, and
`realtime/godot/game/checks.gd` is the first automated coverage of the driver: it lets real
physics frames advance a real game for six seconds and checks that a Hollow closed three ranks in
them, then pauses the driver and checks the clock stopped.

**How bad.** High. The docs are accurate as far as they go and the code is good; the problem is
how little of both there is, and that a bug in this path would have been found by nobody.

---

## 11. Nothing tells a Godot game what time it is

**Trying to do.** Release a wave at second 12. Show "18 / 45 seconds".

**Expected from the docs.** The node has `GetTurn()` for a turn game. I expected the equivalent.

**What happened.** There is none. `CantripRuntime` exposes no clock read-out at all -- not a tick
count, not a seconds count, nothing. The nearest things are both wrong for the job:

- an event's `time` field, which only exists when something happened;
- `TickDriver.TotalTicks()`, which counts **the driver's** ticks since its last `Reset()`, not the
  game's clock. Restore a save taken at second 30 and the two disagree by 600 ticks.

From C# the game has the clock, because it made it: `clock.Now` is right there. From GDScript the
clock is inside the node and there is no door.

**Workaround shipped.** `emberline.gd` keeps `elapsed_ticks` of its own, adds the `count` from
every `Ticked`, and is careful to write it into its own save file beside the rules' save and put it
back on load. It works and it is a thing every real-time Godot game will have to invent.

**How bad.** Moderate to high. Cheap to fix -- one method -- and every single real-time Godot game
needs it.

---

## 12. `Engine.time_scale` does not work, so a Cantrip game cannot be sped up or slowed down

**Trying to do.** Run the hold ten times faster in CI, so the automated run does not take
forty-five wall seconds.

**Expected from the docs.** Nothing; but `Engine.time_scale` is how a Godot game runs fast, runs
slow or pauses, and the Real time section says only that the driver "advances the clock from
`_PhysicsProcess` only, in whole ticks, because the length of a rendered frame is not an input a
deterministic game can use."

**What happened.** `Engine.time_scale = 10.0` changes nothing: the run still took 45.5 seconds.
Reading `addons/cantrip/shared/TickAccumulator.cs` explains why, and explains it well --

> The frame's delta is deliberately ignored. A fixed step is fixed by definition, and using the
> measured delta instead would make the simulation depend on how long the last frame happened to
> take.

-- so the driver counts *physics frames* and converts them at its configured rate. Raising
`Engine.physics_ticks_per_second` does not help either: the accumulator divides by it. And a game
cannot raise `driver.TicksPerSecond` to go faster, because the runtime forces the driver onto its
own rate at `_Ready` (with a warning) precisely so `cooldown 6s` cannot mean two lengths of time.

So on the documented path there is **no game speed control at all**: no fast-forward, no
slow-motion, no bullet time, no "2x speed" option, no speeding a replay up. The escape hatch is the
one line in godot.md that reads like an afterthought -- "A game can also call `Tick(count)`
itself" -- which turns out to be the only way to make a real-time game run at any speed but one.

**Workaround shipped.** `emberline.gd` uses the driver when a person is playing and pumps
`rules.Tick(1)` from `_process` under `--emberline-auto`, which takes the CI run from 45.5 seconds
to 1.6. `checks.gd` covers the driver with real physics frames so the real path is still tested.

**How bad.** Moderate, but it will surprise people. The behaviour is deliberate and correct; that a
consequence this large is nowhere in the docs is the finding.

---

## 13. `cost` on an ability parses, lints clean and does nothing

**Trying to do.** Give an ability a price, so the game has an economy as well as cooldowns.

**Expected from the docs.** csharp.md says "An ability has no cost ... so it never answers
`NotEnoughEnergy` ... today", which is honest. Nothing in language.md's ability section mentions
`cost` either way, and nothing stops you writing it.

**What happened.**

```
ability "Costly"
  cost 2
  cooldown 1s
  effect:
    deal 3 to enemies
```

lints with 0 errors, 0 warnings and 0 notes. `CostOf` returns 2. Using it leaves energy at 3. The
designer gets a free ability and a number that reads like a rule.

The consequence for the genre is larger than the bug: **a real-time game has no resource at all.**
Cards need a turn to refill energy (see #14), and abilities cannot cost anything, so the only
limiter a real-time game has is the cooldown. Mana, rage, focus, heat, a charge that builds --
none of it can gate an action. Emberline is built around that limit; a game that needs a resource
cannot be.

**Also.** csharp.md's sentence "An ability has no cost **and does not settle its own target**, so
it never answers `NotEnoughEnergy` or **`InvalidTarget`** today" is half wrong. language.md says
the opposite, and language.md is right: `UseAbility(ability, null)` settles its own target from
the ability's `target` and `range` lines, and answers `InvalidTarget` when there is nobody in
reach. I relied on it in every test and in both front ends.

**Workaround shipped.** No ability in Emberline has a `cost`, and the design has no resource.

**How bad.** Moderate for the ignored clause -- it is `reference/FINDINGS.md` #5 in a new place --
and moderate for the doc contradiction, which is one sentence denying a feature that exists.

---

## 14. Half the status and resource vocabulary is turn-shaped and silently inert

**Trying to do.** Write a status that wears off.

**Expected from the docs.** Statuses gives four stacking modes and `decay ... on turn_end`, and
writing-content.md teaches `duration` first. Nothing marks any of it as turn-only, and the `clock`
setting is described as checking *units*.

**What happened.** Because `turn_start` fires once and `turn_end` never (#2), a whole half of the
language is quietly dead:

| written | under `clock ticks` |
|---|---|
| `stacking duration` / `refresh` | applied and **never expires** |
| `decay 1 on turn_end` | never decays |
| `until turn_end:` | never reverted |
| `next turn:` | never runs |
| `once per turn` | never resets after the first |
| energy, block, any `reset_on turn_start` resource | reset once at `StartBattle`, never again |
| the hand | drawn once at `StartBattle`, never again |

`lint` reports none of it, **even with `clock ticks` stated**. `apply Chill 3` on a tick clock is a
permanent Chill, with no error, no warning and no note. The one thing that does work is `for <n>s`,
which is checked and correct, so the rule a designer has to learn -- and learn from nowhere -- is
"every status in a real-time game must carry its own `for`".

CT325 shows the shape of the fix already exists: the linter knows the stated clock and checks
lengths against it. It just does not check machinery against it.

**Workaround shipped.** Every status in `realtime/content/statuses.cantrip` is `none` or
`intensity` and is always applied with a `for <n>s`; the file's header comment says why, because
nothing else would.

**How bad.** High. It is a large, silent, load-bearing surface, and the failure mode -- a debuff
that never comes off -- is a balance bug a designer will chase for a long time.

---

## 15. Periodic listeners are in lockstep when they start together, and there is no way to offset one

**Trying to do.** Stop a wave of three Hollows from acting on exactly the same tick, for ever.

**What happened.** `on every 2s:` is relative to **when the listener registered**, which is good
news and better than the alternative: an enemy spawned at tick 7 fires at 27, 47, 67, and one
spawned at tick 0 fires at 20, 40, 60. Firing does not drift either -- a listener whose due tick
falls inside a multi-tick `Tick(n)` fires at that call and keeps its original schedule, so
`Tick(3)` twenty times and `Tick(1)` sixty times give the same times. Both of those are exactly
right.

But two entities created in the same call are in phase for ever, and there is nothing in the
language to offset an interval: no jitter, no phase, no `on every 2s after 1s:`. A wave of five
drones spawned together shoots as one drone with five times the damage, which is not what anybody
means.

**Workaround shipped.** Emberline's schedule never spawns two things in the same second, which
hides the problem rather than solving it. A game that wants a squad would have to spawn its
members on consecutive ticks from the host, or give each a randomised `in <n>s:` warm-up.

**How bad.** Moderate. Easy to work around once you know; invisible until you watch a fight and
notice the damage arriving in slabs.

---

## 16. Small things, each cheap to fix

- **A content verb is called command-style, and the docs only ever show one argument.**
  `hold(leader, 1)` is `CT0010: Expected ')' but found ,` followed by two more errors, none of
  which mentions verbs. The answer is `hold leader 1`. language.md's only example is
  `shatter target`, which reads equally well either way, so nothing distinguishes them until a
  second parameter.
- **`Execute` throws on a parse error instead of answering.** Every other call on `CardRuntime`
  that can fail returns an `ActionResult`; `Execute` returns one too, but a typo in the statements
  is a `DslException` out of a call the host makes on a hot path. A real-time host executes strings
  constantly (#5), so this is the call most likely to be handed a bad string.
- **`Entity.Get` returns a `Num` that will not cast to `long`.** `(long)ability.Get("ready_at")` is
  `CS0030: Cannot convert type 'Cantrip.Num' to 'long'`. `GetInt` is the way, and is documented; a
  developer who wants the un-rounded value has to find the conversion themselves.
- **`GameState.Assign` throws `ArgumentOutOfRangeException` for a slot off the board**, rather than
  refusing the way `before_moved` and `StartBattleOn` refuse. It is undocumented, so this only
  bites someone who found it by reflection, but it is the one board call on the public surface.
- **A `hero`'s abilities are registered against whatever clock exists when `AddHero` runs.**
  `samples/corpus/dota2.tests.cantrip` already warns about this for `realtime 10` and a relic; it
  is true of heroes too, and every test in `realtime/content` puts `realtime 20` first because of
  it. The rule is real and correct; it lives in a comment in a sample rather than in language.md.
- **CT310 (`verb X is never used`) counts uses in content only.** A verb that exists for the host
  to call through `Execute` is reported as unused. It is a note, so `--warnings-as-errors` still
  passes, and the right answer for `loom` was to write a test that calls it -- which was worth
  doing anyway, so no harm done.
- **README.md's first line** says Cantrip is "a rules language and rules engine for the combat in
  single-player, **turn-based** games". A user looking for a real-time engine stops reading there,
  and a user who arrives anyway has no reason to expect the tick clock to be a first-class thing.
  If 1.0 promotes real time, that sentence is the first thing to change.

---

## 17. What was genuinely good

- **Save and restore is the best-behaved thing in the round.** A save taken mid-fight with the
  clock at tick 360, four cooldowns running, a `Scorched` two-thirds of the way through its
  second, a `for 3s` Bulwark part way through, and an `in 2s:` flare in the air came back into a
  brand-new runtime with an identical state hash, and both copies then played five more seconds
  identically. Then the same thing through the Godot node, into a fresh `CantripRuntime` that had
  only loaded its content. First attempt, both times, no surprises. `IGameClock.Restore` putting
  the clock back where it was is the detail that makes it work, and the decision to give it no
  default implementation (stability.md) is vindicated.
- **`on every <n>s:` is the right primitive.** It is relative to registration, it does not drift,
  it survives a save mid-interval, it takes filters and `once per`, and it works on a status, a
  relic and an enemy alike. Emberline's three enemies, its damage-over-time and its whole sense of
  pace are eight of these listeners. The engine's best real-time idea.
- **`in <n>s:` is the thing turns cannot do,** and it is three words. The Signal Flare lights a
  target now and drops its fire two seconds later on whatever is standing there; it survives a
  save; it reads as one effect. Nothing in a turn game can express it and nothing here had to be
  worked around.
- **`cooldown <n>s` and the `cooldown` modifier channel.** Five abilities on five different
  cooldowns, `CanUse` and `LegalTargets` answering for an ability exactly as they do for a card, so
  one targeting UI serves both, and `UseAbility` returning `NotReady` as a distinct answer rather
  than a failure. The one part of real time that is finished.
- **Range on an ability.** `range 2` on Backdraft and `range 5` on Ember Bolt, on a `facing` board
  where distance across the sides is `a.rank + b.rank + 1`, gave a whole game's tactical shape for
  two words of content. `GetLegalTargets` answers it, so the Godot front end greys out what is out
  of reach with no arithmetic of its own.
- **`modify targetable: set 0` on an actor declaration.** The workaround in #7 only exists because
  the language could express "this is on the board and can never be pointed at" in one line.
- **Determinism held everywhere.** Eight seeds, identical results on repeat, identical hashes
  across a save boundary, and `Tick(1)` sixty times indistinguishable from `Tick(60)`. Nothing in
  the tick clock is less deterministic than the turn clock, which is the thing that would have
  been most expensive to discover late.
- **The `realtime N` test verb and `tick N`.** Sixteen tests, no turns in any of them, sub-second,
  and every number in them was found by writing the test rather than by reasoning. `realtime 20`
  followed by `tick 40` is a good way to write about time.
- **The error messages, again.** `Player has no ability 'Backdraft'; use 'grant' first, or list it
  on the hero` and `Board "Hold" (3 lanes, 3 ranks) has no lane 0, rank 4` both said the fix.
- **The `TickDriver` itself.** Driving from `_PhysicsProcess` only, ignoring the frame delta,
  refusing to let the driver's rate disagree with the runtime's, and stopping itself after an
  exception rather than repeating the same failure sixty times a second -- four good decisions, all
  of them in the source with reasons. It deserves documentation and a test, not a rewrite.

---

## 18. Is real time ready to be called stable in 1.0?

**No.** Not because it is broken -- the engine underneath is better than the documentation around
it, and the two hardest things, determinism and saving a running clock, already work. It is not
ready because a 1.0 promise is a promise about a *surface*, and the real-time surface is half
missing and half wrong.

What "stable" would have to mean, and what stands in the way:

1. **The docs would have to describe it.** #1 alone disqualifies it. A developer cannot build
   against a guide that never mentions the mode.
2. **The docs would have to stop describing turn behaviour as universal.** #2 is a diagnostic
   message stating a rule the engine does not implement, and #13 is a guide sentence denying a
   feature that exists. Freezing an API means freezing what it is documented to do.
3. **The turn API would have to refuse.** `EndTurn`, `Pass`, `CanAct` and `ActiveMember` on a tick
   runtime should throw with a message naming `Tick`, the way `Tick` on a turn runtime already
   fails with a message naming turns. That is one afternoon, and it converts the single largest
   class of real-time mistake from silent to loud. It is also a breaking change, which is why it
   belongs before 1.0 rather than after.
4. **A wave game would have to be possible without a decoy.** #7 is a hole under most of the
   genre: survival, horde, endless arena, tower defence.
5. **The tooling would have to tell the truth.** #4 -- `sim` refusing a `clock ticks` folder with
   one sentence would be a strict improvement over what it prints now, and is a ten-line change.
6. **Lint would have to check machinery as well as units.** CT325 already knows the stated clock.
   A `move`, a `pattern`, a `phase`, a `stacking duration`, a `decay ... on turn_end`, an
   `until turn_end:` or a `next turn:` under `clock ticks` is dead content and should say so
   (#3, #14).
7. **CT326 would have to look inside listeners** (#3a), or the party guarantee 1.0 is making does
   not hold in a real-time party game.

A defensible 1.0 could ship with real time still marked experimental and lose nothing: no game
ships on it today, and the turn-based engine is where the value is. What would not be defensible
is promoting it to stable while `EndTurn` silently runs a turn in a game that has none.

**If I could make only one change before 1.0, it would be number 6:** teach the linter that a
stated `clock` decides which *declarations* are live, not just which units are legal. It is the
one change that converts the most silent wrong answers into errors at the moment they are written,
it needs no new API, it breaks no existing content, and it would have saved me more time in this
round than everything else on the list put together.
