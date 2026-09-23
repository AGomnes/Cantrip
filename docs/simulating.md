# Simulating

A test plays one fight the way you tell it to. A **scenario** states a whole run — a deck, some
fights in order, and whatever happens between them — and `cantrip sim` plays it hundreds of times
with a bot and reports what happened.

It is a fuzzer and a coverage tool for your own content. It finds the run that throws, the fight
that never ends, the card that is never playable and the enemy move that never fires, none of
which a single test would reach.

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

The full rules for the body — what `battle`, `runs` and `expect` mean, and which of the test verbs
a scenario shares — are under [Scenarios](language.md#scenarios) in the language reference.

## What the report says

```
The tower, starter deck                             samples/slice/sim.cantrip:8
  500 run(s), seeds 1-500, turn limit 50, placeholder bot, 1.6s
  4 battle(s) and 3 other statement(s)

  What the content allows, whatever the bot did
    nothing threw
    no battle reached the turn limit of 50
    every card the player held was playable at least once
    every move of every enemy fought fired

  What the placeholder bot did with it
    battle                      fought   turns  hp lost
    Cinder Imp                     500     3.0      2.1
    Frost Wisp + Cinder Imp        500     4.9     10.1
    Tower Guard + Frost Wisp       500     6.8     20.7
    Archmage                       500     7.9     39.0

  expect no stalls        ok
  expect no errors        ok
```

The first block is the point of the tool:

| Line | What it means |
|---|---|
| a run threw | Something went wrong at a named line, with the seed to replay it. The report gives the statement and the message. |
| a battle reached the turn limit | The fight never ended inside the limit: a rules loop, an enemy nothing in the deck gets through, or a bot too weak to finish it. |
| a card was held but never playable | It reached a hand, and its cost was never affordable or it never had anything to aim at. |
| a card never reached a hand | It was in the deck and never drawn. |
| an enemy move never fired | The enemy has it and it never ran, usually because a phase or a pattern never reaches it. |
| every run went the same way | Nothing the scenario reaches rolls a die, so the hundredth run said no more than the first. |

Read those lines the right way round. **What happened, happened in your content**, whoever plays:
a move that fired is a move your content can reach, and a run that threw is a bug you can hit.
**What never happened is bounded by what this bot reached**: a fight the bot ends in two turns
never gets to the third move of a pattern, and a card it never affords because it spent the energy
elsewhere is reported like one your content can never play at all. The report says as much in its
last lines.

The second block is what one bot did on one day. Read it for a sense of shape — which fight is the
long one, which one costs the hp — and nothing more.

## What it will not tell you

**Nothing above the fights you name is simulated.** There is no map, no reward offer, no shop and
no gold. A rest, a relic picked up or a curse taken is written as a statement where it happens, so
a scenario measures only what it says.

**A win rate is a fact about the bot, not about your content.** Two bots given the same content can
be ten points apart. This release plays with a placeholder that takes the first card or ability it
can, in hand order; it is there so that a battle finishes, not because it plays well. The report
therefore prints no win rate, no rating and no comparison between decks, and `expect wins >= 55%`,
`expect hp_left >= 20` and `expect turns <= 12` are reported as *not checked* rather than answered
with a number nobody should quote. `expect no stalls` and `expect no errors` are checked, because
they are true of the content whoever plays.

**A scenario never plays a card itself.** `play`, `cast`, `end turn` and `tick` belong to a test: a
line choosing a card by hand would fight the bot. `realtime` is out for the same reason — when to
act in continuous time is the game's own frame loop.

## The command

```
cantrip sim <path>... [options]
```

| Option | Default | What it does |
|---|---|---|
| `--name <text>` | | Only scenarios whose name contains the text. |
| `--runs N` | the `runs` line, else 100 | Play each scenario N times, whatever it asks for. |
| `--seed S` | 1 | The first seed. Run *n* uses S + *n*, so the same command plays the same runs on any machine. |
| `--turn-limit N` | 50 | Turns one battle may take before the run counts as a stall. |
| `--watch SEED` | | Play one run of one scenario and print every statement, turn and play, instead of the report. |
| `--suppress <codes>` | | Leave out diagnostic codes, as `lint` does. |

`sim` refuses to start on a content error, the linter's errors included, so a scenario that names
an enemy nothing defines fails in milliseconds instead of after a hundred runs.

Exit codes: **0** every run finished and every checked expectation held; **1** the content has
errors, there was no scenario to play, a run threw, a battle reached the turn limit, or an
expectation failed; **2** the command line was wrong.

On a build server, one line is usually enough:

```
dotnet cantrip sim samples/slice --runs 100
```

## Watching one run

A seed from the report replays exactly:

```
dotnet cantrip sim samples/slice --watch 7
```

```
The tower, starter deck, seed 7
  player hp 60 energy 3
  deck 4 Zap, 4 Ward, Kindle, Rime
  battle Cinder Imp, at 60 hp
    turn 1  you 60hp | Cinder Imp 24hp -> Claw
      hand: Kindle, Zap, Rime, Zap, Ward (5 to draw)
      Kindle -> Cinder Imp
      Zap -> Cinder Imp
      Rime -> Cinder Imp
    turn 2  you 55hp | Cinder Imp 11hp [Burn3] -> Ember
      hand: Zap, Zap, Ward, Ward, Ward (0 to draw)
      Zap -> Cinder Imp
      Zap -> Cinder Imp
    won after 2 turn(s), 55 hp left
```

Each turn shows the board before the bot acts, the hand it chose from, and the plays it made.
`runs` and `expect` are not shown: they are read before the first run and do nothing during one.

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

Its report is worth reading twice. Nothing in that fight rolls a die, so every run goes the same
way and the report says so instead of offering two hundred repetitions as evidence. And it notes
that the Ghast's Screech never fires: the Ghast is wounded before its turn comes round, and the
wounded phase re-telegraphs, so the move at the end of its pattern is never reached. That is the
sort of thing this tool is for.

## Where next

- [Scenarios](language.md#scenarios) — the body of a scenario, line by line, and the diagnostics
  `lint` reports for one.
- [Tests](language.md#tests) — a single fight, played the way you say.
- [The edit, lint and test loop](writing-content.md#5-the-edit-lint-and-test-loop) — where `sim`
  fits beside `test` and `lint`.
