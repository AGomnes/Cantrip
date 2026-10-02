# Samples

Worked `.cantrip` content, each folder with its tests, and one whole Godot project. On each change, CI runs `test` over each folder in `samples`, and `lint` with `--warnings-as-errors`, so a folder must stay free of warnings as well as errors. Four of them also have a `scenario`, which CI plays with `sim`.

In Godot, start with the demo project in the first row: it is the addon running, with a hand of card buttons and enemies that show their intents. The content folders under it are engine-free. The same `.cantrip` files run in the demo, in the dock, in `dotnet cantrip` and in a console app.

| Folder | What it shows |
|---|---|
| [`godot/Cantrip.Demo`](../godot/Cantrip.Demo) | A Godot project, kept beside the addon rather than here, that plays a battle from GDScript. [`demo/battle.gd`](../godot/Cantrip.Demo/demo/battle.gd) builds a hand of card buttons and enemy panels that show their intents, tells what happened in a log at a steady pace, and answers a card's choice with its first option where a game would open a picker. It plays the content in its `content` folder, which has tests of its own. To run it, build it once with `dotnet build godot/Cantrip.Demo/Cantrip.Demo.csproj`, open the folder in the .NET edition of Godot 4.6 and press Play. CI builds it and has it play a battle by itself. [godot.md](../docs/godot.md) explains the addon it uses. |
| [`basic`](basic) | Small examples of most features in one file, with a test for each: Fireball, Frozen and Kindling from the README, poison and other statuses, a content-defined verb, a real-time ability, and the Strike, Defend and Jaw Worm that the README's C# example and the [C# guide](../docs/csharp.md) use. |
| [`recipes`](recipes) | The content from [Writing content](../docs/writing-content.md). [`recipes/tutorial`](recipes/tutorial) holds the tutorial's finished files, and every other file is one recipe with its tests, such as [a boss that switches moves at half health](recipes/boss-switches-at-half-health.cantrip) or [a debuff that lasts N enemy turns](recipes/debuff-for-enemy-turns.cantrip). Some recipes use the tutorial's Strike, Defend, Poison or Bog Troll, so load the whole folder. |
| [`abilities`](abilities) | A fight with no cards in it, as a turn-based roguelike has: two abilities on cooldowns, a bleed that ticks, and an enemy whose moves change when it is wounded, with tests for each. Everything a deckbuilder uses except the cards. [`sim.cantrip`](abilities/sim.cantrip) plays that fight many times. |
| [`party`](party) | A party of heroes, as a Darkest Dungeon style game has: several actors on the player's side that the game asks for input, each with its own abilities and cooldowns, an enemy that telegraphs which of them it is going to hit, a taunt that re-aims that telegraph, `revive` for a member who fell where `heal` refuses, and a battle that is lost only when the last of them falls. It also shows the other shape a party can take: one hand and one energy pool, with a member named as the one performing the play. It writes out the defaults, `turns: sides` and `order: position`; the reference game in [`reference/`](../reference) sets both the other way, `turns: initiative` with `order: speed`. [`sim.cantrip`](party/sim.cantrip) has a bot fight it, acting with every member before the turn ends. |
| [`board`](board) | A fight on a board with more than one row, as a Monster Train style train has: three floors, three slots to a floor, units summoned onto their summoner's floor, and a floor with no room that turns a summon away instead of growing. It declares a second board to show the other answer to a freed slot, `close_ranks`. [`sim.cantrip`](board/sim.cantrip) plays the climb many times. |
| [`slice`](slice) | A small roguelite: a witch climbing a five-floor tower, fire against frost, with 17 cards, 4 relics and 5 enemies, among them a boss, the Archmage, that changes its moves at half health. [`sim.cantrip`](slice/sim.cantrip) states the tower as a scenario, which `cantrip sim` plays hundreds of times. |
| [`corpus`](corpus) | Effects from nine existing games, re-created under our own names, one content file and one test file per game. [Coverage](../docs/coverage.md) says which the language writes directly, which need a workaround and which it cannot express yet. |

For a first read of the content itself, start with `basic` or the recipes. For a phased enemy and the tests that pin its intents, see the recipe above or the Archmage in [`slice/enemies.cantrip`](slice/enemies.cantrip) and [`slice/tests.cantrip`](slice/tests.cantrip).

## The two whole games

Two whole games live outside this folder, each built with the library the way a game would be (from the published docs and these samples), with its content, a headless C# host and a Godot project of its own:

