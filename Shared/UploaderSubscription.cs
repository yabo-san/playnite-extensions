using System;
using System.Collections.Generic;
using System.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// A watched Internet Archive account. Subscribing to a whole uploader rather than
    /// to individual items is the point: their catalogue appears in the library, and
    /// new uploads land on the next refresh without anyone curating them by hand.
    ///
    /// The list is configured by the user, never shipped. That keeps other people's
    /// email addresses out of a public repo, and it means the plugin is useful to
    /// anyone with their own set of accounts to follow.
    /// </summary>
    public class UploaderSubscription
    {
        /// <summary>
        /// An account handle, or an email. archive.org indexes both in `uploader`.
        ///
        /// Prefer the handle. An email is someone's personal data, and querying the
        /// handle returns the same items as querying the address does.
        /// </summary>
        public string Uploader { get; set; }

        /// <summary>
        /// Query `creator:` instead of `uploader:`.
        ///
        /// Needed for accounts tracked by credit rather than by login, but it is a
        /// free-text field and matches anything sharing the name. "Infidelity" returns
        /// SNES romhacks alongside a drum-and-bass podcast and an 1757 devotional text,
        /// so a creator subscription should default to filtering on mediatype.
        /// </summary>
        public bool ByCreator { get; set; }

        /// <summary>
        /// Drop anything that is not `software`. Off by default for uploader
        /// subscriptions, which are already scoped to an account, and worth turning on
        /// for creator subscriptions, which are not.
        /// </summary>
        public bool SoftwareOnly { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// What this account publishes, which decides where a fetched item lands.
        ///
        /// Different uploaders produce different KINDS of artifact, and each kind has
        /// a different consumer already watching a directory for it. A PC repack and
        /// a SNES romhack are both "a file from archive.org" and belong nowhere near
        /// each other on disk.
        ///
        /// This is the field the old shell script did not have. It carried a single
        /// `DEST`, so routing was expressed by keeping four copies of the script with
        /// four different destinations, which then drifted apart. One field here
        /// replaces all four.
        /// </summary>
        public ArtifactKind Produces { get; set; } = ArtifactKind.PcRepack;

        /// <summary>
        /// Where fetched items are written. Both RomM and Drop index a directory
        /// rather than exposing an ingest API, so "routing" is only ever a path
        /// choice and needs no integration with either.
        ///
        /// Null means the caller decides from <see cref="Produces"/>.
        /// </summary>
        public string Destination { get; set; }

        /// <summary>
        /// Identifiers already surfaced to the user. Persisted, so "new since last
        /// time" survives a restart.
        ///
        /// Identifiers, not a timestamp: archive.org's search has no reliable
        /// monotonic cursor, items get re-indexed, and a clock comparison would either
        /// miss uploads or re-announce old ones. A set is larger but it cannot be wrong.
        /// </summary>
        public HashSet<string> Seen { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DateTime? LastChecked { get; set; }

        public string Describe()
        {
            return (ByCreator ? "creator:" : "uploader:") + Uploader;
        }
    }

    /// <summary>
    /// What a source produces. Each kind already has a consumer watching a folder,
    /// so this is a routing decision, not a new pipeline.
    /// </summary>
    public enum ArtifactKind
    {
        /// <summary>A self-contained PC game folder. Lands where Drop and the folder provider look.</summary>
        PcRepack,

        /// <summary>
        /// A console ROM or romhack. Lands in a RomM platform directory, which RomM
        /// scans on its own. Romhack uploaders are a distinct population from repack
        /// uploaders and must not share a destination.
        /// </summary>
        Rom,

        /// <summary>
        /// Game data for a source port: a ROM, IWAD, PAK or MPQ with no executable of
        /// its own. Staged into a port install rather than filed as a game, because on
        /// its own it does not run.
        /// </summary>
        GameData,
    }

    public class SubscriptionRefresh
    {
        public UploaderSubscription Subscription { get; set; }

        /// <summary>Everything the account currently holds.</summary>
        public IReadOnlyList<ArchiveItem> All { get; set; } = new List<ArchiveItem>();

        /// <summary>Items not previously surfaced. The whole reason to subscribe.</summary>
        public IReadOnlyList<ArchiveItem> New { get; set; } = new List<ArchiveItem>();

        /// <summary>
        /// Previously seen identifiers the account no longer returns.
        ///
        /// Usually a takedown, so this is an early warning rather than housekeeping:
        /// an item that disappears here is one whose copy on disk may now be the only
        /// one. Confirm with a metadata probe before assuming, because a search index
        /// can also just be lagging.
        /// </summary>
        public IReadOnlyList<string> Vanished { get; set; } = new List<string>();

        public string Error { get; set; }
        public bool Failed => Error != null;
    }

    /// <summary>
    /// Refreshes subscriptions. Takes its search as a delegate so it is testable
    /// without network access and so the same logic serves the plugin and any
    /// scheduled check.
    /// </summary>
    public class SubscriptionRefresher
    {
        private readonly Func<UploaderSubscription, IReadOnlyList<ArchiveItem>> _search;

        public SubscriptionRefresher(Func<UploaderSubscription, IReadOnlyList<ArchiveItem>> search)
        {
            _search = search ?? throw new ArgumentNullException(nameof(search));
        }

        /// <summary>
        /// Refreshes one subscription. Mutates <see cref="UploaderSubscription.Seen"/>
        /// ONLY on success, so a failed refresh cannot cause the next one to swallow
        /// genuinely new items.
        /// </summary>
        public SubscriptionRefresh Refresh(UploaderSubscription subscription)
        {
            var result = new SubscriptionRefresh { Subscription = subscription };

            if (subscription == null || string.IsNullOrWhiteSpace(subscription.Uploader))
            {
                result.Error = "subscription has no uploader";
                return result;
            }

            IReadOnlyList<ArchiveItem> found;
            try
            {
                found = _search(subscription) ?? new List<ArchiveItem>();
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            if (subscription.SoftwareOnly)
            {
                found = found
                    .Where(i => string.Equals(i.MediaType, "software", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var seen = subscription.Seen ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = new HashSet<string>(found.Select(i => i.Identifier), StringComparer.OrdinalIgnoreCase);

            result.All = found;
            result.New = found.Where(i => !seen.Contains(i.Identifier)).ToList();

            // Only meaningful once something has been seen. On a first refresh every
            // item is new and nothing can have vanished.
            result.Vanished = seen.Count == 0
                ? new List<string>()
                : seen.Where(id => !current.Contains(id)).ToList();

            foreach (var item in found)
            {
                seen.Add(item.Identifier);
            }

            subscription.Seen = seen;
            subscription.LastChecked = DateTime.UtcNow;

            return result;
        }

        public IReadOnlyList<SubscriptionRefresh> RefreshAll(IEnumerable<UploaderSubscription> subscriptions)
        {
            return (subscriptions ?? Enumerable.Empty<UploaderSubscription>())
                .Where(s => s != null && s.Enabled)
                .Select(Refresh)
                .ToList();
        }
    }
}
