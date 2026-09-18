#if TOOLS
using System;
using System.Collections.Generic;
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
