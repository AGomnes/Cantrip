#nullable enable
using System;

namespace Cantrip.GodotAdapter
{
    /// <summary>Why a save file was not restored.</summary>
    public enum SaveRejection
    {
        None,

        /// <summary>Written by a newer or older version of the addon.</summary>
        WrongFormat,

        /// <summary>The envelope carries no snapshot.</summary>
        NoPayload,

        /// <summary>
        /// The content loaded now is not the content the save was taken against. From
        /// <c>CantripRuntime.LoadSave</c>, it means the rules refused the save: something it needs
        /// has gone.
        /// </summary>
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
        public string ReasonName => NameOf(Reason);

        /// <summary>
        /// The word script is given for each rejection, written out for the same reason as
        /// <see cref="ChoiceAnswer.NameOf"/>: renaming a member must not quietly change one.
        /// </summary>
        public static string NameOf(SaveRejection reason) => reason switch
        {
            SaveRejection.None => "none",
            SaveRejection.WrongFormat => "wrong_format",
            SaveRejection.NoPayload => "no_payload",
            SaveRejection.ContentChanged => "content_changed",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "This rejection has no word for script yet."),
        };

        public override string ToString() => Accepted ? "accepted" : ReasonName + ": " + Message;
    }

    /// <summary>
    /// What a save file carries besides the snapshot itself: which addon wrote it, and which
    /// content it was taken against.
    /// </summary>
    /// <remarks>
    /// The fingerprint covers the kinds and names of every definition, content verb and resource,
    /// and nothing else, so rebalancing a card leaves it as it was, while adding, renaming or
    /// deleting a definition changes it. Comparing it with <c>ContentLibrary.Fingerprint</c> says
    /// "this save is from before a patch" before the snapshot is even read.
    /// <para>
    /// A mismatch is not a verdict on the save. A patch that only adds a card changes the
    /// fingerprint, and the save still has everything it needs. <c>CardRuntime.Restore</c> looks
    /// up what the snapshot needs, the definitions it names and any <c>next turn:</c> or
    /// <c>in N turns:</c> block waiting in it, and refuses by throwing, before it changes anything,
    /// when one has gone. So the node lets a mismatched save through to the restore, and reports
    /// the restore's refusal as <see cref="SaveRejection.ContentChanged"/>; only a wrong format
    /// and a missing payload are refused here.
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
        /// Whether this save was taken against content whose fingerprint is
        /// <paramref name="libraryFingerprint"/>, in a format this addon reads and with a game in
        /// it. It decides nothing by itself: a mismatched fingerprint is a reason to let the
        /// restore check the save, which the node does, and the caller gets the core's own error if
        /// a definition the snapshot needs has really gone.
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
