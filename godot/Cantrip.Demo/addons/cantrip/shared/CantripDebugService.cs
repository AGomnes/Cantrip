#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;

namespace Cantrip.GodotAdapter
{
    /// <summary>What a game says about itself when the editor first reaches it.</summary>
    public sealed class CantripHello
    {
        internal CantripHello(int protocol, int generation, string fingerprint, int definitions, bool inBattle, int turn, bool tracing)
        {
            Protocol = protocol;
            Generation = generation;
            Fingerprint = fingerprint;
            Definitions = definitions;
            InBattle = inBattle;
            Turn = turn;
            Tracing = tracing;
        }

        /// <summary>The protocol the game speaks, so an editor of another age can say so plainly.</summary>
        public int Protocol { get; }

        public int Generation { get; }

        /// <summary>The content the game is running, as the core hashes it.</summary>
        public string Fingerprint { get; }

        public int Definitions { get; }

        public bool InBattle { get; }

        public int Turn { get; }

        public bool Tracing { get; }

        public override string ToString() => $"protocol {Protocol}, {Definitions} definition(s), {Fingerprint}";
    }

    /// <summary>What came of reloading content into a running game.</summary>
    public sealed class CantripReloadResult
    {
        internal CantripReloadResult(bool applied, IReadOnlyList<Diagnostic> diagnostics, int rebound, IReadOnlyList<string> missing, bool rulesetChanged)
        {
            Applied = applied;
            Diagnostics = diagnostics;
            Rebound = rebound;
            Missing = missing;
            RulesetChanged = rulesetChanged;
        }

        /// <summary>
        /// False when the new content has errors. The files are loaded either way, so the editor can
        /// show what is wrong, but nothing live is rebound: a designer saving a half-written file
        /// mid-battle should not have it applied.
        /// </summary>
        public bool Applied { get; }

        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public int Rebound { get; }

        /// <summary>Definitions live entities still use that the reloaded content no longer has.</summary>
        public IReadOnlyList<string> Missing { get; }

        /// <summary>The ruleset changed; a running game keeps the rules it started with.</summary>
        public bool RulesetChanged { get; }

        public override string ToString() =>
            Applied ? $"{Rebound} rebound" : $"not applied, {Diagnostics.Count} problem(s)";
    }

    /// <summary>What came of running statements from the editor's console.</summary>
    public sealed class CantripExecuteResult
    {
        internal CantripExecuteResult(bool ok, string message, SourceSpan span)
        {
            Ok = ok;
            Message = message;
            Span = span;
        }

        public bool Ok { get; }

        /// <summary>Empty when it worked; otherwise why it did not, ready to show.</summary>
        public string Message { get; }

        /// <summary>Where it went wrong, when the failure knows.</summary>
        public SourceSpan Span { get; }

        public override string ToString() => Ok ? "ok" : Message;
    }

    /// <summary>Where a game stands for a debugger: what is being held, and what would run next.</summary>
    public sealed class CantripStepState
    {
        internal CantripStepState(bool paused, bool steppable, int pending, PendingTrigger? next, int breakpoints, string message)
        {
            Paused = paused;
            Steppable = steppable;
            Pending = pending;
            Next = next?.Description ?? string.Empty;
            Event = next?.EventName ?? string.Empty;
            Span = next?.Span ?? SourceSpan.None;
            Breakpoints = breakpoints;
            Message = message ?? string.Empty;
        }

        /// <summary>Whether the queue is held: nothing resolves except one step at a time.</summary>
        public bool Paused { get; }

        /// <summary>
        /// False when the ruleset resolves triggers as they are raised, so nothing ever queues and
        /// there is nothing a debugger could step through.
        /// </summary>
        public bool Steppable { get; }

        public int Pending { get; }

        /// <summary>How the next trigger reads, or empty when nothing is waiting.</summary>
        public string Next { get; }

        /// <summary>The event that queued it, empty for the engine's own follow-up work.</summary>
        public string Event { get; }

        /// <summary>The line of content behind the next trigger, for the editor to open.</summary>
        public SourceSpan Span { get; }

