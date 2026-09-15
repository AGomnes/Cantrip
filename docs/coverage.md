# Coverage corpus

Section 7 of the design notes asks for about 150 reference effects from existing games, to measure how much the language can express without new C#. This page tracks that corpus.

Effects are reproduced under our own names in [`samples/corpus`](../samples/corpus), each with at least one DSL test. Run them with:

```
gedsl test samples/corpus
gedsl lint samples/corpus
```

**Status**

| Status | Meaning |
|---|---|
| Works | Expressed directly, the way a designer would write it |
| Workaround | Expressible, but needs a trick worth removing |
| Not expressible | Needs new language or runtime features |

## Summary

| Game | Effects | Works | Workaround | Not expressible |
|---|---|---|---|---|
| Slay the Spire | 23 | 15 | 8 | 0 |
| Balatro | not started | | | |
| Monster Train | not started | | | |
| Hearthstone | not started | | | |
| Dota 2 (real time) | not started | | | |

## Slay the Spire

| # | Mechanic | Ours | Status | Notes |
|---|---|---|---|---|
| 1 | Bash | Crushing Blow | Works | Damage plus a duration debuff |
| 2 | Whirlwind | Cyclone | Works | X-cost, hits every enemy X times |
| 3 | Body Slam | Shield Slam | Works | Damage equal to current block |
| 4 | Heavy Blade | Titan Blade | Workaround | Strength must count three times; the extra two are written as `2 * player.Strength` because the Strength modifier already adds one |
| 5 | Demon Form | Fiend Form | Works | A power with a turn-start listener |
| 6 | Blade Dance | Knife Flurry | Works | Creates exhausting Shivs in hand |
| 7 | Accuracy | Honed Edge | Workaround | A modifier written on a power card is anchored to that card, so the power applies a status that carries `modify damage where card:Shiv` |
| 8 | Corruption | Rot Pact | Works | Skills cost 0 and exhaust |
| 9 | Barricade | Bastion | Workaround | Cancels the turn-start block reset by listening for `before_block_changed` with `new == 0` |
| 10 | Burst | Reverb | Workaround | The next skill is replayed. A listener that becomes active during an event also hears that event's after phase, so Echo needs `not card:Reverb` |
| 11 | Reaper | Soul Reap | Workaround | Heals for unblocked damage dealt. There are no local variables, so the card stores the counter in a scratch stat |
| 12 | Envenom | Venom Coat | Works | Unblocked attack damage applies Poison |
| 13 | Pain | Ache | Works | A curse that hurts while held |
| 14 | Pen Nib | Quill Nib | Works | Every tenth attack deals double, with a counter stat |
| 15 | Anchor | Heavy Anchor | Workaround | Block granted on `battle_start` is wiped by the first turn's reset, so it is granted on the first `turn_start` instead |
| 16 | Snecko Eye | Serpent Eye | Works | Drawn cards get a random cost |
| 17 | Artifact | Nullify | Works | Cancels the next debuff |
| 18 | Intangible | Phased | Works | Damage taken is clamped to 1 |
| 19 | Cultist and Ritual | Chanter | Workaround | Patterns cannot say "this move first, then cycle", so the opening is a branch inside one move; Ritual skips its first turn with a scratch stat |
| 20 | Louse Curl Up | Rolling Louse | Works | Blocks the first time it is attacked |
| 21 | Gremlin Nob Enrage | Fury | Works | Gains Strength when the player uses a skill |
| 22 | Slime Boss split | Slime King | Workaround | Splits immediately at half hp instead of telegraphing a Split intent |
| 23 | Spore Cloud | Sporeling | Works | Debuffs the player when it dies |

## Gaps, most useful first

1. **Enemy AI phases and sequences** (19, 22). Patterns are cycle or random. An opening move, HP-threshold phases and telegraphed intents would cover most bosses. Section 3.10 already lists these.
2. **Local variables or verbs that return values** (11). `let dealt = ...` would remove scratch stats.
3. **Listeners registered mid-event hear that event** (10). Either skip the event that activated a listener, or give a clean way to say "not the card that applied me".
4. **Cancellable resets as a first-class idea** (9, 15). A resource option such as `keep_on turn_start while <condition>`, or a `battle_start` that runs after the first turn's resets, would replace both tricks.
5. **Modifier scopes for cards other than the anchor** (7). A power card could say `modify damage of cards where name:Shiv` if the `of` scope for outgoing channels selected the card rather than the target.
6. **Scaling a modifier by another modifier's source** (4). "Strength counts three times" wants to read the Strength bonus itself.

## Language notes found while building the corpus

- Names with hyphens or spaces (`Anti-Magic`) cannot be written as bare words in expressions or test setup; use single-word names or strings.
- Winning a battle clears the player's non-persistent statuses, so a test that checks a status the last enemy applied on death needs a second enemy.
