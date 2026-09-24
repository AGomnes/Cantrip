#nullable enable
#if TOOLS
using Godot;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The dock's text editor: a <see cref="CodeEdit"/> that will not put a tab into a
    /// <c>.cantrip</c> file, however the text arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Godot's own settings cover typing. <c>IndentUseSpaces</c> with <c>IndentSize</c> set from the
    /// buffer makes the Tab key insert the step the file already uses, and automatic indenting
    /// carries it on to the next line. Neither touches a paste, which is where a tab from another
    /// editor gets in; the language counts a tab as four columns, so one pasted into a file written
    /// with two spaces silently moves where a block ends.
    /// </para>
    /// <para>
    /// So pasting is what is overridden here, and it does exactly what Godot's does with one step in
    /// front: the indentation of the pasted text is widened into the spaces it already stood for. A
    /// tab inside a string is left alone, because there it is part of the text. Both clipboards are
    /// covered: the ordinary one, and the primary selection that a middle click pastes on Linux.
    /// </para>
    /// <para>
    /// These are the only two routes a script can stand in front of. Text dropped on the editor from
    /// another window goes through <c>TextEdit</c>'s own drop handling, which a script cannot
    /// replace, so <see cref="CantripSourceView"/> catches that one after the fact instead.
    /// </para>
    /// </remarks>
    [Tool]
    public partial class CantripCodeEdit : CodeEdit
    {
        public override void _Paste(int caretIndex) => Insert(DisplayServer.ClipboardGet(), caretIndex);

        public override void _PastePrimaryClipboard(int caretIndex) => Insert(DisplayServer.ClipboardGetPrimary(), caretIndex);

        private void Insert(string text, int caretIndex)
        {
            if (text.Length == 0) return;

            InsertTextAtCaret(CantripIndent.WithoutTabs(text), caretIndex);
        }
    }
}
#endif
