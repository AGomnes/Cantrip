using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Descriptions;
using GameplayEffects.Diagnostics;
using GameplayEffects.Linting;
using GameplayEffects.Runtime;
using GameplayEffects.Tests.Linting;
using Xunit;

namespace GameplayEffects.Tests.Descriptions
{
    public sealed class DescriptionTests
    {
        private static ContentLibrary Samples()
        {
            var content = new ContentLibrary();
            content.LoadFile(Path.Combine(LintTestPaths.RepositoryRoot(), "samples", "basic", "content.ge"));
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }

        private static Description Describe(ContentLibrary content, string name, string kind) =>
            new DescriptionBuilder(content).Describe(content.Find(name, kind)!);

        // Levels ------------------------------------------------------------------------------

        [Fact]
        public void Custom_text_fills_placeholders_from_the_effect()
        {
            Description fireball = Describe(Samples(), "Fireball", "card");

            Assert.Equal(DescriptionLevel.Custom, fireball.Level);
            Assert.Equal("Hurl a ball of flame for 6 damage. Kill it to draw 1.", fireball.ToPlainText());
            Assert.Equal("Warm regards.", fireball.Flavour);

            DescriptionSegment damage = fireball.Find("damage")!;
            Assert.True(damage.HasNumber);
            Assert.Equal(6, damage.Base.ToInt());
            Assert.Equal(ValueTrend.Unchanged, damage.Trend);
            Assert.Equal(2, fireball.Cost!.Current.ToInt());
        }

        [Fact]
        public void Live_values_go_through_the_modifier_pipeline()
        {
            ContentLibrary content = Samples();
            var runtime = new CardRuntime(content);
            runtime.CreatePlayer();
            runtime.AddRelic("Pyromancer's Codex");
            Entity fireball = runtime.AddCard("Fireball", Zones.Hand);

            Description description = new DescriptionBuilder(content).Describe(fireball, runtime);

            DescriptionSegment damage = description.Find("damage")!;
            Assert.Equal(6, damage.Base.ToInt());
            Assert.Equal(9, damage.Current.ToInt());
            Assert.Equal(ValueTrend.Buffed, damage.Trend);
            Assert.Equal("Hurl a ball of flame for 9 damage. Kill it to draw 1.", description.ToPlainText());
            Assert.Contains("~~6~~ 9", description.ToMarkup());

            Assert.Equal(2, description.Cost!.Base.ToInt());
            Assert.Equal(1, description.Cost.Current.ToInt());
            Assert.Equal(ValueTrend.Buffed, description.Cost.Trend);
        }

        [Fact]
        public void Live_damage_against_a_target_includes_damage_taken()
        {
            ContentLibrary content = Samples();
            var runtime = new CardRuntime(content);
            runtime.CreatePlayer();
            Entity enemy = runtime.SpawnEnemy("Jaw Worm");
            runtime.ApplyStatus("Vulnerable", enemy, 2);
            Entity strike = runtime.AddCard("Strike", Zones.Hand);

            Description description = new DescriptionBuilder(content).Describe(strike, runtime, enemy);

            Assert.Equal("Deal 9 damage.", description.ToPlainText());
        }

