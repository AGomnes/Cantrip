using System;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>Why a save file was not restored.</summary>
    public enum SaveRejection
    {
        None,

        /// <summary>Written by a newer or older version of the addon.</summary>
        WrongFormat,

        /// <summary>The envelope carries no snapshot.</summary>
        NoPayload,

        /// <summary>The content loaded now is not the content the save was taken against.</summary>
        ContentChanged,
    }

    /// <summary>The verdict on one save file.</summary>
    public sealed class SaveCheck
    {
        internal SaveCheck(SaveRejection reason, string message)
        {
            Reason = reason;
            Message = message;
        }

        public bool Accepted => Reason == SaveRejection.None;

        public SaveRejection Reason { get; }

        /// <summary>Why it was refused, ready to show. Empty when accepted.</summary>
        public string Message { get; }

        /// <summary>The rejection as a snake_case word, for the dictionary that crosses into script.</summary>
        public string ReasonName => Reason == SaveRejection.None ? "none" : Snake(Reason.ToString());

        private static string Snake(string name)
        {
            var text = new System.Text.StringBuilder(name.Length + 2);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) text.Append('_');
                text.Append(char.ToLowerInvariant(name[i]));
            }
            return text.ToString();
        }

        public override string ToString() => Accepted ? "accepted" : ReasonName + ": " + Message;
    }

    /// <summary>
    /// What a save file carries besides the snapshot itself: which addon wrote it, and which
    /// content it was taken against.
    /// </summary>
    /// <remarks>
    /// The fingerprint is the point of this type. <c>GameState.Restore</c> refuses a snapshot
    /// naming a definition that is no longer loaded, and it refuses it by throwing, halfway
    /// through tearing the old game down. Checking <c>ContentLibrary.Fingerprint</c> first turns
    /// "the player loaded a save from before the patch" into a message the game can show while its
    /// current game is still intact.
    /// <para>
    /// The fingerprint covers the kinds and names of every definition, and nothing else, so
    /// rebalancing a card does not invalidate anyone's save; deleting or renaming one does.
    /// </para>
    /// </remarks>
    public sealed class SaveEnvelope
    {
        /// <summary>The envelope's own version, separate from the core's snapshot format.</summary>
        public const int CurrentFormat = 1;

        public SaveEnvelope(int format, string? fingerprint, string? payload)
        {
            Format = format;
            Fingerprint = fingerprint ?? string.Empty;
            Payload = payload ?? string.Empty;
        }

        public int Format { get; }

        /// <summary>The <c>ContentLibrary.Fingerprint</c> the snapshot was taken against.</summary>
        public string Fingerprint { get; }

        /// <summary>The serialized <c>GameSnapshot</c>. Opaque here: this layer never parses it.</summary>
        public string Payload { get; }

        /// <summary>Seals a snapshot for saving.</summary>
        public static SaveEnvelope Wrap(string fingerprint, string payload) =>
            new SaveEnvelope(CurrentFormat, fingerprint, payload);

        /// <summary>
        /// Whether this save can be restored into content whose fingerprint is
        /// <paramref name="libraryFingerprint"/>. It decides nothing by itself: the caller may
        /// still load a mismatched save deliberately, and gets the core's own error if a definition
        /// the snapshot needs has really gone.
        /// </summary>
        public SaveCheck Check(string? libraryFingerprint)
        {
            if (Format != CurrentFormat)
            {
                return new SaveCheck(
                    SaveRejection.WrongFormat,
                    "This save is in format " + Format + "; this version of the addon writes format " + CurrentFormat + ".");
            }

            if (Payload.Length == 0)
            {
                return new SaveCheck(SaveRejection.NoPayload, "This save carries no game to restore.");
            }

            string current = libraryFingerprint ?? string.Empty;
            if (!string.Equals(Fingerprint, current, StringComparison.Ordinal))
            {
                return new SaveCheck(
                    SaveRejection.ContentChanged,
                    "This save was made against content fingerprinted " + Describe(Fingerprint)
                    + ", and the content loaded now is " + Describe(current)
                    + ". A definition the save needs may no longer exist.");
            }

            return new SaveCheck(SaveRejection.None, string.Empty);
        }

        private static string Describe(string fingerprint) =>
            fingerprint.Length == 0 ? "nothing" : "`" + fingerprint + "`";

        public override string ToString() => "save " + Format + " " + Describe(Fingerprint);
    }
}
