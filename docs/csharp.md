# Using Cantrip from C#

Everything a game does with Cantrip goes through `ContentLibrary`, which loads and checks content, and `CardRuntime`, which plays it. This page is for a game that calls the library itself, whether a console app, a MonoGame or FNA game, or an engine loop of your own, and walks through the parts such a game uses. The [quickstart](quickstart.md) puts the first few together into a playable battle, and the [Godot addon](godot.md) wraps the same calls for GDScript. The package ships its XML documentation, so your editor shows each member's notes as you type.

These docs describe the `main` branch, which can be ahead of the latest release. The changelog's [Unreleased](../CHANGELOG.md#unreleased) section lists what that release lacks, and each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

## Load content and play a battle

The examples use the Fireball, Frozen, Burn and Kindling content from the [README](../README.md), plus the Strike, Defend and Jaw Worm in [samples/basic](../samples/basic/content.cantrip). The types live in a handful of namespaces; this page's snippets use:

```csharp
using Cantrip;               // CardRuntime, RuntimeOptions, PlayResult, Num, Team
using Cantrip.Content;       // ContentLibrary, EntityDefinition
using Cantrip.Runtime;       // Entity, Zones, GameEvent, Value, the choosers, GameSnapshot, RuntimeError, Ruleset
using Cantrip.Descriptions;  // DescriptionBuilder, Description, DescriptionSegment, ValueTrend
using Cantrip.Diagnostics;   // Diagnostic, DiagnosticBag, DslException
using Cantrip.Linting;       // Linter, LintOptions
using Cantrip.Syntax;        // AssignOperator, for verbs written in C#
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

`LoadFolder("content")` reads a folder relative to the working directory. [Shipping content with the game](#shipping-content-with-the-game) covers a game started from its build folder, or packed with no loose files at all.

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

## What a battle screen reads

Everything a battle screen draws comes from the live state:

| To show | Read |
|---|---|
| The player, and the enemies still standing | `runtime.Player`, and `runtime.State.Actors(Team.Enemy)` in board order |
| A pile | `runtime.State.ZoneOf(player, Zones.Hand)`, and likewise `Draw`, `Discard`, `Exhaust`, `Powers` and `Relics` |
| Where a card is | `card.Zone`, such as `"hand"` or `"discard"`; `"play"` while its effect resolves |
| hp, block, energy and other stats | `entity.GetInt("hp")`, after modifiers |
| Statuses and their numbers | `entity.Attached`, and `entity.CounterOf(status.Name)` for each |
| A card's type, for its frame | `card.HasTag("attack")`, or `card.Tags` for all of them, tags added during play included |
| Whether an actor has died | `entity.IsDead`. A dead enemy drops out of `Actors` and waits in the `dead` zone until the next battle starts |
| An enemy's next move | `enemy.Intent` (see [Enemy intents](#enemy-intents)), and `enemy.Phase` for an enemy with [phases](language.md#phases) |
| What the content says about it | `entity.Definition`, an `EntityDefinition`: `HasTag`, `Word` for a word such as `rarity rare`, `ReadString` for a quoted string, and `Stats` for the numbers as written |
| Candidates for a reward screen | `content.Pool("card")`, filtered with `HasTag` or `Word` |
| The same entity later, even in a restored game | its `Id`, with `runtime.State.Find(id)` |

For example:

```csharp
IReadOnlyList<Entity> enemies = runtime.State.Actors(Team.Enemy);   // living enemies, in board order
foreach (Entity status in worm.Attached)                            // statuses and keywords on it
    Console.WriteLine($"{status.Name} {worm.CounterOf(status.Name)}");
int drawPile = runtime.State.ZoneOf(player, Zones.Draw).Count;       // likewise Discard, Exhaust, Powers, Relics
```

A property the engine has no use for, such as `art "cards/fireball.png"`, loads without a warning and stays on the definition, so presentation data can sit in the content beside the rules: `card.Definition!.ReadString("art")` gives `cards/fireball.png`. Rules text and intents have sections of their own below.

## The host

A host supplies what the library cannot know, such as presentation or spatial queries. Every member is optional:

```csharp
sealed class GameHost : EffectHostBase
{
    public List<GameEvent> Events { get; } = new List<GameEvent>();

