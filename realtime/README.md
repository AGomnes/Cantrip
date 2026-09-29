# Emberline

The real-time game for Cantrip 1.0: a small, whole game built on the **tick clock**, the way a
game would be, from the published docs and the samples.

Three keepers hold a burning line for forty-five seconds against things that walk out of the dark.
Nothing in it takes a turn. The host advances a clock twenty times a second from its own fixed
timestep; the keepers' five abilities wait between one and six seconds each; the enemies act from
their own `on every 2s:` and `on every 3s:` listeners; a flare lights a target now and lands its
fire two seconds later; and a save taken mid-fight brings the clock, the cooldowns, the burn part
way through a second and the flare still in the air back with it.

| | |
|---|---|
| [`content/`](content) | The game, as `.cantrip`: a `clock ticks` ruleset, a board, two heroes, six abilities, four statuses, four enemies, two placement verbs and 16 tests |
| [`host/`](host) | A C# host that plays the hold headlessly, with the wave schedule, the frame loop and the save test |
| [`godot/`](godot) | The playable front end, a Godot project of its own, with its clock coming from the engine's physics loop through the addon's `TickDriver` |
| [`FINDINGS.md`](FINDINGS.md) | What hurt while building it. The other half of the deliverable |

## Running it

```
dotnet run --project src/Cantrip.Cli -- lint realtime/content --warnings-as-errors
dotnet run --project src/Cantrip.Cli -- test realtime/content

dotnet run --project realtime/host                  # one hold, second by second
dotnet run --project realtime/host -- --seeds 8     # eight holds, as a table
dotnet run --project realtime/host -- --check       # the run CI checks, with the save test
```

There is **no `scenario` here and no `sim` step**, because `cantrip sim` cannot play a real-time
game: it drives a tick runtime with `EndTurn` and never advances the clock, so every battle stalls
at the turn limit and every number in the report is wrong. [FINDINGS #4](FINDINGS.md) has the
experiment and the report it printed.

In Godot, the project has to be assembled first, because Godot finds scripts by path inside the
project and an addon can only be placed, never referenced:

```
bash realtime/godot/assemble.sh
dotnet build realtime/godot/Cantrip.Realtime.Godot.csproj
Godot_v4.6.2-stable_mono_win64.exe --path realtime/godot
```

`assemble.sh` copies `godot/Cantrip.Demo/addons/cantrip` and `realtime/content` into the project;
neither is in the repository twice, and both are gitignored there.

## What it exercises, and why it was built that way

A hold against waves was chosen over a duel or an arena for one reason: **it makes the clock the
subject**. A duel on a tick clock is a turn game played quickly, and would have hidden most of
what this round was for. A hold has to answer, every second, what is closing, what is in reach,
which of five cooldowns is worth spending now and which is worth saving for the next three
seconds — and it forces the host to own time, spawning, pacing and the ending, which is precisely
the surface a turn game never touches.

- **`ruleset clock ticks`**, so that `cooldown 6s`, `for 3s`, `in 2s:` and `on every 2s:` are
  checked at lint rather than hoped for, and a `2 turns` anywhere in the content is error CT325.
- **Five cooldowns competing.** 1s, 3s, 5s, 6s and 6s, deliberately not multiples of each other,
  so they come back at different moments and the player is never offered all of them at once.
- **A board where range decides everything.** Three lanes, five ranks, `facing`. Keepers hold rank
  0; waves walk in at rank 3 and close a rank at a time. `Ember Bolt` reaches the whole line,
  `Backdraft` two ranks, `Haul` two — so letting something get close is both the danger and the
  only way to use the heavy abilities. The Wisps never close at all, which is what punishes a
  player who only ever shoots what is nearest.
- **Enemies with no telegraph.** There are no turns, so there are no `move` blocks, no patterns,
  no phases and no intents: every enemy's whole behaviour is its own `on every <n>s:` listeners.
  [FINDINGS #3](FINDINGS.md) is about what that costs.
- **A party under ticks.** Two `hero` declarations beside the leader, each with its own abilities
  on their own cooldowns. [FINDINGS #2](FINDINGS.md) is about what `Pass`, `CanAct`, `ActiveMember`
  and `EndTurn` do on a tick runtime, which is not what the party design says they do.
- **Saving mid-fight**, in C# and again through the Godot node: the clock's position, every
  cooldown's `ready_at`, a `Scorched` burn part way through its second, a `for 3s` Bulwark part
  way through its three, and an `in 2s:` flare that has not landed yet. All of it comes back, hash
  for hash, and both copies play on identically. This was the best-behaved thing in the round.
- **The host's share.** `host/Emberline.cs` is the wave schedule, placing every spawn on the
  board, keeping the battle alive between waves, the cooldown read-out and the decision that the
  hold is over. None of it is in the library and most of it has no seam to hang on.

## The two scenes in the Godot project

| Scene | |
|---|---|
| `game/emberline.tscn` | The game, with its clock coming from `TickDriver` in the physics loop. Run with `-- --emberline-auto` and it plays itself and quits, which is what CI does |
| `game/checks.tscn` | 31 checks of the node on a tick clock: real time through the node, abilities and their cooldowns, `ready_at`, reach, a save restored into a fresh node with a delayed effect in the air, the driver advancing the clock from real physics frames, and pausing it |
