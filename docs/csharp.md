# Using Cantrip from C#

> **In Godot?** You want [godot.md](godot.md), not this page. The addon's `CantripRuntime` node wraps everything below and hands it to GDScript as ids and dictionaries, so a Godot game needs none of this C#. Read on if your Godot game is in C# and would rather call the library directly, or to see what the node is doing underneath.

Everything a game does with Cantrip goes through `ContentLibrary`, which loads and checks content, and `CardRuntime`, which plays it. This page is for a game that calls the library itself, whether a console app, a MonoGame or FNA game, or an engine loop of your own, and walks through the parts such a game uses. The [quickstart](quickstart.md) puts the first few together into a playable battle. The package ships its XML documentation, so your editor shows each member's notes as you type.

These docs describe the `main` branch, which can be ahead of the latest release. The changelog's [Unreleased](../CHANGELOG.md#unreleased) section lists what that release lacks, and each release's own docs are in [its tag](https://github.com/AGomnes/Cantrip/tags).

## Load content and play a battle

The examples use the Strike, Defend, Fireball, Frozen, Burn, Kindling and Jaw Worm content in [samples/basic](../samples/basic/content.cantrip). The types live in a handful of namespaces; this page's snippets use:

```csharp
using Cantrip;               // CardRuntime, RuntimeOptions, ActionResult, Num, Team
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

var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 12345 });   // any long is a seed, 0 and negatives too
Entity player = runtime.CreatePlayer(hp: 80, maxEnergy: 3);
runtime.AddDeck("Strike", "Strike", "Defend", "Fireball");
runtime.AddRelic("Kindling");
Entity worm = runtime.SpawnEnemy("Jaw Worm");

runtime.StartBattle();
ActionResult result = runtime.Play("Fireball", worm);   // Played, CannotAfford, OutOfRange...
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
| Everyone on the board | `runtime.State.Actors(Team.Player)`, and `runtime.State.Actors(Team.Enemy)`, each in board order. The player's side is the leader, the heroes beside it and anything they summoned; `runtime.Party` is the living members alone, in the order they take their steps, which is the list to offer input for, and `runtime.Player` is the leader (see [The party](#the-party)) |
| A pile | `runtime.State.ZoneOf(player, Zones.Hand)`, and likewise `Draw`, `Discard`, `Exhaust`, `Powers` and `Relics`. `Zones.Attached` holds what is on an actor rather than in a pile (its statuses and its abilities), and `runtime.AbilitiesOf(who)` is the abilities alone |
| Where a card is | `card.Zone`, such as `"hand"` or `"discard"`; `"play"` while its effect resolves |
| hp, block, energy and other stats | `entity.GetInt("hp")`, after modifiers |
| Statuses and their numbers | `entity.Attached`, and `entity.CounterOf(status.Name)` for each |
| A card's type, for its frame | `card.HasTag("attack")`, or `card.Tags` for all of them, tags added during play included |
| Whether an actor has died | `entity.IsDead`. A dead enemy drops out of `Actors` and waits in the `dead` zone until the next battle starts |
| An enemy's next move | `enemy.Intent` (see [Enemy intents](#enemy-intents)), and `enemy.Phase` for an enemy with [phases](language.md#phases) |
| What the content says about it | `entity.Definition`, an `EntityDefinition`: `HasTag`, `Word` for a word such as `rarity rare`, `ReadString` for a quoted string, and `Stats` for the numbers as written |
| Candidates for a reward screen | `content.Pool("card")`, filtered with `HasTag` or `Word` |
| The same entity later, even in a restored game | its `Id`, with `runtime.State.Find(id)` |
| The whole state as one number, for checking that two runs agree | `runtime.State.ComputeHash()`, a `ulong`; the Godot node prints it as `ComputeHash().ToString("x16")` |

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

`TryResolveName` answers a bare name the same way. Both run in the middle of an effect while the rules wait, so they answer and return without calling back into the runtime. Like [verbs written in C#](#verbs-written-in-c), they are where determinism depends on your code: answer from the game's own state, never from the clock or `System.Random`, or the same seed and inputs stop producing the same game. [Determinism](stability.md#determinism) lists what else to avoid.

## Presenting events in a frame loop

Every call resolves completely before it returns: by the time `Play` answers, the damage is dealt, the triggers have run and the card is in the discard pile. A game with animations records the events as they arrive and plays them back over the following frames. [Built-in events](language.md#built-in-events) lists every event and the fields it carries. The library takes no locks, so make every call from one thread, the one that runs the game's update.

When the host hears about an event depends on the chooser (see [Player choices](#player-choices)):

| Chooser | `OnEvent` is called | Stats read inside `OnEvent` |
|---|---|---|
| any but `DeferredChooser`, including the default `FirstOptionChooser` | during the call, as each event finishes | as they stood when that event finished |
| `DeferredChooser` | once `Play`, `EndTurn`, `Pass`, `StartBattle`, `EndBattle`, `Execute`, `UseAbility` or `Answer` has finished, for all its events in a row; never for an attempt rolled back for a choice | the final values, after the whole call |

Under `DeferredChooser`, the other calls (`AddRelic`, `ApplyStatus`, `Tick`) deliver as the default chooser does.

Events arrive in the order they complete, so an event that wraps others comes after them. A card that hits twice and applies a status reports `damaged`, `damaged`, `status_applied`, then `card_played`; an enemy's move reports its hits, then `move`. Listeners resolve after the action that triggered them (unless the [ruleset](language.md#rulesets) says `triggers: immediate`), so whatever they cause arrives after it too: a relic that hits back when the player is hit reports its damage after the enemy's `move`.

A player expects to see the cause first, so present those two the other way round:

- **A card the player plays.** The game knows the card and its target when it calls `Play`, so start the card's animation as soon as `Play` returns `Played` (or the `Answer` that finishes it does), and treat `card_played` as the animation's end, where the card lands in its pile.
- **An enemy's move.** By the time `EndTurn` returns, the whole enemy turn is in the queue. The events a move's own lines raise name the enemy as their `Source`, so when one of them comes up while that enemy's `move` is still waiting further on, show the move first. The enemy's `turn_start` and `turn_end` name it too, but are not part of its move.

Never call back into the runtime from `OnEvent`, under either chooser: the call that raised the event has not returned yet. Record the event and act once the call is over, as this screen does.

That rule is about `OnEvent` and the other [host callbacks](#the-host), which the interpreter invokes in the middle of resolving. It is not about a game noticing that a battle has ended: once `Play`, `EndTurn` or `Execute` has returned, `runtime.Won` is settled and acting again (the reward, the gold, the next `StartBattle`) is ordinary. The Godot node says the same thing with a signal, and [Acting from a signal](godot.md#acting-from-a-signal) spells it out there.

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
        ActionResult result = runtime.Play(card, target);   // resolves at once, and its events are queued
        if (result == ActionResult.Played) showing = ShowCardFlying(card, target);   // shown before its events
        else if (result == ActionResult.ChoicePending) ShowChoice(runtime.Pending!);   // see Player choices
    }

    public void EndTurnPressed()
    {
        if (Busy) return;
        // The whole enemy turn resolves here, and its events are queued.
        if (runtime.EndTurn() == ActionResult.ChoicePending) ShowChoice(runtime.Pending!);
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
                return ShowCardLanded(gameEvent.Action!);   // into the pile it went to: Action.Zone
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
| Every member's hand going to the discard pile, retained cards apart | during `EndTurn`, after each member's `turn_end`; ethereal cards raise `exhausted` |
| Block falling to 0 and energy refilling | at each `turn_start` |
| A stat changing other than by damage, healing or gaining block, such as the energy paid for a card or `lose 3 hp`, and a status's number changing other than by `apply`: `stacks -1`, `decay`, or `gain 2 Strength` on a host that already has Strength | whenever it happens |
| An enemy's new intent | when intents are rolled: at the start of a battle, after the enemy turn and when an enemy joins; and when a hit takes the enemy into a `retelegraph` phase |
| An enemy's phase | when a hit takes the enemy across a phase's threshold, and otherwise when its intent is rolled |
| Tags added or taken away | whenever it happens |
| The draw pile shuffled, and enemies that died in an earlier battle taken away | during `StartBattle`; shuffling the discard pile into the draw pile raises `shuffled` |
| Every party member's statuses taken away, unless `persistent`, and every card back in its owner's draw pile | when the battle ends, after `battle_end` |

A stat change, the turn-start resets included, does raise `<stat>_changed`, but only when some content listens for it. `CreatePlayer`, `AddHero`, `AddCard` and `AddDeck` raise nothing, and nor do `Restore` and `ApplyContentChanges`; of the setup calls, `AddRelic` (`obtained`), `SpawnEnemy` (`created`) and `ApplyStatus` (`status_applied`) raise an event. An event that a listener cancels never reaches the host; one that an `instead_of_` listener replaced does, with `Replaced` true. So when the queue is empty, redraw the hand, the piles, the bars and the intents from the live state.

## Player choices

Choices (targets, `choose`, `discard 2`) go through a pluggable `IChoiceProvider`: `FirstOptionChooser` (the default), `RandomChooser`, `ScriptedChooser`, or your UI.

```csharp
runtime.Chooser = new RandomChooser(seed: 7);
```

A UI cannot answer on the spot, so it uses `DeferredChooser`. An action that needs a decision rolls back to where it started and says so; answering replays it, deterministically:

```csharp
runtime.Chooser = new DeferredChooser();

if (runtime.Play(card) == ActionResult.ChoicePending)
{
    PendingChoice choice = runtime.Pending!;    // Prompt, Options, Min, Max
    Entity chosen = choice.Options[0];          // ... whichever the player picked ...
    runtime.Answer(chosen.Id);                  // ChoicePending again if it needs another
}
```

`Answer` takes the ids of the picked entities, as here, or the entities themselves in a list, as below.

Nothing happens until the action completes: host events are held back, so the game never animates a hit that was rolled back. While a choice is pending, the game is exactly as it was before the call: the card being played is still in the hand with its cost unpaid, and the `Options` of a choice from the hand leave it out. So draw it as being played yourself, until the call that finishes it returns `Played` or the choice is cancelled.

`Prompt` is the engine's short summary of what is asked, in English: `choose a target`, `discard 2`, `exhaust 1`, `choose 1`, or `discover 1 of 3` for an offer. It suits a log rather than the player. Word what the player sees from the card being played and the options instead: each option's `Zone` says which pile it is in, `Chooser` is who chooses, and `Span` points at the line of content that asked.

`Play` is not the only call that can stop. A relic whose turn-end effect asks the player to choose stops `EndTurn`: the whole call, enemy turn included, is rolled back and runs again once the choice is answered. Every call that can stop says so the same way, by returning `ActionResult.ChoicePending`: `Play`, `Answer`, `StartBattle`, `EndTurn`, `Pass`, `EndBattle`, `Execute` and `UseAbility`.

`UseAbility` answers with the same type, so a cooldown is a different answer from a question:

```csharp
switch (runtime.UseAbility(ability, target))
{
    case ActionResult.Played:        ShowAbility(ability); break;
    case ActionResult.NotReady:      FlashCooldown(ability); break;
    case ActionResult.ChoicePending: ShowChoice(runtime.Pending!); break;
    case ActionResult.NotACard:      break;   // gone, or its owner is dead
}
```

An ability settles its own target from its `target` and `range` lines when none is given, so the three targeting refusals are all real answers: `NoTarget` when there is nobody on the side it asks for, `OutOfRange` when it cannot reach from where its owner stands, and `InvalidTarget` when the one it was handed is somebody it will not take. An ability has no cost: its price is the seconds it makes you wait, and a `cost` line on an `ability` declaration is refused at lint as CT339. So it never answers `CannotAfford`: that one serves cards, and it is in the type for the day an ability does have a cost, so adding one would not change this signature.

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

A chooser that answers on the spot handles offers by overriding `IChoiceProvider.ChooseDefinition`; `RandomChooser` and `ScriptedChooser` already do. It has a default that takes the first candidate, so a chooser that only decides between live entities still runs.

## The party

`CreatePlayer` makes a party of one, and that member is the leader. A game with more than one hero adds the rest from `hero` declarations:

```csharp
Entity leader = runtime.CreatePlayer("Crusader", hp: 38);
Entity vestal = runtime.AddHero("Vestal");          // with the abilities its declaration lists

// The leader is the one member no declaration describes, so a stat it needs is written onto it.
// Under `order: speed` this decides the whole round: an actor with no `speed` reads 0 and takes
// its step last, and the leader's step is when the party's hand is drawn.
runtime.SetStat(leader, "speed", 6);
runtime.StartBattle();

foreach (Entity member in runtime.Party)            // the living members, in step order
{
    if (!runtime.CanAct(member)) continue;
    // ... let the player act with this member ...
    runtime.Pass(member);                           // done for this turn
}
```

When the last member that could act has passed, the enemies take their turn and the next one begins. So for a party of one, `Pass` is `EndTurn`, to the turn number and the state hash. A game that never calls any of this is unchanged: `Party` is `[Player]`, `CanAct(Player)` is true while it is the player's turn, and `EndTurn` means what it always did.

| Member | What it answers |
|---|---|
| `Party` | the living members, in the order they take their steps |
| `Fallen` | the party's dead, in the order they fell. `Party` and `State.Actors` both leave them out, so this is the list a shrine that offers to raise somebody reads; `State.Fallen(team)` answers for either side. Content calls the same group [`fallen`](language.md#death-and-revival). |
| `Player` | the leader: the one `CreatePlayer` made, the one that holds the run's relics and gold. Not nullable: before `CreatePlayer` it throws, and `HasPlayer` is the question to ask in the few lines where that is in doubt |
| `AddHero(name, hp = null)` | adds a member from a `hero` declaration, with its abilities |
| `CanAct(member)` | a battle is running, the member is alive and has not passed, and either it is the party's turn or, under `turns: initiative`, this member's own step |
| `Pass(member)` | that member is done this turn; the last one ends the turn |
| `ActiveMember` | the member whose step it is, or null when none of ours is. Binding under `turns: initiative`; under `turns: sides` it is the one the engine would offer next (the first that has not acted), which a UI highlights and `CanAct` overrules |
| `Revive(actor, hp = 1)` | brings a fallen actor back. False for one that was never dead |
| `SetStat(entity, stat, value)` | writes a stat, exactly as content's `speed = 6` does: the resource's bounds, the `<stat>_changed` event, and death when hp reaches zero. Returns the change applied |
| `ChangeStat(entity, stat, by)` | adds to a stat, or takes away with a negative amount: content's `gain 2 gold` and `lose 2 gold`, and how a shop spends the run's purse |
| `Entity.IsPartyMember` | true for the leader and every `hero`; false for a summon standing beside them |
| `State.TurnOrder` | every living combatant on both sides, in the one order `turns: initiative` runs them in (what an order bar draws) |
| `State.HasActed(actor)` | whether that combatant has already taken its step this round |

**Playing a card with a named performer.** `Play(card, target, performer)` is how one member plays out of the party's hand: the cost comes out of the card controller's pool, and everything else is the performer's (`card_played`'s source, the damage, `source:` filters, that member's own statuses and modifiers). No performer means the card's own controller.

```csharp
runtime.Play(sanctuary, crusader, performer: vestal);
```

**Abilities.** `CanUse(ability)` is `CanPlay`'s companion: the owner is alive, the cooldown is up, and one that needs somebody to point at has somebody. `LegalTargets` and `TargetMode` answer for an ability as well as a card, so the same targeting UI serves both. A cooldown belongs to the ability entity, so two members with the same ability have two of them.

**Two turn modes.** `turns: sides` is the default: the party takes one turn between them, every member's `turn_start` fires at its start, and the game acts with them in any order. `turns: initiative` puts both sides in one order (set by `order: position` or `order: speed`), so a hero acts between two enemies and each combatant's `turn_start` and `turn_end` fire at its own step. There, `ActiveMember` drives the loop:

```csharp
while (runtime.Won == null)
{
    Entity? up = runtime.ActiveMember;              // null while the enemies are taking their steps
    if (up == null) break;
    // ... let the player act with this member ...
    runtime.Pass(up);                               // runs the round on to the next of ours
}
```

`Pass` on anyone but `ActiveMember` throws there, because the order is the rule rather than a suggestion. `EndTurn` still means "pass everyone of ours who has not acted", and the rest of the round happens around them. **One round is one turn in both modes**: `State.Turn` is the round number and the clock advances once a round, so `once per turn`, `on every N turns` and every saved turn number mean what they always meant. For a party of one against one enemy the two modes play the same round.

**Losing.** The battle is lost when no member is alive, not when the leader dies. A surviving summon does not keep it going.

## The board

A battle is fought on a **board**: `lanes` across, and `ranks` along the axis the two sides face
each other on, both counting from 0. Content declares the shapes, which [Boards](language.md#boards)
sets out, and a game with more than one says which fight is fought where:

```csharp
runtime.StartBattle(board: "Train");   // this fight is on the train
runtime.StartBattle();                 // this one is wherever the last was
```

A name no `board` declaration matches throws `ArgumentException`, naming the closest declared
board rather than inventing one: the linter has to know how deep a board is to check what can reach
across it. Content that declares no board is played on one lane with unbounded ranks, facing sides
and a manhattan metric, which is the board every game was on before 1.0, so a game that never
mentions one notices none of this.

| To know | Read |
|---|---|
| Where an actor stands | `entity.Lane` and `entity.Rank`, both from 0, or `entity.Slot` for the pair. `entity.Position` is an alias for `Rank` and stays for the whole 1.x line |
| The shape in play | `runtime.State.Board`, a `BoardShape`: `Lanes`, `Ranks`, `RanksAreUnbounded`, and `LaneWord` and `RankWord` for a game that calls a lane a floor |
| How far apart two are | `runtime.State.Distance(a, b)`, in steps, by the board's metric. Across a facing board the rank term is `a.Rank + b.Rank + 1`, so two front-rank actors are one step apart however deep the board is. Anything not on the board is `int.MaxValue` |
| Who is one step away | `runtime.State.Neighbours(actor)`, **on that actor's own side** |
| Who is on a slot | `runtime.State.At(team, lane, rank)`, or `null` |
| Whether a lane has room | `runtime.State.HasRoom(team, lane)` |
| Where an arrival stands | `runtime.Place(actor, lane, rank)`: see [Placing what arrives](#placing-what-arrives) |

`LegalTargets` already answers what an action can reach, so a targeting highlight needs no
distance arithmetic of its own. Content moves an actor by writing `target.rank = 0`, and `Place`
is that write from C#.

## Winning, losing and several battles

`runtime.Won` is null while a battle runs, and before the first one; once a side is gone it is true or false. A battle ends when no party member is alive or no enemy is left alive, so check `Won` after each call, or listen for `battle_end`, whose data holds `won`.

**A fight that does not end with the last enemy.** A wave defence or a survival run has an empty board every few seconds by design, and under the default rule the first gap between two waves wins it. Content says so once, in its ruleset:

```
ruleset
  ends: called
```

An empty board is then just an empty board: the battle runs until the party falls, which is still the rules' own answer, or until the game says otherwise with `runtime.EndBattle(won)`. That call raises `battle_end`, ends the temporary statuses, sends the cards home and sets `Won`, exactly as the last enemy falling does. It works under the default rule too, for a retreat or a surrender.

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
| Every party member, with its hp, max hp and any other stats | Enemies: the dead are removed when the next battle starts |
| Every card still in the game, back in its owner's draw pile, exhausted cards and cards made during the battle included | Every member's statuses, unless flagged `persistent` |
| Relics, and `once per run` limits | `until` effects, which are undone, and scheduled work, which is dropped |
| | `once per battle` limits, the battle's history counters and the turn number |

A card made during a battle, such as a Wound, stays in the deck like any other. To make it temporary, take it out between battles with `runtime.RemoveCard(wound)`, which is `destroy` in content and the same call the [Godot node](godot.md#removing-upgrading-rewards-and-a-new-run) has. An upgrade is the same two calls on either side: `RemoveCard(censer)` then `AddCard("Censer+")`.

When a run is over and the game starts a new one with a new runtime, let the old one go with `runtime.Dispose()`. A runtime listens to its clock from the moment it is built. That is how scheduled work, `on every` triggers and timed statuses run. So a runtime given a clock through `RuntimeOptions.Clock` that the game keeps using goes on resolving effects on a game nobody is playing, and the clock holds it alive while it does. A runtime that made its own clock, which is every turn-based game that passes no clock, is collected with it either way. `Dispose` tears nothing else down: the state, the entities and the content are ordinary objects and still read afterwards.

[src/Cantrip.Sim/ScenarioRunner.cs](../src/Cantrip.Sim/ScenarioRunner.cs) is a worked example of a run above the battle: one runtime carries hp, deck and relics from fight to fight, and whatever the scenario writes between them (`heal 12`, `relic "Ember Charm"`) runs as a statement.

## Real time

A real-time game measures time in **ticks** instead of turns. Everything else on this page is the same: the same runtime, the same cards and abilities, the same board, the same saves. What changes is what makes time pass, and that nothing takes a turn.

Two things have to agree. Content says which clock it is written for, so that `cooldown 6s` and `2 turns` are checked rather than hoped for:

```
ruleset
  clock ticks
```

and the game gives the runtime a `TickClock`, whose one argument is **how many ticks make a second**:

```csharp
using Cantrip;
using Cantrip.Runtime;

var clock = new TickClock(20);                     // 20 ticks a second
var runtime = new CardRuntime(content, new RuntimeOptions { Clock = clock, Seed = seed });
```

**The tick rate is part of the game, not part of the machine.** Every `cooldown 1s`, `for 3s`, `in 2s:` and `on every 2s:` in the content converts through it, so the same content at `new TickClock(20)` and at `new TickClock(60)` is the same game. But a rate the content was not balanced against is a different one. Keep the number beside the content it belongs to, not beside the frame rate. Content whose ruleset says `clock ticks` and which is given no clock gets a `TickClock(60)` of its own, which is a reasonable default and nobody's considered choice.

Then `Tick` is the whole of the game's clock:

```csharp
void FixedUpdate()                                  // your engine's fixed timestep
{
    runtime.Tick();                                 // or Tick(n) to catch up several at once
}
```

`Tick(n)` and `n` calls to `Tick(1)` are the same game, to the state hash: a listener whose due tick falls inside a multi-tick call fires at that call and keeps its original schedule. Drive it from a **fixed** step and never from a rendered frame, because a step measured from how long the last frame took makes the simulation depend on the frame rate.

### No turns means no turns

Every turn-shaped call refuses on a tick runtime, the way `Tick` refuses on a turn-based one:

| Called on a `TickClock` runtime | |
|---|---|
| `EndTurn()`, `Pass(member)`, `CanAct(member)`, `ActiveMember` | throw `InvalidOperationException`, with a message naming `Tick()` |
| `State.Turn` | stays 0 for the whole fight |
| `turn_start`, `turn_end` | never raised |

So a shared front end asks before it draws an **End turn** button. There is no flag to read from C#, because the runtime's own clock answers it: `runtime.State.Clock is TickClock`. (The Godot node, which makes its own, has `IsRealTime()`.)

Half the turn-based vocabulary follows the turn events and is therefore dead under ticks: `move`, `pattern`, `phase`, `stacking duration`, `decay ... on turn_end`, `until turn_end:`, `next turn:`, `once per turn`, `reset_on turn_start`. **The linter refuses all of it as CT337** when the ruleset says `clock ticks`, and each message names the real-time shape of the same idea, so this is a thing content is told once and not a rule to remember. In particular: an enemy's whole behaviour is `on every <n>s:` listeners, and a status ends because something said `for <n>s` where it was applied.

**`cantrip sim` cannot play a real-time game and refuses to try.** When to act in continuous time is the game's own frame loop, not a bot's. Cover a real-time game with `test` blocks, which have `realtime <rate>` and `tick <n>`.

### Placing what arrives

A wave game decides where things walk in, which is a decision above the fight:

```csharp
Entity hollow = runtime.SpawnEnemy("Hollow");       // raises `created`, so content can meet it
runtime.Place(hollow, lane: 1, rank: 3);            // and the game says where it arrives
```

`Place` is content's `target.rank = 0` from C#: it raises `moved`, a `before_moved` listener can refuse it, and it answers whether the actor stands there afterwards. A slot the board does not have throws `ArgumentException`, naming the board and its shape, rather than being clamped, because a wave arriving at a rank that does not exist is a bug in the schedule. Writing a slot that is taken **swaps** the two actors, which is the same rule content gets.

`SpawnEnemy` raises `created`, the same event `create` raises, so an arrival is something content can hear: an entrance effect, a relic that reacts to anything joining the fight, an enemy that places itself. It is an announcement rather than a gate: the enemy is already in the game when it is raised, so a `before created:` listener cannot cancel a spawn the game has decided on.

### What a real-time interface reads

A row of ability buttons with cooldown sweeps is three calls:

```csharp
foreach (Entity ability in runtime.AbilitiesOf(member))
{
    bool usable = runtime.CanUse(ability);                        // off cooldown, and something in reach
    double seconds = (double)runtime.ReadyIn(ability) / clock.TicksPerSecond;
    DrawButton(ability.Name, usable, sweep: seconds);
}
```

| Call | |
|---|---|
| `AbilitiesOf(owner)` | the abilities that actor is carrying, in the order they were granted |
| `GrantAbility(name, owner)` | gives one from an `ability` declaration. A `hero`'s own are granted by `AddHero` |
| `IsReady(ability)` | whether its cooldown has run out |
| `ReadyIn(ability)` | how much longer it has to wait, **in clock units**: ticks here, turns on a turn clock. 0 when it is ready |
| `CanUse(ability)` | ready, its owner alive, and somebody in reach if it needs one: what a button is greyed out on |
| `LegalTargets(ability)`, `TargetMode(ability)` | answer for an ability exactly as for a card, so one targeting UI serves both |
| `UseAbility(ability, target = null)` | with no target it settles its own from the ability's `target` and `range`, and answers `NoTarget`, `OutOfRange` or `InvalidTarget` when it cannot be aimed |
| `clock.Now` | what time it is, in ticks. Divide by `TicksPerSecond` for a read-out |

Content can ask `IsReady` and `CanUse` itself, which is how a test says what a keeper may *not* do yet: `leader.is_ready(Bulwark)` is whether the cooldown has run out, and `leader.can_use(Bulwark)` also asks whether anything is in reach.

### Saving a running clock

A save carries the clock's position, every cooldown's due tick, a burn part way through its second, a `for 3s` buff part way through its three and an `in 2s:` effect still in the air. Restore it into a runtime with a `TickClock` of the same rate and both copies play on identically, hash for hash. `IGameClock.Restore` puts the clock back where it was.

What a save does **not** carry is anything above the fight: a wave schedule, a mission timer, the score. Those are the game's, and go beside the save in the game's own file.

### A whole one

[realtime/](../realtime) is a complete real-time game built on this (*Emberline*, a forty-five second hold against waves), with the content, a headless C# host and a Godot front end. [realtime/host/Emberline.cs](../realtime/host/Emberline.cs) is the shortest thing to read first: a clock, a fixed timestep, a wave schedule and an ending.

## Enemy intents

`Entity.Intent` is the name of the move an enemy will make next. It is rolled when the battle starts, after each enemy turn, and for an enemy that joins mid-battle. A phase marked `retelegraph` also rolls it again the moment a hit moves the enemy into that phase (see [Phases](language.md#phases)), so read it again after every call, not only after the enemy turn. It is null before the first battle starts, and for an enemy with no move it can use. For the panel above an enemy, describe the move with the numbers it would deal now, after its Strength and the player's Vulnerable:

```csharp
var text = new DescriptionBuilder(content);
string move = worm.Intent!;                                // "Chomp", for choosing an icon
Description intent = text.DescribeIntent(worm, runtime);   // "Deal 11 damage to the player."
```

`IntentTargetOf(enemy)` is who that move is aimed at: the member the enemy telegraphed, while that member is still a legal target, and otherwise whoever is left. **It is recomputed on every call**, so a taunt applied mid-turn, a death or a swap changes what the panel shows with no event for the UI to have missed. It agrees with what the move itself does when it runs. It is null while `Intent` is. `Entity.IntentTarget` is the raw telegraph as it was rolled, which is what a save holds; a UI wants `IntentTargetOf`.

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

    // Your run: the floor, the map, which rewards have been offered, the run's own random stream.
    // Cantrip has no idea what a run is, so `Game` holds the battle, the party, the deck, the
    // relics and the gold, and nothing above them. The file is a pair, and every write and every
    // read has to keep the two halves together or a restored game lands on the wrong floor.
    public RunState Run { get; set; } = new RunState();
}
```

```csharp
// using System.Text.Json;
if (runtime.CanCapture)   // false while a save cannot be taken (mid-action included), so a save button can grey out
{
    var save = new SaveFile { Fingerprint = content.Fingerprint, Game = runtime.Capture(), Run = run };
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

`Restore` looks up everything a save needs before it changes anything, so when it refuses one, the game it was called on carries on untouched. That includes a damaged save, one with a list or a record missing, which it refuses as damaged. Restoring into the running runtime keeps its options and the verbs the game registered with `RegisterVerb`, and an `Entity` the game holds stays the same object if the save has it too. A new runtime works as well, given the same options (a real-time game's `TickClock` included) and the same verbs; its entities are new objects, so look them up again: `runtime.Party` is the living members, `runtime.State.Actors(Team.Enemy)` the enemies still standing, and `runtime.State.Find(id)` finds anything else by the `Id` it had, which the save keeps.

The fingerprint covers the kinds and names of the definitions, content verbs and resources, and nothing else, so rebalancing a card leaves it unchanged. What a content change does to a save:

| Since the save, the content has | `Restore` |
|---|---|
| changed a definition's numbers or effects | Succeeds. Each restored entity keeps the stats it was saved with, so a card saved at cost 1 stays at cost 1 although the content now says 2, and a stat the definition has since gained reads 0. Effects, listeners and modifiers are the new ones, and copies made from now on have the new numbers. Work waiting in the save is the exception, below. |
| added a definition | Succeeds, though the fingerprint differs. |
| renamed or removed a definition the save uses | Refuses the save with `InvalidOperationException`, before changing anything. |

Work waiting in the save, such as the `next turn:` block of a card played before saving, runs as it was when the game was saved. The save records the block's place in its definition and a hash of its statements, so an edit that moves the block within its definition, such as a line added above it, or that only changes its layout or comments, is safe. If an edit has changed the block's statements or removed it, `Restore` refuses the save, naming the definition, rather than run something else. Saves with nothing waiting are not affected. A save made by 0.1.0-preview.2 or earlier has no hash, so its waiting blocks are found by place alone: after an edit that moves one, it fails to load or runs another block.

Listener limits and timers stay with their listener too. Which turn or battle a `once per` listener last fired in, and when an `on every` listener is next due, are saved with the listener's place among its definition's `on` blocks and a hash of the listener, so an edit that adds, removes or reorders `on` blocks, or only changes their layout or comments, is safe. So is an edit to the body of a listener that keeps its place, such as a new number: a `once per battle` listener that has fired stays used. A listener whose `on` line has changed (its event, filter, `once per`, `priority` or interval), or whose body changed as it moved, is not taken for the old one. It starts afresh, as a newly added listener would: it can fire once more in the turn, battle or run whose limit it had used, and an `on every` listener waits a full interval from the moment of the save. The save is never refused for this, and a record never goes to a listener with a different `on` line. A save made by 0.1.0-preview.2 or earlier records the place alone, so after an edit that adds, removes or reorders `on` blocks it can give a limit or timer to another listener.

The Godot node's `LoadSave` treats the fingerprint the same way: a save whose fingerprint differs still goes to `Restore`, and is refused only when `Restore` refuses it (see [Saving](godot.md#saving)).

### What a save says about itself

Four fields at the top of a `GameSnapshot` describe the save rather than the game.

| Field | What it is |
|---|---|
| `FormatVersion` | The format the save is written in, `GameSnapshot.CurrentFormat` at the time it was written. It only ever increases, and it moves whenever the shape of a save changes at all, an added field included. |
| `MinimumReader` | The oldest `CurrentFormat` that can be trusted with the save. It moves only when a change would make an older build get the game *wrong*, rather than merely miss something it never knew about. 0 in a save made before it was recorded, which is read as `FormatVersion`. |
| `WrittenBy` | The version of Cantrip.Core that wrote the save, such as `1.0.0`, as `cantrip --version` gives it. Nothing branches on it; a refusal quotes it, and [Stability](stability.md) asks you to keep it with a replay or a bug report, so the engine now keeps it for you. Empty in a save made before it was recorded, and in a `GameSnapshot` built by hand rather than captured. |
| `RngGenerator` | The generator the saved random state came from, `xoshiro256**` today. Empty means that one: four numbers can only be a game's random future while something says what reads them, and a release may change the generator. |

**Older saves keep loading.** `Restore` refuses a save only when it needs a reader this build is not (when `GameSnapshot.ReaderNeededBy(save)` is above `GameSnapshot.CurrentFormat`), and says so, naming the version that wrote it:

> This save is in format 5 and needs a Cantrip that reads format 4; this one reads up to format 3. It was written by Cantrip 1.4.0.

Anything older goes through an upgrade step first, which brings it into the current shape in place; the `GameSnapshot` you passed comes back at `CurrentFormat`. A save from a *newer* release is read whenever that release said it could be (that is what `MinimumReader` is for), and fields this build has never heard of are ignored. Call `GameSnapshot.ReaderNeededBy` yourself before restoring if you would rather tell the player that a save needs a newer version of your game than catch the exception.

That is the promise in [Stability](stability.md): after 1.0, a save made by any 1.x release loads in every later 1.x. Bumping `FormatVersion` is how a release describes its saves honestly, not how it stops reading old ones.

A save whose `RngGenerator` this build does not have is refused in the same way, before anything changes, rather than restored with four numbers another generator would read differently.

**Format 3**, which this release writes, adds the [board](language.md#boards): `BoardName` and `Board` on the save, and a `Lane` on every entity beside the `Position` that is now its rank. A format 2 save was played on one lane with no floor, which is exactly the default board, so the upgrade step gives everyone lane 0 and the default board and moves nothing else. A patch that *reshapes* a board refuses a save only when a saved actor stands outside the new bounds, naming the board and where that actor was standing; growing a board loads fine. A save whose board the loaded content no longer declares at all is played on the shape the save itself carries, so an edit never strands a save. `MinimumReader` moves to 3 with it: a format 2 reader handed one of these saves would put every actor in lane 0, which is getting the game wrong rather than missing a field.

**Format 2** renamed two fields of `ScheduledSnapshot`, because a name on disk is frozen at 1.0 and these two said the wrong thing: `Block` is now `BlockAddress` (it holds an address such as `card:Prepare/effect/0.body`, while `block` in the language is a stat, a verb and a modifier channel), and `Deadline` is now `UntilEvent` (it holds the event an `until` block waits for, and sat beside `DueAt`, which is a time). Format 1 saves are still read under both old names, and are not written back with them.

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

- [godot.md](godot.md) is the same ground for a Godot game: the node that wraps these calls, the dictionaries it hands GDScript, and the editor dock.
- [language.md](language.md) describes everything content can say; its [Built-in events](language.md#built-in-events) table lists each event's fields.
- [architecture.md](architecture.md) explains how the library fits together, and [Extending](architecture.md#extending) lists every seam a game can plug into.
- [The API reference](api/README.md) lists every public type and member of `Cantrip.Core`, generated from the sources. This page teaches the calls a game needs, in the order it needs them; that one is the list of everything.
- [troubleshooting.md](troubleshooting.md) is the other end of this page: what each refusal means, how to read a `RuntimeError` and a trace, what every save refusal is telling you, and what a published game has to do differently.
- [stability.md](stability.md) says what may change in a 1.x release, which platforms are tested, and what is known not to work yet.
- [src/Cantrip.Sim](../src/Cantrip.Sim) is what `cantrip sim` runs: a scenario runner, three bots that play through this API, and a meter that records what the engine raised.