    // Presentation hangs off events. Record them here and show them later: see the next section.
    public override void OnEvent(GameEvent gameEvent) => Events.Add(gameEvent);

    public override bool TryCall(string function, IReadOnlyList<Value> arguments, EvalContext context, out Value value)
    {
        // Answer `enemies within 5m` from the game's own world state here.
        value = Value.None;
        return false;
    }
}
```

```csharp
var host = new GameHost();
var runtime = new CardRuntime(content, new RuntimeOptions { Host = host });
```

`TryResolveName` answers a bare name the same way. Both run in the middle of an effect while the rules wait, so they answer and return without calling back into the runtime. They are also the one place where determinism depends on your code, as [verbs written in C#](#verbs-written-in-c) are: answer from the game's own state, never from the clock or `System.Random`, or the same seed and inputs stop producing the same game. [Determinism](stability.md#determinism) lists what else to avoid.

## Presenting events in a frame loop

Every call resolves completely before it returns: by the time `Play` answers, the damage is dealt, the triggers have run and the card is in the discard pile. A game with animations records the events as they arrive and plays them back over the following frames. [Built-in events](language.md#built-in-events) lists every event and the fields it carries. The library takes no locks, so make every call from one thread, the one that runs the game's update.

When the host hears about an event depends on the chooser (see [Player choices](#player-choices)):

| Chooser | `OnEvent` is called | Stats read inside `OnEvent` |
|---|---|---|
| any but `DeferredChooser`, including the default `FirstOptionChooser` | during the call, as each event finishes | as they stood when that event finished |
| `DeferredChooser` | once `Play`, `EndTurn`, `StartBattle`, `Execute`, `UseAbility` or `Answer` has finished, for all its events in a row; never for an attempt rolled back for a choice | the final values, after the whole call |

Under `DeferredChooser`, the other calls (`AddRelic`, `ApplyStatus`, `Tick`) deliver as the default chooser does.

Events arrive in the order they complete, so an event that wraps others comes after them. A card that hits twice and applies a status reports `damaged`, `damaged`, `status_applied`, then `card_played`; an enemy's move reports its hits, then `move`. Listeners resolve after the action that triggered them (unless the [ruleset](language.md#rulesets) says `triggers: immediate`), so whatever they cause arrives after it too: a relic that hits back when the player is hit reports its damage after the enemy's `move`.

A player expects to see the cause first, so present those two the other way round:

- **A card the player plays.** The game knows the card and its target when it calls `Play`, so start the card's animation as soon as `Play` returns `Played` (or the `Answer` that finishes it does), and treat `card_played` as the animation's end, where the card lands in its pile.
- **An enemy's move.** By the time `EndTurn` returns, the whole enemy turn is in the queue. The events a move's own lines raise name the enemy as their `Source`, so when one of them comes up while that enemy's `move` is still waiting further on, show the move first. The enemy's `turn_start` and `turn_end` name it too, but are not part of its move.

Never call back into the runtime from `OnEvent`, under either chooser: the call that raised the event has not returned yet. Record the event and act once the call is over, as this screen does:

```csharp
sealed class BattleScreen
{
    private readonly CardRuntime runtime;
    private readonly GameHost host;
    private float showing;   // seconds left of the animation on screen

    public BattleScreen(CardRuntime runtime, GameHost host)
    {
        this.runtime = runtime;
        this.host = host;
    }

    // Input waits until everything that happened has been shown.
    public bool Busy => showing > 0 || host.Events.Count > 0;

    public void CardDropped(Entity card, Entity? target)
    {
        if (Busy) return;
        PlayResult result = runtime.Play(card, target);   // resolves at once, and its events are queued
        if (result == PlayResult.Played) showing = ShowCardFlying(card, target);   // shown before its events
        else if (result == PlayResult.ChoicePending) ShowChoice(runtime.Pending!);   // see Player choices
    }

    public void EndTurnPressed()
    {
        if (Busy) return;
        runtime.EndTurn();   // the whole enemy turn resolves here, and its events are queued
        if (runtime.Pending != null) ShowChoice(runtime.Pending);
    }

    // Called once per frame from the game's Update.
    public void Update(float seconds)
    {
        if (showing > 0) showing -= seconds;
        else if (host.Events.Count > 0) showing = Present(TakeNext());
    }

