# Gameplay Effects DSL

*Working title.*

A C# library for writing cards, statuses, relics, enemies and abilities as short text files instead of code. It targets Godot/.NET, keeps the rules engine free of engine references, and runs the same content turn-based or in real time.

```
card "Fireball"
  cost 2
  target enemy
  tags attack, fire
  effect:
    deal 6 to target
    deal 2 to adjacent(target)
    if target.dead: draw 1
  text: "Hurl a ball of flame for {damage} damage. Kill it to draw {draw}."

status "Frozen"
  tags control, ice
  on owner.damaged(tag:fire):
    remove Frozen from owner
    deal 10 to owner

relic "Kindling"
  on status_removed(tag:ice):
    apply Burn 2 to event.target

test "Fireball kills a 6 HP enemy"
  setup: enemy hp 6
  play Fireball on enemy
  expect enemy.dead
```

The design notes are in [gameplay-effects-dsl.md](gameplay-effects-dsl.md). This README describes what exists today.

## Status

An early MVP of the core, per step 1 of the plan in the design notes. It is not yet a Godot plugin.

**Working now**

- The DSL: cards, statuses, relics, enemies with move patterns, abilities, keywords, resources, rulesets, content-defined verbs, and test blocks
- Everything is an entity: statuses are entities attached to their host, so `stacks -1` and `remove tag:dot` need no special cases
- Events with `before`, `instead` and `after` phases, deterministic listener ordering, loop protection and `once per turn/battle/run/chain` limits
- A layered modifier pipeline (add, multiply, clamp, override) with sensible default scopes and explicit `of` scopes
- A tree-walking interpreter, a battle runtime (turns, card play, draw, enemy intents) and a fixed-timestep tick clock for real time
- Deterministic fixed-point math and RNG, state hashing, and save/load snapshots that replay exactly
- Hot reload: edit content and a running game picks it up, keeping the state the game has changed
- Player choices a UI answers mid-effect: the action rolls back, reports what it needs, and replays exactly once answered
- A causality trace, a static linter, generated and custom descriptions with live values, and a DSL test runner
- The `gedsl` command-line tool
- A Godot 4.6 addon: one node drives a battle from GDScript, `.ge` files import so they reach an exported build, and an editor dock shows problems, DSL tests and card text ([docs/godot.md](docs/godot.md))

