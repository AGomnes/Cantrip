# Building the reference game on Cantrip: what hurt

Notes kept while building `reference/` -- *The Drowned Chapel* -- from the docs alone, as a user
would: `README.md`, `docs/language.md`, `docs/csharp.md`, `docs/godot.md`,
`docs/writing-content.md`, `docs/simulating.md`, `docs/coverage.md` and `samples/`.
Engine source was read only where noted, and each time that is a finding of its own.

Ranked by how much each would hurt a real developer.

**Every finding here has been read and acted on.** Each one now carries a **Fixed** or **Kept**
line saying what happened; the findings themselves are left as they were written, because they are
the record of why these things changed. The game in `reference/` no longer carries any of the
workarounds below: where one is quoted, the file it was quoted from now says the natural thing.

---


---

## 1. A runtime call from a `BattleEnded` handler crashes the game with a stack overflow

**Trying to do.** Give the party gold when a battle is won, from the Godot node's `BattleEnded`
signal -- the one signal whose name says a battle has just ended.

**Expected from the docs.** godot.md, Signals: "`BattleEnded(won: bool)` | The action that won or
lost the battle **has finished**". And Between battles gives the handler and the follow-up as two
functions, but says only "when the reward screen closes", which reads like presentation advice
rather than a rule. csharp.md's "Never call back into the runtime from `OnEvent`" is about
`OnEvent`, not about this signal.

**What happened.** The process dies:

```
Stack overflow.
Repeated 789 times:
   at Cantrip.GodotAdapter.CantripRuntime+RunLoop.AfterAction()
   at Cantrip.GodotAdapter.CantripRuntime.Act[Boolean](Func`1<Boolean>)
   at Cantrip.GodotAdapter.CantripRuntime.Execute(String, Int32, Int32)
```

Any call that goes through an action re-enters the handler, which calls again, for ever.
`reference/godot/game/reentry.tscn` is a fifteen-line reproduction, and it separates the two
cases exactly:

| called from inside `BattleEnded` | handler runs |
|---|---|
| `rules.GetTurn()` and other reads | once |
| `rules.Execute(...)`, and by the same path `Play`, `Pass`, `AddCard` | for ever, until the stack dies |

The first thing anybody does when a battle ends is hand out a reward, and the signal is called
`BattleEnded`.

**Workaround shipped.** The handler records the result and defers:

```gdscript
func _on_battle_ended(won: bool) -> void:
    _won_last = won
    call_deferred("_after_battle")
```

which then forces the headless self-play loop to yield a frame per step, since the run cannot
continue inside the handler.

**How bad.** The worst *failure mode* of the round, even though #2 is the worse bug: it is a hard
crash with no diagnostic, on the most obvious line of code a game will write, and nothing in the
docs forbids it. A read is fine and a write is fatal, which is the hardest kind of rule to learn
by accident.

**Fixed.** It works. The node recorded the end-of-battle transition *after* emitting the signal, so
a handler that acted came back round to a check that had not yet noticed the battle was over, and
told the game again, for ever. The transition is now written down the moment it is noticed, which
also means a handler that starts the next battle is recorded properly and that battle tells its own
end when it comes. `docs/godot.md` has an [Acting from a
signal](../docs/godot.md#acting-from-a-signal) section stating the rule, `docs/csharp.md` separates
it from the `OnEvent` rule it was being confused with, and `docs/stability.md` has lost the
limitation. `game/reentry.tscn` is deleted, as its note said it should be; `game/chapel.gd` hands
out the reward in the handler and its self-play loop no longer yields a frame per step.
## 2. `modify <channel> of <group>` matches the *target*, and the docs never say so

**Trying to do.** A relic that makes the whole party's holy cards hit for 2 more:

```
relic "Choirmaster's Baton"
  modify damage of party where tag:holy: +2
