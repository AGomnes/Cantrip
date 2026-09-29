using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Which end of a value an <c>of ...</c> group is matched against.
    /// </summary>
    /// <remarks>
    /// The rule is that the group names whoever the value belongs to, and that is the same end the
    /// bare form uses: whoever deals the damage on <c>damage</c>, whoever takes it on
    /// <c>damage_taken</c>. It used to be the query's subject on every channel, so on <c>damage</c>
    /// an <c>of</c> group was matched against the one being hit while the bare form was matched
    /// against the one swinging. The two spellings that read as widenings of each other were scoped
    /// to opposite ends of the same hit, and <c>modify damage of party: +2</c> — the line a party
    /// game is most likely to want — matched nothing at all and said nothing about it.
    /// </remarks>
    public sealed class ModifierScopeTests
    {
        private const string Content = """
            hero "Warden"
              hp 30
              tags devout

            hero "Squire"
              hp 30

            card "Swing"
              cost 1
              target enemy
              effect:
                deal 10 to target

            relic "Bare"
              modify damage: +2

            relic "OfParty"
              modify damage of party: +2

            relic "OfAllies"
              modify damage of allies: +2

            relic "OfEveryone"
              modify damage of everyone: +2

            relic "OfEnemies"
              modify damage of enemies: +2

            relic "OfTheDevout"
              modify damage of party where it.has(tag:devout): +2

            relic "TakenByParty"
              modify damage_taken of party: +2

            relic "PartysHealth"
              modify max_hp of party: +5
            """;

        private static CardRuntime Party(string? relic, out Entity warden, out Entity enemy)
        {
            CardRuntime runtime = Create(Content);
            warden = runtime.AddHero("Warden");
            enemy = Enemy(runtime, 100);
            if (relic != null) runtime.AddRelic(relic);
            Start(runtime);
            return runtime;
        }

        private static int SwungByTheWarden(string? relic)
        {
            CardRuntime runtime = Party(relic, out Entity warden, out Entity enemy);
            Entity swing = runtime.AddCard("Swing", Zones.Hand);
            runtime.Play(swing, enemy, warden);
            return 100 - Hp(enemy);
        }

        private static int SwungByTheEnemy(string? relic)
        {
            CardRuntime runtime = Party(relic, out Entity warden, out Entity enemy);
            runtime.Execute("deal 10 to target", self: enemy, target: warden);
            return 30 - Hp(warden);
        }

        [Fact]
        public void Of_party_reaches_what_a_party_member_deals()
        {
            Assert.Equal(10, SwungByTheWarden(null));
            Assert.Equal(12, SwungByTheWarden("OfParty"));
            Assert.Equal(12, SwungByTheWarden("OfAllies"));
            Assert.Equal(12, SwungByTheWarden("OfEveryone"));
        }

        /// <summary>
        /// A relic belongs to the leader and a hero is its own controller, so the bare form still
        /// reaches the leader's own plays and nobody else's. That is what <c>of party</c> widens.
        /// </summary>
        [Fact]
        public void And_the_bare_form_still_reaches_the_holder_alone()
        {
            Assert.Equal(10, SwungByTheWarden("Bare"));

            CardRuntime runtime = Party("Bare", out _, out Entity enemy);
            Entity swing = runtime.AddCard("Swing", Zones.Hand);
            runtime.Play(swing, enemy);
            Assert.Equal(12, 100 - Hp(enemy));
        }

        [Fact]
        public void Of_enemies_on_damage_is_what_the_enemies_deal()
        {
            Assert.Equal(10, SwungByTheWarden("OfEnemies"));
            Assert.Equal(12, SwungByTheEnemy("OfEnemies"));
            Assert.Equal(10, SwungByTheEnemy("OfParty"));
        }

        /// <summary>A <c>where</c> on the scope reads <c>it</c> as the one that matched.</summary>
        [Fact]
        public void The_scopes_where_tests_the_one_the_value_belongs_to()
        {
            Assert.Equal(12, SwungByTheWarden("OfTheDevout"));

            CardRuntime runtime = Create(Content);
            Entity squire = runtime.AddHero("Squire");
            Entity enemy = Enemy(runtime, 100);
            runtime.AddRelic("OfTheDevout");
            Start(runtime);

            Entity swing = runtime.AddCard("Swing", Zones.Hand);
            runtime.Play(swing, enemy, squire);
            Assert.Equal(10, 100 - Hp(enemy));
        }

        /// <summary>
        /// The channels whose value already belonged to the subject do not move: <c>damage_taken</c>
        /// is what a group takes, and a stat is whoever holds it.
        /// </summary>
        [Fact]
        public void The_taken_channels_and_stats_keep_naming_the_subject()
        {
            Assert.Equal(12, SwungByTheEnemy("TakenByParty"));
            Assert.Equal(10, SwungByTheWarden("TakenByParty"));

            CardRuntime runtime = Party("PartysHealth", out Entity warden, out Entity enemy);
            Assert.Equal(35, warden.GetInt("max_hp"));
            Assert.Equal(100, enemy.GetInt("max_hp"));
        }
    }
}
