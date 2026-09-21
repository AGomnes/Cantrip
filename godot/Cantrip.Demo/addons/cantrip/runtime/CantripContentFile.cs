#nullable enable
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// One imported <c>.cantrip</c> file: its text, and the <c>res://</c> path it was written at.
    /// </summary>
    /// <remarks>
    /// The source path is stored rather than derived because <see cref="Resource.ResourcePath"/>
    /// points at the imported copy under <c>.godot/imported</c>, and a diagnostic has to name a
    /// file the designer can open. Keeping the text here is what lets an exported game load
    /// content at all: the source file itself is not in the pack.
    /// </remarks>
    [Tool]
    [GlobalClass]
    public partial class CantripContentFile : Resource
    {
        /// <summary>Where the text came from, as <c>res://content/cards.cantrip</c>.</summary>
        [Export]
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>The file's contents, verbatim. The DSL is whitespace-sensitive, so nothing is trimmed.</summary>
        [Export(PropertyHint.MultilineText)]
        public string Text { get; set; } = string.Empty;
    }
}
