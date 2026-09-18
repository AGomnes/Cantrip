#if TOOLS
using System;
using System.Collections.Generic;
using GameplayEffects.Content;
using GameplayEffects.Runtime;
using Godot;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// The editor end of the channel to a running game: one tab per debug session, showing what the
    /// rules did and why.
    /// </summary>
    /// <remarks>
    /// Godot gives a plugin a session per game being debugged, so everything here is keyed by
    /// session: two games running at once get a tab each, and a tab empties when its game stops
    /// rather than quietly showing another game's history.
    /// </remarks>
    [Tool]
    public partial class GeDebuggerPlugin : EditorDebuggerPlugin
    {
        private readonly Dictionary<int, GeTraceView> _views = new Dictionary<int, GeTraceView>();
        private readonly Dictionary<int, GeInspectorView> _inspectors = new Dictionary<int, GeInspectorView>();

        /// <summary>Raised when someone picks a step in any session, so the dock can open its line.</summary>
        public event Action<string, int, int>? NavigateRequested;

        public override bool _HasCapture(string capture) => capture == GeProtocol.Capture;

        public override bool _Capture(string message, Godot.Collections.Array data, int sessionId)
        {
            if (!GeProtocol.TryName(message, out string name)) return false;
            if (!_views.TryGetValue(sessionId, out GeTraceView? view)) return false;

            Godot.Collections.Dictionary payload = data.Count > 0 && data[0].VariantType == Variant.Type.Dictionary
                ? data[0].AsGodotDictionary()
                : new Godot.Collections.Dictionary();

            // Both tabs watch the same conversation: each takes the messages it understands, which
            // is also how the inspector knows to refresh after the game reloads its content.
            view.Receive(name, payload);
            if (_inspectors.TryGetValue(sessionId, out GeInspectorView? inspector)) inspector.Receive(name, payload);
            return true;
        }

        public override void _SetupSession(int sessionId)
        {
            EditorDebuggerSession session = GetSession(sessionId);
            if (session == null) return;

            var view = new GeTraceView();
            view.Initialize((name, arguments) => GetSession(sessionId)?.SendMessage(GeProtocol.Message(name), arguments));
            view.NavigateRequested += OnNavigateRequested;

            _views[sessionId] = view;
            session.AddSessionTab(view);

            var inspector = new GeInspectorView();
            inspector.Initialize((name, arguments) => GetSession(sessionId)?.SendMessage(GeProtocol.Message(name), arguments));
            inspector.NavigateRequested += OnNavigateRequested;

            _inspectors[sessionId] = inspector;
            session.AddSessionTab(inspector);

            session.Started += view.OnStarted;
            session.Stopped += view.OnStopped;
            session.Started += inspector.OnStarted;
            session.Stopped += inspector.OnStopped;

            // A session that is already running when the plugin loads still needs its greeting.
            if (session.IsActive())
            {
                view.OnStarted();
                inspector.OnStarted();
            }
        }

        /// <summary>
        /// Drives both session tabs without a session: it stands up a game from the project's own
        /// content, asks it the questions the editor asks over the channel, and gives the real
        /// answers to the real views.
        /// </summary>
        /// <remarks>
        /// A live editor-to-game round trip cannot be staged without a person, but this covers the
        /// parts that actually break: a key renamed on one side of the channel and not the other, and
        /// a Godot call that compiles and then throws when it is finally made.
        /// </remarks>
        public IReadOnlyList<string> SelfTest(ContentLibrary content)
        {
            var report = new List<string>();
            if (content == null)
            {
                report.Add("debugger: no content to run");
                return report;
            }

            var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();

            var agent = new GeDebugAgent(new GeDebugService(runtime));
            var trace = new GeTraceView();
            var inspector = new GeInspectorView();

            try
            {
                var raised = new List<string>();
                Action<string, Godot.Collections.Array> send = (name, arguments) => raised.Add(name);

                trace.Initialize(send);
                inspector.Initialize(send);

                Relay(agent, trace, inspector, GeProtocol.Hello, new Godot.Collections.Array());
                Relay(agent, trace, inspector, GeProtocol.TraceEnable, new Godot.Collections.Array { true, 200 });
                Relay(agent, trace, inspector, GeProtocol.TraceFetch, new Godot.Collections.Array { 0, 50 });
                Relay(agent, trace, inspector, GeProtocol.Entities, new Godot.Collections.Array { string.Empty, string.Empty });
                Relay(agent, trace, inspector, GeProtocol.Entity, new Godot.Collections.Array { runtime.Player == null ? 0 : runtime.Player.Id });

                report.Add($"debugger: 2 session tab(s) driven, {raised.Count} request(s) raised back");
                report.Add(inspector.SelfTest());
            }
            finally
            {
                trace.QueueFree();
                inspector.QueueFree();
            }

            return report;
        }

        /// <summary>Asks the game one thing and hands its answer to both tabs, as a session would.</summary>
        private static void Relay(GeDebugAgent agent, GeTraceView trace, GeInspectorView inspector, string name, Godot.Collections.Array arguments)
        {
            if (!agent.Respond(GeProtocol.Message(name), arguments, out string reply, out Godot.Collections.Dictionary payload)) return;

            trace.Receive(reply, payload);
            inspector.Receive(reply, payload);
        }

        /// <summary>Lets go of every session's tab. Called when the addon is disabled or rebuilt.</summary>
        public void Close()
        {
            foreach (KeyValuePair<int, GeTraceView> entry in _views)
            {
                entry.Value.NavigateRequested -= OnNavigateRequested;
                GetSession(entry.Key)?.RemoveSessionTab(entry.Value);
                entry.Value.QueueFree();
            }
            _views.Clear();

            foreach (KeyValuePair<int, GeInspectorView> entry in _inspectors)
            {
                entry.Value.NavigateRequested -= OnNavigateRequested;
                GetSession(entry.Key)?.RemoveSessionTab(entry.Value);
                entry.Value.QueueFree();
            }
            _inspectors.Clear();
        }

        private void OnNavigateRequested(string file, int line, int column) => NavigateRequested?.Invoke(file, line, column);
    }
}
#endif
