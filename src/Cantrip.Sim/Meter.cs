using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Diagnostics;
using Cantrip.Runtime;

namespace Cantrip.Sim
{
    /// <summary>One thing the engine kept doing, and how much of it there was.</summary>
    /// <remarks>
    /// <see cref="Amount"/> is whatever the event carried: hp for damage and healing, block for
    /// block, stacks for a status, energy for a card. <see cref="Times"/> is how many events there
    /// were, which is the honest number when the amount is not a quantity anyone adds up.
    /// </remarks>
    public sealed class Tally
    {
        private readonly HashSet<string> _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal Tally(string name) => Name = name;

        /// <summary>What did it: a card, a status, an enemy, a move, a tag.</summary>
        public string Name { get; }

        /// <summary>The engine's own amounts, added up.</summary>
        public long Amount { get; internal set; }

        /// <summary>How many events made it up.</summary>
        public int Times { get; internal set; }

        /// <summary>
        /// Every tag the events counted here carried, in order. For damage that is the type of the
        /// hits: it is how the report can say that nothing a card dealt was ever <c>fire</c>.
        /// </summary>
        public IReadOnlyList<string> Tags => _tags.OrderBy(t => t, StringComparer.Ordinal).ToList();

        internal void Tagged(IEnumerable<string> tags)
        {
            foreach (string tag in tags) _tags.Add(tag);
        }

        /// <summary>Whether any event counted here carried <paramref name="tag"/>.</summary>
        public bool Carried(string tag) => _tags.Contains(tag);

        public override string ToString() => $"{Name} {Amount} in {Times}";
    }

    /// <summary>A choice content asked for mid-effect that the bot answered at random.</summary>
    public sealed class ChoiceTally
    {
        internal ChoiceTally(SourceSpan span, string? owner)
        {
            Span = span;
            Owner = owner;
        }

        /// <summary>The <c>choose</c> or <c>discover</c> line that asked.</summary>
        public SourceSpan Span { get; }

        /// <summary>The declaration that line is in, when the span can be placed in one.</summary>
        public string? Owner { get; }

        /// <summary>How many times it was asked, in play that counted.</summary>
        public int Times { get; internal set; }

        /// <summary>What the report calls it: the card's name where there is one, else the line.</summary>
        public string Label => Owner ?? Span.ToString();
    }

    /// <summary>
    /// Records what the engine raised while the game was really being played: damage by what dealt
    /// it and by tag, healing, block, the moves the enemies used, the statuses that landed and the
    /// cards that were played. It is a plain <see cref="IEffectHost"/>, so it works on any runtime
    /// and not only inside a scenario.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are engine truth, not a judgement: every number is the sum of an <see cref="GameEvent.Amount"/>
    /// the engine itself set after the hit landed, which is hp actually lost, hp actually restored
    /// and block actually gained. What it cannot tell you is whether those are the plays anyone
    /// would have made — that part belongs to whoever was playing.
    /// </para>
    /// <para>
    /// <see cref="Recording"/> is the whole correctness problem. A bot that looks ahead plays its
    /// options through the engine and rolls the game back, and a play tried that way raises the
    /// same events as a real one: over 200 runs of <c>samples/slice</c> the cautious bot's trials
    /// raise about 1.18 million events against 91 thousand in the play that counted, thirteen
    /// times as many. Counted, every number here would be an order of magnitude too big. So a
    /// trial turns this off; see <see cref="Trials"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var meter = new Meter();
    /// var runtime = new CardRuntime(content, new RuntimeOptions { Host = meter });
    /// // ... play ...
    /// foreach (Tally source in meter.DamageDealt) Console.WriteLine($"{source.Name} {source.Amount}");
    /// </code>
    /// </example>
    public class Meter : EffectHostBase
    {
        private readonly Dictionary<string, Tally> _dealt = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tally> _taken = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tally> _tags = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tally> _healed = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tally> _blocked = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tally> _moves = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tally> _statuses = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tally> _cards = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly Dictionary<SourceSpan, ChoiceTally> _choices = new Dictionary<SourceSpan, ChoiceTally>();

        /// <summary>What a damage tag column calls a hit that carried no tag at all.</summary>
        public const string Untagged = "(no tag)";

        /// <summary>
        /// False while a bot is trying a play it will roll back, when nothing that happens is
        /// happening and so nothing may be counted.
        /// </summary>
        public bool Recording { get; set; } = true;

        /// <summary>This run's hp ledger, for checking these numbers against the engine's own hp.</summary>
        internal HpLedger? Ledger { get; set; }