| | |
|---|---|
| [`reference/`](../reference) | *The Drowned Chapel*, turn-based: a party of three down seven floors of a flooded chapel, on a board, under `turns: initiative`, with cards and abilities together and a run that persists between battles |
| [`realtime/`](../realtime) | *Emberline*, real-time: a forty-five second hold under `clock ticks`, with no turns, no intents and no `scenario` anywhere in it (the host owns the frame loop and says when the fight is over) |

Each keeps a `FINDINGS.md` beside it ([reference/FINDINGS.md](../reference/FINDINGS.md) and [realtime/FINDINGS.md](../realtime/FINDINGS.md)). It is what went wrong or read badly while that game was built, ranked by how much each would hurt a real developer, with a **Fixed** or **Kept** line on every finding saying what happened to it.

## Testing and linting a sample

From the root of a clone of the repository, with the tool run from source:

```
dotnet run --project src/Cantrip.Cli -- test samples/basic
dotnet run --project src/Cantrip.Cli -- lint samples/basic
```

Put `samples/recipes`, `samples/abilities`, `samples/board`, `samples/party`, `samples/slice` or `samples/corpus` in place of `samples/basic` for the others. `test` runs every test and prints PASS or FAIL for each; `lint` checks the files for mistakes a test might not reach. Both end with a count, and `lint` reports notes as well as errors and warnings: a note is information, not a failure. `lint` fails only on errors unless you add `--warnings-as-errors`, as CI does.

Load one folder at a time. The folders define some of the same names, such as `Strength`, so loading two together fails with error CT0110, "already defined".

In Godot the dock does `test` and `lint` in the editor, on the files the project holds. To read one of these folders there, copy it into the project and set `cantrip/content/folder` to it in Project Settings, as [Before you start](../docs/writing-content.md#before-you-start) explains: the dock reads every `.cantrip` file in the project otherwise, and two sample folders together clash on the same names.

In a project that has the tool installed, as the [quickstart](../docs/quickstart.md) sets up, the same commands are `dotnet cantrip test <folder>` and `dotnet cantrip lint <folder>`. `dotnet cantrip describe <folder>` prints the rules text of each definition, generated or written with `text:`, and `dotnet cantrip repl <folder>` runs single lines against a live battle. [The edit, lint and test loop](../docs/writing-content.md#5-the-edit-lint-and-test-loop) explains the first three, and [Trying lines in the REPL](../docs/writing-content.md#6-trying-lines-in-the-repl) the fourth.

## Simulating a sample

`samples/slice`, `samples/abilities`, `samples/board` and `samples/party` each hold a `scenario`: the state a run starts from (a deck, or the heroes and abilities a fight with no cards in it begins with), some fights in order, and whatever happens between them, played many times by a bot.

```
dotnet run --project src/Cantrip.Cli -- sim samples/slice
dotnet run --project src/Cantrip.Cli -- sim samples/slice --bot random
dotnet run --project src/Cantrip.Cli -- sim samples/abilities --watch 1
```

The report leads with what the content allowed, whichever bot played: a run that threw, with the seed to replay it; a battle that reached the turn limit; a card that was never playable; an enemy move that never fired. What it found holds whoever plays; what it did not find is bounded by what the bots reached, and the block says so. Under it comes a table per bot, for what that bot did with it.

Two bots play by default: `cautious` and `patient`, which looks a turn further ahead. Each plays every run, so the slice's 500 runs take about twice as long as one bot would. `--bot cautious` plays one of them, and `--bot random` is eight to eleven times quicker again and is the one for fuzzing. `--watch SEED` plays one run and prints every statement, turn and play. `--against <path>` plays a second folder with the same bots and the same seeds and reports the difference seed by seed, which is how a change to content is measured rather than guessed. [Simulating](../docs/simulating.md) covers the command and the bots, says what the numbers under them do not mean, and has [Comparing two things](../docs/simulating.md#comparing-two-things) for `--against`.

The tables under each bot say where the hp went: what dealt the damage the enemies took, what tags those hits carried, and what took the player's hp. Those amounts are the engine's own (it raised every one of them), so they hold for anyone who made those plays. Which plays were made is still the bot's, and so is the mix: on the slice, Burn is 37.6% of what the cautious bot's plays dealt and 48.2% of what the patient bot's dealt. What both agree on is that no tick of Burn ever carried `attack`, so a modifier written on that tag would have reached none of it. That is the kind of thing a tag column is for.

Nothing a bot merely tried is in any of it. A bot that looks ahead plays its options through the engine and rolls them back, and over 200 runs of the slice that raises 1.18 million events against 91 thousand in the play that counted. That is thirteen times as many. The meter is switched off for the duration, or every number above would be an order of magnitude too big.
