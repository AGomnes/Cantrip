# Using Cantrip from C#

Everything a game does with Cantrip goes through `ContentLibrary`, which loads and checks content, and `CardRuntime`, which plays it. This page walks through the parts a game uses; the [quickstart](quickstart.md) puts the first few together into a playable battle.

## Load content and play a battle

The examples use the Fireball, Frozen, Burn and Kindling content from the [README](../README.md), plus the Strike, Defend and Jaw Worm in [samples/basic](../samples/basic/content.cantrip). The types live in a handful of namespaces; this page's snippets use:

```csharp
using Cantrip;               // CardRuntime, RuntimeOptions, PlayResult, Num
using Cantrip.Content;       // ContentLibrary, EntityDefinition
using Cantrip.Runtime;       // Entity, Zones, GameEvent, Value, the choosers, GameSnapshot
using Cantrip.Descriptions;  // DescriptionBuilder, Description
using Cantrip.Diagnostics;   // Diagnostic
using Cantrip.Linting;       // Linter, LintOptions
using Cantrip.Testing;       // DslTestRunner
```

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

## Ask before acting

A UI greys out what cannot be played and highlights what can be aimed at; a bot needs the same answers:

```csharp
foreach (Entity card in runtime.State.ZoneOf(player, Zones.Hand))
{
    bool playable = runtime.CanPlay(card);                  // in hand, affordable, has a legal target
    IReadOnlyList<Entity> targets = runtime.LegalTargets(card);   // after taunt, stealth and the like
}

int hp = worm.GetInt("hp");            // stats, after modifiers
int burn = worm.CounterOf("Burn");     // a status's stacks (or duration); Get("Burn") is 0
```

## Verbs written in C#

Content can call verbs the game implements:

```csharp
runtime.RegisterVerb("corrupt", call =>
{
    Num amount = call.Number(0, Num.One);
    foreach (Entity target in call.Targets("to"))
        target.SetBase("corruption", target.GetBase("corruption") + amount);
});
```

The linter only knows the verbs content defines, so tell it about yours, or it reports each use as an unknown verb (CT301):

```csharp
var options = new LintOptions();
options.HostVerbs.Add("corrupt");
IReadOnlyList<Diagnostic> problems = Linter.Lint(content, options);
```

The `cantrip` tool cannot run a verb that lives in your game, so content tests that use one belong in your game's own test suite, through `DslTestRunner` with the verb registered.

## The host

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

## Player choices

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

`discover` offers content that does not exist yet, so its pending choice lists candidates rather than entities: `IsOffer` is true, `Options` is empty, and `Definitions` holds what is on offer. Answer with the definition the player picked:

```csharp
if (runtime.Pending!.IsOffer)
{
    EntityDefinition picked = runtime.Pending.Definitions[0];   // ... whichever the player chose
    runtime.Answer(picked);
}
```

A chooser that answers on the spot handles offers through `IDefinitionChooser`; `RandomChooser` and `ScriptedChooser` already do.

## Hot reload

Load the changed files, then rebind the running game:

```csharp
content.LoadFile("content/cards.cantrip");
CardRuntime.ReloadReport report = runtime.ApplyContentChanges();
```

Stats the game has changed keep their values; a card still at its printed cost takes the new one. Definitions that vanished are listed in the report, and their entities keep playing.

## Save and load

A snapshot is plain data, taken between actions; restoring it into a runtime with the same content continues the game exactly:

```csharp
// using System.Text.Json;
GameSnapshot save = runtime.Capture();
string json = JsonSerializer.Serialize(save);
runtime.Restore(JsonSerializer.Deserialize<GameSnapshot>(json)!);
```

## Rules text with live values

For card frames and tooltips, descriptions show the numbers as they are now, after modifiers. With a relic in play that multiplies fire damage by 1.5, such as Pyromancer's Codex in samples/basic:

```csharp
Description text = new DescriptionBuilder(content).Describe(card, runtime, target: worm);
text.ToPlainText();   // "Hurl a ball of flame for 9 damage. Kill it to draw 1."
text.ToMarkup();      // "Hurl a ball of flame for ~~6~~ 9 damage. ..."
```

With nothing modifying it, the same card reads "for 6 damage", and the markup has nothing struck through.

## Tracing, linting and tests

The causality trace records why everything happened; the linter and the DSL test runner are the same ones the `cantrip` tool uses:

```csharp
var traced = new CardRuntime(content, new RuntimeOptions { Trace = true });
// ... play ...
Console.WriteLine(traced.State.Trace.FormatTree());

IReadOnlyList<Diagnostic> problems = Linter.Lint(content);
IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
```
