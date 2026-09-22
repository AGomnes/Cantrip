using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Cantrip.Tests.Docs
{
    /// <summary>
    /// CONTRIBUTING.md and docs/stability.md promise that the build fails, not merely warns, when
    /// Cantrip.Core's public API changes without being written into PublicAPI.Unshipped.txt. That
    /// promise lives in one project setting, which this holds in place.
    /// </summary>
    public sealed class PublicApiRecordTests
    {
        [Fact]
        public void An_unrecorded_public_API_change_is_a_build_error()
        {
            XDocument project = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Cantrip.Core", "Cantrip.Core.csproj"));

            Assert.Contains(project.Descendants("PackageReference"), reference =>
                (string?)reference.Attribute("Include") == "Microsoft.CodeAnalysis.PublicApiAnalyzers");

            string[] errors = project.Descendants("WarningsAsErrors")
                .SelectMany(element => element.Value.Split(';'))
                .Select(code => code.Trim())
                .ToArray();

            Assert.Contains("RS0016", errors);   // a public member added without its line
            Assert.Contains("RS0017", errors);   // a recorded member removed or changed
            Assert.Contains("$(WarningsAsErrors)", errors);   // keeps the repository's own, such as nullable
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
