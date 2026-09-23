using System.Collections.Generic;
using System.Linq;
using Cantrip.Runtime;

namespace Cantrip.Sim
{
    /// <summary>
    /// What a turn looks like written down, for <c>--watch</c>: the board before the bot acts, and
    /// the hand it chose from.
    /// </summary>
    internal static class Watch
    {
        public static string Board(CardRuntime runtime)
        {
            Entity player = runtime.Player!;
            IEnumerable<string> enemies = runtime.State.Actors(Team.Enemy).Select(e =>
                $"{e.Name} {e.GetInt("hp")}hp{Statuses(e)} -> {e.Intent}");
            return $"you {player.GetInt("hp")}hp{Statuses(player)} | " + string.Join(" | ", enemies);
        }

        /// <summary>
        /// The player's hand and what is left to draw, which is what the bot chose from, or null
        /// in a fight with no cards in it, where there is nothing to say.
        /// </summary>
        public static string? Hand(CardRuntime runtime)
        {
            Entity player = runtime.Player!;
            IReadOnlyList<Entity> hand = runtime.State.ZoneOf(player, Zones.Hand);
            int draw = runtime.State.ZoneOf(player, Zones.Draw).Count;
            if (hand.Count == 0 && draw == 0) return null;

            return $"hand: {string.Join(", ", hand.Select(c => c.Name))} ({draw} to draw)";
        }

        private static string Statuses(Entity entity)
        {
            List<string> parts = entity.Attached
                .Where(s => s.Kind == EntityKind.Status)
                .Select(s => $"{s.Name}{s.GetInt("stacks")}")
                .ToList();
            int block = entity.GetInt("block");
            if (block > 0) parts.Insert(0, "block" + block);
            return parts.Count == 0 ? "" : " [" + string.Join(" ", parts) + "]";
        }
    }
}