**Not yet**: live debugging from the Godot editor, an export smoke test, Asset Library packaging, the compiled backend, spatial selectors (`within` needs a host), `every Xs` triggers, the VS Code extension, and the full coverage corpus. See [Roadmap](#roadmap) and the gaps in [docs/coverage.md](docs/coverage.md).

## Building

Requires the .NET 9 SDK or later.

```
dotnet build GameplayEffects.sln
dotnet test tests/GameplayEffects.Core.Tests
dotnet run --project src/GameplayEffects.Cli -- test samples/basic
```

The core library targets `netstandard2.1` and C# 9, to keep Unity possible later. The CLI and tests target `net9.0`.

## Command line

```
gedsl validate <path>...                 parse and load content, report problems
gedsl lint <path>... [--suppress codes]  static checks (GE301-GE312, GE401-GE403)
gedsl test <path>... [--filter text] [--trace]
gedsl describe <path>... [--name name]   print generated descriptions
gedsl repl <path>...                     run DSL statements against a live game
```

Paths are files or folders; folders load every `*.ge` file recursively. Exit code 0 means success, 1 means content errors or failing tests, 2 means bad usage.

## Using it from C#

Load content and play a battle:

```csharp
var content = new ContentLibrary();
content.LoadFolder("content");
content.Diagnostics.ThrowIfErrors();

var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 12345 });
Entity player = runtime.CreatePlayer(hp: 80, maxEnergy: 3);
runtime.AddDeck("Strike", "Strike", "Defend", "Fireball");
runtime.AddRelic("Kindling");
Entity worm = runtime.SpawnEnemy("Jaw Worm");

runtime.StartBattle();
PlayResult result = runtime.Play("Fireball", worm);   // Played, NotEnoughEnergy, InvalidTarget...
runtime.EndTurn();
```

Verbs implemented in C#:

```csharp
runtime.RegisterVerb("corrupt", call =>
{
    Num amount = call.Number(0, Num.One);
    foreach (Entity target in call.Targets("to"))
        target.SetBase("corruption", target.GetBase("corruption") + amount);
});
```

A host supplies what the library cannot know, such as presentation or spatial queries. Every member is optional:

```csharp
sealed class GameHost : EffectHostBase
{
    public override void OnEvent(GameEvent gameEvent)
    {
        // Presentation hangs off events; the rules have already resolved.
        if (gameEvent.Name == "damaged" && gameEvent.Target != null)
            ShowDamageNumber(gameEvent.Target, gameEvent.Amount.ToInt());
    }

    public override bool TryCall(string function, IReadOnlyList<Value> arguments, EvalContext context, out Value value)
    {
        // Answer `enemies within 5m` from the game's own world state here.
        value = Value.None;
        return false;
    }
}

var runtime = new CardRuntime(content, new RuntimeOptions { Host = new GameHost() });
```

Choices (targets, `choose`, `discard 2`) go through a pluggable `IChoiceProvider`: `FirstOptionChooser` (the default), `RandomChooser`, `ScriptedChooser`, or your UI.

```csharp
runtime.Chooser = new RandomChooser(seed: 7);
```

A UI cannot answer on the spot, so it uses `DeferredChooser`. An action that needs a decision rolls back to where it started and says so; answering replays it, deterministically:

```csharp
runtime.Chooser = new DeferredChooser();

if (runtime.Play(card) == PlayResult.ChoicePending)
{
    PendingChoice choice = runtime.Pending!;       // prompt, options, min, max
    // ... ask the player, then ...
    runtime.Answer(chosen.Id);                     // ChoicePending again if it needs another
}
```

Nothing happens until the action completes: host events are held back, so the game never animates a hit that was rolled back.

Hot reload. Load the changed files, then rebind the running game:

```csharp
content.LoadFile("content/cards.ge");
CardRuntime.ReloadReport report = runtime.ApplyContentChanges();
```

Stats the game has changed keep their values; a card still at its printed cost takes the new one. Definitions that vanished are listed in the report, and their entities keep playing.

Save and load. A snapshot is plain data, taken between actions; restoring it into a runtime with the same content continues the game exactly:

```csharp
GameSnapshot save = runtime.Capture();
string json = JsonSerializer.Serialize(save);
runtime.Restore(JsonSerializer.Deserialize<GameSnapshot>(json)!);
```

Descriptions with live values, for card frames and tooltips:

```csharp
Description text = new DescriptionBuilder(content).Describe(card, runtime, target: worm);
text.ToPlainText();   // "Hurl a ball of flame for 9 damage. Kill it to draw 1."
text.ToMarkup();      // "Hurl a ball of flame for ~~6~~ 9 damage. ..."
```

Tracing, linting and DSL tests:

```csharp
var traced = new CardRuntime(content, new RuntimeOptions { Trace = true });
// ... play ...
Console.WriteLine(traced.State.Trace.FormatTree());

IReadOnlyList<Diagnostic> problems = Linter.Lint(content);
IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
```

## Documentation

- [docs/language.md](docs/language.md): the DSL reference
- [docs/architecture.md](docs/architecture.md): how the library is put together, and where to extend it
- [docs/coverage.md](docs/coverage.md): which reference effects the language can express today
- [docs/godot.md](docs/godot.md): the Godot addon, and the two rules GDScript imposes on it
- [gameplay-effects-dsl.md](gameplay-effects-dsl.md): the design notes this project follows

## Project layout

| Path | Contents |
|---|---|
| `src/GameplayEffects.Core` | Parser, content loading, entities, events, modifiers, interpreter, runtime, linter, descriptions, test runner |
| `src/GameplayEffects.Cli` | The `gedsl` tool |
| `tests/GameplayEffects.Core.Tests` | Unit tests |
| `samples/basic` | Every example from the design notes, with DSL tests |
| `samples/corpus` | Reference effects from existing games, with DSL tests |
| `godot/GameplayEffects.Demo` | The Godot 4.6 addon, with demo content and headless tests |
| `tests/GameplayEffects.Godot.Tests` | The adapter's engine-free layer, tested without Godot |
| `tools` | Packaging the addon as a zip someone can drop into their own project |

## Roadmap

Following section 7 of the design notes:

1. **MVP core**: done (entities, events, statuses, modifiers, turn clock, tree-walk interpreter, trace log).
2. **Save/load and deterministic math**: done.
3. **Validate turn-based**: build inside a real roguelite.
4. **Validate real-time**: the tick clock exists; it needs a real-time project, spatial selectors and allocation-free event paths.
5. **Coverage corpus**: 66 effects from Slay the Spire, Monster Train, Hearthstone, Balatro, Dota 2 and Magic so far: 39 work directly, 15 need a workaround and 12 are not expressible yet ([docs/coverage.md](docs/coverage.md)). The design notes aim for about 150.
6. **Release**: the Godot addon is done and packaged — node, importer, export check, editor dock, debugger tabs showing a running game's causality tree and what each live entity is made of, pause, step and breakpoints over the debug channel, and a demo. A packaged build has been run to prove content reaches it. Still to do: a docs site and a cookbook.
7. **Project setup**: the name is still to be chosen.

## License

MIT. Copyright (c) 2026 Alexander Gomnæs. See [LICENSE](LICENSE).