    // The next event to show. The first event an enemy's move raises brings that move forward.
    private GameEvent TakeNext()
    {
        GameEvent next = host.Events[0];
        int index = 0;
        if (next.Name != "turn_start" && next.Name != "turn_end")
            index = Math.Max(0, host.Events.FindIndex(e => e.Name == "move" && e.Source == next.Source));

        GameEvent taken = host.Events[index];
        host.Events.RemoveAt(index);
        return taken;
    }

    // Starts an event's animation and returns how long it lasts.
    private float Present(GameEvent gameEvent)
    {
        switch (gameEvent.Name)
        {
            case "move":
                return ShowMove(gameEvent.Source!, gameEvent.Data["move"].Text!);   // "Chomp"
            case "damaged":
                // Amount is the hp lost; the hit's other numbers are in Data.
                gameEvent.Data.TryGetValue("blocked", out Value blocked);
                return ShowHit(gameEvent.Target!, gameEvent.Amount.ToInt(), blocked.Number.ToInt());
            case "card_played":
                return ShowCardLanded(gameEvent.Card!);   // into the pile it went to: Card.Zone
            default:
                return 0;
        }
    }
}
```

With a relic that hits back, an enemy turn of two Jaw Worms reaches the queue as the worms' `turn_start`s, then `damaged` (the player), `move`, `damaged` (the first worm, by the relic), `damaged` (the player), `move`, `damaged` (the second worm), then their `turn_end`s. This screen shows each worm's move, then the hit it made, then the relic's reply.

An enemy's own listeners, and `next turn:` work its moves scheduled, name the enemy as `Source` too. So an enemy that acts in its turn before its move, such as one with `on turn_start: block 4` in its own definition, is shown making its move before that block. A status's listeners name the status instead, so Poison ticking on an enemy is shown in its place.

Reading `hp` while an event is on screen gives the live value, where the whole call ended rather than where that event left it. Move bars by each event's own numbers instead (`Amount` is the hp lost on `damaged`, the hp regained on `healed`, the block gained on `gained_block`), or, under any chooser but `DeferredChooser`, copy the stats you need inside `OnEvent`, where they are still as they were then.

Not everything raises an event. These changes happen without one:

| Change | When |
|---|---|
| A played card moving to the `play` zone, then to the discard pile, or to `powers` for a power | during `Play`; a card that exhausts raises `exhausted` instead of reaching the discard pile |
| The hand going to the discard pile, retained cards apart | during `EndTurn`, after the player's `turn_end`; ethereal cards raise `exhausted` |
| Block falling to 0 and energy refilling | at each `turn_start` |
| A stat changing other than by damage, healing or gaining block, such as the energy paid for a card or `lose 3 hp`, and a status's number changing other than by `apply`: `stacks -1`, `decay`, or `gain 2 Strength` on a host that already has Strength | whenever it happens |
| An enemy's new intent | when intents are rolled: at the start of a battle, after the enemy turn and when an enemy joins; and when a hit takes the enemy into a `retelegraph` phase |
| An enemy's phase | when a hit takes the enemy across a phase's threshold, and otherwise when its intent is rolled |
| Tags added or taken away | whenever it happens |
| The draw pile shuffled, and enemies that died in an earlier battle taken away | during `StartBattle`; shuffling the discard pile into the draw pile raises `shuffled` |
| The player's statuses taken away, unless `persistent`, and every card back in the draw pile | when the battle ends, after `battle_end` |

A stat change, the turn-start resets included, does raise `<stat>_changed`, but only when some content listens for it. `CreatePlayer`, `AddCard`, `AddDeck` and `SpawnEnemy` raise nothing, and nor do `Restore` and `ApplyContentChanges`; of the setup calls, only `AddRelic` (`obtained`) and `ApplyStatus` (`status_applied`) raise an event. An event that a listener cancels never reaches the host; one that an `instead_of_` listener replaced does, with `Replaced` true. So when the queue is empty, redraw the hand, the piles, the bars and the intents from the live state.

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
    PendingChoice choice = runtime.Pending!;    // Prompt, Options, Min, Max
    Entity chosen = choice.Options[0];          // ... whichever the player picked ...
    runtime.Answer(chosen.Id);                  // ChoicePending again if it needs another
}
```

`Answer` takes the ids of the picked entities, as here, or the entities themselves in a list, as below.