        /// <summary>Told about every <c>(enemy, move)</c> that fired, or null when nobody is collecting.</summary>
        internal Action<string, string>? MoveFired { get; set; }

        /// <summary>
        /// Told about a card that reached the player's hand, and whether this is the moment it was
        /// played. A hand read at the start of a turn misses a card drawn or created part way
        /// through one, and a report that said such a card never reached a hand would be wrong
        /// about the content rather than merely bounded by the bot.
        /// </summary>
        internal Action<Entity, bool>? CardInHand { get; set; }

        /// <summary>Names the declaration a span falls in, for the choices nothing could judge.</summary>
        internal Func<SourceSpan, string?>? Owner { get; set; }

        /// <summary>
        /// Hp everything but the player lost, by what dealt it: the card where a card was played,
        /// and otherwise whatever was running — a status ticking, or the actor that swung.
        /// </summary>
        public IReadOnlyList<Tally> DamageDealt => Sorted(_dealt);

        /// <summary>Hp the player lost, named the same way.</summary>
        public IReadOnlyList<Tally> DamageTaken => Sorted(_taken);

        /// <summary>
        /// The same hp as <see cref="DamageDealt"/>, by the tags each hit carried, so a hit with
        /// two tags is counted under both and the shares do not add up to one. A hit with no tag
        /// at all is under <see cref="Untagged"/>.
        /// </summary>
        public IReadOnlyList<Tally> DamageDealtByTag => Sorted(_tags);

        /// <summary>Hp restored, by who got it.</summary>
        public IReadOnlyList<Tally> Healing => Sorted(_healed);

        /// <summary>Block gained, by who gained it.</summary>
        public IReadOnlyList<Tally> Block => Sorted(_blocked);

        /// <summary>Moves that ran, as <c>Enemy: Move</c>, and how often.</summary>
        public IReadOnlyList<Tally> Moves => Sorted(_moves);

        /// <summary>Statuses that landed, by name. The amount is the stacks the event carried.</summary>
        public IReadOnlyList<Tally> Statuses => Sorted(_statuses);

        /// <summary>Cards that were played, by name. The amount is the energy paid.</summary>
        public IReadOnlyList<Tally> Cards => Sorted(_cards);

        /// <summary>
        /// Choices content asked for mid-effect that were answered at random, most asked first.
        /// Whatever was played after one of those is luck, so the report names them rather than
        /// leaving them to be read as judgement.
        /// </summary>
        public IReadOnlyList<ChoiceTally> Choices =>
            _choices.Values
                .OrderByDescending(c => c.Times)
                .ThenBy(c => c.Label, StringComparer.Ordinal)
                .ThenBy(c => c.Span.Line)
                .ToList();

        /// <summary>All the hp everything but the player lost. The denominator of those shares.</summary>
        public long TotalDealt => _dealt.Values.Sum(t => t.Amount);

        /// <summary>All the hp the player lost.</summary>
        public long TotalTaken => _taken.Values.Sum(t => t.Amount);

        /// <summary>
        /// The finding a tag column exists for: a large share of the damage that never once
        /// carried a tag most of the rest of it does, so a modifier filtered on that tag would
        /// miss it. Null when the numbers do not say it.
        /// </summary>
        /// <param name="atLeast">
        /// The share of the damage both the source and the tag have to reach before the pair is
        /// worth a line. Below that the report would be pointing at a rounding error.
        /// </param>
        /// <remarks>
        /// Every pair is scored by how much damage it is about: the hp the source dealt, times the
        /// hp that carries the tag. That picks the pair where a modifier written on that tag would
        /// miss the most of what a designer meant it to reach. A source whose hits carried no tag
        /// at all is left out, because <c>(no tag)</c> already has a row of its own and saying it
        /// again for each tag in turn would be noise rather than a finding.
        /// </remarks>
        /// <example>
        /// On <c>samples/slice</c> this is Burn, which is deliberately not tagged <c>fire</c>: a
        /// fire hit shatters Frozen and a tick of Burn should not, and the cost of that decision is
        /// that a modifier on <c>tag:fire</c> never touches a third of the damage the deck deals.
        /// </example>
        public (string Source, string Tag, double Share)? MissingTag(double atLeast = 0.15)
        {
            long total = TotalDealt;
            if (total <= 0) return null;

            double floor = atLeast * total;
            List<Tally> tags = DamageDealtByTag.Where(t => t.Name != Untagged && t.Amount >= floor).ToList();
            if (tags.Count == 0) return null;

            (string Source, string Tag, double Share)? best = null;
            double most = 0;

            foreach (Tally source in DamageDealt)
            {
                if (source.Amount < floor) break; // biggest first, so nothing after this qualifies
                if (source.Tags.Count == 0) continue;

                foreach (Tally tag in tags)
                {
                    if (source.Carried(tag.Name)) continue;

                    double reach = (double)source.Amount * tag.Amount;
                    if (best != null && reach <= most) continue;

                    most = reach;
                    best = (source.Name, tag.Name, (double)source.Amount / total);
                }
            }
            return best;
        }

