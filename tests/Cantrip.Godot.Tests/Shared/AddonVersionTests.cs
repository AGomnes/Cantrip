using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The addon ships beside the NuGet packages and names its zip after plugin.cfg's version, so
    /// the two must not drift. Directory.Build.props is the one place the version is set.
    /// </summary>
    public sealed class AddonVersionTests
    {
        [Fact]
        public void The_addon_carries_the_same_version_as_the_packages()
        {
            string root = RepositoryRoot();

            string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
            string prefix = Regex.Match(props, @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value;
            string suffix = Regex.Match(props, @"<VersionSuffix>([^<]*)</VersionSuffix>").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(prefix), "Directory.Build.props sets no VersionPrefix.");
            string packages = suffix.Length == 0 ? prefix : prefix + "-" + suffix;

            string plugin = File.ReadAllText(Path.Combine(root, "godot", "Cantrip.Demo", "addons", "cantrip", "plugin.cfg"));
            string addon = Regex.Match(plugin, "^version=\"([^\"]*)\"", RegexOptions.Multiline).Groups[1].Value;

            Assert.Equal(packages, addon);

            // Both install guides pin the package to the addon's version, because --prerelease alone
            // would fetch the newest preview whether or not it matches the addon in hand.
            string pin = "dotnet add package Cantrip.Core --version " + packages;
            foreach (string guide in new[] { Path.Combine("godot", "Cantrip.Demo", "addons", "cantrip", "README.md"), Path.Combine("docs", "godot.md") })
            {
                Assert.True(File.ReadAllText(Path.Combine(root, guide)).Contains(pin), guide + " should say: " + pin);
            }
        }

        private static string RepositoryRoot()
        {
            for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Cantrip.sln"))) return directory.FullName;
            }
            throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}
