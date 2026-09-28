using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cantrip.Diagnostics;
using Cantrip.Syntax;

namespace Cantrip.Content
{
    /// <summary>How the distance between two slots is measured.</summary>
    public enum BoardMetric
    {
        // Saved games store these as numbers: never renumber or reuse one, only add.

        /// <summary>The two terms added. A diagonal step is two.</summary>
        Manhattan = 0,

        /// <summary>The larger of the two terms. A diagonal step is one.</summary>
        Chebyshev = 1,
    }

    /// <summary>Whether the two sides stand on mirrored grids or on one shared grid.</summary>
    public enum BoardSides
    {
        // Saved games store these as numbers: never renumber or reuse one, only add.

        /// <summary>
        /// Each side has its own grid, rank 0 nearest the middle. Two actors on opposite sides at
        /// rank 0 are one step apart, and a rank never means the same place on both sides.
        /// </summary>
        Facing = 0,

        /// <summary>One grid, shared. A rank is the same place whoever is standing on it.</summary>
        Shared = 1,
    }

    /// <summary>What happens to the actors behind a slot when its occupant leaves.</summary>
    public enum OnVacated
    {
        // Saved games store these as numbers: never renumber or reuse one, only add.

        /// <summary>Nothing. The hole stays until something fills it, and survivors never move.</summary>
        Gap = 0,

        /// <summary>Everyone behind it in that lane steps forward one rank.</summary>
        CloseRanks = 1,
    }

    /// <summary>
    /// The shape of the board a battle is fought on: a rectangle of <c>lanes</c> across and
    /// <c>ranks</c> along the facing axis, both counting from 0. Content declares any number of
    /// them by name and a game picks one per battle; content that declares none gets
    /// <see cref="Default"/>, which is the board every game had before boards existed.
    /// </summary>
    /// <remarks>
    /// A shape is content, never something a game invents at runtime, because the linter has to
    /// know the depth to say that <c>it.rank &lt;= 3</c> on a two-rank board matches nothing.
    /// </remarks>
    public sealed class BoardShape
    {
        /// <summary>The name of the board content gets when it declares none.</summary>
        public const string DefaultName = "default";

        /// <summary><see cref="Ranks"/> when a lane goes on for as long as anything is put in it.</summary>
        public const int Unbounded = 0;

        public BoardShape(
            string name,
            int lanes = 1,
            int ranks = Unbounded,
            BoardSides sides = BoardSides.Facing,
            BoardMetric metric = BoardMetric.Manhattan,
            OnVacated onVacated = OnVacated.Gap,
            string? laneWord = null,
            string? rankWord = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (lanes < 1) throw new ArgumentOutOfRangeException(nameof(lanes), lanes, "A board has at least one lane.");
            if (ranks < 0) throw new ArgumentOutOfRangeException(nameof(ranks), ranks, "A board has at least one rank, or none stated for unbounded.");

            Name = name;
            Lanes = lanes;
            Ranks = ranks;
            Sides = sides;
            Metric = metric;
            OnVacated = onVacated;
            LaneWord = string.IsNullOrWhiteSpace(laneWord) ? "lane" : laneWord!;
            RankWord = string.IsNullOrWhiteSpace(rankWord) ? "rank" : rankWord!;
        }

        /// <summary>
        /// <c>lanes 1, ranks unbounded, facing, manhattan, gap</c>: today's board, spelled out. It is
        /// what content that declares no board is played on, which is why no sample moves.
        /// </summary>
        public static BoardShape Default { get; } = new BoardShape(DefaultName);

        public string Name { get; }

        /// <summary>Slots across. One or more.</summary>
        public int Lanes { get; }

        /// <summary>Slots deep, or <see cref="Unbounded"/> for a lane with no floor.</summary>
        public int Ranks { get; }

        public BoardSides Sides { get; }

        public BoardMetric Metric { get; }

        public OnVacated OnVacated { get; }

        /// <summary>What this game calls a lane, for rules text. <c>lane</c> unless content says.</summary>
        public string LaneWord { get; }

        /// <summary>What this game calls a rank, for rules text. <c>rank</c> unless content says.</summary>
        public string RankWord { get; }

        public bool RanksAreUnbounded => Ranks == Unbounded;

        /// <summary>Whether a lane number is one this board has.</summary>
        public bool HasLane(int lane) => lane >= 0 && lane < Lanes;

        /// <summary>Whether a rank number is one this board has. Every rank from 0 up, when unbounded.</summary>
        public bool HasRank(int rank) => rank >= 0 && (RanksAreUnbounded || rank < Ranks);

        public bool Holds(int lane, int rank) => HasLane(lane) && HasRank(rank);

        /// <summary>
        /// The greatest number of steps this board can put between two actors, or null when the
        /// ranks are unbounded and there is no such number. A reach of this much or more reaches
        /// everybody, which is what CT332 says out loud.
        /// </summary>
        /// <remarks>
        /// On a <c>facing</c> board the two sides are mirrored and the rank term across them is
        /// <c>a.rank + b.rank + 1</c>, so the deepest pair is the two back ranks facing each other:
        /// <c>2 * (Ranks - 1) + 1</c>. On a <c>shared</c> board a rank is one place for everybody,
        /// so it is <c>Ranks - 1</c>.
        /// </remarks>
        public int? MaxDistance
        {
            get
            {
                if (RanksAreUnbounded) return null;
                int lanes = Lanes - 1;
                int ranks = Sides == BoardSides.Facing ? (2 * (Ranks - 1)) + 1 : Ranks - 1;
                return Metric == BoardMetric.Chebyshev ? Math.Max(lanes, ranks) : lanes + ranks;
            }
        }