```

**Expected from the docs.** language.md, Modifiers, says the default scope is the anchor's
*controller*: "`damage`, `block`, `heal`, `draw` | what the anchor's controller **deals**". And of
`of`: "**`of` scope** replaces the default: the modifier applies when the value being computed
**belongs to** someone in the group." A relic belongs to the leader, a hero is its own controller,
so the bare form reaches the leader alone; `of party` reads as the obvious widening.

**What happened.** `of party` and `of allies` fire for *nothing the party does*. `of everyone` and
`of enemies` both fire when the party attacks an enemy. Pinned down, one relic per row, held by
the leader:

| written on the relic | the Warden hits an enemy | an enemy hits the Warden |
|---|---|---|
| `modify damage: +2` (bare) | no | no |
| `modify damage of party: +2` | no | no |
| `modify damage of allies: +2` | no | **yes** |
| `modify damage of enemies: +2` | **yes** | no |
| `modify damage of everyone: +2` | **yes** | yes |

So on the `damage` channel, `of <group>` is matched against the damage's **target**, while the
bare form is matched against the **source**. The two spellings that look like widenings of each
other are scoped to opposite ends of the same hit. `modify damage of party: +2` -- the most
natural line anyone will write in a party game -- is **silently inert**: no error, no warning, no
lint note.

`damage_taken of allies`, `max_hp of allies` and `max_hp of party` all work, because for those
channels the value really does belong to the target, which is why the trap stays invisible until
the one channel where it does not.

**Workaround shipped.**

```
relic "Choirmaster's Baton"
  modify damage of everyone where tag:holy, source:allies: +2
```

`of everyone` to make the query run at all, and `source:allies` to put back the side the `of` was
supposed to say. The natural spelling would have been `of party`.

**How bad.** Worst finding of the round. A wrong answer given quietly, in the declaration a
designer writes most, in exactly the game shape 1.0 is adding party support for. A shipped game
would have a relic that does nothing, and no way to find out but a test that guessed the number.

**Fixed.** An `of` group now names whoever the value **belongs to**, and which end of an action
that is the channel decides â the same end the bare form uses. So `of party` on `damage` is
"the party deals", `of party` on `damage_taken` is "the party takes", and the two spellings that
read as widenings of each other finally are. `docs/language.md` has the table. Two lines in
`samples/corpus` were written against the old behaviour and now say what they mean
(`modify damage of player where card:Shiv`, `modify damage of allies`), and the Baton in this game
is `modify damage of party where tag:holy: +2` with no `source:` propping it up. A group that can
never hold what the channel names â `modify damage of hand` â is now **CT336**, a warning.

---


---

## 3. The `cooldown` channel cannot be reached from a relic for a party member

**Trying to do.** A relic that halves every cooldown the party waits.

**Expected from the docs.** language.md, Modifiers, Cooldowns: "On a relic or a status that
shortens every ability its holder has".

**What happened.** `modify cooldown: x50%` on a relic shortens the **leader's** granted abilities
and nothing a `hero` owns. None of `of party`, `of allies` or `of everyone` reaches a hero's
ability either -- `of everyone` works on `damage` but not here. So the relic every roguelite has,
"your abilities recharge faster", cannot be written for a party at all.

Verified both ways: with no `hero` declared, `modify cooldown: x50%` on a relic does halve the
leader's own granted ability (3 turns becomes 2). Add a `hero`, give that hero the ability, and
the same relic does nothing.

**Workaround shipped.** Push it onto each member as a status, and have the relic hand the status
out:

```
status "Quickened"
  stacking none
  modify cooldown: x50%

relic "Bell Rope"
  on battle_start:
    apply Quickened 1 to party