Nothing happens until the action completes: host events are held back, so the game never animates a hit that was rolled back. While a choice is pending, the game is exactly as it was before the call: the card being played is still in the hand with its cost unpaid, and the `Options` of a choice from the hand leave it out. So draw it as being played yourself, until the call that finishes it returns `Played` or the choice is cancelled.

`Prompt` is the engine's short summary of what is asked, in English: `choose a target`, `discard 2`, `exhaust 1`, `choose 1`, or `discover 1 of 3` for an offer. It suits a log rather than the player. Word what the player sees from the card being played and the options instead: each option's `Zone` says which pile it is in, `Chooser` is who chooses, and `Span` points at the line of content that asked.

`Play` is not the only call that can stop. A relic whose turn-start effect asks the player to choose stops `EndTurn`: the whole call, enemy turn included, is rolled back and runs again once the choice is answered, so a game that checks only `Play` seems to hang on End Turn. Check `runtime.Pending` after each of these:

| Call | Says it has stopped by |
|---|---|
| `Play` | returning `PlayResult.ChoicePending` |
| `Answer` | returning `PlayResult.ChoicePending`, when the replay asks again |
| `EndTurn`, `StartBattle`, `Execute` | setting `runtime.Pending` (they return nothing) |
| `UseAbility` | returning false and setting `runtime.Pending` |

`AddRelic`, `ApplyStatus` and `Tick` never stop: a choice they raise takes the first option.

A choice that takes several picks, such as `discard 2` (`Min` and `Max` both 2), is answered with all of them in one call:

```csharp
PendingChoice discard = runtime.Pending!;
runtime.Answer(new[] { discard.Options[0], discard.Options[2] });   // both cards at once
```

Picks that are not among the `Options` are ignored, picks beyond `Max` are dropped, and too few are made up from the front of `Options`. `CancelPending()` abandons the choice and leaves the game exactly as it was; starting another action abandons it too.

`discover` offers content that does not exist yet, so its pending choice lists candidates rather than entities: `IsOffer` is true, `Options` is empty, and `Definitions` holds what is on offer. Answer with the definition the player picked:

```csharp
if (runtime.Pending!.IsOffer)
{
    EntityDefinition picked = runtime.Pending.Definitions[0];   // ... whichever the player chose
    runtime.Answer(picked);
}
```

A chooser that answers on the spot handles offers through `IDefinitionChooser`; `RandomChooser` and `ScriptedChooser` already do.

## Winning, losing and several battles

`runtime.Won` is null while a battle runs, and before the first one; once a side is gone it is true or false. A battle ends when the player dies or no enemy is left alive, so check `Won` after each call, or listen for `battle_end`, whose data holds `won`.

One runtime plays a whole run. Between battles, give rewards, heal, spawn the next encounter and start again:

```csharp
if (runtime.Won == true)
{
    runtime.AddCard("Fireball");        // a reward, into the draw pile
    runtime.AddRelic("Lizard Tail");
    runtime.Execute("heal 12");         // a rest: any statements, run as the player
    runtime.SpawnEnemy("Jaw Worm");     // the next encounter
    runtime.StartBattle();
}
```

| Carries over | Starts afresh |
|---|---|
| The player, with its hp, max hp and any other stats | Enemies: the dead are removed when the next battle starts |
| Every card still in the game, back in the draw pile, exhausted cards and cards made during the battle included | The player's statuses, unless flagged `persistent` |
| Relics, and `once per run` limits | `until` effects, which are undone, and scheduled work, which is dropped |
| | `once per battle` limits, the battle's history counters and the turn number |

A card made during a battle, such as a Wound, stays in the deck like any other. To make it temporary, take it out between battles with `runtime.Execute("destroy target", target: wound)`.

[src/Cantrip.Sim/ScenarioRunner.cs](../src/Cantrip.Sim/ScenarioRunner.cs) is a worked example of a run above the battle: one runtime carries hp, deck and relics from fight to fight, and whatever the scenario writes between them — `heal 12`, `relic "Ember Charm"` — runs as a statement.

## Enemy intents

