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

            view.Receive(name, payload);
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

            session.Started += view.OnStarted;
            session.Stopped += view.OnStopped;

            // A session that is already running when the plugin loads still needs its greeting.
            if (session.IsActive()) view.OnStarted();
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
        }

        private void OnNavigateRequested(string file, int line, int column) => NavigateRequested?.Invoke(file, line, column);
    }
}
#endif
