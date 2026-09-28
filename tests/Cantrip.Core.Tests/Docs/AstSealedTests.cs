#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Cantrip.Syntax;
using Xunit;

namespace Cantrip.Tests.Docs
{
    /// <summary>
    /// The syntax tree is the interpreter's own vocabulary, not an extension point. A node type the
    /// library has never heard of has no behaviour: executing one throws "Cannot execute ...". So
    /// the abstract bases cannot be derived from outside the library — their constructors are
    /// <c>private protected</c> — and every node that does exist is sealed.
    /// </summary>
    public sealed class AstSealedTests
    {
        [Theory]
        [InlineData(typeof(Node))]
        [InlineData(typeof(ExprNode))]
        [InlineData(typeof(StatementNode))]
        [InlineData(typeof(DeclarationNode))]
        [InlineData(typeof(MemberNode))]
        public void An_abstract_node_cannot_be_derived_from_outside_the_library(Type node)
        {
            Assert.True(node.IsAbstract, node.Name + " is not abstract.");

            ConstructorInfo[] constructors = node.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.NotEmpty(constructors);
            foreach (ConstructorInfo constructor in constructors)
            {
                // private protected: derivable only by a type in this assembly.
                Assert.True(constructor.IsFamilyAndAssembly,
                    node.Name + " has a constructor another assembly can call: " + constructor.Attributes);
            }
        }

        [Fact]
        public void Every_concrete_node_is_sealed()
        {
            Type[] loose = typeof(Node).Assembly.GetExportedTypes()
                .Where(type => typeof(Node).IsAssignableFrom(type) && !type.IsAbstract && !type.IsSealed)
                .ToArray();

            Assert.Empty(loose);
        }
    }
}
