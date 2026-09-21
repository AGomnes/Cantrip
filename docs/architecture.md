# Architecture

How the library is put together, what each part is responsible for, and where to extend it. The language itself is described in [language.md](language.md).

## Layers

```
 .cantrip files ──► Syntax ──► Content ──► Runtime + Interpreter ──► CardRuntime ──► your game
                      │           │              │
                      └───────────┴──── Tools ───┘   (Linter, Descriptions, DslTestRunner, cantrip)
```

Everything lives in `Cantrip.Core`, which references no engine. `Cantrip.Cli` is a thin command-line wrapper.

| Namespace | Responsibility |
|---|---|
| `Cantrip` | `Num` (fixed-point), `Rng`, shared enums, `CardRuntime` |
| `Cantrip.Syntax` | `Lexer`, `Parser`, AST nodes, `AstPrinter`, `AstWalker` |
| `Cantrip.Content` | `ContentLibrary`, `EntityDefinition`, `VerbDefinition`, `ResourceRule` |
| `Cantrip.Runtime` | `GameState`, `Entity`, `EventBus`, `ModifierPipeline`, clocks, `TraceLog`, snapshots, `Interpreter` |
| `Cantrip.Linting` | `Linter` |
| `Cantrip.Descriptions` | `DescriptionBuilder`, localization |
| `Cantrip.Testing` | `DslTestRunner` |
| `Cantrip.Diagnostics` | `SourceSpan`, `Diagnostic`, "did you mean" suggestions |

## Syntax

