#nullable enable
using System;
using System.Collections.Generic;

namespace Cantrip.GodotAdapter
{
    /// <summary>
    /// The files the editor dock has open, each as two texts: what is on disk, and what the person
    /// has typed since. A draft is the difference between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Content is loaded a folder at a time, so a buffer cannot be checked on its own: a card that
    /// names a status defined next door is correct. What is checked is therefore the folder as
    /// saved, with every unsaved buffer put in place of its own file, and this is the book of those
    /// buffers. Keeping them here rather than in the view is what lets a person leave a file with
    /// changes pending, work on another, and come back to find their work where they left it.
    /// </para>
    /// <para>
    /// Nothing here writes anything. Saving belongs to the view, which calls <see cref="Saved"/>
    /// afterwards; a draft that is byte-identical to the file is not a draft at all, so typing a
    /// character and taking it out again leaves nothing behind.
    /// </para>
    /// <para>
    /// Every text that arrives is put in the line endings the editor's buffer uses, because Godot's
    /// <c>CodeEdit</c> holds a line without its carriage return. Without that, a file written on
    /// Windows differs from its own buffer the moment it is read, and typing a character and taking
    /// it out again would leave it marked unsaved for ever. Nothing downstream cares: the lexer
    /// reads either.
    /// </para>
    /// </remarks>
    public sealed class CantripDrafts
    {
        private readonly Dictionary<string, Entry> _open = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised whenever the set of unsaved files changes, so a dock can mark itself.</summary>
        public event Action? Changed;

        /// <summary>How many open files have unsaved changes.</summary>
        public int UnsavedCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<string, Entry> entry in _open)
                {
                    if (entry.Value.IsDirty) count++;
                }
                return count;
            }
        }

        /// <summary>Every path with unsaved changes, ordinally sorted so a message reads the same twice.</summary>
        public IReadOnlyList<string> Unsaved
        {
            get
            {
                var paths = new List<string>();
                foreach (KeyValuePair<string, Entry> entry in _open)
                {
                    if (entry.Value.IsDirty) paths.Add(entry.Key);
                }
                paths.Sort(ContentPaths.Compare);
                return paths;
            }
        }

        /// <summary>Records what a file holds on disk, on opening it, saving it or rereading it.</summary>
        public void Opened(string? path, string? diskText)
        {
            if (string.IsNullOrEmpty(path)) return;

            string text = AsBuffer(diskText);
            bool was = IsDirty(path);
            _open[path!] = new Entry(text, text);
            if (was) Changed?.Invoke();
        }

        /// <summary>Records what the buffer holds now. Text equal to the file's clears the draft.</summary>
        public void Edited(string? path, string? bufferText)
        {
            if (string.IsNullOrEmpty(path) || !_open.TryGetValue(path!, out Entry entry)) return;

            bool was = entry.IsDirty;
            _open[path!] = new Entry(entry.Disk, AsBuffer(bufferText));
            if (was != IsDirty(path)) Changed?.Invoke();
        }

        /// <summary>Records that the buffer was written to disk, so it is no longer a draft.</summary>
        public void Saved(string? path, string? savedText) => Opened(path, savedText);

        /// <summary>Records what the file holds now, leaving the buffer as it is: the file changed underneath.</summary>
        public void DiskChanged(string? path, string? diskText)
        {
            if (string.IsNullOrEmpty(path) || !_open.TryGetValue(path!, out Entry entry)) return;

            bool was = entry.IsDirty;
            _open[path!] = new Entry(AsBuffer(diskText), entry.Buffer);
            if (was != IsDirty(path)) Changed?.Invoke();
        }

        /// <summary>
        /// True when a file's text is what this book last recorded for it, whatever line endings it
        /// arrived in. This is the question the editor asks when the project's files change: a file
        /// saved from here as `\n` and read back as `\r\n` has not changed underneath anybody.
        /// </summary>
        public bool SameAsDisk(string? path, string? diskText) =>
            !string.IsNullOrEmpty(path)
            && _open.TryGetValue(path!, out Entry entry)
            && string.Equals(entry.Disk, AsBuffer(diskText), StringComparison.Ordinal);

        /// <summary>
        /// Text with the line endings the editor's buffer holds. Godot's <c>CodeEdit</c> drops a
        /// carriage return on the way in and does not put it back, so every text is compared in the
        /// one form both sides can be in.
        /// </summary>
        public static string AsBuffer(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text!.IndexOf('\r') < 0) return text;
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>Stops tracking a file, draft and all.</summary>
        public void Closed(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;

            bool was = IsDirty(path);
            if (_open.Remove(path!) && was) Changed?.Invoke();
        }

        /// <summary>Forgets every draft, for a reload that starts from what is on disk.</summary>
        public void Clear()
        {
            bool any = UnsavedCount > 0;
            _open.Clear();
            if (any) Changed?.Invoke();
        }

        public bool IsOpen(string? path) => !string.IsNullOrEmpty(path) && _open.ContainsKey(path!);

        public bool IsDirty(string? path) =>
            !string.IsNullOrEmpty(path) && _open.TryGetValue(path!, out Entry entry) && entry.IsDirty;

        /// <summary>What the buffer holds, whether or not it differs from the file. Null when not open.</summary>
        public string? Buffer(string? path) =>
            !string.IsNullOrEmpty(path) && _open.TryGetValue(path!, out Entry entry) ? entry.Buffer : null;

        /// <summary>What the file held when it was last read or written. Null when not open.</summary>
        public string? Disk(string? path) =>
            !string.IsNullOrEmpty(path) && _open.TryGetValue(path!, out Entry entry) ? entry.Disk : null;

        /// <summary>
        /// The text to load for a file: the unsaved buffer when there is one, otherwise null, which
        /// means "read the file". This is the whole of what a check sees differently from disk.
        /// </summary>
        public string? TextFor(string? path) =>
            !string.IsNullOrEmpty(path) && _open.TryGetValue(path!, out Entry entry) && entry.IsDirty ? entry.Buffer : null;

        private readonly struct Entry
        {
            public Entry(string disk, string buffer)
            {
                Disk = disk;
                Buffer = buffer;
            }

            public string Disk { get; }

            public string Buffer { get; }

            public bool IsDirty => !string.Equals(Disk, Buffer, StringComparison.Ordinal);
        }
    }
}