        /// <summary>True when nothing was measured at all, as in a scenario that threw at its first line.</summary>
        public bool IsEmpty => _dealt.Count == 0 && _taken.Count == 0 && _healed.Count == 0
            && _blocked.Count == 0 && _moves.Count == 0 && _statuses.Count == 0 && _cards.Count == 0;

        /// <summary>
        /// Called after every event's after phase. Override it to see the events themselves, and
        /// call this to keep the counting.
        /// </summary>
        public override void OnEvent(GameEvent gameEvent)
        {
            if (gameEvent == null) throw new ArgumentNullException(nameof(gameEvent));
            if (!Recording) return;

            // An `instead` listener ran in place of the default action, so the hit never landed and
            // the amount on the event is the one nobody took. Whatever the replacement did raised
            // its own events and was counted there.
            if (gameEvent.Replaced) return;

            switch (gameEvent.Name)
            {
                case BuiltinEvents.Damaged: Damaged(gameEvent); break;
                case BuiltinEvents.Healed: Healed(gameEvent); break;
                case BuiltinEvents.GainedBlock: Add(_blocked, Who(gameEvent.Target), gameEvent.Amount.ToInt()); break;
                case BuiltinEvents.Move: Moved(gameEvent); break;
                case BuiltinEvents.StatusApplied: Applied(gameEvent); break;
                case BuiltinEvents.CardPlayed: Played(gameEvent); break;

                // Not counted, only noticed: a card reaching a hand is how the report knows that a
                // card the player never saw at the start of a turn was still drawn or created into
                // one, and played from it.
                case BuiltinEvents.Drawn:
                case BuiltinEvents.Created:
                case BuiltinEvents.Moved: Reached(gameEvent); break;
            }
        }

        /// <summary>
        /// Counts a choice content asked for mid-effect and nothing could judge. Only choices made
        /// in play that counted are here: a trial's are not, and neither are the ones a scenario's
        /// <c>answer</c> lines settled, which were judged by whoever wrote them.
        /// </summary>
        internal void Guessed(SourceSpan span)
        {
            if (!Recording) return;

            if (!_choices.TryGetValue(span, out ChoiceTally? tally))
            {
                tally = new ChoiceTally(span, Owner?.Invoke(span));
                _choices[span] = tally;
            }
            tally.Times++;
        }

        // What the engine raised ----------------------------------------------------------------

        /// <summary>
        /// A hit that landed. <c>amount</c> is hp actually lost: block has come off it, and a hit
        /// that killed is only worth what was left. So these add up to the hp an actor lost, which
        /// is what <see cref="HpLedger"/> checks them against.
        /// </summary>
        private void Damaged(GameEvent gameEvent)
        {
            int hp = gameEvent.Amount.ToInt();
            Entity? target = gameEvent.Target;
            string source = Dealer(gameEvent);

            Tally landed = target != null && target.Team == Team.Player ? Add(_taken, source, hp) : Add(_dealt, source, hp);
            landed.Tagged(gameEvent.Tags);

            if (target != null && target.Team != Team.Player)
            {
                if (gameEvent.Tags.Count == 0) Add(_tags, Untagged, hp);
                else foreach (string tag in gameEvent.Tags) Add(_tags, tag, hp);
            }

            Ledger?.Hurt(target, hp);
        }

        private void Healed(GameEvent gameEvent)
        {
            int hp = gameEvent.Amount.ToInt();
            Add(_healed, Who(gameEvent.Target), hp);
            Ledger?.Healed(gameEvent.Target, hp);
        }

        private void Moved(GameEvent gameEvent)
        {
            if (gameEvent.Source == null) return;
            if (!gameEvent.Data.TryGetValue("move", out Value move) || move.Text == null) return;

            Add(_moves, gameEvent.Source.Name + ": " + move.Text, 0);
            MoveFired?.Invoke(gameEvent.Source.Name, move.Text);
        }

        /// <summary>
        /// A card was played. It was in a hand to be played from, and it was playable, whatever a
        /// snapshot taken at the start of the turn happened to show.
        /// </summary>
        private void Played(GameEvent gameEvent)
        {
            Add(_cards, Who(gameEvent.Card), gameEvent.Amount.ToInt());
            if (Card(gameEvent.Card) is Entity card) CardInHand?.Invoke(card, true);
        }

