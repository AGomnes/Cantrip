using Cantrip;
using Cantrip.Runtime;

namespace Cantrip.Realtime;

/// <summary>
/// Stands in for the player's hands. In a game these are five buttons and a mouse; here it is a
/// policy, so that CI can play the hold with nobody watching.
///
/// The policy is the whole of real-time play: every tick, look at what is off cooldown and decide
/// whether this is the second to spend it. Nothing in Cantrip helps with this and nothing should
/// -- but note what it has to do without. A tick game has no intents and no telegraph
/// (FINDINGS #3), so the only warning of anything is where things are standing. `cantrip sim`
/// cannot stand in for it either, and says so: when to act in continuous time is a game's own
/// frame loop, not a bot's.
/// </summary>
public static class Keepers
{
    public static void Act(Emberline game)
    {
        CardRuntime rules = game.Runtime;
        foreach (Entity member in game.Party.ToList())
        {
            if (!member.IsAlive) continue;
            foreach (Entity ability in Emberline.AbilitiesOf(rules, member).ToList())
            {
                if (!rules.CanUse(ability)) continue;
                if (!Worth(game, ability, out Entity? target)) continue;
                rules.UseAbility(ability, target);
            }
        }
    }

    /// <summary>Whether to spend this ability now, and at whom.</summary>
    static bool Worth(Emberline game, Entity ability, out Entity? target)
    {
        CardRuntime rules = game.Runtime;
        IReadOnlyList<Entity> legal = rules.LegalTargets(ability);
        target = null;
        switch (ability.Name)
        {
            case "Ember Bolt":
                // Finish what is nearly dead, otherwise hit what is closest to the line.
                target = legal.OrderBy(e => e.GetInt("hp")).ThenBy(e => e.Rank).FirstOrDefault();
                return target != null;

            case "Backdraft":
                // Worth a six-second wait only where it catches more than one.
                Entity? blast = legal
                    .OrderByDescending(e => game.Enemies.Count(o => rules.State.Distance(e, o) <= 1))
                    .FirstOrDefault();
                target = blast;
                return blast != null
                    && game.Enemies.Count(o => rules.State.Distance(blast, o) <= 1) >= 2;

            case "Haul":
                // Only for something that has reached the line.
                target = legal.FirstOrDefault(e => e.Rank == 0);
                return target != null;

            case "Signal Flare":
                // Two seconds of lead time, so aim it at what will still be there: the big ones.
                target = legal.OrderByDescending(e => e.GetInt("hp")).FirstOrDefault();
                return target != null;

            case "Bulwark":
                // No target line. Hold it until something is in swinging distance.
                return game.Enemies.Any(e => e.Rank == 0);

            case "Mend":
                target = legal
                    .Where(e => e.GetInt("hp") * 2 <= e.GetInt("max_hp"))
                    .OrderBy(e => e.GetInt("hp")).FirstOrDefault();
                return target != null;
        }
        return false;
    }
}