```

That works, and costs a status the player sees in the bar and a designer has to remember not to
flag `persistent`, so that it is re-applied each battle.

**How bad.** Severe. Unlike #1 the failure is semi-loud (the ability simply is never ready), but
the documented sentence is true only for a party of one.

**Fixed, by #2.** On `cooldown` the value belongs to whoever is waiting for the ability, so
`modify cooldown of party: x50%` reaches every member's abilities. `Bell Rope` is that one line
now, and the `Quickened` status is gone.

---

## 4. Nothing in content can name a dead ally, so `revive` cannot be spelled

**Trying to do.** `ability "Last Rites"` -- bring a fallen hero back at 8 hp.

**Expected from the docs.** language.md, Death and revival: "`heal` refuses a dead target,
deliberately and permanently, so bringing one back is its own verb: `revive <who> [N]`". A verb
exists, so a card must be able to use it.

**What happened.** There is no way to *name* the argument.

| tried | result |
|---|---|
| `ability ... target ally` | `InvalidTarget`; the docs say "Needs a **living** ally" |
| `ability ... target any` | `InvalidTarget`; "Any **living** actor" |
| `revive allies.last` | `allies` excludes the dead: `allies.count` is 2 of 3 |
| `revive party.last` | the same |
| `choose 1 from everyone where zone:dead as fallen` | binds 0 candidates |

`zone:dead` is a documented zone (godot.md, Zones: "`dead` for an enemy that died, until the next
battle starts") and `everyone where zone:dead` finds nobody. The fallen are reachable only from
C#, where the host still holds the `Entity`. `revive Cantor 8` does work in a *test*, because a
test binds each `hero` line to its own name, which makes the gap look shallower than it is.

**Workaround shipped.** Revival is a **run** service, not a combat one: the Chapel's shrines call
`runtime.Revive(entity, hp)` between battles, from the roster the host keeps in C# anyway. No card
and no ability in the game revives.

**How bad.** Severe for the genre -- every party roguelite has an in-combat raise -- and the
clearest "cannot express at all" of the round. The natural spelling would have been a `target
fallen` word, or `fallen` as a group name beside `party` and `allies`.

**Fixed.** `fallen` is a group name beside `party` and `allies`: the dead of the running side, in
the order they fell. `revive fallen.first 8` and `choose 1 from fallen as who` both work, and
`Last Rites` in this game's deck is the first of those. C# gets `runtime.Fallen` and
`State.Fallen(team)`, GDScript gets `GetFallen()`, and the Chapel's shrine reads that instead of
walking its own roster. The roster is still there, because a run wants to know who it recruited and
when, but nothing needs it to find a corpse any more.

---


## 5. An enemy cannot put a card into the player's deck, and the clause that should say so is accepted and ignored

**Trying to do.** The Tidewalker's Seepage: a curse card into the player's deck, which is Slay
the Spire's Wound, Monster Train's Pyre-damage cards and Inscryption's decay.

**Expected from the docs.** language.md, Built-in verbs: "`create Card [N] [into zone]` (default
hand)", and `create` is listed as reading the clauses `into`, `to` and `onto`. So
`create Brine into discard to leader` reads as "make a Brine in the leader's discard pile".

**What happened.** The card is made in the **enemy's** discard pile, and the `to` clause is
silently ignored. From the trace, with `log created.first.controller.name`:

```
[verb] create  @ probe.cantrip:15:5
  [event] created {source=E1#2}
[log] ctrl E1
[log] zone discard
```

`to leader`, `to target`, `to enemies`, `to enemies.first` and `onto leader` all give the same
answer: controller E1, zone discard. That is worse than an error, because language.md is emphatic
that "a clause a verb does not read is error **CT323** -- at lint and at run -- because a dropped
clause is a card that reads as one thing and does another." `create` *does* read `to`; it just
does not read it for this.

**Workaround shipped.** Make the effect run as something the leader controls. The move applies a
status to the leader, and the status makes the card when it hears its own application:

```
status "Seeping"
  stacking none
  on status_applied(Seeping, target:owner):
    create Brine into discard

enemy "Tidewalker"
  move "Seepage":
    apply Seeping 1 to leader
```

It works, and the player sees a status they cannot act on appear and vanish for no reason. The
natural spelling would have been `create Brine into discard to leader` doing what it says.

**How bad.** High. It is a mechanic in three of the nine games docs/coverage.md re-creates, it is
not listed as a gap there, and the workaround is not discoverable -- nothing in the docs suggests
that who *runs* a `create` decides whose pile it lands in.

**Fixed: `to` works.** `into` is the pile and `to` is whose, which is what `to` means for every
other verb in the language; `onto` stays a second spelling of `into`. `create Brine into discard to
leader` is the Tidewalker's whole move now, and the `Seeping` status is gone. `copy` takes the same
pair. `to` naming a pile (`create Shiv 2 to hand`) is refused by name rather than left meaning two
things, because that ambiguity is what made the clause droppable in the first place.
## 6. `CreatePlayer` cannot give the leader a stat, and under `order: speed` that decides the game

**Trying to do.** `turns: initiative` with `order: speed`. Each `hero` declares its own `speed`.
The leader is made by `CreatePlayer(name, hp, maxEnergy)`: no fourth argument, and no declaration
it reads.

**What happened.** "An actor with no `speed` reads 0" (language.md, The turn), so the leader goes
**last** in the round. The leader holds the party's hand, and a member draws at its own step under
`initiative`, so the party's hand arrives *after* everyone else has already acted. The first round
of every battle is played with an empty hand, silently. The two sentences that cause that are
three sections apart and neither points at the other.

**Workaround shipped.** Declare `speed` as a resource so it can be written at all, then write it
from the host:

```
resource "speed"
  min 0
  max 99
```
```csharp
runtime.CreatePlayer("Acolyte", hp: 40, maxEnergy: 3);
runtime.Execute("speed = 6");   // the only documented way to put a stat on the leader
```

**How bad.** High. `Execute` is documented for "a rest: any statements, run as the player", not as
the way to finish creating the player, and a reader of csharp.md would not find it. The natural
spelling would have been `hero` working for the leader too, or `CreatePlayer` taking the name of a
`hero` declaration.

**Fixed.** `runtime.SetStat(leader, "speed", 6)` -- a typed call that does exactly what content's
`speed = 6` does, bounds and `<stat>_changed` included -- with `ChangeStat` beside it for
`gain`/`lose`. It is documented in the party table in `docs/csharp.md`, next to `CreatePlayer`
rather than three sections away, and the Godot node has both. That also answers 7(c): the shop
spends gold with `ChangeStat(Leader, "gold", -amount)` instead of interpolating a statement.

---


---

## 7. What the run above the battle cost

This is the part the brief most wanted measured, so here are numbers first. The run is written
twice, once in C# and once in GDScript, against the same content:

| | lines | what it does |
|---|---|---|
| `host/Chapel.cs` | 300 | floors, rewards, shrine, shop, roster, gold, save |
| `host/RunState.cs` | 90 | the run's own state and its own random stream |
| `host/Tactician.cs` | 120 | the player, so CI can run a descent |
| `godot/game/chapel.gd` | 520 | the same run again, plus the screen |
| `content/*.cantrip` | 480 | the whole game: heroes, cards, statuses, relics, enemies, tests |

**The division is right.** Nothing in the run code wanted to be content and nothing in the content
wanted to be code. A map is not a rule, and writing `battle Crawler` in a scenario next to
`heal 12` is already as far as a rules language should go. The `scenario` block reaching exactly
that far -- statements between fights, and no map -- is a good line.

**Four things cost real effort, all of them about the boundary rather than the split.**

**(a) The roster.** A fallen hero is in none of `party`, `allies`, `GetParty()`, `GetAllies()` or
`State.Actors`. It is in the `dead` zone, which content cannot query (#4) and which no C# call
enumerates either. So the run has to keep its own list of member ids from the moment it creates
them:

```csharp
private IEnumerable<Entity> Fallen()
{
    foreach (RunState.RosterEntry entry in run.Roster)
    {
        Entity? member = runtime.State.Find(entry.Id);
        if (member != null && member.IsDead) yield return member;
    }
}
```

That list has to be saved, versioned and kept in step with the snapshot. It exists only because
there is no `runtime.Fallen` and no `State.Actors(Team.Player, includeDead: true)`.

**Fixed.** `runtime.Fallen`, `State.Fallen(team)` and the node's `GetFallen()`, and `fallen` in
content. The roster is still saved, because it is the run's own record of who joined and when, but
nothing needs it to find a corpse.

**(b) Two saves that must be written as one.** `runtime.Capture()` holds the battle, the party,
the deck, the relics and the gold. It does not hold which floor you are on, which rewards have
been offered or the run's random stream, and it cannot: Cantrip has no idea what a run is. So the
file is a pair, and every write and every read has to keep them together:

```csharp
public ChapelSave Save() => new ChapelSave
{
    Fingerprint = content.Fingerprint,
    Run = run,          // floor, roster, the run's own rng
    Game = runtime.Capture(),
};
```

This is the right shape and the docs never show it. csharp.md's `SaveFile` has `Fingerprint` and
`Game` and nothing else, which is a save for a game with no run above the battle -- that is, for
no roguelite at all. One extra field in that example, with one sentence, would have saved an hour
of deciding whether I was missing an API.

**Fixed.** csharp.md's `SaveFile` has a `Run` field and three sentences saying what belongs in it
and why the file is a pair.

**(c) Gold is on the leader, and the purse is the run's.** `gold` is a built-in resource, a relic
earns it inside a battle (`on killed(target:enemies): gain 2 gold`) and the shop spends it outside
one. That is a genuinely good seam -- the run gets a currency that saves and restores for free.
But there is no C# call that changes a stat, so the shop writes DSL text from C#:

```csharp
private void Spend(int amount) => runtime.Execute($"lose {amount} gold");
```

A string-interpolated statement, unchecked until it runs, for subtracting an integer. I would be
embarrassed to show that to another developer, and it is the only way the docs offer.

**Fixed with #6.** `runtime.ChangeStat(Leader, "gold", -amount)`, and `SetStat` for an outright
write. The seam itself was right and is unchanged.

**(d) Removing and upgrading a card.** godot.md has `RemoveCard` on the node and a worked
`upgrade_card`. `CardRuntime` has no `RemoveCard`, and csharp.md gives instead:

```csharp
runtime.Execute("destroy target", target: wound);
```

So the C# half and the Godot half of the same operation are a typed call on one side and a parsed
string on the other, and only the Godot one is written up as a recipe. The upgrade is then two
calls and a naming convention (`"Censer" + "+"`), reimplemented in both languages.

**Fixed.** `CardRuntime.RemoveCard(card)`, the same call the node has had all along, written up in
csharp.md's Between battles beside the upgrade it is half of. The naming convention is still the
game's, which is right: Cantrip does not know what an upgrade is.

**What it did not cost.** Carrying the party between battles is free and exactly right: hp, max
hp, the deck, exhausted cards, relics, `once per run` limits and `persistent` statuses all come
across with no code at all, and `Benediction` -- a status flagged `persistent` -- turned out to be
the cleanest run-level reward in the game. `StartBattle` after `SpawnEnemy` is the whole of "the
next encounter". That half of the boundary is finished.


---

## 8. A board can strand a fight with nothing in reach, and nothing sees it coming

The starter deck's melee card printed `range 1..2`. On a 2x2 `facing` board the far aisle's back
rank is 3 steps from our front rank and 4 from our back one, so a summoned Tooth standing at
(1,1) was out of reach of every card and every ability in the deck. The party could not kill it,
it could not kill them, and the Godot self-play ran to turn 576 before its step cap stopped it.

Nothing caught it beforehand:

- **Lint** knows the board's depth -- it reports CT327 for a rank that could never be a slot --
  but says nothing about a `range` that cannot span the board its own side stands on.
- **`cantrip sim`** would have caught it as a stall, and did not, because the scenario bought
  Aspersion (which prints no range) by fiat, while the deck a player actually held did not.
- **The tests** all passed: every one of them puts the enemy in reach, because that is what you
  write when you are testing that the card works.

**Workaround shipped.** `range 1..3` on Censer, with the arithmetic written out in a comment so
the next person does not have to redo it, and a test that pins both ends (`distance` 3 reachable,
4 not).

**How bad.** Moderate, and it will happen to everybody who ships a board. A lint note of the shape
"`range 1..2` cannot reach rank 1 of lane 1 from any slot on your side of board Chapel" would
have cost me nothing to read and saved the whole detour.

**Kept.** Deferred as design work rather than a fix: what "cannot reach" means depends on where a
side may stand, which a summon changes mid-fight. The workaround stays, arithmetic and all.

**Written down in packaging.** The lint note is still not written, so this is still a detour
waiting to happen, but it is a detour with a map now.
[troubleshooting.md](../docs/troubleshooting.md#which-refusal-was-it)
gives the cross-side arithmetic (`a.rank + b.rank + 1`), says outright that `range 1..2` on a board
two ranks deep can be unable to reach half the enemies for ever with nothing warning, and
[The fight never ends](../docs/troubleshooting.md#the-fight-never-ends) lists it first among the
four things a stall usually turns out to be.

## 9. `InvalidTarget` is one word for four different refusals

`play Censer by Warden on enemy2` answers `InvalidTarget` and nothing says why. On a `facing`
board the cross-side term is `a.rank + b.rank + 1`, so on a 2x2 chapel our back rank to their back
rank is three steps and `range 1..2` cannot make it. The rule is right and the board is good, but
`InvalidTarget` is the same word for "out of reach", "taunted away", "the card's own `where`
excluded it" and "nobody alive", and `GetLegalTargets` is the only way to tell them apart. A
designer testing a card sees one word, and a `test` will not even let them assert the refusal
(language.md, What a test cannot do).

Lint already reports CT327 when a written rank could never be a slot, so it has the board's depth.
A `range` a card can never satisfy from where its own side stands is the same class of mistake.

**How bad.** Moderate. Recoverable, but it cost the first hour of this build.

**Kept.** Deferred: splitting `InvalidTarget` adds vocabulary to a frozen enum, and which refusals
are worth telling apart is a question the four answers here do not settle.

**Documented in packaging, which is the other half of it.** The enum stays one word and the enum
member now says so in its own documentation: `InvalidTarget` names all four refusals, says that
`LegalTargets` is how they are told apart, and says that on a board it is most often `range`. That
reaches a reader through the generated [API reference](../docs/api/Cantrip.md#actionresult) and
through [troubleshooting.md](../docs/troubleshooting.md#a-card-or-ability-will-not-play), which
turns "call `GetLegalTargets`" into four cases and what each one means. The hour this cost is the
hour a reader now does not spend; the word is still one word.

**Reopened and done at 1.0.** The freeze audit called it now-or-never rather than deferrable:
adding these later would *narrow* what `InvalidTarget` means, so a game that wrote
`if (result == InvalidTarget) ShowOutOfRangeHint()` would have gone on compiling and quietly
stopped firing. `ActionResult` has `OutOfRange` and `NoTarget` now (reach, and a side with nobody
on it), and `InvalidTarget` is left meaning "somebody the action will not take", which is a taunt
or the action's own `where`. Telling *those two* apart is still `LegalTargets`, and a
`WhyNotTargetable` would be additive, so it can arrive in a 1.x release.

---


---

## 10. The state hash is recommended three times and named nowhere

csharp.md tells you to compare state hashes ("so for a party of one `Pass` is `EndTurn`, to the
turn number and the state hash"), stability.md says the test suite does it, godot.md gives the
node's `StateHash()`. **csharp.md never says what to call.** `runtime.State.Hash()` does not
exist; searching the guide for "hash" finds only prose.

I found it by reading `godot/Cantrip.Demo/addons/cantrip/runtime/CantripRuntime.cs` -- the first
time in this build that the docs failed and the source had to answer:

```csharp
public string StateHash() => EnsureRuntime().State.ComputeHash().ToString("x16", CultureInfo.InvariantCulture);
```

`State.ComputeHash()` it is. The reference host now carries that line with a comment saying where
it came from.

**How bad.** Moderate. It is one table row in csharp.md's "What a battle screen reads", and it is
the single most useful call for anyone testing that their save works.

**Fixed.** It is that table row now, naming `runtime.State.ComputeHash()` and how the node prints
it. The comment in the reference host saying where the call came from is gone.

---

## 11. The Godot node cannot choose a board

`CardRuntime.StartBattle(board: "Chapel")` picks a board. The node's
`StartBattle(shuffle: bool, draw_opening_hand: bool)` has no third argument, so a Godot game with
more than one `board` declared is stuck with the first one declared and cannot say which fight is
fought where. godot.md does not have a Boards section at all; the word appears twice, once as the
`zone` value `"board"` and once as the `lane`/`rank` keys in the entity dictionary.

Godot is the primary engine and the board is the newest feature. The reference game gets away
with it by declaring exactly one board.

**How bad.** Moderate for a Godot game with a train or a corridor, invisible otherwise.

**Fixed.** `StartBattleOn(board, shuffle, draw_opening_hand)` -- a method of its own, not a third
argument, because a C# default is not a default in GDScript and a third parameter would have broken
every game that already calls `StartBattle`, at parse time. `docs/godot.md` has a
[Boards](../docs/godot.md#boards) section now. This game declares a second board, the Nave, and both
halves of it name one: `StartBattle(board:)` in C#, `StartBattleOn` in GDScript. `board` is also a
`scenario` verb now, so `cantrip sim` plays the fight on the board the game plays it on.

---

## 12. `GetStat` does not answer a status's counter, and the node has no `CounterOf`

In content, `Warden.Fervour` is the number of Fervour stacks. In C#, `entity.CounterOf("Fervour")`
is documented. In GDScript, `GetStat(warden, "Fervour")` does not answer it -- "0 both when the id
is unknown and when the entity has no such stat" is true, and a status is not a stat -- and there
is no `CounterOf` on the node. The only route is to walk the entity dictionary:

```gdscript
func _counter(id: int, status: String) -> int:
    for entry in rules.GetEntity(id)["statuses"]:
        if entry["name"] == status: return entry["counter"]
    return 0
```

`GetEntity` builds every stat, every tag and every status to answer one number, in a function a
status bar calls once per member per frame. It is written up, in the `statuses` table, so this is
an ergonomics gap rather than a wrong answer -- but a check I wrote from the DSL's own vocabulary
failed silently against 0, which is exactly the shape of bug that ships.

**How bad.** Moderate. Every Godot game with statuses writes this helper.

**Fixed.** `CounterOf(entity_id, status)` on the node, which is content's `Warden.Fervour` and
C#'s `entity.CounterOf` under a third name rather than a fourth shape. `GetStat`'s row in the
methods table now says a status is not a stat and points at it. `game/checks.gd` has lost its
helper.


---

## 13. There is no way to share a listener between declarations

Every fight in the chapel could run for ever, because our side has heals and block and theirs has
a fixed number: `cantrip sim` found three battles at the turn limit on the first run. The fix is
the standard one -- the enemies get stronger -- and it is two lines:

```
  on every 3 turns:
    gain 1 Fervour
```

Those two lines are now copied into six declarations, because a `.cantrip` file has no base
declaration, no template, no mixin and no "apply this to every enemy". The alternatives I tried:

- A relic. Relics belong to the leader, and there is no enemy equivalent.
- A status applied at battle start. Nothing in content runs at battle start except a listener on
  something that already exists, so it needs an owner, and the only always-present owner is the
  leader -- from which nothing can reach the other side's cooldowns or stats generally (#2, #3).
- The host. `ApplyStatus` on every enemy after `StartBattle` works, and then the scenario cannot
  do it, so `cantrip sim` no longer plays the game the host plays.

**How bad.** Low today, high at scale. Six copies is fine; a game with forty enemies and three
rules that apply to all of them is 120 lines that must not drift.

**Kept.** Deferred: a base declaration, a template and a mixin are three different languages, and
picking one is design work rather than a fix. The six copies are still six copies.

---

## 14. Lint reports CT306 for a listener that cannot re-trigger itself

```
relic "Drowned Coin"
  on killed(target:enemies):
    gain 2 gold
```

> `info CT306: These listeners can re-trigger each other: killed  killed.`

One listener, paired with itself, whose body is `gain 2 gold`. It is a note rather than a warning,
so `--warnings-as-errors` still passes, but a folder where several relics earn gold on a kill
prints one of these each and teaches the reader to ignore CT306.

**How bad.** Low, and cosmetic -- but noise in a linter is how a real warning gets missed.

**Fixed.** `gain`, `lose` and `change` cover a resource and a status with one word, so the verb
table has to list a death and a status applied for all three; CT306 now reads the word the line
actually names, and only `hp` kills. `reference/content` lints clean, and so does Inscryption's
`on killed: gain 1 bones` in `samples/corpus`.

## 15. Small things, each cheap to fix

**All but the last are fixed**; each entry says what happened.

- **`RuntimeOptions.Seed` is `ulong`.** csharp.md's `new RuntimeOptions { Seed = 12345 }` compiles
  because it is a literal. `Seed = seed` with an `int` variable does not, and godot.md says "Any
  64-bit number is a seed of its own, 0 and negative ones included", which reads like `long`. Every
  host that seeds a run from a number it computed writes `(ulong)(uint)seed`.
  **Fixed:** it is `long`, which is what the Godot node's own export already was. The reference
  host writes `Seed = seed`.
- **`CardRuntime.Player` is nullable.** csharp.md writes `runtime.Player` bare in five places. In a
  project with `<Nullable>enable</Nullable>` -- which this repository's own
  `Directory.Build.props` sets, with `WarningsAsErrors=nullable` -- every one of those is a build
  error. The reference host has a `private Entity Leader => runtime.Player!;` for exactly this.
  **Fixed:** `Player` is not nullable. Before `CreatePlayer` it throws, with a message naming the
  call to make, and `HasPlayer` is the question for the few lines where that is genuinely in doubt.
  The reference host's `Leader` has lost its `!` and its comment.
- **`created` is a group, and a group answers 0 for `zone` and `controller`.** `log created.zone`
  printed `0`, and `created.controller.name` failed with `` `0` has no property `name` ``. The
  documented group members are `count`, `size`, `length`, `first`, `last`, `empty`, `any`, `lane`,
  `rank` and stats, so `zone` fell through to "a stat nothing has". `created.first.zone` is right
  and reads `discard`. The fall-through to 0 for an unknown member on a group is the same trap
  language.md's "One is a group of one" paragraph describes fixing for `.first`.
  **Fixed:** `zone`, `controller` and `name` answer for a group the way `lane` and `rank` already
  did -- from the first of them.
- **`cantrip sim` reports an `unplayable` card as a finding.** "1 card(s) were held but never
  playable: Brine" is the headline block, above the bots' tables, for a card tagged
  `curse, unplayable` -- which is never being playable on purpose. The one report in the tool that
  is meant to hold whoever plays now has a line in it that will never go away.
  **Fixed:** a card tagged `unplayable` is left out of that finding. Everything else about the
  block is unchanged.
- **`cantrip sim` lowercases a bare enemy name in its battle labels.** `battle "Bell Warden",
  Tidewalker` prints as `Bell Warden + tidewalker`, while the hp table below it says `Tidewalker`.
  The label appears to use the raw token rather than the definition's name.
  **Fixed:** the label spells each enemy as its declaration does. (The guess was right: a bare name
  after a comma arrives as a clause keyword, and the parser lower-cases those.)
- **Kept: `test` cannot assert a refusal.** Deferred with #9, which is the same gap seen from the
  other side. language.md says so plainly ("check that a play was refused,
  since `play` fails the test when a card cannot be played"), and on a board it bites harder than
  it does elsewhere: the whole point of `range` and `target ... where` is what they *exclude*, and
  the only way to test exclusion is to play the card with no target and check which one it picked.
  Three of this game's board tests are written that way and read worse for it.
  **Packaging wrote it down** rather than fixing it:
  [troubleshooting.md](../docs/troubleshooting.md#a-card-or-ability-will-not-play) says a `test`
  cannot assert a refusal and names playing the card with no target as what people do instead.

---

## 16. What was genuinely good, and should not be touched

- **The tests.** 28 content tests, written in the content, run in under a second, and every one of
  them found something. `hero Warden` binding the member to its own name, so `expect Warden.hp ==
  30` reads the live actor, is the single nicest thing in the language. A `--trace` on a failure
  that shows the event tree with `[log]` lines in it answered every "why" I had in one command.
- **`cantrip sim`.** It found the free unlimited nuke, the endless fight and the stall, before a
  person would have. The report's insistence on the difference between "what the content allows,
  whoever plays" and "what this bot did" is honest in a way tooling usually is not, and the
  `hp went` tables told me which card was carrying the deck in one glance.
- **Telegraphed targets.** `move "Clutch" at lowest hp enemies:` plus a Taunt written as an
  ordinary `targetable` modifier, and `intent_target` recomputed on every read, is the whole of a
  party game's readability for nine words of content. It worked first time, in the DSL, in C# and
  through the Godot node, and `DescribeIntent`'s `line` -- `"Clutch -> Cantor: Deal 6 damage and
  apply 1 Soaked."` -- is a finished UI string.
- **The board.** Two axes, five selectors, movement as a member write, and `until` reverting a
  move because a slot is two integers. `target.rank = 0` being the whole of a hook, and a swap
  being its own inverse, is a design decision that paid for itself three times in this game.
- **Carrying a party between battles.** hp, max hp, the deck, exhausted cards, relics,
  `once per run` limits and `persistent` statuses all cross a battle boundary with no code. A
  status flagged `persistent` turned out to be the cleanest run-level reward I could write.
- **Save and restore.** Eight seeds, saved mid-run, restored into a separate runtime, played to
  the end twice, identical state hash every time, first attempt, no surprises. Entity ids surviving
  a restore is what makes a host-side roster possible at all.
- **The error messages.** `` Player has no ability `Harpoon`; use `grant` first, or list it on the
  hero `` told me the fix in the message. `CT326` refusing `player` inside an enemy move, with the
  reasoning, stopped me writing the exact bug it describes.