        /// <summary>A card drawn, created or moved. It counts only if it ended up in a hand.</summary>
        private void Reached(GameEvent gameEvent)
        {
            if (Card(gameEvent.Card ?? gameEvent.Target) is Entity card && card.Zone == Zones.Hand)
                CardInHand?.Invoke(card, false);
        }

        /// <summary>The entity, if it is one of the player's cards rather than anything else.</summary>
        private static Entity? Card(Entity? entity) =>
            entity != null && entity.Kind == EntityKind.Card && entity.Controller.Team == Team.Player ? entity : null;

        private void Applied(GameEvent gameEvent)
        {
            string name = gameEvent.Data.TryGetValue("status_name", out Value status) && status.Text != null
                ? status.Text
                : Who(gameEvent.Data.TryGetValue("status", out Value entity) ? entity.Entity : null);
            Add(_statuses, name, gameEvent.Amount.ToInt());
        }

        /// <summary>
        /// What the report calls the thing that dealt a hit: the card where a card was played, and
        /// otherwise whatever was running — the status whose listener ticked, or the enemy that
        /// swung. <c>damaged.Source</c> is the actor a hit belongs to and <c>damaged.Card</c> the
        /// card that caused it, and inside a status's listener the source is the status itself.
        /// </summary>
        private static string Dealer(GameEvent gameEvent) =>
            gameEvent.Card != null ? gameEvent.Card.Name : Who(gameEvent.Source);

        private static string Who(Entity? entity) => entity?.Name ?? "(nothing)";

        private static Tally Add(Dictionary<string, Tally> table, string name, int amount)
        {
            if (!table.TryGetValue(name, out Tally? tally))
            {
                tally = new Tally(name);
                table[name] = tally;
            }
            tally.Amount += amount;
            tally.Times++;
            return tally;
        }

        /// <summary>
        /// Biggest first, then by name, so two runs of the same command print the same table and a
        /// tie never falls out of the order a dictionary happened to be built in.
        /// </summary>
        private static IReadOnlyList<Tally> Sorted(Dictionary<string, Tally> table) =>
            table.Values
                .OrderByDescending(t => t.Amount)
                .ThenByDescending(t => t.Times)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>
    /// What the meter says happened to each actor's hp in one run, against what the engine says its
    /// hp actually did. They have to agree: damage taken less healing is the hp an actor lost, and
    /// both sides of that come from the engine — one from the amounts it put on its events, the
    /// other from <c>Entity.GetInt("hp")</c>.
    /// </summary>
    /// <remarks>
    /// This is the check that catches the <see cref="Meter.Recording"/> flag being wrong. A meter
    /// that counted a bot's trials would claim an actor took ten times the damage its hp ever
    /// moved by, and nothing else in the report would show it.
    /// </remarks>
    internal sealed class HpLedger
    {
        private readonly Dictionary<int, Row> _rows = new Dictionary<int, Row>();

        /// <summary>Starts watching an actor, at whatever hp the engine says it has now.</summary>
        public void Watch(Entity? actor)
        {
            if (actor == null || actor.Kind != EntityKind.Actor || _rows.ContainsKey(actor.Id)) return;
            _rows[actor.Id] = new Row(actor, actor.GetInt("hp"));
        }

        public void Hurt(Entity? actor, int hp)
        {
            if (actor != null && _rows.TryGetValue(actor.Id, out Row? row)) row.Damage += hp;
        }

        public void Healed(Entity? actor, int hp)
        {
            if (actor != null && _rows.TryGetValue(actor.Id, out Row? row)) row.Healing += hp;
        }

        /// <summary>
        /// Every actor whose hp moved by something other than the damage and healing counted for
        /// it. Empty is the only right answer.
        /// </summary>
        public IReadOnlyList<string> Disagreements()
        {
            var wrong = new List<string>();
            foreach (Row row in _rows.Values.OrderBy(r => r.Actor.Id))
            {
                int lost = row.HpAtStart - row.Actor.GetInt("hp");
                int counted = row.Damage - row.Healing;
                if (lost != counted)
                    wrong.Add($"{row.Actor.Name} lost {lost} hp, but the meter counted {row.Damage} damage and {row.Healing} healing");
            }
            return wrong;
        }

        private sealed class Row
        {
            public Row(Entity actor, int hpAtStart)
            {
                Actor = actor;
                HpAtStart = hpAtStart;
            }

            public Entity Actor { get; }
            public int HpAtStart { get; }
            public int Damage { get; set; }
            public int Healing { get; set; }
        }
    }
}
