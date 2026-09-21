using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// What the adapter's <c>GodotEffectHost</c> does with an event, minus the engine: append it and
    /// nothing else. The Godot half adds only the Callables, so the pacing rules can be tested here
    /// against a real game.
    /// </summary>
    internal sealed class BufferHost : EffectHostBase
    {
        public BufferHost(EventBuffer buffer) => Buffer = buffer;

        public EventBuffer Buffer { get; }

        /// <summary>Set once the runtime exists; until then the event's own entities are asked.</summary>
        public GameState? State { get; set; }

        public IReadOnlyList<string>? TrackedStats { get; set; }

        public override void OnEvent(GameEvent gameEvent)
        {
            GameState? state = State ?? gameEvent.Target?.State ?? gameEvent.Source?.State ?? gameEvent.Card?.State;
            if (state == null) return;

            Buffer.Add(gameEvent, state, TrackedStats);
        }
    }

    /// <summary>
    /// Shared setup. Every test drives a real game rather than a stand-in, because what is being
    /// tested is exactly how the adapter behaves against the core's own event timing.
    /// </summary>
    internal static class AdapterTestKit
    {
        /// <summary>
        /// A card that hits twice is the whole argument for snapshotting stats per event: the two
        /// hits have different numbers to show, and by the time either is animated the enemy is at
        /// the end of both.
        /// </summary>
        public const string Content = @"enemy ""Dummy""
  hp 20

card ""Twin""
  cost 0
  target enemy
  tags attack, fire
  effect:
    deal 6 to target
    deal 4 to target

card ""Strike""
  cost 0
  target enemy
  effect:
    deal 5 to target

card ""Recycle""
  cost 0
  effect:
    choose 1 from hand as picked
    exhaust picked

card ""Twice""
  cost 0
  effect:
    choose 1 from hand as first
    exhaust first
    choose 1 from hand as second
    exhaust second
";

        /// <summary>A game state with no runtime, for the buffer's own mechanics.</summary>
        public static GameState BareState()
        {
            ContentLibrary library = Library();
            return new GameState(library, library.BuildRuleset(), new TurnClock(), 1);
        }

        public static Entity Actor(GameState state, string name = "Hero", int hp = 30)
        {
            Entity actor = state.Spawn(name, EntityKind.Actor, null, Team.Player, Zones.Board);
            actor.SetBase("max_hp", hp);
            actor.SetBase("hp", hp);
            actor.SetBase("block", 0);
            return actor;
        }

        /// <summary>A started battle whose host records into <paramref name="buffer"/>.</summary>
        public static CardRuntime Start(EventBuffer buffer, out BufferHost host, params string[] hand)
        {
            var recorder = new BufferHost(buffer);
            CardRuntime runtime = Start(recorder, null, hand);
            recorder.State = runtime.State;

            // Setting up a battle raises events of its own; tests care about what they play.
            buffer.Discard();
            host = recorder;
            return runtime;
        }

        public static CardRuntime Start(IEffectHost? host, IChoiceProvider? chooser, params string[] hand)
        {
            var runtime = new CardRuntime(Library(), new RuntimeOptions { Seed = 1, Host = host, Chooser = chooser });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            foreach (string card in hand) runtime.AddCard(card, Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        public static Entity Enemy(CardRuntime runtime) => runtime.State.Actors(Team.Enemy)[0];

        public static List<EventRecord> DrainAll(EventBuffer buffer)
        {
            var records = new List<EventRecord>();
            buffer.Drain(records.Add);
            return records;
        }

        private static ContentLibrary Library()
        {
            ContentLibrary library = ContentLibrary.FromText(Content);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());
            return library;
        }
    }
}
