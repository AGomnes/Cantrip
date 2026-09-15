using System.Collections.Generic;
using GameplayEffects.Content;
using GameplayEffects.Testing;
using Xunit;

namespace GameplayEffects.Tests.Docs
{
    /// <summary>
    /// Defects found while writing docs/language.md. Each test states, as a DSL test block, what a
    /// content author reading the reference would expect, so it fails until the defect is fixed.
    /// The "Known issues" section of the reference describes today's behaviour and workarounds.
    /// </summary>
    public sealed class DocsBugTests
    {
        [Fact]
        public void HarnessPassesAWorkingTest() => DocsDsl.AssertPasses(@"
card ""Strike""
  cost 1
  target enemy
  effect:
    deal 6 to target

test ""strike""
  enemy hp 10
  play Strike on enemy
  expect enemy.hp == 4
");

        [Fact]
        [Trait("Regression", "listener-phase-after-scope")]
        public void PhasePrefixAfterScopeIsHonoured() => DocsDsl.AssertPasses(@"
status ""Thorny""
  on owner.before_damaged:
    event.amount = 0

test ""owner.before_damaged runs before the hit lands""
  enemy hp 20
  apply Thorny 1 to enemy
  deal 5 to enemy
  expect enemy.hp == 20
");

        [Fact]
        [Trait("Regression", "until-existing-status")]
        public void UntilRevertsStacksAddedToAnExistingStatus() => DocsDsl.AssertPasses(@"
status ""Strength""
  stacking intensity
  modify damage: +stacks

card ""Flex""
  cost 0
  effect:
    until turn_end:
      gain 2 Strength

test ""until reverts a gain on a status the player already had""
  enemy hp 50
  player Strength 1
  play Flex
  end turn
  expect player.Strength == 1
");

        [Fact]
        [Trait("Regression", "kill-positional")]
        public void KillTakesItsVictimAsAnArgument() => DocsDsl.AssertPasses(@"
test ""kill enemy2 kills enemy2""
  enemy hp 20
  enemy hp 20
  kill enemy2
  expect enemy2.dead
");

        [Fact]
        [Trait("Regression", "modify-draw")]
        public void DrawModifiersChangeHowManyCardsAreDrawn() => DocsDsl.AssertPasses(@"
card ""Strike""
  cost 1
  effect:
    block 1

relic ""Card Draw Boost""
  modify draw: +1

test ""draw 1 with a +1 draw modifier draws two""
  deck Strike Strike Strike Strike
  relic ""Card Draw Boost""
  draw 1
  expect hand.count == 2
");

        [Fact]
        [Trait("Regression", "cost-applied-twice")]
        public void CostModifiersApplyOnceWhenPaying() => DocsDsl.AssertPasses(@"
card ""Fireball""
  cost 2
  target enemy
  tags fire
  effect:
    deal 6 to target

relic ""Discount""
  modify cost: -1

test ""a -1 cost modifier makes a 2-cost card cost 1""
  enemy hp 50
  relic Discount
  play Fireball on enemy
  expect player.energy == 2
");

        [Fact]
        [Trait("Regression", "decay-custom-event")]
        public void DecayOnANonTurnEventDecays() => DocsDsl.AssertPasses(@"
card ""Skip""
  cost 0
  effect:
    block 1

status ""Fading""
  stacking intensity
  decay 1 on card_played

test ""decay 1 on card_played""
  apply Fading 3 to player
  play Skip
  expect player.Fading == 2
");

        [Fact]
        [Trait("Regression", "resource-reset-on")]
        public void ResourceResetsOnANonTurnEvent() => DocsDsl.AssertPasses(@"
resource ""rage""
  min 0
  reset_to 0
  reset_on battle_start

test ""reset_on battle_start resets the resource""
  player rage 5
  enemy hp 50
  expect player.rage == 0
");

        [Fact]
        [Trait("Regression", "duration-status-read")]
        public void ReadingADurationStatusGivesItsDuration() => DocsDsl.AssertPasses(@"
status ""Vulnerable""
  stacking duration
  modify damage_taken: x1.5

test ""enemy.Vulnerable reads the remaining duration""
  enemy hp 20
  apply Vulnerable 2 to enemy
  expect enemy.Vulnerable == 2
");

        [Fact]
        [Trait("Regression", "random-in-precedence")]
        public void RandomCardsInHandPicksFromTheHand() => DocsDsl.AssertPasses(@"
card ""Strike""
  cost 1
  effect:
    block 1

test ""random 2 cards in hand picks two cards from the hand""
  hand Strike Strike Strike
  deck Strike Strike Strike Strike Strike Strike Strike Strike Strike Strike
  expect (random 2 cards in hand).count == 2
");

        [Fact]
        [Trait("Regression", "change-compact-form")]
        public void ChangeCompactFormWorks() => DocsDsl.AssertPasses(@"
test ""change hp -5 on enemy""
  enemy hp 50
  change hp -5 on enemy
  expect enemy.hp == 45
");

        [Fact]
        [Trait("Regression", "combined-filter-involvement")]
        public void DefinitionFilterStillRequiresInvolvementWhenCombined() => DocsDsl.AssertPasses(@"
status ""Poison""
  stacking intensity

status ""Weak""
  stacking duration

relic ""Snake Eye""
  on status_applied(Poison, target:enemies):
    gain 1 poison_apps

test ""applying Weak does not trigger a Poison listener""
  enemy hp 50
  relic ""Snake Eye""
  apply Weak 1 to enemy
  expect player.poison_apps == 0
");

        [Fact]
        [Trait("Regression", "in-turns-before-reset")]
        public void InOneTurnEnergySurvivesTheTurnStartReset() => DocsDsl.AssertPasses(@"
card ""Delayed Energy""
  cost 0
  effect:
    in 1 turn:
      gain 1 energy

test ""in 1 turn: gain 1 energy""
  enemy hp 50
  play ""Delayed Energy""
  end turn
  expect player.energy == 4
");

        [Fact]
        [Trait("Regression", "died-after-on-self")]
        public void AnEnemyHearsItsOwnDeath() => DocsDsl.AssertPasses(@"
enemy ""Bomber""
  hp 10
  on died:
    deal 5 to player

test ""a dying enemy's on died listener runs""
  enemy Bomber
  enemy hp 50
  player hp 50
  deal 20 to enemy
  expect player.hp == 45
");

        [Fact]
        [Trait("Regression", "member-inline-block")]
        public void EffectWithInlineBodyRunsOrIsRejected() => DocsDsl.AssertPassesOrReportsError(@"
card ""Inline""
  cost 0
  target enemy
  effect: deal 6 to target

test ""effect: with its body on the same line""
  enemy hp 20
  play Inline on enemy
  expect enemy.hp == 14
");
    }

    /// <summary>Runs DSL test blocks from a string and turns their failures into xunit failures.</summary>
    internal static class DocsDsl
    {
        public static void AssertPasses(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "docs-bug.ge");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            AssertAllPass(content);
        }

        /// <summary>For defects where rejecting the content at load time would be an equally good fix.</summary>
        public static void AssertPassesOrReportsError(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "docs-bug.ge");
            if (content.Diagnostics.HasErrors) return;
            AssertAllPass(content);
        }

        private static void AssertAllPass(ContentLibrary content)
        {
            IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }
    }
}
