using Cantrip;
using Cantrip.Runtime;

namespace Cantrip.Reference;

/// <summary>
/// The player, played by the host so CI can run a whole descent with nobody at the keyboard.
/// It is deliberately simple and deliberately deterministic: it makes the same plays for the
/// same seed, which is what lets the save test compare two state hashes.
/// </summary>
public sealed class Tactician
{
    private readonly CardRuntime runtime;
    private readonly Action<string> say;

    public Tactician(CardRuntime runtime, Action<string> say)
    {
        this.runtime = runtime;
        this.say = say;
    }

    public int TurnLimit { get; init; } = 60;

    /// <summary>Plays the battle that is running to its end. Returns true if it was won.</summary>
    public bool FightToTheEnd()
    {
        int guard = 0;
        while (runtime.Won == null)
        {
            // Under `turns: initiative` ActiveMember is the rule, not a suggestion: Pass on
            // anybody else throws. It comes back null once no member of ours has a step left,
            // which in a running battle means the battle is over.
            Entity? up = runtime.ActiveMember;
            if (up == null) break;
            if (++guard > TurnLimit * 8) throw new InvalidOperationException("The battle would not end.");

            TakeStep(up);
            if (runtime.Won != null) break;
            runtime.Pass(up);
        }

        return runtime.Won == true;
    }

    private void TakeStep(Entity member)
    {
        // Abilities first: they cost nothing, so there is never a reason to hold one back.
        foreach (Entity ability in member.Attached.Where(a => a.Kind == EntityKind.Ability).ToList())
        {
            if (runtime.Won != null) return;
            if (!runtime.CanUse(ability)) continue;
            Entity? aim = BestTarget(ability, member);
            if (runtime.TargetMode(ability) is "enemy" or "ally" or "any" && aim == null) continue;
            runtime.UseAbility(ability, aim);
        }

        // Then cards, out of whichever hand this member plays from: its own if it has one, and
        // the leader's otherwise. A member with no pile of its own draws nothing, so the leader's
        // hand is the party's.
        for (int safety = 0; safety < 24; safety++)
        {
            if (runtime.Won != null) return;
            Entity? best = null;
            int bestScore = int.MinValue;
            foreach (Entity card in Hand(member))
            {
                if (!runtime.CanPlay(card)) continue;
                int score = Score(card, member);
                if (score > bestScore) { bestScore = score; best = card; }
            }

            if (best == null) return;
            Entity? aim = BestTarget(best, member);
            if (runtime.TargetMode(best) is "enemy" or "ally" or "any" && aim == null) return;
            if (runtime.Play(best, aim, performer: member) != ActionResult.Played) return;
        }
    }

    private IReadOnlyList<Entity> Hand(Entity member)
    {
        IReadOnlyList<Entity> own = runtime.State.ZoneOf(member, Zones.Hand);
        return own.Count > 0 ? own : runtime.State.ZoneOf(runtime.Player!, Zones.Hand);
    }

    private int Score(Entity card, Entity member)
    {
        int score = card.HasTag("attack") ? 40 : 10;
        if (card.HasTag("curse")) return int.MinValue;

        // A mover is worth a great deal when the member cannot reach anything, and nothing at
        // all when it can. This is the whole of the board's tactics in the bot.
        if (card.Name is "Wade" && member.GetInt("rank") > 0) score = 70;
        else if (card.Name is "Wade") score = -10;

        if (card.Name is "Grace" or "Litany" && WoundedFriend() == null) score = -5;
        return score - card.GetInt("cost");
    }

    private Entity? WoundedFriend() =>
        runtime.State.Actors(Team.Player)
            .Where(a => a.GetInt("hp") < a.GetInt("max_hp"))
            .OrderBy(a => a.GetInt("hp"))
            .FirstOrDefault();

    private Entity? BestTarget(Entity action, Entity member)
    {
        IReadOnlyList<Entity> legal = runtime.LegalTargets(action);
        if (legal.Count == 0) return null;

        string mode = runtime.TargetMode(action);
        if (mode == "ally")
        {
            // The Ferryman's Punt is a shove, not a heal, so it wants whoever is furthest back.
            if (action.Name is "Punt" or "Wade")
                return legal.OrderByDescending(a => a.GetInt("rank")).ThenBy(a => a.Id).First();
            return legal.OrderBy(a => a.GetInt("hp")).ThenBy(a => a.Id).First();
        }

        return legal.OrderBy(a => a.GetInt("hp")).ThenBy(a => a.Id).First();
    }

    public void Report(string line) => say(line);
}
