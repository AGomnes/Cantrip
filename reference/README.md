# The Drowned Chapel

The reference game for Cantrip 1.0: a small, whole roguelite, built with the library the way a
game would be, from the published docs and the samples.

Three heroes descend seven floors of a flooded chapel. Five fights, a shrine, a vestry, rewards
between them, and a boss that changes its mind at half health. The party persists: hp, the deck,
the relics, the gold, the Benediction on a member's soul and a hero who did not get up.

| | |
|---|---|
| [`content/`](content) | The game, as `.cantrip`: a ruleset, a board, three heroes with abilities, thirteen cards, six statuses, six relics, five enemies and a boss, 28 tests and a scenario |
| [`host/`](host) | A C# host that plays a whole descent headlessly, and the run above the battle |
| [`godot/`](godot) | The playable front end, a Godot project of its own |
| [`FINDINGS.md`](FINDINGS.md) | What hurt while building it. The other half of the deliverable |

## Running it

```
dotnet run --project src/Cantrip.Cli -- lint reference/content --warnings-as-errors
dotnet run --project src/Cantrip.Cli -- test reference/content
dotnet run --project src/Cantrip.Cli -- sim  reference/content

dotnet run --project reference/host -- --seed 7      # one descent, told as it happens
dotnet run --project reference/host -- --check       # the run CI checks, with the save test
```

In Godot, the project has to be assembled first, because Godot finds scripts by path inside the
project and an addon can only be placed, never referenced:

```
bash reference/godot/assemble.sh
dotnet build reference/godot/Cantrip.Reference.Godot.csproj
Godot_v4.6.2-stable_mono_win64.exe --path reference/godot
```

`assemble.sh` copies `godot/Cantrip.Demo/addons/cantrip` and `reference/content` into the project;
neither is in the repository twice, and both are gitignored there. A game outside this repository
does the same thing by unzipping a release into its project folder.

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
- **Saving and restoring mid-run.** `--check` saves after the third floor, restores into a
  separate runtime and plays the rest of the run twice, comparing state hashes at every boundary.
  The Godot checks do the same through the node.
- **The run above the battle**, in C# in `host/Chapel.cs` and again in GDScript in
  `godot/game/chapel.gd`. Both are longer than the battle code they wrap, and
  [FINDINGS.md](FINDINGS.md) says what that cost.

## The two scenes in the Godot project

| Scene | |
|---|---|
| `game/chapel.tscn` | The game. Run with `-- --chapel-auto` and it plays itself and quits, which is what CI does |
| `game/checks.tscn` | 30 checks of the node against this game's content: the board, the party, initiative, a taunt re-aiming an intent, a play by a named member, a save restored into a fresh node, and a revive |
| `game/reentry.tscn` | Not a test. A fifteen-line reproduction of the crash in FINDINGS.md: a runtime call made from a `BattleEnded` handler recurses until the process dies |
