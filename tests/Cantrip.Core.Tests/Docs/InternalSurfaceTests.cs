#nullable enable
using System;
using System.Reflection;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Docs
{
    /// <summary>
    /// Types and members that were public only because a test or the simulator needed them. After
    /// 1.0 every public name is a promise, so these are internal and reached through
    /// InternalsVisibleTo instead — which is why this test can still name them at compile time and
    /// has to ask reflection whether they are public.
    /// </summary>
    public sealed class InternalSurfaceTests
    {
        private static readonly Assembly Core = typeof(CardRuntime).Assembly;

        [Theory]
        [InlineData("Cantrip.Testing.SetupSession")]
        [InlineData("Cantrip.Content.Scenario")]
        [InlineData("Cantrip.Diagnostics.Suggest")]
        [InlineData("Cantrip.Runtime.TargetRule")]
        public void A_type_only_the_library_and_its_tools_use_is_internal(string name)
        {
            Type type = Core.GetType(name) ?? throw new InvalidOperationException(name + " is gone entirely.");

            Assert.False(type.IsPublic, name + " is public.");
            Assert.False(type.IsNestedPublic, name + " is public.");
            Assert.DoesNotContain(Core.GetExportedTypes(), exported => exported == type);
        }

        [Theory]
        [InlineData("ExpireTimedStatuses")]
        [InlineData("ResetResources")]
        [InlineData("AdjustStatusStacks")]
        [InlineData("ShuffleZone")]
        [InlineData("ShuffleDiscardIntoDraw")]
        [InlineData("IsBuiltinVerb")]
        public void An_interpreter_pump_internal_is_not_part_of_the_surface(string method)
        {
            Assert.DoesNotContain(typeof(Interpreter).GetMethods(BindingFlags.Public | BindingFlags.Instance),
                m => m.Name == method);

            // Still there, still called by the runtime — just not something a game may lean on.
            Assert.Contains(typeof(Interpreter).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance),
                m => m.Name == method);
        }

        /// <summary>
        /// The other half of the same decision: these are real layers a game builds on, and the
        /// Godot debugger drives the stepping ones, so they stay public.
        /// </summary>
        [Theory]
        [InlineData(typeof(Interpreter), "Pause")]
        [InlineData(typeof(Interpreter), "Resume")]
        [InlineData(typeof(Interpreter), "TryDrainStep")]
        [InlineData(typeof(Interpreter), "Next")]
        [InlineData(typeof(Interpreter), "Drain")]
        [InlineData(typeof(Interpreter), "Copy")]
        [InlineData(typeof(Interpreter), "Transform")]
        [InlineData(typeof(GameState), "Become")]
        [InlineData(typeof(GameState), "Duplicate")]
        public void A_layer_a_game_builds_on_stays_public(Type type, string member)
        {
            Assert.NotEmpty(type.GetMember(member, BindingFlags.Public | BindingFlags.Instance));
        }

        [Theory]
        [InlineData("Paused")]
        [InlineData("Breakpoints")]
        [InlineData("PendingTriggers")]
        public void The_debuggers_stepping_state_stays_public(string property)
        {
            Assert.NotNull(typeof(Interpreter).GetProperty(property, BindingFlags.Public | BindingFlags.Instance));
        }
    }
}
