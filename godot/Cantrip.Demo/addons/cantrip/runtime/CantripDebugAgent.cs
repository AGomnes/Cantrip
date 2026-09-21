#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Diagnostics;
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The game end of the editor channel: it registers a message capture while a debugger is
    /// attached, answers what the editor asks, and says nothing otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All the thinking is in <see cref="CantripDebugService"/>, which has no engine in it and is tested
    /// on its own. This turns Variants into those calls and their answers back into Variants.
    /// </para>
    /// <para>
    /// Nothing is pushed except one greeting. Godot's debugger channel is capped — a live 4.6.1 run
    /// reports 2048 queued messages and 32768 characters a second — so the editor pulls batches at
    /// its own pace, and a game that is busy cannot flood it or be slowed by it.
    /// </para>
    /// </remarks>
    internal sealed class CantripDebugAgent
    {
        private readonly CantripDebugService _service;
        private bool _installed;

        internal CantripDebugAgent(CantripDebugService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>
        /// Starts listening, but only with a debugger attached: an exported game pays nothing for
        /// this, and a game run without the editor has nobody to talk to.
        /// </summary>
        internal void Install()
        {
            if (_installed || !EngineDebugger.IsActive()) return;

            EngineDebugger.RegisterMessageCapture(CantripProtocol.Capture, Callable.From<string, Godot.Collections.Array, bool>(OnMessage));
            _installed = true;

            Send(CantripProtocol.Welcome, Welcome());
        }

        internal void Uninstall()
        {
            if (!_installed) return;

            EngineDebugger.UnregisterMessageCapture(CantripProtocol.Capture);
            _installed = false;
        }

        /// <summary>
        /// Answers one message. Returning false tells Godot the message was not ours, so anything
        /// addressed elsewhere carries on to whoever wanted it.
        /// </summary>
        private bool OnMessage(string message, Godot.Collections.Array data)
        {
            if (!Respond(message, data, out string reply, out Godot.Collections.Dictionary payload)) return false;

            Send(reply, payload);
            return true;
        }

        /// <summary>
        /// Works out the answer to one message without sending it. Splitting this out is what lets
        /// the headless tests exercise the whole conversation with no editor on the other end, which
        /// is the only way this plumbing gets covered at all.
        /// </summary>
        internal bool Respond(string message, Godot.Collections.Array data, out string reply, out Godot.Collections.Dictionary payload)
        {
            reply = CantripProtocol.Failed;
            payload = new Godot.Collections.Dictionary();

            if (!CantripProtocol.TryName(message, out string name)) return false;

            try
            {
                switch (name)
                {
                    case CantripProtocol.Hello:
                        reply = CantripProtocol.Welcome;
                        payload = Welcome();
                        return true;

                    case CantripProtocol.TraceEnable:
                        _service.EnableTrace(Flag(data, 0, true), Number(data, 1, 2000));
                        reply = CantripProtocol.Welcome;
                        payload = Welcome();
                        return true;

                    case CantripProtocol.TraceFetch:
                        reply = CantripProtocol.Trace;
                        payload = Batch(_service.Fetch(Number(data, 0, 0), Number(data, 1, TraceBatch.DefaultMax)));
                        return true;

                    case CantripProtocol.Reload:
                        reply = CantripProtocol.Reloaded;
                        payload = Reloaded(_service.Reload(Files(data)));
                        return true;

                    case CantripProtocol.Entities:
                        reply = CantripProtocol.EntityList;
                        payload = EntityList(_service.Entities(Text(data, 0), Text(data, 1)));
                        return true;

                    case CantripProtocol.Entity:
                        reply = CantripProtocol.EntityDetail;
                        payload = Detail(_service.Detail(Number(data, 0, 0)));
                        return true;

                    case CantripProtocol.Pause:
                        reply = CantripProtocol.StepState;
                        payload = Stepped(_service.Pause());
                        return true;

                    case CantripProtocol.Resume:
                        reply = CantripProtocol.StepState;
                        payload = Stepped(_service.Resume());
                        return true;

                    case CantripProtocol.Step:
                        reply = CantripProtocol.StepState;
                        payload = Stepped(_service.Step());
                        return true;

                    case CantripProtocol.BreakLine:
                        reply = CantripProtocol.StepState;
                        payload = Stepped(_service.Break(Text(data, 0), Number(data, 1, 0), Flag(data, 2, true)));
                        return true;

                    case CantripProtocol.BreakEvent:
                        reply = CantripProtocol.StepState;
                        payload = Stepped(_service.BreakOnEvent(Text(data, 0), Flag(data, 1, true)));
                        return true;

                    case CantripProtocol.BreakClear:
                        reply = CantripProtocol.StepState;
                        payload = Stepped(_service.ClearBreakpoints());
                        return true;

                    case CantripProtocol.Execute:
                    {
                        CantripExecuteResult result = _service.Execute(Text(data, 0));
                        reply = CantripProtocol.Ran;
                        payload = new Godot.Collections.Dictionary
                        {
                            ["ok"] = result.Ok,
                            ["message"] = result.Message,
                            ["file"] = result.Span.File ?? string.Empty,
                            ["line"] = result.Span.Line,
                        };
                        return true;
                    }

                    default:
                        return false;
                }
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                // A debugger that takes the game down with it is worse than one that says nothing.
                reply = CantripProtocol.Failed;
                payload = new Godot.Collections.Dictionary
                {
                    ["ok"] = false,
                    ["message"] = error.Message,
                    ["file"] = string.Empty,
                    ["line"] = 0,
                };
                return true;
            }
        }

        private static void Send(string name, Godot.Collections.Dictionary payload) =>
            EngineDebugger.SendMessage(CantripProtocol.Message(name), new Godot.Collections.Array { payload });

        private Godot.Collections.Dictionary Welcome()
        {
            CantripHello hello = _service.Hello();
            return new Godot.Collections.Dictionary
            {
                ["protocol"] = hello.Protocol,
                ["generation"] = hello.Generation,
                ["fingerprint"] = hello.Fingerprint,
                ["definitions"] = hello.Definitions,
                ["in_battle"] = hello.InBattle,
                ["turn"] = hello.Turn,
                ["tracing"] = hello.Tracing,
            };
        }

        private static Godot.Collections.Dictionary Batch(TraceBatch batch)
        {
            var entries = new Godot.Collections.Array();
            for (int i = 0; i < batch.Entries.Count; i++)
            {
                TraceDto entry = batch.Entries[i];

                var values = new Godot.Collections.Dictionary();
                foreach (KeyValuePair<string, string> value in entry.Values) values[value.Key] = value.Value;

                entries.Add(new Godot.Collections.Dictionary
                {
                    ["id"] = entry.Id,
                    ["parent"] = entry.Parent,
                    ["time"] = entry.Time,
                    ["kind"] = entry.Kind,
                    ["text"] = entry.Text,
                    ["source"] = entry.Source,
                    ["listener"] = entry.Listener,
                    ["file"] = entry.File,
                    ["line"] = entry.Line,
                    ["column"] = entry.Column,
                    ["values"] = values,
                });
            }

            return new Godot.Collections.Dictionary
            {
                ["entries"] = entries,
                ["next"] = batch.NextId,
                ["dropped"] = batch.Dropped,
                ["more"] = batch.More,
            };
        }

        private static Godot.Collections.Dictionary Stepped(CantripStepState state) =>
            new Godot.Collections.Dictionary
            {
                ["paused"] = state.Paused,
                ["steppable"] = state.Steppable,
                ["pending"] = state.Pending,
                ["next"] = state.Next,
                ["event"] = state.Event,
                ["file"] = state.Span.File ?? string.Empty,
                ["line"] = state.Span.Line,
                ["column"] = state.Span.Column,
                ["breakpoints"] = state.Breakpoints,
                ["message"] = state.Message,
            };

        private static Godot.Collections.Dictionary EntityList(IReadOnlyList<EntityView> entities)
        {
            var array = new Godot.Collections.Array();
            for (int i = 0; i < entities.Count; i++) array.Add(VariantMap.Entity(entities[i]));

            return new Godot.Collections.Dictionary { ["entities"] = array };
        }

        private static Godot.Collections.Dictionary Detail(EntityDetail? detail)
        {
            if (detail == null) return new Godot.Collections.Dictionary { ["found"] = false };

            var baseStats = new Godot.Collections.Dictionary();
            foreach (KeyValuePair<string, int> stat in detail.BaseStats) baseStats[stat.Key] = stat.Value;

            var listeners = new Godot.Collections.Array();
            for (int i = 0; i < detail.Listeners.Count; i++)
            {
                ListenerView listener = detail.Listeners[i];
                listeners.Add(new Godot.Collections.Dictionary
                {
                    ["id"] = listener.Id,
                    ["event"] = listener.Event,
                    ["scope"] = listener.Scope,
                    ["phase"] = listener.Phase,
                    ["limit"] = listener.Limit,
                    ["priority"] = listener.Priority,
                    ["text"] = listener.Text,
                    ["file"] = listener.File,
                    ["line"] = listener.Line,
                    ["column"] = listener.Column,
                });
            }

            var modifiers = new Godot.Collections.Array();
            for (int i = 0; i < detail.Modifiers.Count; i++)
            {
                ModifierView modifier = detail.Modifiers[i];
                modifiers.Add(new Godot.Collections.Dictionary
                {
                    ["id"] = modifier.Id,
                    ["channel"] = modifier.Channel,
                    ["layer"] = modifier.Layer,
                    ["scope"] = modifier.Scope,
                    ["amount"] = modifier.Amount,
                    ["text"] = modifier.Text,
                    ["file"] = modifier.File,
                    ["line"] = modifier.Line,
                    ["column"] = modifier.Column,
                });
            }

            return new Godot.Collections.Dictionary
            {
                ["found"] = true,
                ["entity"] = VariantMap.Entity(detail.Entity),
                ["active"] = detail.Active,
                ["definition"] = detail.Definition,
                ["base_stats"] = baseStats,
                ["listeners"] = listeners,
                ["modifiers"] = modifiers,
            };
        }

        private static Godot.Collections.Dictionary Reloaded(CantripReloadResult result)
        {
            var diagnostics = new Godot.Collections.Array();
            foreach (Diagnostic diagnostic in result.Diagnostics) diagnostics.Add(VariantMap.Diagnostic(diagnostic));

            return new Godot.Collections.Dictionary
            {
                ["applied"] = result.Applied,
                ["rebound"] = result.Rebound,
                ["missing"] = VariantMap.Strings(result.Missing),
                ["ruleset_changed"] = result.RulesetChanged,
                ["diagnostics"] = diagnostics,
            };
        }

        // Reading what the editor sent -------------------------------------------------------------

        /// <summary>Files arrive as a flat array of path, text, path, text: one message, no nesting.</summary>
        private static IEnumerable<KeyValuePair<string, string>> Files(Godot.Collections.Array data)
        {
            for (int i = 0; i + 1 < data.Count; i += 2)
            {
                yield return new KeyValuePair<string, string>(data[i].AsString(), data[i + 1].AsString());
            }
        }

        private static string Text(Godot.Collections.Array data, int index) =>
            index < data.Count ? data[index].AsString() : string.Empty;

        private static int Number(Godot.Collections.Array data, int index, int fallback) =>
            index < data.Count && (data[index].VariantType == Variant.Type.Int || data[index].VariantType == Variant.Type.Float)
                ? data[index].AsInt32()
                : fallback;

        private static bool Flag(Godot.Collections.Array data, int index, bool fallback) =>
            index < data.Count && data[index].VariantType == Variant.Type.Bool ? data[index].AsBool() : fallback;
    }
}
