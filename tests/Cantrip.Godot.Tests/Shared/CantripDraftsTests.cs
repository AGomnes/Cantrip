using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// The book the dock's editor keeps of what is open and what has been typed into it. Everything
    /// the dock promises about not losing work is decided here: what a check is run against, what
    /// survives switching files, and when the unsaved mark goes away.
    /// </summary>
    public sealed class CantripDraftsTests
    {
        private const string Cards = "res://content/cards.cantrip";
        private const string Statuses = "res://content/statuses.cantrip";

        [Fact]
        public void A_file_just_opened_is_not_a_draft()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\n");

            Assert.True(drafts.IsOpen(Cards));
            Assert.False(drafts.IsDirty(Cards));
            Assert.Equal(0, drafts.UnsavedCount);

            // Nothing to put in place of the file, so a check reads the file.
            Assert.Null(drafts.TextFor(Cards));
        }

        [Fact]
        public void Typing_makes_a_draft_and_a_check_reads_it_instead_of_the_file()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"A\"\n  cost 1\n");

            Assert.True(drafts.IsDirty(Cards));
            Assert.Equal(1, drafts.UnsavedCount);
            Assert.Equal("card \"A\"\n  cost 1\n", drafts.TextFor(Cards));
            Assert.Equal("card \"A\"\n", drafts.Disk(Cards));
        }

        [Fact]
        public void Typing_a_character_and_taking_it_out_again_leaves_nothing_behind()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"AB\"\n");
            drafts.Edited(Cards, "card \"A\"\n");

            Assert.False(drafts.IsDirty(Cards));
            Assert.Null(drafts.TextFor(Cards));
        }

        [Fact]
        public void Saving_clears_the_draft_without_closing_the_file()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"B\"\n");
            drafts.Saved(Cards, "card \"B\"\n");

            Assert.True(drafts.IsOpen(Cards));
            Assert.False(drafts.IsDirty(Cards));
            Assert.Equal("card \"B\"\n", drafts.Disk(Cards));
            Assert.Equal("card \"B\"\n", drafts.Buffer(Cards));
        }

        [Fact]
        public void A_draft_survives_another_file_being_opened()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"A\"\n  cost 1\n");
            drafts.Opened(Statuses, "status \"S\"\n");

            Assert.Equal(1, drafts.UnsavedCount);
            Assert.Equal(new[] { Cards }, drafts.Unsaved);
            Assert.Equal("card \"A\"\n  cost 1\n", drafts.Buffer(Cards));
        }

        [Fact]
        public void Every_unsaved_buffer_is_offered_in_place_of_its_own_file()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Statuses, "status \"S\"\n");
            drafts.Edited(Statuses, "status \"S\"\n  stacks\n");
            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"A\"\n  cost 1\n");

            // Ordinally sorted, which is the order content loads in, so a message reads the same twice.
            Assert.Equal(new[] { Cards, Statuses }, drafts.Unsaved);
            Assert.Equal(2, drafts.UnsavedCount);
        }

        [Fact]
        public void A_file_changed_on_disk_leaves_the_buffer_alone()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"MINE\"\n");
            drafts.DiskChanged(Cards, "card \"THEIRS\"\n");

            Assert.Equal("card \"MINE\"\n", drafts.Buffer(Cards));
            Assert.Equal("card \"THEIRS\"\n", drafts.Disk(Cards));
            Assert.True(drafts.IsDirty(Cards));

            // Recording their text twice does not make a second conflict out of the same change.
            drafts.DiskChanged(Cards, "card \"THEIRS\"\n");
            Assert.Equal("card \"MINE\"\n", drafts.TextFor(Cards));
        }

        [Fact]
        public void Reverting_and_closing_both_end_the_draft()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"B\"\n");

            drafts.Closed(Cards);
            Assert.False(drafts.IsOpen(Cards));
            Assert.False(drafts.IsDirty(Cards));
            Assert.Null(drafts.Buffer(Cards));

            drafts.Opened(Cards, "card \"A\"\n");
            drafts.Edited(Cards, "card \"B\"\n");
            drafts.Clear();
            Assert.Equal(0, drafts.UnsavedCount);
        }

        [Fact]
        public void The_unsaved_mark_is_raised_once_each_way()
        {
            var drafts = new CantripDrafts();
            int changes = 0;
            drafts.Changed += () => changes++;

            drafts.Opened(Cards, "card \"A\"\n");
            Assert.Equal(0, changes);           // opening a clean file marks nothing

            drafts.Edited(Cards, "card \"B\"\n");
            Assert.Equal(1, changes);

            drafts.Edited(Cards, "card \"C\"\n");
            Assert.Equal(1, changes);           // still unsaved, so still the same mark

            drafts.Saved(Cards, "card \"C\"\n");
            Assert.Equal(2, changes);
        }

        /// <summary>
        /// Every <c>.cantrip</c> file in a Windows checkout of this repository ends its lines with
        /// <c>\r\n</c>, and Godot's editing buffer holds them without the <c>\r</c>. Unless both
        /// sides are compared in the same form, such a file is a draft from the moment it is opened
        /// and can never be put back: the Save button stays lit, the tab keeps saying a file is
        /// waiting, and the next save rewrites a file nobody changed.
        /// </summary>
        [Fact]
        public void A_file_written_on_windows_is_not_a_draft_just_for_being_read()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\r\n  cost 1\r\n");

            // What the editor's buffer holds, which is the same file without its carriage returns.
            drafts.Edited(Cards, "card \"A\"\n  cost 1\n");

            Assert.False(drafts.IsDirty(Cards));
            Assert.Equal(0, drafts.UnsavedCount);
            Assert.Null(drafts.TextFor(Cards));
        }

        [Fact]
        public void Typing_into_a_file_written_on_windows_and_undoing_it_leaves_nothing_behind()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\r\n");

            drafts.Edited(Cards, "card \"AB\"\n");
            Assert.True(drafts.IsDirty(Cards));

            drafts.Edited(Cards, "card \"A\"\n");
            Assert.False(drafts.IsDirty(Cards));
        }

        /// <summary>
        /// The question the editor asks when the project's files change. A file saved from the dock
        /// as <c>\n</c> and read back as <c>\r\n</c>, or the other way about, has not changed
        /// underneath anybody, and raising a conflict over it would be a warning about nothing.
        /// </summary>
        [Fact]
        public void The_same_file_in_other_line_endings_has_not_changed_underneath_anybody()
        {
            var drafts = new CantripDrafts();
            drafts.Opened(Cards, "card \"A\"\r\n  cost 1\r\n");

            Assert.True(drafts.SameAsDisk(Cards, "card \"A\"\r\n  cost 1\r\n"));
            Assert.True(drafts.SameAsDisk(Cards, "card \"A\"\n  cost 1\n"));
            Assert.False(drafts.SameAsDisk(Cards, "card \"A\"\n  cost 2\n"));

            // A file nobody has open is not the same as anything.
            Assert.False(drafts.SameAsDisk(Statuses, "card \"A\"\n"));
            Assert.False(drafts.SameAsDisk(null, string.Empty));
        }

        [Fact]
        public void A_file_nobody_opened_is_nobody_s_business()
        {
            var drafts = new CantripDrafts();

            drafts.Edited(Cards, "card \"B\"\n");
            drafts.DiskChanged(Cards, "card \"C\"\n");
            drafts.Closed(Cards);

            Assert.False(drafts.IsOpen(Cards));
            Assert.Null(drafts.TextFor(Cards));
            Assert.Null(drafts.Buffer(null));
            Assert.False(drafts.IsDirty(null));
            Assert.Equal(0, drafts.UnsavedCount);
        }
    }
}