        [Fact]
        public void Text_override_is_shown_verbatim_with_no_live_values()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                card "Mystery"
                  cost 1
                  effect:
                    draw 2
                  text_override: "Something happens."
                """);

            Description description = new DescriptionBuilder(content).Describe(content.Find("Mystery", "card")!);

            Assert.Equal(DescriptionLevel.Override, description.Level);
            Assert.Equal("Something happens.", description.ToPlainText());
            Assert.Empty(description.Values);
        }

        // Automatic text ----------------------------------------------------------------------

        [Fact]
        public void Automatic_text_for_a_simple_attack()
        {
            Description strike = Describe(Samples(), "Strike", "card");

            Assert.Equal(DescriptionLevel.Auto, strike.Level);
            Assert.Equal("Deal 6 damage.", strike.ToPlainText());
        }

        [Fact]
        public void Automatic_text_for_poison()
        {
            string text = Describe(Samples(), "Poison", "status").ToPlainText();

            Assert.Equal("At the end of its holder's turn: Deal X damage to its holder, ignoring Block. Lose 1 stack.", text);
        }

        [Fact]
        public void Automatic_text_for_strength_shows_stacks_live()
        {
            ContentLibrary content = Samples();
            Assert.Equal("Damage dealt +X.", Describe(content, "Strength", "status").ToPlainText());

            var runtime = new CardRuntime(content);
            Entity player = runtime.CreatePlayer();
            Entity strength = runtime.ApplyStatus("Strength", player, 3)!;

            Description live = new DescriptionBuilder(content).Describe(strength, runtime);
            Assert.Equal("Damage dealt +3.", live.ToPlainText());
        }

        [Fact]
        public void Automatic_text_for_frozen_and_kindling()
        {
            ContentLibrary content = Samples();

            Assert.Equal(
                "When its holder takes fire damage: Remove Frozen from its holder. Deal 10 damage to its holder.",
                Describe(content, "Frozen", "status").ToPlainText());

            Description kindling = Describe(content, "Kindling", "relic");
            Assert.Equal("When an ice status is removed: Apply 2 Burn to it.", kindling.ToPlainText());
            Assert.Equal(new[] { "Burn" }, kindling.Tooltips.Select(t => t.Name));
        }

        [Fact]
        public void Modifiers_describe_their_channel_and_filter()
        {
            string text = Describe(Samples(), "Pyromancer's Codex", "relic").ToPlainText();

            Assert.Equal("Damage dealt ×1.5 (fire). Cost -1 for cards (fire).", text);
        }

        [Fact]
        public void Abilities_and_replacement_listeners_read_naturally()
        {
            ContentLibrary content = Samples();

            Description nova = Describe(content, "Frost Nova", "ability");
            Assert.Equal("Apply 40% Slow to ALL enemies for 3s. Cooldown 8s.", nova.ToPlainText());
            Assert.Empty(nova.Tooltips); // Slow is a marker with no rules text of its own

            Assert.Equal("If you die, instead (once per combat): Heal 40 HP.", Describe(content, "Lizard Tail", "relic").ToPlainText());
        }

        [Fact]
        public void Control_flow_and_card_keywords_read_as_sentences()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                status "Weak"
                  stacking duration

                card "Gambit"
                  cost 1
                  target enemy
                  tags exhaust
                  effect:
                    deal 8 to target
                    if target.dead: draw 2
                    until turn_end:
                      gain 1 Weak
                """);

            string text = new DescriptionBuilder(content).Describe(content.Find("Gambit", "card")!).ToPlainText();