The lexer turns indentation into `Indent` and `Dedent` tokens, so the parser never guesses where a block ends. It lexes `tag:fire` as one token (only for a closed set of qualifier prefixes, which is what keeps it apart from a block's colon), and `x1.5` as multiplication.

The parser is recursive descent. Its main design choice is that **it knows nothing about verbs**: a statement is a verb name, positional arguments, named clauses (`to`, `from`, `for`...) and trailing flags. Verb implementations interpret those. That is what lets content and games add verbs without touching the grammar.

Errors are collected, not thrown, with recovery to the end of the line or block, so a file with several mistakes reports all of them. Nesting deeper than 256 levels is a diagnostic rather than a stack overflow.

### Adding a node

No switch over node types in this codebase is exhaustive, so the compiler will not tell you where a new AST node has to be handled. Exactly two places fail loudly — `Interpreter.ExecuteStatement` and `Interpreter.Evaluate` both throw on a node they do not know — and every other place fails quietly:

| Place | What goes wrong when it is missed |
|---|---|
| `AstWalker.VisitStatement` | the walk stops at the node, so the linter goes blind inside it |
| `Linter.Facts.VisitStatement` | a name the node binds is not recorded, so every use of it warns CT302 |
| `DescriptionBuilder.Statement` | the node contributes nothing to generated rules text |
| `DescriptionBuilder.Canonical.Statement` | the effect hash stops noticing edits, so `text_checked` drift protection goes quiet |
| `Content/BlockAddresses` | a scheduled block nested inside the node has no address, and `Capture` later throws somewhere unrelated |
| `Definitions` member switch, `ContentLibrary.Register` | a new member or declaration is silently ignored |
| `Descriptions/Localization` | a missing phrase key falls back to the bare template instead of erroring |
| `AstPrinter` (expressions only) | error messages and failing tests print the node's type name instead of its source text |

`let`, enemy `phase` and `on every ...:` were each added by working down this list.

### Adding a verb

The parser knows nothing about verbs, so a new one introduces no AST node and the list above does not apply to it. Three other places have to learn about it:

| Place | What goes wrong when it is missed |
|---|---|
| `Interpreter.RegisterVerb` | nothing runs the verb |
| `BuiltinEvents.VerbEvents` | `IsKnownVerb` is that table, so the linter calls the verb unknown (CT301). This one fails loudly: `BuiltinEventsTests.The_verb_catalogue_matches_the_registered_verbs` walks every registered verb and refuses any the table does not list |
| `Linter.Facts.VisitStatement` | a clause that binds a name is not recorded there, so every use of the bound name warns CT302 |

`attack` and `discover` were added this way. The `into` clause is what taught the third row its lesson: it shipped without being recorded, every use of the name it bound warned, and the corpus lint caught it only because that folder had been clean all along.

## Content

`ContentLibrary` parses files and registers definitions keyed by kind and name. Loading a file again replaces exactly what it contributed, which is the basis for hot reload. Diagnostics are kept per file.

`EntityDefinition` is built once from the syntax tree: numeric properties become stats, and listeners, modifiers, blocks, moves and status configuration are pulled out. Every entity created from it shares it.

## Runtime

**`GameState`** holds all rules state: entities, zones, activation, history counters, scheduled work, the clock and the RNG. It carries a `Version` that every mutation increments; caches compare against it instead of tracking dependencies. Presentation state never lives here.

**Entities.** Actors, cards, relics, statuses and keywords are all `Entity`. A status is an entity attached to its host, with `stacks` as an ordinary stat. An entity's listeners and modifiers are registered while it is **active**, which depends on its zone: actors on the board, cards in hand (powers only once played), relics in the relics zone, statuses while their host is active.

**`EventBus`** stores listeners by event name and answers which ones hear an event, in the ruleset's deterministic order. It executes nothing.

**`ModifierPipeline`** stores modifiers by channel and computes a value through the ruleset's layers. The interpreter implements `IModifierEvaluator`, which decides whether a modifier applies (scope and filter) and evaluates its amount.

**Clocks.** `IGameClock` exposes whole units only. `TurnClock` advances once per round, `TickClock` once per fixed step, so durations, cooldowns and scheduling share one code path.

**`TraceLog`** records steps with parent ids when enabled, and costs one branch per recording site when not.

**Snapshots.** `GameState.Capture` produces plain data. Scheduled blocks are stored by content address (`card:Prepare/effect/0.body`), not object reference, so a save stays valid across processes. `Restore` rebuilds entities, zones and scheduled work, then re-registers listeners and modifiers in creation order and restores listener limit windows. Restoring into the same game reuses the entity instances whose ids match, so an `Entity` the game is holding stays the same object; anything the snapshot does not mention is marked removed.

**Hot reload.** `ContentLibrary` replaces exactly what a file contributed, and `CardRuntime.ApplyContentChanges` then points every live entity at the definition now loaded under its kind and name, re-registering its listeners and modifiers. A stat the game has changed keeps its value, because editing a card's cost must not heal an enemy mid-fight; a stat still at the definition's old number takes the new one.

**Player choices.** A UI cannot answer a `choose` while the interpreter is inside it, so `DeferredChooser` throws instead of guessing. `CardRuntime` wraps each top-level action: it snapshots first, and on a pending choice it abandons queued work, restores, truncates the trace and reports the choice. Answering replays the same action with the answers in order. The rollback is exact, so the replay follows the identical path, and host notifications are buffered for the attempt so presentation only ever sees events that really happened.

## Interpreter

`Interpreter` is one class split across files:

| File | Contents |
|---|---|
| `Interpreter.cs` | statements, assignments, content verb calls, scheduling, modifier scope evaluation |
| `Interpreter.Expressions.cs` | names, members, calls, operators, selectors, qualifier tests |
| `Interpreter.Events.cs` | `Raise`, dispatch, filters, loop protection, limits, the work queue |
| `Interpreter.Actions.cs` | the primitives: `ChangeStat`, `ApplyStatus`, `DealDamage`, `Kill`, `Draw`, `MoveCard`, `Create`, decay, until-reverts, intents |
| `Interpreter.Verbs.cs` | the built-in verb table |

### Execution model

Statements execute immediately, in order, so `if target.dead` sees the damage dealt on the line before. Every primitive raises its event through `Raise`:

```
Raise(event, action):
  before listeners run now            (may change event.amount or cancel)
  committed()                         (card play pays its cost here)
  instead listeners run now           (if any fire, the action is skipped)
  action()
  resources with reset_on <event> reset
  after listeners are queued          (or run now, with triggers: immediate)
  decay and until-reverts for <event> are queued behind them
  host.OnEvent(event)
```

`CardRuntime` wraps every top-level operation (play, a turn phase, `Execute`) in `Run`, which starts a new causal chain, resets the step budget, drains the queue, and drops queued work if the operation fails.

### Playing Fireball

1. `CardRuntime.Play` checks energy through `CostOf` (the `cost` channel) and resolves the target.
2. It raises `card_played`. The committed step pays the cost and moves the card to `play`.
3. The action runs the effect block. `deal 6 to target` calls `DealDamage`, which computes the amount through the `damage` channel (Pyromancer's Codex multiplies it), then `damage_taken`, then raises `damaged`. Frozen's `on owner.damaged(tag:fire)` is queued.
4. `if target.dead: draw 1` reads the state that step 3 already changed.
5. The card moves to the discard pile, the queue drains (Frozen removes itself, raising `status_removed`, which queues Kindling), and the battle checks whether it is over.

### Loop protection

Each queued trigger carries its `Chain`: an immutable list of the listeners that led to it. A listener already in its own chain is skipped, and chains stop at `max_depth`. So a listener cannot re-trigger itself forever, but "whenever an enemy dies, deal 1 to all" can still fire once per death in a chain reaction. Content verb calls have a separate depth limit, and every top-level action has a step budget.

## Determinism

- `Num` is a 64-bit fixed-point value with six decimal places. Multiplication splits integer and fractional parts so no intermediate overflows for values up to about ±1 million.
- `Rng` is xoshiro256** seeded through splitmix64, with rejection sampling for bounded values. Its full state is saved in snapshots.
- Anything that could depend on hash order is sorted: listener candidates, resource resets, zone and history hashing.
- `GameState.ComputeHash` covers entities, zones, RNG, clock, history, scheduled work and listener limits. Tests play a hundred random battles twice each, and play a battle side by side with a saved-and-restored copy of itself, comparing hashes after every step.

## Tools

**Linter.** Walks every body (effects, listeners, modifiers, verbs, tests) with an `AstWalker`, gathers global facts (verbs, stats, tags, emitted and listened events), then checks each body and the event graph. `BuiltinEvents` is the single catalogue of built-in events and which verbs raise them; unit tests keep it in step with the sources.

**Descriptions.** One walk over a definition both writes the automatic text and names each value (`damage`, `damage2`, `Poison`...), which is what links a writer's placeholders to the effect. Live descriptions evaluate values without side effects (anything involving ranges or `random` is shown symbolically) and pass them through the same modifier queries the rules use.

**DslTestRunner.** Creates a fresh `CardRuntime` per test and registers test-only verbs from a single table, which the linter also reads.

## Extending

| To add | Do this |
|---|---|
| A verb in C# | `runtime.RegisterVerb(name, call => ...)`. Use `call.Argument`, `call.Number`, `call.Clause`, `call.Targets` and the interpreter's primitives so events and modifiers still apply. |
| A verb in content | `verb name(params):` |
| Names or functions the rules cannot know | Implement `IEffectHost.TryResolveName` or `TryCall` (subclass `EffectHostBase`). |
| Presentation | `IEffectHost.OnEvent` sees every resolved event. |
| A decision maker | Implement `IChoiceProvider`. |
| A clock | Implement `IGameClock`, advance it from the engine's fixed step, and pass it in `RuntimeOptions.Clock`. |
| Translations | Implement `IDescriptionLocalizer` or subclass `EnglishDescriptions`. |
| Lint rules for your game | Pass `LintOptions` with host verbs, events and names, or suppress codes. |
