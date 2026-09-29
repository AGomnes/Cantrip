using System.Text.Json;
using System.Text.Json.Serialization;
using Cantrip.Runtime;

namespace Cantrip.Reference;

/// <summary>
/// Everything about the run that Cantrip does not model, which is everything above the battle:
/// where on the map we are, which rewards have already been offered, and the run's own random
/// stream. Cantrip keeps hp, the deck, the relics, the party and gold, because those are stats
/// and entities inside a battle; it keeps nothing about the descent they are making.
/// </summary>
public sealed class RunState
{
    public int Seed { get; set; }
    public int Floor { get; set; }
    public ulong RunRng { get; set; }
    public List<string> Log { get; set; } = new();

    /// <summary>
    /// The party roster by entity id, which the host has to keep because a fallen member is not
    /// in <c>runtime.Party</c>, is not in <c>allies</c>, and cannot be named from content at all.
    /// Without this list a hero that dies on floor 2 is gone from the run with no record that it
    /// ever existed.
    /// </summary>
    public List<RosterEntry> Roster { get; set; } = new();

    public sealed class RosterEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool Recruited { get; set; }
    }
}

/// <summary>
/// The whole save: the run's own state beside the battle snapshot, with the content fingerprint
/// that says whether the two still describe the same game.
/// </summary>
public sealed class ChapelSave
{
    public string Fingerprint { get; set; } = "";
    public RunState Run { get; set; } = new();
    public GameSnapshot Game { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ChapelSave FromJson(string json) =>
        JsonSerializer.Deserialize<ChapelSave>(json, Options)
        ?? throw new InvalidOperationException("The save file is empty.");
}

/// <summary>
/// The run's own random stream, kept apart from the rules engine's so that a reward roll never
/// moves the battle's dice and a replay of the same seed offers the same cards. Cantrip's own
/// generator is deliberately not reachable for this, which is right: a map is not a rules roll.
/// </summary>
public sealed class RunRandom
{
    private ulong state;

    public RunRandom(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15ul : seed;

    public ulong State => state;

    public int Next(int exclusiveUpperBound)
    {
        // splitmix64, so the run's stream is reproducible from the save without depending on
        // System.Random's implementation.
        state += 0x9E3779B97F4A7C15ul;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
        z ^= z >> 31;
        return exclusiveUpperBound <= 1 ? 0 : (int)(z % (ulong)exclusiveUpperBound);
    }

    public T Take<T>(IList<T> from)
    {
        int index = Next(from.Count);
        T picked = from[index];
        from.RemoveAt(index);
        return picked;
    }
}
