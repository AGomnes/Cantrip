# Samples

Worked `.cantrip` content, each folder with its tests. On each change, CI runs `test` over each folder in `samples`, and `lint` with `--warnings-as-errors`, so a folder must stay free of warnings as well as errors. Two of them also have a `scenario`, which CI plays with `sim`.

| Folder | What it shows |
|---|---|
| [`basic`](basic) | Small examples of most features in one file, with a test for each: Fireball, Frozen and Kindling from the README, poison and other statuses, a content-defined verb, a real-time ability, and the Strike, Defend and Jaw Worm that the README's C# example and the [C# guide](../docs/csharp.md) use. |
| [`recipes`](recipes) | The content from [Writing content](../docs/writing-content.md). [`recipes/tutorial`](recipes/tutorial) holds the tutorial's finished files, and every other file is one recipe with its tests, such as [a boss that switches moves at half health](recipes/boss-switches-at-half-health.cantrip) or [a debuff that lasts N enemy turns](recipes/debuff-for-enemy-turns.cantrip). Some recipes use the tutorial's Strike, Defend, Poison or Bog Troll, so load the whole folder. |
| [`abilities`](abilities) | A fight with no cards in it, as a turn-based roguelike has: two abilities on cooldowns, a bleed that ticks, and an enemy whose moves change when it is wounded, with tests for each. Everything a deckbuilder uses except the cards. [`sim.cantrip`](abilities/sim.cantrip) plays that fight many times. |
| [`slice`](slice) | A small roguelite: a witch climbing a five-floor tower, fire against frost, with 17 cards, 4 relics and 5 enemies, among them a boss, the Archmage, that changes its moves at half health. [`sim.cantrip`](slice/sim.cantrip) states the tower as a scenario, which `cantrip sim` plays hundreds of times. |
| [`corpus`](corpus) | Effects from nine existing games, re-created under our own names, one content file and one test file per game. [Coverage](../docs/coverage.md) says which the language writes directly, which need a workaround and which it cannot express yet. |
| [`godot/Cantrip.Demo`](../godot/Cantrip.Demo) | A Godot project, kept beside the addon rather than here, that plays a battle from GDScript. [`demo/battle.gd`](../godot/Cantrip.Demo/demo/battle.gd) builds a hand of card buttons and enemy panels that show their intents, tells what happened in a log at a steady pace, and answers a card's choice with its first option where a game would open a picker. It plays the content in its `content` folder, which has tests of its own. To run it, build it once with `dotnet build godot/Cantrip.Demo/Cantrip.Demo.csproj`, open the folder in the .NET edition of Godot 4.6 and press Play. CI builds it and has it play a battle by itself. [godot.md](../docs/godot.md) explains the addon it uses. |

For a first read, start with `basic` or the recipes. For a phased enemy and the tests that pin its intents, see the recipe above or the Archmage in [`slice/enemies.cantrip`](slice/enemies.cantrip) and [`slice/tests.cantrip`](slice/tests.cantrip).

## Testing and linting a sample

From the root of a clone of the repository, with the tool run from source:

```
dotnet run --project src/Cantrip.Cli -- test samples/basic
dotnet run --project src/Cantrip.Cli -- lint samples/basic
```

Put `samples/recipes`, `samples/abilities`, `samples/slice` or `samples/corpus` in place of `samples/basic` for the others. `test` runs every test and prints PASS or FAIL for each; `lint` checks the files for mistakes a test might not reach. Both end with a count, and `lint` reports notes as well as errors and warnings: a note is information, not a failure. `lint` fails only on errors unless you add `--warnings-as-errors`, as CI does.

Load one folder at a time. The folders define some of the same names, such as `Strength`, so loading two together fails with error CT0110, "already defined".

In a project that has the tool installed, as the [quickstart](../docs/quickstart.md) sets up, the same commands are `dotnet cantrip test <folder>` and `dotnet cantrip lint <folder>`. `dotnet cantrip describe <folder>` prints the rules text of each definition, generated or written with `text:`, and `dotnet cantrip repl <folder>` runs single lines against a live battle. [The edit, lint and test loop](../docs/writing-content.md#5-the-edit-lint-and-test-loop) explains all four.

## Simulating a sample

`samples/slice` and `samples/abilities` each hold a `scenario`: a deck, some fights in order, and whatever happens between them, played many times by a bot.

```
dotnet run --project src/Cantrip.Cli -- sim samples/slice
dotnet run --project src/Cantrip.Cli -- sim samples/slice --bot random
dotnet run --project src/Cantrip.Cli -- sim samples/abilities --watch 1
```

The report leads with what the content allowed, whichever bot played: a run that threw, with the seed to replay it; a battle that reached the turn limit; a card that was never playable; an enemy move that never fired. What it found holds whoever plays; what it did not find is bounded by what the bots reached, and the block says so. Under it comes a table per bot, for what that bot did with it.

Two bots play by default — `cautious` and `patient`, which looks a turn further ahead — and each plays every run, so the slice's 500 runs take about 30 seconds rather than about 14. `--bot cautious` plays one of them, and `--bot random` is eight times quicker again and is the one for fuzzing. `--watch SEED` plays one run and prints every statement, turn and play. [Simulating](../docs/simulating.md) covers the command and the bots, and says what the numbers under them do not mean.

The tables under each bot say where the hp went: what dealt the damage the enemies took, what tags those hits carried, and what took the player's hp. Those amounts are the engine's own — it raised every one of them — so they hold for anyone who made those plays. Which plays were made is still the bot's, and so is the mix: on the slice, Burn is 37.6% of what the cautious bot's plays dealt and 48.2% of what the patient bot's dealt. What both agree on is that no tick of Burn ever carried `attack`, so a modifier written on that tag would have reached none of it — which is the kind of thing a tag column is for.

Nothing a bot merely tried is in any of it. A bot that looks ahead plays its options through the engine and rolls them back, and over 200 runs of the slice that raises 1.18 million events against 91 thousand in the play that counted — thirteen times as many. The meter is switched off for the duration, or every number above would be an order of magnitude too big.