        /// <summary>
        /// Whether a lane on this board holds one actor at most, which is true of a board one rank
        /// deep: <c>lane(who)</c> is then <c>who</c> alone, however it is written.
        /// </summary>
        public bool LaneHoldsOne => Ranks == 1;

        /// <summary>
        /// Whether a rank on this board holds one actor at most, which is true of a board one lane
        /// wide — today's board, and so worth saying out loud when content writes <c>rank(who)</c>
        /// on it and gets a group of one.
        /// </summary>
        public bool RankHoldsOne => Lanes == 1;

        /// <summary>
        /// Whether this is the same board as <paramref name="other"/>, by everything that changes a
        /// result. The two words are left out: renaming a lane changes rules text, never a slot.
        /// </summary>
        public bool SameShapeAs(BoardShape? other) =>
            other != null
            && Lanes == other.Lanes
            && Ranks == other.Ranks
            && Sides == other.Sides
            && Metric == other.Metric
            && OnVacated == other.OnVacated;

        /// <summary>The shape as a save writes it and a message says it: <c>3 lanes, 2 ranks</c>.</summary>
        public string Describe() =>
            (Lanes == 1 ? "1 lane" : Lanes.ToString(CultureInfo.InvariantCulture) + " lanes")
            + ", "
            + (RanksAreUnbounded ? "unbounded ranks" : Ranks == 1 ? "1 rank" : Ranks.ToString(CultureInfo.InvariantCulture) + " ranks");

        public override string ToString() => $"board \"{Name}\" ({Describe()})";

        internal const string Diagnostic = "CT0114";

        private static readonly string[] Known =
        {
            "lanes", "ranks", "facing", "shared", "metric", "on_vacated", "lane_word", "rank_word",
        };

        /// <summary>Reads a <c>board</c> declaration, reporting anything it cannot make sense of.</summary>
        internal static BoardShape FromDefinition(EntityDefinition definition, DiagnosticBag diagnostics)
        {
            int lanes = 1;
            int ranks = Unbounded;
            BoardSides sides = BoardSides.Facing;
            BoardMetric metric = BoardMetric.Manhattan;
            OnVacated onVacated = OnVacated.Gap;
            string? laneWord = null;
            string? rankWord = null;

            void Bad(string message, SourceSpan span) => diagnostics.Error(Diagnostic, message, span);

            foreach (PropertyNode property in definition.Syntax.Members.OfType<PropertyNode>())
            {
                string name = property.Name;
                switch (name)
                {
                    case "lanes":
                        lanes = Count(property, 1, $"`lanes` in board \"{definition.Name}\"") ?? lanes;
                        break;

                    case "ranks":
                        // `ranks unbounded` is how a lane says it has no floor, which is also what
                        // leaving the line out says. Both are spelled here so a board can say so.
                        if (Word(property) == "unbounded") ranks = Unbounded;
                        else ranks = Count(property, 1, $"`ranks` in board \"{definition.Name}\"") ?? ranks;
                        break;

                    case "facing":
                    case "shared":
                        if (property.Values.Count > 0)
                            Bad($"`{name}` in board \"{definition.Name}\" is written on its own, with nothing after it.", property.Span);
                        sides = name == "shared" ? BoardSides.Shared : BoardSides.Facing;
                        break;

                    case "metric":
                        switch (Word(property))
                        {
                            case "manhattan": metric = BoardMetric.Manhattan; break;
                            case "chebyshev": metric = BoardMetric.Chebyshev; break;
                            default:
                                Bad($"`metric` in board \"{definition.Name}\" is `manhattan` or `chebyshev`.", property.Span);
                                break;
                        }
                        break;

                    case "on_vacated":
                        switch (Word(property))
                        {
                            case "gap": onVacated = OnVacated.Gap; break;
                            case "close_ranks": onVacated = OnVacated.CloseRanks; break;
                            default:
                                Bad($"`on_vacated` in board \"{definition.Name}\" is `gap` — the default, where survivors never move — or `close_ranks`.", property.Span);
                                break;
                        }
                        break;

                    case "lane_word":
                        laneWord = Text(property) ?? laneWord;
                        break;

                    case "rank_word":
                        rankWord = Text(property) ?? rankWord;
                        break;

                    default:
                        Bad(
                            $"`{name}` is not something a board says." + (Suggest.Closest(name, Known) is string close ? $" Did you mean `{close}`?" : string.Empty),
                            property.Span);
                        break;
                }
            }

            foreach (MemberNode member in definition.Syntax.Members)
            {
                if (member is PropertyNode) continue;
                Bad($"A board is a shape, not a behaviour, so board \"{definition.Name}\" cannot hold this.", member.Span);
            }

            return new BoardShape(definition.Name, lanes, ranks, sides, metric, onVacated, laneWord, rankWord);

            int? Count(PropertyNode property, int least, string what)
            {
                if (property.Values.Count != 1 || property.Values[0] is not NumberExpr number
                    || number.Value != number.Value.Floor() || number.Value < Num.FromInt(least))
                {
                    Bad($"{what} is a whole number of {least} or more.", property.Span);
                    return null;
                }
                return number.Value.ToInt();
            }

            static string? Word(PropertyNode property) =>
                property.Values.Count == 1 ? EntityDefinition.ReadWords(property.Values[0]).FirstOrDefault()?.ToLowerInvariant() : null;

            string? Text(PropertyNode property)
            {
                if (property.Values.Count == 1 && property.Values[0] is StringExpr text && text.Value.Length > 0) return text.Value;
                if (Word(property) is string word && word.Length > 0) return word;
                Bad($"`{property.Name}` in board \"{definition.Name}\" is one word, as in `lane_word \"room\"`.", property.Span);
                return null;
            }
        }
    }
}