`Entity.Intent` is the name of the move an enemy will make next. It is rolled when the battle starts, after each enemy turn, and for an enemy that joins mid-battle. A phase marked `retelegraph` also rolls it again the moment a hit moves the enemy into that phase (see [Phases](language.md#phases)), so read it again after every call, not only after the enemy turn. It is null before the first battle starts, and for an enemy with no move it can use. For the panel above an enemy, describe the move with the numbers it would deal now, after its Strength and the player's Vulnerable:

```csharp
var text = new DescriptionBuilder(content);
string move = worm.Intent!;                                // "Chomp", for choosing an icon
Description intent = text.DescribeIntent(worm, runtime);   // "Deal 11 damage to the player."
```

`DescribeIntent` is empty while `Intent` is null. It returns the same `Description` as a card's rules text, so it is drawn the same way (below); in an intent, a `Buffed` value is one the enemy hits harder with. `DescribeMove(definition, moveName, runtime, enemy)` describes any one of an enemy's moves, for a bestiary or a tooltip.

## Rules text with live values

For card frames and tooltips, descriptions show the numbers as they are now, after modifiers. With a relic in play that multiplies fire damage by 1.5, such as Pyromancer's Codex in samples/basic:

```csharp
Description text = new DescriptionBuilder(content).Describe(card, runtime, target: worm);
text.ToPlainText();   // "Hurl a ball of flame for 9 damage. Kill it to draw 1."
text.ToMarkup();      // "Hurl a ball of flame for ~~6~~ 9 damage. ..."
```

With nothing modifying it, the same card reads "for 6 damage", and the markup has nothing struck through.

A renderer without markup, such as a SpriteFont, draws the segments instead. Each is either words or a value, and a value carries its printed number (`Base`), its live one (`Current`) and which way the modifiers moved it (`Trend`):

```csharp
foreach (DescriptionSegment segment in text.Segments)
{
    string colour = segment.Trend switch
    {
        ValueTrend.Buffed => "green",    // better for whoever uses it: more damage, a lower cost
        ValueTrend.Debuffed => "red",
        _ => "white",                    // words, and values nothing has changed
    };
    DrawText(segment.Text, colour);      // your renderer; segment.BaseText is the printed value
}
```

`text.Cost` is the card's cost as a live value, for the corner of the frame; it is not part of `Segments`. `text.Tooltips` explains each status or keyword the text mentions that has rules of its own, each with a `Description` of its own, and `text.Flavour` is the flavour line, kept out of the rules text. Automatic rules text is English; `DescriptionBuilder` also takes an `IDescriptionLocalizer` for other languages (see [Extending](architecture.md#extending)).

## Save and load

A snapshot is plain data, taken between actions; restoring it into a runtime with the same content continues the game exactly. Store the content's `Fingerprint` beside it, so that a save made before a content update is recognised before it is restored:

```csharp
sealed class SaveFile
{
    public string Fingerprint { get; set; } = "";
    public GameSnapshot Game { get; set; } = new GameSnapshot();
}
```

```csharp
// using System.Text.Json;
if (runtime.CanCapture)   // false while a save cannot be taken (mid-action included), so a save button can grey out
{
    var save = new SaveFile { Fingerprint = content.Fingerprint, Game = runtime.Capture() };
    File.WriteAllText("slot1.json", JsonSerializer.Serialize(save));
}
```

Loading checks the fingerprint, then restores:

```csharp
SaveFile save = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText("slot1.json"))!;
if (save.Fingerprint != content.Fingerprint)
{
    // A definition has been added, renamed or removed since this save was made.
    // Tell the player, or try it anyway: a save Restore refuses leaves the game as it was.
}

try
{
    runtime.Restore(save.Game);
}
catch (InvalidOperationException error)
{
    ShowMessage(error.Message);   // The snapshot needs card "Defend", which is not loaded.
}
```

`Restore` looks up everything a save needs before it changes anything, so when it refuses one, the game it was called on carries on untouched. That includes a damaged save, one with a list or a record missing, which it refuses as damaged. Restoring into the running runtime keeps its options and the verbs the game registered with `RegisterVerb`, and an `Entity` the game holds stays the same object if the save has it too. A new runtime works as well, given the same options (a real-time game's `TickClock` included) and the same verbs; its entities are new objects, so look them up again: `runtime.Player` is the player, `runtime.State.Actors(Team.Enemy)` the enemies still standing, and `runtime.State.Find(id)` finds anything else by the `Id` it had, which the save keeps.

The fingerprint covers the kinds and names of the definitions, content verbs and resources, and nothing else, so rebalancing a card leaves it unchanged. What a content change does to a save:

| Since the save, the content has | `Restore` |
|---|---|
| changed a definition's numbers or effects | Succeeds. Each restored entity keeps the stats it was saved with, so a card saved at cost 1 stays at cost 1 although the content now says 2, and a stat the definition has since gained reads 0. Effects, listeners and modifiers are the new ones, and copies made from now on have the new numbers. Work waiting in the save is the exception, below. |
| added a definition | Succeeds, though the fingerprint differs. |
| renamed or removed a definition the save uses | Refuses the save with `InvalidOperationException`, before changing anything. |

Work waiting in the save, such as the `next turn:` block of a card played before saving, runs as it was when the game was saved. The save records the block's place in its definition and a hash of its statements, so an edit that moves the block within its definition, such as a line added above it, or that only changes its layout or comments, is safe. If an edit has changed the block's statements or removed it, `Restore` refuses the save, naming the definition, rather than run something else. Saves with nothing waiting are not affected. A save made by 0.1.0-preview.2 or earlier has no hash, so its waiting blocks are found by place alone: after an edit that moves one, it fails to load or runs another block.

Listener limits and timers stay with their listener too. Which turn or battle a `once per` listener last fired in, and when an `on every` listener is next due, are saved with the listener's place among its definition's `on` blocks and a hash of the listener, so an edit that adds, removes or reorders `on` blocks, or only changes their layout or comments, is safe. So is an edit to the body of a listener that keeps its place, such as a new number: a `once per battle` listener that has fired stays used. A listener whose `on` line has changed (its event, filter, `once per`, `priority` or interval), or whose body changed as it moved, is not taken for the old one. It starts afresh, as a newly added listener would: it can fire once more in the turn, battle or run whose limit it had used, and an `on every` listener waits a full interval from the moment of the save. The save is never refused for this, and a record never goes to a listener with a different `on` line. A save made by 0.1.0-preview.2 or earlier records the place alone, so after an edit that adds, removes or reorders `on` blocks it can give a limit or timer to another listener.

`Restore` also refuses a snapshot from a different save format, before touching anything. The Godot node's `LoadSave` treats the fingerprint the same way: a save whose fingerprint differs still goes to `Restore`, and is refused only when `Restore` refuses it (see [Saving](godot.md#saving)).

`Restore` abandons a pending choice, which belonged to the game being replaced. A save taken while a choice is pending holds the game as it was before the call that asked, so after loading, the player makes that move again.

Work that `Execute` schedules, such as `Execute("next turn: draw 1")`, is saved with the statements themselves, so it restores whatever content is loaded. Apart from effects still resolving, one thing stops a save: a waiting block that the loaded content does not contain, either because a [hot reload](#hot-reload) changed it after it was scheduled, or because game code parsed it and ran it through `runtime.Interpreter` itself. `CanCapture` is false, and `Capture` throws, until that block has run. Choices are not affected: under `DeferredChooser`, an action that asks is rolled back to a copy of the game kept in memory, not to a save.

A save is trusted input. It sets every stat, and it holds the text of any work that `Execute` scheduled, which `Restore` parses and the game runs when that work comes due; that text can call any verb, the C# verbs the game registered included. `Restore` refuses text that does not parse, but anything that parses could have come from `Execute`, so an edited save can make the game do anything its content and verbs can. The hashes in a save let `Restore` recognise content; they are no check that the save is unaltered, since anyone editing it can compute them again. A game that loads saves it did not write itself, such as shared, downloaded or cloud saves, should sign them or verify them another way before calling `Restore`.

## Hot reload

Load the changed file, check it, then rebind the running game:

```csharp
DiagnosticBag problems = content.LoadFile("content/cards.cantrip");
if (problems.HasErrors)
{
    ShowProblems(problems.Errors);   // keep playing: the game is still bound to what it had
}
else
{
    CardRuntime.ReloadReport report = runtime.ApplyContentChanges();
}
```

Stats the game has changed keep their values; a card still at its printed cost takes the new one. Listeners keep what they remember: a `once per battle` listener that has fired stays used, and an `on every` listener stays on its interval. They are matched to the reloaded listeners as a restored save matches them (see [Save and load](#save-and-load)), so one whose `on` line the reload changed, or whose body changed as it moved, starts afresh. Definitions that vanished are listed in the report, and their entities keep playing. `report.RulesetChanged` says the ruleset changed, which a running game does not pick up. Call `ApplyContentChanges` between actions: it throws while effects are resolving.

Work already waiting, such as the `next turn:` block of a card played before the reload, runs as it was when it was scheduled. If the reload changed that block, the game cannot be saved until it has run, and `CanCapture` says so.

A file with errors is still loaded, as far as it goes. `LoadFile` replaces everything the file contributed with what the parser could recover, so a definition can come back with a block missing (an `effect` line without its colon leaves a card with no effect) or not come back at all (a misspelt `crad Defend` loses Defend). Rebinding to that would give the running game the broken version, or report the lost definitions as missing, which is why the check comes first. Until the file is fixed and loaded again, the running game keeps the definitions it is bound to, while anything newly made from content, by `AddCard` or `create`, comes from the broken version.

## Shipping content with the game

`LoadFolder` and `LoadFile` read the file system. A desktop build copies the files next to the game and loads them from there, whatever the working directory:

```xml
<ItemGroup>
  <None Update="content\**\*.cantrip" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

```csharp
content.LoadFolder(Path.Combine(AppContext.BaseDirectory, "content"));
```

A build without loose files, as on a phone, reads the text its own way and hands it over with `LoadText(text, name)`. Load the files in ordinal order of their names, as `LoadFolder` does, so that every build loads them in the same order. Embedded resources travel inside the game's own assembly:

```xml
<ItemGroup>
  <EmbeddedResource Include="content\**\*.cantrip" />
</ItemGroup>
```

```csharp
// using System.Reflection;
Assembly assembly = Assembly.GetExecutingAssembly();   // the assembly the files are embedded in
var content = new ContentLibrary();
foreach (string name in assembly.GetManifestResourceNames()
    .Where(n => n.EndsWith(".cantrip", StringComparison.Ordinal))
    .OrderBy(n => n, StringComparer.Ordinal))
{
    using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
    content.LoadText(reader.ReadToEnd(), name);          // the name is what error messages show
}
content.Diagnostics.ThrowIfErrors();
```

The same loop serves MonoGame's `TitleContainer.OpenStream`, given a list of the file names to open, since a title container cannot be listed. Saves record definitions, not file names, so moving content from files to resources keeps them valid.

## When content fails at runtime

The linter and `dotnet cantrip test` catch most mistakes before a game runs, but some only show when an effect does: a lint warning nobody acted on, a loop that runs past the step budget (`max_steps` in the [ruleset](language.md#rulesets)), a C# verb or host function that throws. The failure surfaces as an exception from the call that ran the content, `Play`, `EndTurn` or any other. A content error is a `RuntimeError`, whose message starts with the file, line and column (`` cards.cantrip:21:15: Unknown name `nobody_here`. ``) and whose `Span` says the same; an exception from your own verb or host passes through unchanged. `Execute` also throws `DslException` for statements that do not parse.

The call is not rolled back. What ran before the failure stays done, the triggers queued behind it are dropped, and the game is left between actions, where it can be saved or played on. For a card whose second line failed, the energy is paid, the first line's block is gained, and the card is left in the `play` zone. Under the default chooser the host has already been told the events up to the failure; under `DeferredChooser` it is told none of them.

To put the game back, restore a snapshot taken before the call:

```csharp
GameSnapshot before = runtime.Capture();
try
{
    runtime.Play(card, worm);
}
catch (RuntimeError error)
{
    Log(error.Message);          // file:line:column, then what went wrong
    runtime.Restore(before);     // as if the card had never been played
}
```

Calls the game gets wrong, such as `AddCard` with a name that is not loaded or `CreatePlayer` a second time, throw `ArgumentException` or `InvalidOperationException` before they change anything.

Content the game did not write, such as a mod, deserves limits of the game's own. The step budget and the nesting limit for content verbs come from the content's [ruleset](language.md#rulesets), which that content can raise. `RuntimeOptions.Rules` replaces the ruleset content declares, so take the declared one and cap it:

```csharp
Ruleset rules = content.BuildRuleset();   // what the content declares, or the defaults
rules.MaxStepsPerAction = Math.Min(rules.MaxStepsPerAction, 100_000);
rules.MaxCallDepth = Math.Min(rules.MaxCallDepth, 64);
var runtime = new CardRuntime(content, new RuntimeOptions { Rules = rules });
```

Nothing in the language itself reads files, the network or the system clock; only verbs and functions the game registers can.

## Verbs written in C#

Content can call verbs the game implements:

```csharp
runtime.RegisterVerb("corrupt", call =>
{
    Num amount = call.Number(0, Num.One);
    foreach (Entity target in call.Targets("to"))
    {
        Num added = call.Interpreter.ChangeStat(target, "corruption", AssignOperator.Add, amount, call.Context, call.Span);

        // An event of the game's own, which the host always hears and content can listen for.
        call.Interpreter.Raise(new GameEvent("corrupted") { Source = call.Context.Source, Target = target, Amount = added }, call.Context);
    }
});
```

`ChangeStat` is the primitive behind content's own `gain` and assignments: it keeps a resource within its bounds, removes a status whose counter runs out, kills an actor whose hp reaches 0, and raises `corruption_changed` for any content listening. Writing the stat with `target.SetBase` does none of that. Because `<stat>_changed` is raised only when content listens for it, the verb raises `corrupted` as well, so the host hears about every change.

The linter only knows the verbs and events content defines, so tell it about yours, or it reports each use of the verb as unknown (CT301) and each `on corrupted` as a listener on an event nothing raises (CT304):

```csharp
var options = new LintOptions();
options.HostVerbs.Add("corrupt");
options.HostEvents.Add("corrupted");
IReadOnlyList<Diagnostic> problems = Linter.Lint(content, options);
```

A block the game runs itself, reading it from `EntityDefinition.Blocks` and running it with `runtime.Interpreter.Execute`, goes in `options.HostBlocks`, as in `options.HostBlocks.Add("on_reveal")`. Otherwise the linter reports it as a line that never runs (CT313), since Cantrip itself runs only `effect:`, `move ...:` and listeners. The `cantrip` tool cannot be told about such a block, so run `dotnet cantrip lint` with `--suppress CT313`.

The `cantrip` tool cannot run a verb that lives in your game, so content tests that use one belong in your game's own test suite, run by `DslTestRunner`. Its `ConfigureRuntime` runs on each test's new runtime before the test's first line, which is where the verb is registered, and its `CreateHost` makes each test's host, for the names and functions your game answers. Keep the verb's body in a method, such as `static void Corrupt(VerbCall call)`, so the game and its tests register the same one:

```csharp
var runner = new DslTestRunner(content)
{
    CreateHost = () => new GameHost(),
    ConfigureRuntime = runtime => runtime.RegisterVerb("corrupt", Corrupt),
};
foreach (DslTestResult result in runner.RunAll())
    Assert.True(result.Passed, result.ToString());   // xUnit here; any test framework will do
```

Each test gets a new runtime and a new host. `ConfigureRuntime` runs before the test has a player, so a game that starts its player differently can create one there, as in `runtime.CreatePlayer(hp: 70)`, and the test uses it. For the tool's checks, pass `--suppress CT301,CT304` to `dotnet cantrip validate` and `dotnet cantrip lint`.

The other ways to extend the library, from host names to clocks and translations, are listed under [Extending](architecture.md#extending).

## Tracing, linting and tests

The causality trace records why everything happened; the linter and the DSL test runner are the same ones the `cantrip` tool uses:

```csharp
var traced = new CardRuntime(content, new RuntimeOptions { Trace = true });
// ... play ...
Console.WriteLine(traced.State.Trace.FormatTree());

IReadOnlyList<Diagnostic> problems = Linter.Lint(content);
IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
```

## Where next

- [language.md](language.md) describes everything content can say; its [Built-in events](language.md#built-in-events) table lists each event's fields.
- [architecture.md](architecture.md) explains how the library fits together, and [Extending](architecture.md#extending) lists every seam a game can plug into.
- [stability.md](stability.md) says what may change between previews, which platforms are tested, and what is known not to work yet.
- [src/Cantrip.Sim](../src/Cantrip.Sim) is what `cantrip sim` runs: a scenario runner, three bots that play through this API, and a meter that records what the engine raised.
