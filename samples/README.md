# Samples

Worked `.cantrip` content, each folder with its tests. On each change, CI runs `test` over each of the four folders in `samples`, and `lint` with `--warnings-as-errors`, so a folder must stay free of warnings as well as errors.

| Folder | What it shows |
|---|---|
| [`basic`](basic) | Small examples of most features in one file, with a test for each: Fireball, Frozen and Kindling from the README, poison and other statuses, a content-defined verb, a real-time ability, and the Strike, Defend and Jaw Worm that the README's C# example and the [C# guide](../docs/csharp.md) use. |
| [`recipes`](recipes) | The content from [Writing content](../docs/writing-content.md). [`recipes/tutorial`](recipes/tutorial) holds the tutorial's finished files, and every other file is one recipe with its tests, such as [a boss that switches moves at half health](recipes/boss-switches-at-half-health.cantrip) or [a debuff that lasts N enemy turns](recipes/debuff-for-enemy-turns.cantrip). Some recipes use the tutorial's Strike, Defend, Poison or Bog Troll, so load the whole folder. |
| [`slice`](slice) | A small roguelite: a witch climbing a five-floor tower, fire against frost, with 17 cards, 4 relics and 5 enemies, among them a boss, the Archmage, that changes its moves at half health. The simulator below plays it. |
| [`corpus`](corpus) | Effects from nine existing games, re-created under our own names, one content file and one test file per game. [Coverage](../docs/coverage.md) says which the language writes directly, which need a workaround and which it cannot express yet. |
| [`godot/Cantrip.Demo`](../godot/Cantrip.Demo) | A Godot project, kept beside the addon rather than here, that plays a battle from GDScript. [`demo/battle.gd`](../godot/Cantrip.Demo/demo/battle.gd) builds a hand of card buttons and enemy panels that show their intents, tells what happened in a log at a steady pace, and answers a card's choice with its first option where a game would open a picker. It plays the content in its `content` folder, which has tests of its own. To run it, build it once with `dotnet build godot/Cantrip.Demo/Cantrip.Demo.csproj`, open the folder in the .NET edition of Godot 4.6 and press Play. CI builds it and has it play a battle by itself. [godot.md](../docs/godot.md) explains the addon it uses. |

For a first read, start with `basic` or the recipes. For a phased enemy and the tests that pin its intents, see the recipe above or the Archmage in [`slice/enemies.cantrip`](slice/enemies.cantrip) and [`slice/tests.cantrip`](slice/tests.cantrip).

## Testing and linting a sample

From the root of a clone of the repository, with the tool run from source:

```
dotnet run --project src/Cantrip.Cli -- test samples/basic
dotnet run --project src/Cantrip.Cli -- lint samples/basic
```

Put `samples/recipes`, `samples/slice` or `samples/corpus` in place of `samples/basic` for the others. `test` runs every test and prints PASS or FAIL for each; `lint` checks the files for mistakes a test might not reach. Both end with a count, and `lint` reports notes as well as errors and warnings: a note is information, not a failure. `lint` fails only on errors unless you add `--warnings-as-errors`, as CI does.

Load one folder at a time. The folders define some of the same names, such as `Strength`, so loading two together fails with error CT0110, "already defined".

In a project that has the tool installed, as the [quickstart](../docs/quickstart.md) sets up, the same commands are `dotnet cantrip test <folder>` and `dotnet cantrip lint <folder>`. `dotnet cantrip describe <folder>` prints the rules text of each definition, generated or written with `text:`, and `dotnet cantrip repl <folder>` runs single lines against a live battle. [The edit, lint and test loop](../docs/writing-content.md#5-the-edit-lint-and-test-loop) explains all four.

## Running the simulator

`src/Cantrip.Sim` plays whole runs of the slice with a bot and reports how they went. From the root of the repository:

```
dotnet run --project src/Cantrip.Sim -c Release -- --runs 500
```

A run is five floors: two battles, an elite or a rest, another battle and the Archmage. After each battle before the boss the bot is offered three cards and takes one, and beating the elite also gives a relic. The report gives the win rate, how many floors runs cleared, where runs ended, and a table each for encounters, cards and relics: how often a card was offered and taken, and how runs that took it did against runs that did not. Its last line says whether any run threw an error; if one did, the command exits with 1 and names the seeds to replay.

| Option | Default | What it does |
|---|---|---|
| `--runs N` | 500 | How many runs to play. |
| `--seed S` | 1 | The first seed. Runs use S, S+1, and so on, so the same options play the same runs. |
| `--watch SEED` | | Plays one run and prints every turn, every card played and every reward, instead of the report. |
| `--bot greedy` or `--bot random` | `greedy` | `greedy` looks one card ahead through the engine; `random` plays anything it can. |
| `--picks random` or `--picks rollout` | `random` | How the bot picks a reward card: at random, or by playing out the rest of the run with each offered card on a copy of the game. Random picks compare cards most cleanly; rollout picks are slower but show what a card is worth. |
| `--elite auto`, `always`, `never` or `rollout` | `auto` | Whether floor 3 is the elite fight or a rest. `auto` leaves it to the bot: the greedy bot fights at 60% hp or more, and the random bot at random. `rollout` plays out both and takes the better. |
| `--rollouts N` | 2 | Play-outs per option for `--picks rollout` and `--elite rollout`. |
| `--content PATH` | `samples/slice` | The content folder. It must define the cards of the starter deck and the enemies of the tower, which are in `Run.cs`; cards tagged `reward` are the offers. |
| `--help` | | Lists the options. |

For example, to follow one run turn by turn, then compare a random bot with the greedy one:

```
dotnet run --project src/Cantrip.Sim -c Release -- --watch 7
dotnet run --project src/Cantrip.Sim -c Release -- --runs 500 --bot random
```

The tower, the rewards and the rest between floors are C# in [`src/Cantrip.Sim/Run.cs`](../src/Cantrip.Sim/Run.cs). It is a worked example of carrying one player through several battles in one runtime, which [Winning, losing and several battles](../docs/csharp.md#winning-losing-and-several-battles) describes.
