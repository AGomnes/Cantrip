# Slice friction log

Roadmap step 3 asks whether the library holds up inside a real roguelite. The slice in `samples/slice` is that roguelite, played headlessly by `src/Cantrip.Sim`. This log records every place where building it needed a workaround, C# the content could not replace, or a surprise. Each entry is classed as a **language gap**, an **API gap**, a **defect**, or **fine as host code**.

## Found while building the first cut

1. **Defect: the Godot adapter's legal targets ignore `targetable`.** `CantripRuntime.GetLegalTargets` lists every living actor on the other side, but `CardRuntime.Play` filters candidates through the `targetable` channel. With a taunt or stealth on the board, a UI would highlight targets that the play then refuses as `invalid_target`. The core has the correct filter (`Targetable`, `IsTargetable`) but keeps it private.
2. **API gap: "what can I play, and at what?" is not in the core.** The simulator's bot had to rebuild it from `CostOf`, `CostResourceOf`, `IsXCost`, the `unplayable` tag and `Definition.Word("target")`, which is the same code the Godot adapter's `CanPlay` and `GetLegalTargets` already duplicate. A public `CanPlay(card)` and `LegalTargets(card)` on `CardRuntime` would serve both, and would fix the defect above in one place.
3. **API trap: `entity.Get("Burn")` reads 0.** In content, `enemy.Burn` is the status's counter. From C#, `Entity.Get` only reads stats, so the same name silently gives 0. The bot's first scoring function counted no Burn at all because of this. `FindAttached("Burn")?.GetInt("stacks")` works; a `StatusCounter(name)` helper, or making `Get` fall back to a status the way content does, would remove the trap.
4. **Fine as host code, for now: the run.** Hp carries over by itself, since the player entity persists. `AddCard` and `AddRelic` between battles, and `Execute("heal N")` for a rest, were all that was needed. What the host does own is the map, the encounter list and reward pools: `encounter` is a declaration the parser accepts and nothing consumes, so the tower is a C# array. Gap 17 in [coverage.md](coverage.md) stays open, but this slice did not suffer for it. Revisit once a run needs rules that content should own, such as events between battles or relics that act on the map.
5. **Worked around: a threshold on a status's own stacks.** Three Chill should become Frozen. That is written as a content verb, `chill`, that applies Chill and then checks the count. It works, and it is tested, but any content that writes `apply Chill` directly skips the check. Worth finding out whether a status can reliably watch its own stacks growing (`on status_applied` on itself) before calling this a gap.

## What went well

- Every DSL test for the slice passed on the first run, and lint was clean apart from one intended loop note on Frozen.
- The runtime already supported several battles in a row with one runtime: `StartBattle` counts battles, cards return to the draw pile, and non-persistent statuses clear.
- Snapshots are cheap enough to drive a lookahead bot: 500 whole runs with a one-card lookahead take about ten seconds, and every run replays exactly from its seed.
