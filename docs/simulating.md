# Simulating

A test plays one fight the way you tell it to. A **scenario** states a whole run: a deck, some
fights in order, and whatever happens between them. `cantrip sim` plays it hundreds of times with
two bots and reports what happened.

> These docs describe the `main` branch, which can be ahead of the latest release. The changelog's [Unreleased](../CHANGELOG.md#unreleased) section lists what that release lacks, and each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

It is a fuzzer and a coverage tool for your own content. It finds the run that throws, the fight
that never ends, the card that is never playable and the enemy move that never fires, none of
which a single test would reach.

`sim` has no tab in the Godot addon's editor dock, so a Godot project installs the command-line
tool to run it; [The command](#the-command) says how, and how to point it at the game's content.

## A scenario

Put this in a file beside your content. [samples/slice/sim.cantrip](../samples/slice/sim.cantrip)
is the working version.

```
scenario "The tower, starter deck"
  runs 500

  player hp 60 energy 3
  deck 4 Zap, 4 Ward, Kindle, Rime

  battle "Cinder Imp"
  battle "Frost Wisp", "Cinder Imp"
  heal 12                                # the rest between fights, stated rather than chosen
  battle "Tower Guard", "Frost Wisp"
  battle Archmage

  expect no stalls
  expect no errors
```

Then:

```
dotnet cantrip sim samples/slice
```

Every run starts a fresh game, plays each statement in the order it is written, and fights a real
battle at every `battle` line. Hp, deck, relics and persistent statuses carry from one fight to
the next, exactly as the engine leaves them. A run stops when the player dies, when a fight
reaches the turn limit, or when something throws.

The full rules for the body are under [Scenarios](language.md#scenarios) in the language reference:
what `battle`, `runs` and `expect` mean, and which of the test verbs a scenario shares.

## What the report says

```
The tower, starter deck                             samples/slice/sim.cantrip:8
  500 run(s) by each of 2 bots, seeds 1-500, turn limit 50, cautious and patient bots
  4 battle(s) and 3 other statement(s)

  What the content allows, whatever the bot did
    nothing threw
    no battle reached the turn limit of 50
    every card the player held was playable at least once
    every move of every enemy fought fired

    Read it the right way round: what happened at least once happened in your content,
    whoever plays. What never happened may instead be something these bots never reached:
    a fight ended in two turns never gets to the third move of a pattern.

  What the cautious bot did with it, in 13.3s
    battle                      fought     won   turns  hp lost
    Cinder Imp                     500  100.0%     2.1      1.3
    Frost Wisp + Cinder Imp        500  100.0%     4.1      5.5
    Tower Guard + Frost Wisp       500  100.0%     5.0     11.8
    Archmage                       500   70.8%    10.8     45.2

  What the patient bot did with it, in 16.7s
    battle                      fought     won   turns  hp lost
    Cinder Imp                     500  100.0%     2.5      1.5
    Frost Wisp + Cinder Imp        500  100.0%     4.3      7.3
    Tower Guard + Frost Wisp       500  100.0%     4.9     12.5
    Archmage                       500   74.0%     9.5     41.8

  Levels, for reference only
    cautious bot               70.8%   of 500 run(s) won
    patient bot                74.0%   of 500 run(s) won
    These bots played the same content, from the same seeds, and are 3.2 points apart.
    A level is a fact about the bot, not about your content: do not quote one, and
    do not compare one with another bot's or with another release's.

  expect no stalls        ok
  expect no errors        ok
```

Each bot's block also carries a table of where the hp went, which is left out above and shown
in full under [The meter](#the-meter).

The first block is the point of the tool:

| Line | What it means |
|---|---|
| a run threw | Something went wrong at a named line, with the seed to replay it. The report gives the statement and the message. |
| a battle reached the turn limit | The fight never ended inside the limit: a rules loop, an enemy nothing in the deck gets through, or a bot too weak to finish it. |
| a card was held but never playable | It reached a hand, and its cost was never affordable or it never had anything to aim at. |
| a card never reached a hand | It was in the deck and never drawn. |
| an enemy move never fired | The enemy has it and it never ran, usually because a phase or a pattern never reaches it. |
| every run came out the same way | Every run ended with the same battles, turns and hp, so the hundredth said no more than the first. Either nothing the scenario reaches rolls a die, or nothing it rolls changed the outcome. |

Read those lines the right way round, as the block itself says. **What happened, happened in your
content**, whoever plays: a move that fired is a move your content can reach, and a run that threw
is a bug you can hit. A fact either bot found counts, which is why that block is one block for both
of them. **What never happened is bounded by what the bots reached**: a fight they end in two turns
never gets to the third move of a pattern, and a card never afforded because the energy went
elsewhere is reported like one your content can never play at all.

The one place the report does not guess is the hand. A card drawn or created part way through a
turn (by a `discover`, or by a card that draws) counts as having reached a hand and, if it was
played, as having been playable, because the engine raised an event saying so. Reading the hand
only at the start of each turn would have called such a card undrawable.

The blocks after it are what one bot did on one day. Read them for a sense of shape and nothing
more: which fight is the long one, which one costs the hp, where the two bots part company.

**Nothing a bot merely tried is in the first block.** A bot that looks ahead plays each of its
options through the engine and rolls the game back, and a play tried that way raises the same
events as a real one: the enemies answer every trial. Those moves are not recorded, so "every move
of every enemy fired" still means what it says. Nor does a trial spend an `answer` the scenario
wrote, or read the dice the run is about to roll. It rolls its own.

## The bots

Three of them, and two play by default.

| Bot | What it does | What it is for |
|---|---|---|
| `cautious` | Tries every legal play and ability through the engine: it captures the game, makes the play, ends the turn so the enemies answer, scores what is left, and rolls back. It then makes the best play it found, and stops when ending the turn scores as well as anything it could do. | The default. It plays a fight the way a careful player might, so a run reaches the later floors. |
| `patient` | The same, scoring two turns out instead of one. | To disagree. A card that does nothing this turn and a lot next turn is invisible to the cautious bot and obvious to this one. |
| `random` | Plays legal cards and abilities at random until it can play no more. | The floor, and the fastest way to fuzz: it tries nothing first, so it is eight to eleven times quicker than the other two. |

None of them knows a card, status, ability or enemy by name, so new content needs no change to
any of them. A position is scored on hp alone (one and a half times the party's, less the
enemies'), taken after the enemies have answered, so a block that stopped a hit shows up as hp the
player still has.

**A party takes its turn a member at a time.** Each bot asks every member that still has a step,
plays with it and passes, and the turn ends when the last one has. Under
[`turns: initiative`](language.md#the-turn) that is the only way the round moves at all. A game with
no `hero` in it has a party of one, is never passed, and plays the run it always did, to the seed.
The `hp lost` column is the party's health between them, so a fight the heroes are losing while
the leader stands untouched reads as the loss it is.

**They are wrong in the same direction.** All three share that one way of weighing a position, so
they all undervalue a card that draws, a card that gives energy, and anything else that pays off
several turns later. Two of them agreeing is therefore not evidence that either is right. What it
does show is where they *disagree*, and that is worth reading: on `samples/slice`, the patient bot
finishes the Archmage a turn sooner and 3.4 hp better off, and wins 3.2 points more of its runs.

**A bot plays every run from the run's own seed**, so `--bot cautious` and `--bot both` play the
same runs, and a seed in the report replays under the bot that produced it.

## The meter

Under each bot's table is the one part of the report that is not a judgement about play: what the
engine actually raised while the game was being played.

```
  Where the hp went, under the cautious bot
    Every amount here is one the engine raised, so it holds for anyone who made these
    plays. Which plays were made is this bot's doing; what each one cost is your content's.

    dealt to the enemies, by what dealt it          hp   share
      Zap                                        73658   57.9%
      Burn                                       47874   37.6%
      Rime                                        5710    4.5%

    the same hits, by the tags they carried         hp   share
      attack                                     79368   62.4%
      starter                                    79368   62.4%
      arcane                                     73658   57.9%
      burn                                       47874   37.6%
      debuff                                     47874   37.6%
      dot                                        47874   37.6%
      frost                                       5710    4.5%

    taken by the player, by what dealt it           hp   share
      Archmage                                   22596   70.8%
      Tower Guard                                 3595   11.3%
      Frost Wisp                                  2798    8.8%
      Cinder Imp                                  2068    6.5%
      Burn                                         852    2.7%

    Of the damage these plays dealt, Burn is 37.6%, and not one of its hits
    carried `attack`: `modify damage where tag:attack` would have reached none of it.

    also counted, over the same plays
      healing                                    16144  hp restored
      block                                      99817  gained
      statuses                                    5981  applied, of 3 kind(s)
      cards                                      31863  played, for 31863 energy
      enemy moves                                12184  used, of 10 kind(s)
```

Every number there is the sum of an amount the engine set itself. A `damaged` event carries the hp
a hit actually took off, after block and capped by the hp that was left, so a card that says 30
against an enemy on 20 hp counts as 20. `healed` and `gained_block` carry what was actually
restored and actually gained. The report adds those up and divides. It invents nothing, and it
names each hit after the card that caused it, or after whatever was running when there was no card:
a status ticking, or the enemy that swung. An ability is the one thing it cannot name. The engine
puts the actor on a `damaged` event and not the ability, so in a fight with no cards in it the
player's own hits are counted under the player. The tags on those hits are still the ability's.

**What it proves.** Given the plays that were made, that is where the hp went, and it is where the
hp would have gone for anyone who made the same plays. Which is why the Burn line is worth reading.
The slice tags Burn `burn`, `dot` and `debuff`, and deliberately not `attack`, nor `fire`, because
a fire hit is meant to shatter Frozen and a tick of Burn is not. The price of that decision is on
screen: a modifier written as `modify damage where tag:attack` would have reached none of Burn's
share of the damage, and that share is a large one.

**How large is the bot's doing.** Burn is 37.6% of what the cautious bot's plays dealt and 48.2% of
what the patient bot's dealt, over the same 500 seeds and the same content. So the share is a
number about a bot as much as about the deck, and neither figure should be quoted on its own. That
no hit of Burn carried `attack` is the content's, and it holds for both.

**What it does not prove.** It does not say that those were the plays to make. Which cards were
played, and how often, is the bot's doing, so the *mix* in the table moves with the bot even
though every amount in it is your content's. That is why there is one meter per bot rather than
one for the report, and why it sits inside the bot's block.

**What the tool cannot judge, it names.** When content asks the player to choose part way through
an effect (`choose`, `discover`), a bot has nothing to decide it with: it looks one play ahead,
not into the middle of one. Those choices are answered at random, counted with the line that asked
for them, and reported. The slice's own scenario never holds a Spellbook, so this is from one whose
deck is `4 Zap, 4 Ward, Spellbook`, over 20 runs of two battles with the cautious bot:

```
    1 line(s) asked for a choice mid-effect, which this bot answered at random
      Spellbook at samples/slice/cards.cantrip:127:5, 4 time(s)
      Nothing above judges those: a bot looks one play ahead, not into the middle of one.
```

**Nothing a bot merely tried is counted**, and this is the whole correctness problem of the meter.
It is larger than it looks: over 200 runs of the slice, the plays the cautious bot tried and rolled
back raise about 1.18 million events, thirteen times the 91 thousand in the play that counted.
Counted, every number above would be an order of magnitude too big. The check is one the engine can
make on its own: for every actor, the damage counted less the healing counted has to be the hp that
actor actually lost, read back from the engine's own `hp`. A test runs that over both sample folders
with each bot on every build, and it fails loudly if the switch is ever wrong. With the flag removed
the player on one seed of the slice "lost 60 hp" while the meter claimed 2160.

## What it will not tell you

**Nothing above the fights you name is simulated.** There is no map, no reward offer, no shop and
no gold. A rest, a relic picked up or a curse taken is written as a statement where it happens, so
a scenario measures only what it says.

**A win rate is a fact about the bot, not about your content**, and never more so than for a party.
On the slice, over the same 500 seeds, the cautious bot finished 70.8% of its runs, the patient bot
74.0% and the random bot 0.6%. With several heroes it is worse again: every bot weighs the party's
hp against the enemies', which cannot see that keeping the healer alive is what wins the fight, so
it will spend her heal on whoever is nearest to dead.
That is one piece of content and three answers. So the report prints each bot's level under a
heading that says not to quote it, next to the other bot's, and `expect wins >= 55%`,
`expect hp_left >= 20` and `expect turns <= 12` are reported as *not checked* rather than answered
with a number nobody should quote. `expect no stalls` and `expect no errors` are checked, because
they are true of the content whoever plays.

**No knobs.** There is no option for how far a bot looks ahead, and no way to tell it what a status
is worth. Both were tried and measured while this was designed, and both moved the answer by tens
of points with no principled way to choose the setting. That would make the report an argument
about the setting rather than about the content.

**A scenario never plays a card itself.** `play`, `cast`, `end turn` and `tick` belong to a test: a
line choosing a card by hand would fight the bot. `realtime` is out for the same reason: when to
act in continuous time is the game's own frame loop.

**`sim` cannot play a real-time game and refuses to try.** A bot plays a scenario by deciding what
to do and then ending the turn, and content whose ruleset says `clock ticks` has no turn to end: the
clock would stand at zero for the whole run, so every `on every` listener would be silent, every
ability used once would never come back, and no battle could end. The command refuses such a folder
with one sentence, and a `scenario` written in `clock ticks` content is error CT338 at lint. Cover a
real-time game with [`test` blocks](language.md#tests) instead, which have `realtime <rate>` and
`tick <n>`; [realtime/content/tests.cantrip](../realtime/content/tests.cantrip) is eighteen of them.

## The command

```
cantrip sim <path>... [options]
```

| Option | Default | What it does |
|---|---|---|
| `--name <text>` | | Only scenarios whose name contains the text. |
| `--runs N` | the `runs` line, else 100 | Play each scenario N times, whatever it asks for. |
| `--seed S` | 1 | The first seed. Run *n* uses S + *n*, so the same command plays the same runs on any machine. |
| `--bot <name>` | `both` | `cautious`, `patient`, `random`, or `both` for the cautious and patient bots together. |
| `--turn-limit N` | 50 | Turns one battle may take before the run counts as a stall. |
| `--watch SEED` | | Play one run of one scenario, with the first bot, and print every statement, turn and play, instead of the report. |
| `--against <path>` | | Also play the scenarios in `<path>`, with the same bots and the same seeds, and report the difference seed by seed. May be given more than once, like a path. See [Comparing two things](#comparing-two-things). |
| `--against-name <text>` | `--name` | Which scenario in `--against` to compare with, when it is not called the same thing. |
| `--suppress <codes>` | | Leave out diagnostic codes, as `lint` does. |

**Two bots cost about twice as long as one**, because each one plays every run. On one core of a
laptop (an Intel Core Ultra 5 125U), the slice's 500 runs take about 30 seconds with both, about
14 with `--bot cautious`, about 19 with `--bot patient` and under 2 with `--bot random`. So
`--bot cautious` roughly halves it, and `--bot random` is the one to reach for when what you want
is to fuzz content rather than see it played.

`sim` refuses to start on a content error, the linter's errors included, so a scenario that names
an enemy nothing defines fails in milliseconds instead of after a hundred runs.

In a Godot project the addon's editor dock lints, tests and previews, but it does not simulate:
its Tests tab runs `test` blocks only, and neither counts nor plays a `scenario`. Install the tool
beside the game, with the two lines in [Before you start](writing-content.md#before-you-start),
point it at the folder the game loads from (`res://content` unless the game was told otherwise),
and run it there:

```
dotnet cantrip sim content
```

Exit codes: **0** every run finished and every checked expectation held; **1** the content has
errors, there was no scenario to play, a run threw, a battle reached the turn limit, or an
expectation failed; **2** the command line was wrong.

On a build server, one line is usually enough:

```
dotnet cantrip sim samples/slice --runs 100
```

## Comparing two things

A level cannot be quoted, and the page above says so four times. A *difference* between two levels
can, as long as nothing but the content moved: the same bot, playing run *n* from seed *n* on both
sides, with the same turn limit and the same number of runs. That is what `--against` does, and it
is the only comparison the tool makes.

```
cantrip sim content --against ../old-content       # before and after a change
cantrip sim content --name Tall --against content --against-name Wide     # two decks
```

Both sides are played by one command, on purpose. Two separate runs of `sim` could differ in the
bot, the first seed, the turn limit or the run count, and any of those differences would land in
the numbers with nothing to say so.

### What it prints, in the order it is worth reading

**What the content allows.** A card that was playable and is not any more; an enemy move that
fired and no longer does. These are true whoever plays, they are usually the half that was not
intended, and they come first for that reason. A card whose cost went from 2 to 9 shows up here as
`1 card(s) that were playable and are not any more: Ruin` and needs no statistics at all.

**The seeds that changed hands.** Not a delta: the paired counts.

```
  cautious bot, 200 seed(s) played by both
    seeds that changed hands             101
      lost before, won now                85
      won before, lost now                16
      came out the same way               99

    85 of the 101 went your way. A change that decided nothing would split 101 seeds at least this
    unevenly about once in more than a million tries.
```

Two percentages cannot tell 101 seeds moving apart from 16, and the difference between those two
readings is the difference between "this changed the game" and "this is noise". The last line is
the only statistic in the tool: every seed is an independent game, so it is the exact two-sided
tail of a fair coin tossed once per seed that changed hands, stated as odds rather than as a
*p*-value. Twelve seeds all going one way is once in 2,048 tries. Six and six is what a change
that decided nothing does.

**Turns, hp lost, stalls and throws**, before and after, over the paired seeds.

**The two levels**, last and smallest, carrying the same warning they carry anywhere else.

### What it still will not tell you

It will not tell you the change is **good**. The bots weigh the party's hp against the enemies',
so they are wrong in a known direction about a card that draws and about anything that pays off
several turns later; a change these numbers like may be the change that made the game duller. The
honest reading of a comparison is *this bot finished more runs of this scenario*, and the block
about what the content allows is the part that is true whoever plays.

**A comparison never changes the exit code.** It is a reading, not a check. What fails `cantrip
sim` is what always failed it, in the content the command was pointed at: a run that threw, a
battle that hit the turn limit, an expectation that did not hold. A baseline that does not load, or
has no scenario to compare with, is refused before either side is played.

Scenarios pair by name. One that is only on one side is named and skipped rather than dropped. Two
scenarios called different things pair when `--name` and `--against-name` narrow each side to
exactly one, which is how two decks stated in one folder are compared.

## Watching one run

A seed from the report replays exactly:

```
dotnet cantrip sim samples/slice --watch 7
```

```
The tower, starter deck, seed 7, cautious bot
  player hp 60 energy 3
  deck 4 Zap, 4 Ward, Kindle, Rime
  battle Cinder Imp, at 60 hp
    turn 1  you 60hp | Cinder Imp 24hp -> Claw
      hand: Kindle, Zap, Rime, Zap, Ward (5 to draw)
      Ward
      Zap -> Cinder Imp
      Zap -> Cinder Imp
    turn 2  you 59hp | Cinder Imp 12hp -> Ember
      hand: Zap, Zap, Ward, Ward, Ward (0 to draw)
      Zap -> Cinder Imp
      Zap -> Cinder Imp
    won after 2 turn(s), 59 hp left
```

Each turn shows the board before the bot acts, the hand it chose from, and the plays it made. The
first line names the bot, because another one plays the same seed differently: `--watch` uses the
first bot `--bot` asked for, which is the cautious one by default. `runs` and `expect` are not
shown: they are read before the first run and do nothing during one.

## Counting cards

`deck 4 Zap, 4 Ward` puts four of each in the draw pile. A whole number before a name repeats it,
on `deck`, `hand`, `discard_pile`, `relic`, `grant` and `answer`, in a `scenario` and in a `test`
alike, and on `battle`, so `battle 2 "Cinder Imp"` fights two of them. The most any one count may
be is 1000.

## A fight with no cards in it

A scenario does not need a deck. [samples/abilities/sim.cantrip](../samples/abilities/sim.cantrip)
fights an enemy with two abilities on cooldowns and no cards anywhere:

```
scenario "The Ghast, with Cleave and Brace"
  runs 200

  player hp 40
  grant Cleave, Brace

  battle Ghast

  expect no stalls
  expect no errors
```

Its report is worth reading twice. Nothing in that fight rolls a die, so every run comes out the
same way and the report says so instead of offering two hundred repetitions as evidence. And it notes
that the Ghast's Screech never fires: the Ghast is wounded before its turn comes round, and the
wounded phase re-telegraphs, so the move at the end of its pattern is never reached. That is the
sort of thing this tool is for.

Both bots win it in five turns for six hp, and both would still report Screech as never fired. Each
of them ends a turn dozens of times while deciding what to do, in trials where the Ghast does move.
But a trial is not play, and the first block counts only what was played.

## Where next

- [Scenarios](language.md#scenarios): the body of a scenario, line by line, and the diagnostics
  `lint` reports for one.
- [Tests](language.md#tests): a single fight, played the way you say.
- [The edit, lint and test loop](writing-content.md#5-the-edit-lint-and-test-loop): where `sim`
  fits beside `test` and `lint`.
- [Troubleshooting](troubleshooting.md#the-fight-never-ends): what a stall usually turns out to
  be, and how to find out which kind you have.
