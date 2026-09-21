using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The addon's shared layer is compiled into this project without the engine, which is the only
    /// reason its rules can be unit tested at all. This test is what keeps that true.
    /// </summary>
    public sealed class SharedLayerTests
    {
        [Fact]
        public void No_shared_source_reaches_for_the_engine()
        {
            string[] files = Directory.GetFiles(SharedDirectory(), "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(files);

            var offenders = new List<string>();
            foreach (string file in files)
            {
                // Prose may name the engine; code may not. The namespace Cantrip.GodotAdapter
                // is not a match, because the word boundary falls inside GodotAdapter.
                string code = WithoutComments(File.ReadAllText(file));
                if (Regex.IsMatch(code, @"\bGodot\b")) offenders.Add(Path.GetFileName(file));
            }

            Assert.True(
                offenders.Count == 0,
                "shared/ is compiled into an engine-free project, but these use Godot: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_whole_shared_layer_is_compiled_here()
        {
            // A glob that silently matched nothing would make the test above pass for the wrong reason.
            string[] files = Directory.GetFiles(SharedDirectory(), "*.cs", SearchOption.AllDirectories);
            var missing = new List<string>();

            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (Type.GetType("Cantrip.GodotAdapter." + name + ", Cantrip.Godot.Tests") == null)
                    missing.Add(name);
            }

            Assert.True(missing.Count == 0, "Not compiled into the test assembly: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The addon is C# source compiled inside the user's own project, which does not enable
        /// nullable annotations. This repository enables them project-wide, which hides the problem:
        /// without the directive in each file, a user's first build shows a warning for every <c>?</c>.
        /// </summary>
        [Fact]
        public void Every_addon_source_file_enables_nullable_itself()
        {
            string addon = Directory.GetParent(SharedDirectory())!.FullName;
            var missing = new List<string>();
            foreach (string file in Directory.GetFiles(addon, "*.cs", SearchOption.AllDirectories))
            {
                using var reader = new StreamReader(file);
                if (reader.ReadLine()?.Trim() != "#nullable enable") missing.Add(Path.GetRelativePath(addon, file));
            }

            Assert.True(missing.Count == 0, "First line should be `#nullable enable`: " + string.Join(", ", missing));
        }

        private static string SharedDirectory()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Cantrip.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory != null, "No repository root above " + AppContext.BaseDirectory);

            string shared = Path.Combine(
                directory!.FullName, "godot", "Cantrip.Demo", "addons", "cantrip", "shared");
            Assert.True(Directory.Exists(shared), "No shared layer at " + shared);
            return shared;
        }

        private static string WithoutComments(string code)
        {
            var text = new StringBuilder(code.Length);

            for (int i = 0; i < code.Length; i++)
            {
                if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    while (i < code.Length && code[i] != '\n') i++;
                    text.Append('\n');
                }
                else if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++;
                    i++;
                }
                else
                {
                    text.Append(code[i]);
                }
            }

            return text.ToString();
        }
    }
}