        public int Breakpoints { get; }

        /// <summary>Why the last request did nothing, ready to show. Empty when it did something.</summary>
        public string Message { get; }

        public override string ToString() => Paused ? $"paused, {Pending} queued" : "running";
    }

    /// <summary>
    /// The game's half of the editor channel, with no engine in it: what the editor can ask a
    /// running game, and what it gets back.
    /// </summary>
    /// <remarks>
    /// Keeping this engine-free is what makes it testable. The Godot side is then a thin adapter
    /// that turns Variants into these calls and their results back into Variants, which is a shape
    /// worth keeping: the awkward part of a debug channel is deciding what to send and when, not
    /// the sending.
    /// </remarks>
    public sealed class CantripDebugService
    {
        private static readonly IReadOnlyList<Diagnostic> NoDiagnostics = new Diagnostic[0];
        private static readonly IReadOnlyList<string> NoMissing = new string[0];

        public CantripDebugService(CardRuntime runtime)
        {
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public CardRuntime Runtime { get; }

        public ContentLibrary Content => Runtime.Content;

        private TraceLog Log => Runtime.State.Trace;

        /// <summary>Who the game is, answered as soon as the editor says hello.</summary>
        public CantripHello Hello()
        {
            int definitions = 0;
            foreach (EntityDefinition _ in Content.Definitions) definitions++;

            return new CantripHello(
                CantripProtocol.Version,
                Content.Generation,
                Content.Fingerprint,
                definitions,
                Runtime.State.InBattle,
                Runtime.State.Turn,
                Log.Enabled);
        }

        /// <summary>
        /// Turns the causality trace on or off from the editor, and bounds it. Tracing is not free,
        /// so it is off until someone asks, and the bound is what keeps a long session from growing
        /// without limit.
        /// </summary>
        public void EnableTrace(bool enabled, int capacity = 2000)
        {
            Log.Enabled = enabled;
            Log.Capacity = capacity > 0 ? capacity : (int?)null;
            if (!enabled) Log.Clear();
        }

        /// <summary>The steps recorded after a cursor. See <see cref="TraceBatch"/> for why it is pulled.</summary>
        public TraceBatch Fetch(long sinceId = 0, int max = TraceBatch.DefaultMax) => TraceBatch.From(Log, sinceId, max);

        /// <summary>
        /// The live entities, in the order the game made them, optionally narrowed to one zone or
        /// side. Removed entities are left out: an inspector is for what is in play.
        /// </summary>
        public IReadOnlyList<EntityView> Entities(string? zone = null, string? team = null)
        {
            var result = new List<EntityView>();
            IReadOnlyList<Entity> all = Runtime.State.Entities;

            for (int i = 0; i < all.Count; i++)
            {
                Entity entity = all[i];
                if (entity.IsRemoved) continue;
                if (!string.IsNullOrEmpty(zone) && !string.Equals(entity.Zone, zone, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(team) && !string.Equals(entity.Team.ToString(), team, StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(EntityView.Of(entity));
            }

            return result;
        }

        /// <summary>
        /// Everything about one entity, including the listeners and modifiers it has registered and
        /// the line of content each came from. Null when the id is not in the game.
        /// </summary>
        public EntityDetail? Detail(int entityId)
        {
            Entity? entity = Runtime.State.Find(entityId);
            return entity == null ? null : EntityDetail.Of(entity);
        }

        /// <summary>Where the game stands, with an optional note about what was just refused.</summary>
        public CantripStepState Stepping(string message = "") => new CantripStepState(
            Runtime.Interpreter.Paused,
            Runtime.State.Rules.Triggers == TriggerResolution.Queued,
            Runtime.Interpreter.PendingTriggers,
            Runtime.Interpreter.Next,
            Runtime.Interpreter.Breakpoints.Count,
            message);

        /// <summary>Holds the queue. Whatever is running finishes; what it queued waits.</summary>
        public CantripStepState Pause()
        {
            Runtime.Interpreter.Pause();
            return Stepping();
        }

        public CantripStepState Resume()
        {
            Runtime.Interpreter.Resume();
            return Stepping();
        }

        /// <summary>
        /// Resolves one queued trigger.
        /// </summary>
        /// <remarks>
        /// A choice waiting to be answered refuses the step instead of taking it: answering one rolls
        /// its action back and replays it, which would undo the very triggers just stepped through.
        /// Everything else that cannot be stepped is reported the same way, because a debugger button
        /// that silently does nothing is worse than one that says why.
        /// </remarks>
        public CantripStepState Step()
        {
            if (Runtime.Pending != null)
                return Stepping("A choice is waiting to be answered; answer it before stepping.");

            if (Runtime.State.Rules.Triggers != TriggerResolution.Queued)
                return Stepping("This ruleset resolves triggers as they are raised, so there is nothing to step.");

            if (Runtime.Interpreter.PendingTriggers == 0) return Stepping("Nothing is queued.");

            try
            {
                Runtime.Interpreter.TryDrainStep();
                return Stepping();
            }
            catch (RuntimeError error)
            {
                return Stepping(error.Message);
            }
            catch (DslException error)
            {
                return Stepping(error.Message);
            }
        }

        /// <summary>Stops the game before any trigger written on one line.</summary>
        public CantripStepState Break(string file, int line, bool on)
        {
            if (on) Runtime.Interpreter.Breakpoints.Add(file, line);
            else Runtime.Interpreter.Breakpoints.Remove(file, line);
            return Stepping();
        }

        /// <summary>Stops the game before anything queued by one event, wherever it was written.</summary>
        public CantripStepState BreakOnEvent(string eventName, bool on)
        {
            if (on) Runtime.Interpreter.Breakpoints.AddEvent(eventName);
            else Runtime.Interpreter.Breakpoints.RemoveEvent(eventName);
            return Stepping();
        }

        public CantripStepState ClearBreakpoints()
        {
            Runtime.Interpreter.Breakpoints.Clear();
            return Stepping();
        }

        /// <summary>
        /// Reloads content the editor has just saved and rebinds the running game to it. Files are
        /// loaded even when they are broken, so the editor can show why; the rebinding is what is
        /// held back until they are clean.
        /// </summary>
        public CantripReloadResult Reload(IEnumerable<KeyValuePair<string, string>> files)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));

            var problems = new List<Diagnostic>();
            bool any = false;
            foreach (KeyValuePair<string, string> file in files)
            {
                if (string.IsNullOrEmpty(file.Key)) continue;
                any = true;
                problems.AddRange(Content.LoadText(file.Value ?? string.Empty, file.Key));
            }

            if (!any) return new CantripReloadResult(false, NoDiagnostics, 0, NoMissing, false);

            bool broken = false;
            foreach (Diagnostic problem in problems)
            {
                if (problem.Severity == DiagnosticSeverity.Error) broken = true;
            }

            if (broken) return new CantripReloadResult(false, problems, 0, NoMissing, false);

            CardRuntime.ReloadReport report = Runtime.ApplyContentChanges();
            return new CantripReloadResult(true, problems, report.Rebound, report.Missing, report.RulesetChanged);
        }

        /// <summary>
        /// Runs DSL statements against the running game, which is the editor's console. A failure is
        /// reported rather than thrown: the point of a console is to try things that might not work.
        /// </summary>
        public CantripExecuteResult Execute(string statements)
        {
            if (string.IsNullOrWhiteSpace(statements)) return new CantripExecuteResult(false, "Nothing to run.", SourceSpan.None);

            try
            {
                Runtime.Execute(statements);
                return new CantripExecuteResult(true, string.Empty, SourceSpan.None);
            }
            catch (RuntimeError error)
            {
                return new CantripExecuteResult(false, error.Message, error.Span);
            }
            catch (DslException error)
            {
                return new CantripExecuteResult(false, error.Message, SourceSpan.None);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                return new CantripExecuteResult(false, error.Message, SourceSpan.None);
            }
        }
    }
}
