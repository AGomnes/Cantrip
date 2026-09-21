#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Cantrip.Content;
using Cantrip.Runtime;

namespace Cantrip.Tests.Battle
{
    /// <summary>
    /// Shared setup for the battle-flow tests. Every test builds its own runtime from inline
    /// content, so test classes can run in parallel without sharing any mutable state.
    /// </summary>
    internal static class BattleKit
    {
        public static CardRuntime Create(
            string content,
            ulong seed = 1,
            IChoiceProvider? chooser = null,
            IGameClock? clock = null,
            Ruleset? rules = null,
            IEffectHost? host = null)
        {
            return CardRuntime.FromText(content, new RuntimeOptions
            {
                Seed = seed,
                Chooser = chooser,
                Clock = clock,
                Rules = rules,
                Host = host,
            });
        }

        /// <summary>
        /// Content for interchangeable one-cost cards named Card0, Card1... Their only purpose is to
        /// make zone order readable in assertions.
        /// </summary>
        public static string PlainCards(int count, string prefix = "Card")
        {
            var text = new StringBuilder();
            foreach (string name in CardNames(count, prefix))
            {
                text.Append("card \"").Append(name).Append("\"\n");
                text.Append("  cost 1\n");
                text.Append("  effect:\n");
                text.Append("    block 1\n\n");
            }
            return text.ToString();
        }

        public static string[] CardNames(int count, string prefix = "Card") =>
            Enumerable.Range(0, count).Select(i => prefix + i.ToString(CultureInfo.InvariantCulture)).ToArray();

        /// <summary>A snapshot of one of the player's zones. ZoneOf returns the live list, which later steps would mutate.</summary>
        public static IReadOnlyList<Entity> Zone(CardRuntime runtime, string zone) =>
            runtime.State.ZoneOf(runtime.Player, zone).ToArray();

        public static string[] ZoneNames(CardRuntime runtime, string zone) => Names(Zone(runtime, zone));

        public static string[] Names(IEnumerable<Entity> entities) => entities.Select(e => e.Name).ToArray();

        /// <summary>
        /// Finds samples/basic by walking up from the test binaries, so the sample tests run against
        /// the real files instead of a copy that could drift.
        /// </summary>
        public static string SamplesFolder()
        {
            for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "samples", "basic");
                if (File.Exists(Path.Combine(candidate, "content.cantrip"))) return candidate;
            }
            throw new DirectoryNotFoundException("Could not find samples/basic above " + AppContext.BaseDirectory);
        }

        public static ContentLibrary LoadSamples()
        {
            var content = new ContentLibrary();
            content.LoadFolder(SamplesFolder());
            content.Diagnostics.ThrowIfErrors();
            return content;
        }
    }

    /// <summary>
    /// A host that keeps a copy of every resolved event. Asserting on the events themselves checks
    /// what the runtime reported, not only the state it left behind.
    /// </summary>
    internal sealed class BattleEventRecorder : EffectHostBase
    {
        public List<BattleRecordedEvent> Events { get; } = new List<BattleRecordedEvent>();

        /// <summary>Runs as each event is reported, for reading state at that exact moment.</summary>
        public Action<GameEvent>? Hook { get; set; }

        public override void OnEvent(GameEvent gameEvent)
        {
            Events.Add(new BattleRecordedEvent(gameEvent));
            Hook?.Invoke(gameEvent);
        }

        public List<BattleRecordedEvent> Named(string name) =>
            Events.Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();

        public List<string> Names() => Events.Select(e => e.Name).ToList();

        public void Clear() => Events.Clear();
    }

    /// <summary>
    /// A copy of a <see cref="GameEvent"/> taken when it was reported. The event object itself may be
    /// changed afterwards, so the recorder must not keep a reference to it.
    /// </summary>
    internal sealed class BattleRecordedEvent
    {
        public BattleRecordedEvent(GameEvent gameEvent)
        {
            Name = gameEvent.Name;
            Source = gameEvent.Source;
            Target = gameEvent.Target;
            Card = gameEvent.Card;
            Amount = gameEvent.Amount.ToInt();
            Replaced = gameEvent.Replaced;
            Tags = gameEvent.Tags.OrderBy(t => t, StringComparer.Ordinal).ToArray();
            Data = new Dictionary<string, Value>(gameEvent.Data, StringComparer.OrdinalIgnoreCase);
        }

        public string Name { get; }
        public Entity? Source { get; }
        public Entity? Target { get; }
        public Entity? Card { get; }
        public int Amount { get; }
        public bool Replaced { get; }
        public string[] Tags { get; }
        public Dictionary<string, Value> Data { get; }

        public int DataInt(string key) => Data.TryGetValue(key, out Value value) ? value.Number.ToInt() : 0;

        public string? DataText(string key) => Data.TryGetValue(key, out Value value) ? value.Text : null;

        public bool DataBool(string key) => Data.TryGetValue(key, out Value value) && value.AsBool();

        public override string ToString() => $"{Name} {Source} -> {Target} ({Amount})";
    }

    /// <summary>Records every choice request, then answers with a wrapped provider or a fixed rule.</summary>
    internal sealed class BattleRecordingChooser : IChoiceProvider
    {
        private readonly Func<ChoiceRequest, GameState, IReadOnlyList<Entity>> _answer;

        public BattleRecordingChooser(IChoiceProvider? inner = null)
        {
            IChoiceProvider provider = inner ?? new FirstOptionChooser();
            _answer = provider.Choose;
        }

        public BattleRecordingChooser(Func<ChoiceRequest, GameState, IReadOnlyList<Entity>> answer) => _answer = answer;

        public List<ChoiceRequest> Requests { get; } = new List<ChoiceRequest>();

        public IReadOnlyList<Entity> Choose(ChoiceRequest request, GameState state)
        {
            Requests.Add(request);
            return _answer(request, state);
        }
    }
}
