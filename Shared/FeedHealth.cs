using System;
using System.Collections.Generic;
using System.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// Why a published entry stopped working. The distinction matters because each
    /// case has a different remedy, and one of them is time-sensitive.
    /// </summary>
    public enum FeedPartHealth
    {
        /// <summary>Resolves to a file. Nothing to do.</summary>
        Ok,

        /// <summary>
        /// Resolves, but to a different or newer file than the spec names. Upstream
        /// renamed or republished. Re-resolve, update the spec, re-archive.
        /// Real case: "Twilight Princess Decompilation (v1.0).zip" became
        /// "Dusklight (v1.4.1).zip" with the item otherwise untouched.
        /// </summary>
        Moved,

        /// <summary>
        /// The item exists and resolves nothing. Its archives were removed while the
        /// item stayed up, or the spec was transcribed wrong.
        /// </summary>
        NoMatchingFile,

        /// <summary>
        /// Withheld by archive.org. **Nothing can be fetched any more.** If a copy is
        /// already held locally it is now the only one; if not, it is gone. This is the
        /// only state where acting late costs the file rather than costing effort.
        /// </summary>
        Dark,

        /// <summary>The identifier was never used. A bad spec, not a takedown.</summary>
        Missing,

        /// <summary>The check itself failed. Says nothing about the item.</summary>
        Unknown,
    }

    public class PartDiagnosis
    {
        public FeedPart Part { get; set; }
        public FeedPartHealth Health { get; set; }

        /// <summary>The file that actually resolved, when one did.</summary>
        public string ResolvedFile { get; set; }

        /// <summary>Candidate the spec should probably be updated to, for Moved.</summary>
        public string SuggestedFile { get; set; }

        public string Detail { get; set; }

        /// <summary>
        /// True when the upstream can still be fetched. Drives the remediation: an
        /// entry that is still fetchable should be re-archived NOW, because the
        /// alternative is discovering later that it went dark in between.
        /// </summary>
        public bool CanStillFetch =>
            Health == FeedPartHealth.Ok || Health == FeedPartHealth.Moved;

        /// <summary>
        /// Whether a copy exists locally. Only consulted when the upstream is dark,
        /// where it decides between two opposite remedies.
        /// </summary>
        public bool HasLocalCopy { get; set; }

        /// <summary>
        /// Dark upstream AND held locally. The publisher is now the only source, so
        /// the feed entry repoints at their own instance instead of being withdrawn.
        /// Measured 2026-09-25: 2 of 10 dark entries.
        /// </summary>
        public bool IsLastCopy => Health == FeedPartHealth.Dark && HasLocalCopy;

        /// <summary>
        /// Dark upstream and NOT held. The entry promises a download nobody can make.
        /// Worse than a missing entry, because a subscriber sees an installable game
        /// and gets a dead link. Measured 2026-09-25: 8 of 10.
        /// </summary>
        public bool IsOrphaned => Health == FeedPartHealth.Dark && !HasLocalCopy;

        /// <summary>
        /// Needs a decision from the publisher: repoint at their own copy, or withdraw.
        /// </summary>
        public bool NeedsFallback => Health == FeedPartHealth.Dark;
    }

    public class EntryDiagnosis
    {
        public FeedEntry Entry { get; set; }
        public List<PartDiagnosis> Parts { get; set; } = new List<PartDiagnosis>();

        public bool IsHealthy => Parts.All(p => p.Health == FeedPartHealth.Ok);

        /// <summary>
        /// Worth a notification. A Moved part still works today, so it is not urgent,
        /// but it is the window in which re-archiving is still possible.
        /// </summary>
        public bool NeedsAttention => Parts.Any(p => p.Health != FeedPartHealth.Ok);

        /// <summary>Re-archiving these is still possible and should not wait.</summary>
        public IEnumerable<PartDiagnosis> ReArchivable =>
            Parts.Where(p => p.Health == FeedPartHealth.Moved);

        /// <summary>Dark upstream but held: repoint the entry at our own instance.</summary>
        public IEnumerable<PartDiagnosis> LastCopyHeldLocally =>
            Parts.Where(p => p.IsLastCopy);

        /// <summary>
        /// Dark upstream and not held. These are dead links being advertised as
        /// installable, which is the worst state an entry can be in.
        /// </summary>
        public IEnumerable<PartDiagnosis> Orphaned =>
            Parts.Where(p => p.IsOrphaned);

        /// <summary>One line per problem, for a notification body.</summary>
        public IEnumerable<string> Summary()
        {
            foreach (var p in Parts.Where(x => x.Health != FeedPartHealth.Ok))
            {
                var suffix = string.IsNullOrEmpty(p.SuggestedFile) ? "" : $" -> '{p.SuggestedFile}'";
                yield return $"{Entry?.Name ?? Entry?.Id}: {p.Part?.Role} part is {p.Health}{suffix}. {p.Detail}".TrimEnd();
            }
        }
    }

    /// <summary>
    /// Classifies published entries against their upstreams. Takes its lookups as
    /// delegates so it can be tested without network access, and so the same logic
    /// serves both the DEV plugin's notification and a scheduled CI check.
    /// </summary>
    public class FeedHealthChecker
    {
        private readonly Func<string, ArchiveLookup> _lookup;
        private readonly Func<string, bool> _holdsCopy;

        /// <param name="holdsCopy">
        /// Whether a local copy of an identifier exists. Optional, and when omitted
        /// every dark part reports as orphaned, which is the safe default: it never
        /// claims a fallback that might not be there.
        /// </param>
        public FeedHealthChecker(Func<string, ArchiveLookup> lookup, Func<string, bool> holdsCopy = null)
        {
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _holdsCopy = holdsCopy ?? (_ => false);
        }

        public EntryDiagnosis Check(FeedEntry entry)
        {
            var result = new EntryDiagnosis { Entry = entry };

            foreach (var part in entry?.Parts ?? new List<FeedPart>())
            {
                result.Parts.Add(CheckPart(part));
            }

            return result;
        }

        private PartDiagnosis CheckPart(FeedPart part)
        {
            // Only archive.org is classified here. GitHub releases move for different
            // reasons and deserve their own checker rather than a lowest-common
            // denominator that reports both badly.
            if (part.Source != SourceKind.InternetArchive)
            {
                return new PartDiagnosis
                {
                    Part = part,
                    Health = FeedPartHealth.Unknown,
                    Detail = $"{part.Source} parts are not checked here.",
                };
            }

            ContentSpec spec;
            try
            {
                spec = ContentSpec.Parse(part.Locator);
            }
            catch (ArgumentException ex)
            {
                return new PartDiagnosis { Part = part, Health = FeedPartHealth.Missing, Detail = ex.Message };
            }

            ArchiveLookup item;
            try
            {
                item = _lookup(spec.Identifier);
            }
            catch (Exception ex)
            {
                return new PartDiagnosis { Part = part, Health = FeedPartHealth.Unknown, Detail = ex.Message };
            }

            if (item == null || !item.Exists)
            {
                return new PartDiagnosis
                {
                    Part = part,
                    Health = FeedPartHealth.Missing,
                    Detail = $"archive.org has no item '{spec.Identifier}'.",
                };
            }

            if (item.IsDark)
            {
                var held = _holdsCopy(spec.Identifier);
                return new PartDiagnosis
                {
                    Part = part,
                    Health = FeedPartHealth.Dark,
                    HasLocalCopy = held,
                    Detail = held
                        ? $"'{spec.Identifier}' is dark upstream and held locally. This instance is " +
                          "now the only source: repoint the entry at it."
                        : $"'{spec.Identifier}' is dark upstream and NOT held. The entry promises a " +
                          "download nobody can make: withdraw it or repoint it at a live item.",
                };
            }

            var hit = spec.Resolve(item.Files);
            if (hit != null)
            {
                return new PartDiagnosis { Part = part, Health = FeedPartHealth.Ok, ResolvedFile = hit };
            }

            // Item is live but the named file is gone. If an archive is still in there,
            // upstream almost certainly renamed or republished, which is recoverable
            // and worth doing while it lasts.
            var candidate = spec.IsExplicit ? ContentSpec.LargestArchive(item.Files) : null;

            if (candidate != null)
            {
                return new PartDiagnosis
                {
                    Part = part,
                    Health = FeedPartHealth.Moved,
                    SuggestedFile = candidate,
                    Detail = $"'{spec.FilePath}' is gone but '{candidate}' is present. " +
                             "Re-resolve and re-archive while the item is still up.",
                };
            }

            return new PartDiagnosis
            {
                Part = part,
                Health = FeedPartHealth.NoMatchingFile,
                Detail = $"'{spec.Identifier}' is live but holds no archive to download.",
            };
        }
    }

    /// <summary>
    /// What the checker needs from a source. Kept separate from ArchiveOrgClient's
    /// own types so this file has no dependency on Newtonsoft or on the network.
    /// </summary>
    public class ArchiveLookup
    {
        public bool Exists { get; set; }
        public bool IsDark { get; set; }
        public IReadOnlyList<FileCandidate> Files { get; set; } = new List<FileCandidate>();
    }
}