            Assert.Equal("Deal 8 damage. If the target dies, draw 2 cards. Until the end of the turn: Gain 1 Weak. Exhaust.", text);
        }

        // Tooltips ----------------------------------------------------------------------------

        [Fact]
        public void Tooltips_are_flattened_and_listed_once()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                status "Burn"
                  on turn_end:
                    deal 2 to owner

                status "Scorch"
                  on turn_end:
                    apply Burn 1 to owner

                card "Inferno"
                  cost 1
                  target enemy
                  effect:
                    apply Scorch 1 to target
                    apply Burn 2 to target
                """);

            Description inferno = new DescriptionBuilder(content).Describe(content.Find("Inferno", "card")!);

            Assert.Equal(new[] { "Scorch", "Burn" }, inferno.Tooltips.Select(t => t.Name));
            Assert.All(inferno.Tooltips, t => Assert.Empty(t.Description.Tooltips));
            Assert.Equal("At the end of its holder's turn: Apply 1 Burn to its holder.", inferno.Tooltips[0].Description.ToPlainText());
        }

        // Localization ------------------------------------------------------------------------

        private sealed class French : EnglishDescriptions
        {
            public override string? Name(EntityDefinition definition) => definition.Name == "Strike" ? "Frappe" : null;

            public override string? Phrase(string key) => key == "deal" ? "Inflige {amount} dégâts{target}." : base.Phrase(key);

            public override string? Text(EntityDefinition definition) =>
                definition.Name == "Fireball" ? "Lance une boule de feu : {damage} dégâts." : null;
        }

        [Fact]
        public void A_localizer_supplies_names_phrases_and_text_with_the_same_placeholders()
        {
            ContentLibrary content = Samples();
            var builder = new DescriptionBuilder(content, new French());

            Description strike = builder.Describe(content.Find("Strike", "card")!);
            Assert.Equal("Frappe", strike.Name);
            Assert.Equal("Inflige 6 dégâts.", strike.ToPlainText());

            Assert.Equal("Lance une boule de feu : 6 dégâts.", builder.Describe(content.Find("Fireball", "card")!).ToPlainText());
        }

        [Fact]
        public void Numbers_are_formatted_the_same_in_every_culture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Contains("×1.5", Describe(Samples(), "Pyromancer's Codex", "relic").ToPlainText());
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        // Drift protection --------------------------------------------------------------------

        [Fact]
        public void A_placeholder_that_points_at_nothing_is_reported()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                card "Typo"
                  cost 1
                  target enemy
                  effect:
                    deal 6 to target
                  text: "Deal {damge} damage."
                """);

            IReadOnlyList<Diagnostic> diagnostics = new DescriptionBuilder(content).Validate(content.Find("Typo", "card")!);

            Diagnostic diagnostic = Assert.Single(diagnostics);
            Assert.Equal(DescriptionBuilder.UnknownPlaceholder, diagnostic.Code);
            Assert.Equal("damage", diagnostic.Suggestion);
            Assert.Contains(Linter.Lint(content), d => d.Code == DescriptionBuilder.UnknownPlaceholder);
        }

        [Fact]
        public void Text_checked_reports_when_the_effect_changes_but_not_when_only_the_text_does()
        {
            const string Card = """
                card "Bolt"
                  cost 1
                  target enemy
                  effect:
                    deal {0} to target
                  text: "{1}"
                  flavour: "{2}"
                """;

            // The raw string has no trailing newline, so the optional property starts its own line.
            string Source(int damage, string text, string flavour, string? checkedHash) =>
                Card.Replace("{0}", damage.ToString(CultureInfo.InvariantCulture)).Replace("{1}", text).Replace("{2}", flavour)
                + (checkedHash == null ? "\n" : $"\n  text_checked \"{checkedHash}\"\n");

            EntityDefinition original = ContentLibrary.FromText(Source(6, "Zap for {damage}.", "Bzzt.", null)).Find("Bolt", "card")!;
            string hash = DescriptionBuilder.EffectHash(original);
            Assert.Matches("^[0-9a-f]{16}$", hash);

            ContentLibrary reworded = ContentLibrary.FromText(Source(6, "Shock for {damage}.", "Crackle.", hash));
            Assert.Equal(hash, DescriptionBuilder.EffectHash(reworded.Find("Bolt", "card")!));
            Assert.Empty(new DescriptionBuilder(reworded).Validate(reworded.Find("Bolt", "card")!));

            ContentLibrary rebalanced = ContentLibrary.FromText(Source(8, "Zap for {damage}.", "Bzzt.", hash));
            Diagnostic stale = Assert.Single(new DescriptionBuilder(rebalanced).Validate(rebalanced.Find("Bolt", "card")!));
            Assert.Equal(DescriptionBuilder.StaleText, stale.Code);
            Assert.Contains(DescriptionBuilder.EffectHash(rebalanced.Find("Bolt", "card")!), stale.Message);
        }

        [Fact]
        public void Every_sample_definition_can_be_described()
        {
            ContentLibrary content = Samples();
            var builder = new DescriptionBuilder(content);

            foreach (EntityDefinition definition in content.Definitions.Where(d => d.KindName != "resource"))
            {
                Description description = builder.Describe(definition);

                // A marker status such as Slow has no rules of its own, so only things a player
                // reads off a card or relic must say something.
                if (definition.Kind == EntityKind.Card || definition.Kind == EntityKind.Relic)
                    Assert.False(description.IsEmpty, $"{definition} has an empty description");
                Assert.DoesNotContain("{", description.ToPlainText());
            }
        }
    }
}
