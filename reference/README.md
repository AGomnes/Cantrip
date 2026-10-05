# The Drowned Chapel

The reference game for Cantrip 1.0: a small, whole roguelite, built with the library the way a
game would be, from the published docs and the samples.

A party of three descends seven floors of a flooded chapel: five fights, a shrine where a fourth
member can join, a vestry, rewards between them, and a boss that changes its mind at half health.
The party persists: hp, the deck, the relics, the gold, the Benediction on a member's soul and a
hero who did not get up.

| | |
|---|---|
| [`content/`](content) | The game, as `.cantrip`: a ruleset, two boards, three heroes with six abilities between them and a seventh the host grants the leader, thirteen cards, six statuses, six relics, five enemies of which the last is the boss, the Tooth two of them summon, 33 tests and a scenario |
| [`host/`](host) | A C# host that plays a whole descent headlessly, and the run above the battle |
| [`godot/`](godot) | The playable front end, a Godot project of its own |
| [`FINDINGS.md`](FINDINGS.md) | Fifteen things that went wrong or read badly while it was built, ranked by how much each would hurt a real developer, each with a **Fixed** or **Kept** line saying what happened to it; and a sixteenth section on what was genuinely good |

## Running it

```
dotnet run --project src/Cantrip.Cli -- lint reference/content --warnings-as-errors
dotnet run --project src/Cantrip.Cli -- test reference/content
dotnet run --project src/Cantrip.Cli -- sim reference/content

dotnet run --project reference/host -- --seed 7      # one descent, told as it happens
dotnet run --project reference/host -- --check       # the run CI checks, with the save test
```

In Godot, the project has to be assembled first, because Godot finds scripts by path inside the
project and an addon can only be placed, never referenced:

```
bash reference/godot/assemble.sh
dotnet build reference/godot/Cantrip.Reference.Godot.csproj
godot --path reference/godot
```

`assemble.sh` copies `godot/Cantrip.Demo/addons/cantrip` and `reference/content` into the project;
neither is in the repository twice, and both are gitignored there. A game outside this repository
does the same thing by unzipping a release into its project folder.

`godot` is whatever the Godot 4.6.x .NET edition binary is called where you unpacked it; the addon
is tested on 4.6.1 and 4.6.2, and CI runs 4.6.1. The third line plays the game in a window, since
`chapel.tscn` is the project's main scene. After the same first two, CI runs it with no window
instead, importing once because a fresh clone has no `.godot` folder:

```
godot --headless --path reference/godot --import
godot --headless --path reference/godot res://game/chapel.tscn -- --chapel-auto
godot --headless --path reference/godot res://game/checks.tscn
```

## What it exercises, and why it was built that way

- **A party of three, `turns: initiative`, `order: speed`.** `sides` would have been easier. A
  chapel fight is about who is standing where and who acts when, so one interleaved order over
  both sides is the rule the game wants, and it is the mode that forces a host into an
  `ActiveMember` loop where `Pass` on anyone else is refused. It is also the mode with the trap in
  it: the leader holds the party's hand, a member draws at its own step, and a leader with no
  `speed` draws last. The Acolyte's `speed 6` is the whole reason `resource "speed"` is declared.
- **A board with two aisles and two ranks**, `facing`, so a rank never means the same place on
  both sides. `Censer` prints `range 1..3`, `Pike` writes its own `target enemy where it.rank <= 0`,
  the Bell Warden's Toll takes a `range` in its move header, `Wade` and `Punt` move an ally,
  `Harpoon` drags an enemy forward, the Tidewalker's Drag shoves a hero back out of melee.
- **Cards and abilities together.** One hand and one energy pool, held by the leader; each hero has
  its own abilities on their own cooldowns, and any member can perform a play out of the party's
  hand.
- **Statuses, relics and telegraphed intents.** Guard is a taunt that re-aims a telegraph with no
  second roll. Benediction is `persistent`, so it is the one status a run carries between battles.
  Every enemy move says who it is going to hit with `at`.
- **Saving and restoring mid-run.** `--check` saves after the fourth floor, restores into a
  separate runtime and plays the rest of the run twice, comparing state hashes at every boundary.
  The Godot checks do the same through the node.
- **The run above the battle**, in C# in `host/Chapel.cs` and again in GDScript in
  `godot/game/chapel.gd`. Both are longer than the battle code they wrap, and
  [FINDINGS.md](FINDINGS.md) says what that cost.
- **Publishing.** This is the only application in the repository that publishes trimmed and with
  Native AOT, so it is what proves the engine survives both. `bash tools/publish-check.sh`
  publishes it each way with the trim and AOT analysers on and every one of their warnings an
  error, then plays eight whole descents, with a save and a restore in each, out of the binary
  that came out. CI runs it on every change; on a machine with no platform linker the AOT half is
  skipped, loudly, and the trimmed half still runs.
  [docs/stability.md](../docs/stability.md#publishing-a-game) says what each half is worth, and
  the one thing a published game has to do for itself: a source-generated serializer, which is
  the `ChapelJson` context in [`host/RunState.cs`](host/RunState.cs).

## The two scenes in the Godot project

| Scene | |
|---|---|
| `game/chapel.tscn` | The game. Run with `-- --chapel-auto` and it plays itself and quits, which is what CI does |
| `game/checks.tscn` | 31 checks of the node against this game's content: the board, the party, initiative, a taunt re-aiming an intent, a play by a named member, a status counter, a save restored into a fresh node, and a revive |
