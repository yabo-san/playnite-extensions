using System;
using System.Collections.Generic;
using System.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// What DEV publishes and PROD consumes: a recipe, not a file.
    ///
    /// Owner's description: "heres where u get the files and heres how u install it,
    /// thats it, so now that person can easily download it without access to MY drop
    /// instance necessarily."
    ///
    /// So an entry carries POINTERS to public upstreams plus assembly instructions.
    /// The publisher never serves bytes. Their Drop copy is private insurance for the
    /// case where an upstream goes dark, not a distribution channel.
    ///
    /// The reason this is a list of parts and not one URL is the whole "collisions"
    /// problem: a recompilation is a port binary from GitHub PLUS game data from
    /// somewhere else, assembled. One source is the easy case, not the general one.
    /// </summary>
    public class FeedEntry
    {
        /// <summary>Stable across renames. Never the display name.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Whatever the publisher last announced. Drives "update available".</summary>
        public string Version { get; set; }

        public List<FeedPart> Parts { get; set; } = new List<FeedPart>();

        public InstallKind Install { get; set; } = InstallKind.Unknown;

        /// <summary>
        /// Preferred executable once assembled. A hint, not a guarantee: ExePicker
        /// still scores candidates, because a repack's exe can be renamed between
        /// versions and a stale hint should degrade to a guess, not to a dead button.
        /// </summary>
        public string ExeHint { get; set; }

        public string Notes { get; set; }

        /// <summary>
        /// The part that carries the game itself. Everything else stages into it.
        /// </summary>
        public FeedPart Primary =>
            Parts?.FirstOrDefault(p => p.Role == PartRole.Game)
            // A launcher outranks the engines it manages: in a Doom bundle the user
            // opens DoomLauncher, not doomretro.exe.
            ?? Parts?.FirstOrDefault(p => p.Role == PartRole.Launcher)
            ?? Parts?.FirstOrDefault(p => p.Role == PartRole.Port)
            ?? Parts?.FirstOrDefault();

        /// <summary>
        /// True when assembly needs more than one download. These are the entries
        /// that made the original collision code expensive, and the ones most likely
        /// to break when any single upstream moves.
        /// </summary>
        public bool IsComposite => Parts != null && Parts.Count > 1;

        /// <summary>
        /// Problems that make an entry unusable, described rather than thrown. A feed
        /// is edited by hand, so PROD has to survive a malformed entry by skipping it
        /// and saying why, not by failing the whole library refresh.
        /// </summary>
        public IEnumerable<string> Validate()
        {
            if (string.IsNullOrWhiteSpace(Id))
            {
                yield return "entry has no id";
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                yield return $"'{Id}': no name";
            }

            if (Parts == null || Parts.Count == 0)
            {
                yield return $"'{Id}': no parts, so there is nothing to download";
                yield break;
            }

            foreach (var problem in Parts.SelectMany(p => p.Validate(Id)))
            {
                yield return problem;
            }

            // A port with no data is the classic half-published entry: it installs,
            // it launches, and it fails at the title screen looking for an asset the
            // recipe never mentioned.
            if (Parts.Any(p => p.Role == PartRole.Port || p.Role == PartRole.Launcher)
                && !Parts.Any(p => p.Role == PartRole.Data))
            {
                yield return $"'{Id}': has a port but no data part. " +
                             "If the user supplies their own ROM or IWAD, say so in notes.";
            }

            // A launcher with nothing to launch is the one combination that is always
            // a mistake: the front end opens and lists an empty library.
            if (Parts.Any(p => p.Role == PartRole.Launcher) && !Parts.Any(p => p.Role == PartRole.Port))
            {
                yield return $"'{Id}': ships a launcher but no engine for it to run.";
            }

            // Not fatal, but it is the difference between "here are three downloads"
            // and "we configured it for you", which is the whole value of the entry.
            if (Parts.Any(p => p.Role == PartRole.Launcher) && !Parts.Any(p => p.Role == PartRole.Config))
            {
                yield return $"'{Id}': ships a launcher with no config, so it opens unconfigured.";
            }
        }
    }

    /// <summary>One download, and what it is for.</summary>
    public class FeedPart
    {
        public PartRole Role { get; set; } = PartRole.Game;

        public SourceKind Source { get; set; } = SourceKind.Unknown;

        /// <summary>
        /// Meaning depends on Source. For InternetArchive it is a <see cref="ContentSpec"/>
        /// string, identical in syntax to dev/ia-matches.json so curated matches carry
        /// over unchanged. For GitHub it is "owner/repo". For DirectUrl it is a URL.
        /// </summary>
        public string Locator { get; set; }

        /// <summary>
        /// Where this part lands inside the assembled install, relative to its root.
        /// Null means the install root. This is the staging half of a collision:
        /// a port wants its IWAD at "base/", not beside the exe.
        ///
        /// Named to match `apps.json`'s existing `dataFiles[].targetSubpath`.
        /// </summary>
        public string StageTo { get; set; }

        /// <summary>
        /// The filename this part must end up as, from `dataFiles[].name`.
        ///
        /// Separate from <see cref="StageTo"/>, which is the directory. One download can
        /// produce SEVERAL staged files: `devilutionx-gamedata::gamedata.zip` yields
        /// diabdat.mpq, hellfire.mpq, hfmonk.mpq, hfmusic.mpq and hfvoice.mpq, and a
        /// part that does not record which one it is cannot be installed or verified.
        ///
        /// It is also the rename target: Ship of Harkinian ships `ZELOOTD.zip` and the
        /// port expects `Ocarina of Time.z64`.
        /// </summary>
        public string StageAs { get; set; }

        /// <summary>
        /// Expected SHA-1 of the staged file, when the catalogue knows it.
        ///
        /// Taken from `apps.json`, where 47 entries already carry one. It matters more
        /// for data than for ports: a ROM has exactly one correct dump, and a port that
        /// boots to a black screen because the user supplied the wrong region is the
        /// single worst failure mode here, because nothing reports an error.
        /// </summary>
        public string Sha1 { get; set; }

        /// <summary>
        /// How this part is obtained. Orthogonal to <see cref="Source"/>: a part can be
        /// fetched from somewhere, or supplied by the user, and only the former has a
        /// meaningful locator.
        /// </summary>
        public AcquisitionKind Acquire { get; set; } = AcquisitionKind.Fetch;

        public bool Optional { get; set; }

        /// <summary>
        /// Literal file content, for a <see cref="AcquisitionKind.Generated"/> part.
        /// Mutually exclusive with <see cref="Locator"/>: a generated file has no URL,
        /// and a fetched file has no inline body.
        /// </summary>
        public string Content { get; set; }

        public IEnumerable<string> Validate(string entryId)
        {
            // A generated part is the one kind with no upstream. It carries content and
            // a destination instead of a locator.
            if (Acquire == AcquisitionKind.Generated)
            {
                if (string.IsNullOrEmpty(Content))
                {
                    yield return $"'{entryId}': a generated {Role} part has no content to write";
                }

                if (string.IsNullOrWhiteSpace(StageTo))
                {
                    yield return $"'{entryId}': a generated {Role} part has no path to write to";
                }

                yield break;
            }

            if (Source == SourceKind.Unknown)
            {
                yield return $"'{entryId}': a {Role} part has no source kind";
            }

            if (string.IsNullOrWhiteSpace(Locator))
            {
                yield return $"'{entryId}': a {Role} part has no locator";
                yield break;
            }

            if (Source == SourceKind.InternetArchive)
            {
                // Catch a malformed spec at feed-validation time rather than at
                // download time on someone else's machine.
                var error = (string)null;
                try { ContentSpec.Parse(Locator); }
                catch (ArgumentException ex) { error = ex.Message; }
                if (error != null)
                {
                    yield return $"'{entryId}': {error}";
                }
            }

            if (Source == SourceKind.GitHub && !Locator.Contains("/"))
            {
                yield return $"'{entryId}': GitHub locator '{Locator}' is not owner/repo";
            }
        }
    }

    public enum PartRole
    {
        /// <summary>A complete, playable thing. The common case.</summary>
        Game,

        /// <summary>An engine, recompilation or source port. Needs data staged into it.</summary>
        Port,

        /// <summary>
        /// A front end that manages the port: DoomLauncher, QuakeInjector and the like.
        ///
        /// Distinct from Port because it is not what runs the game, it is what picks
        /// the WAD and the engine and then runs one. A bundle can ship a launcher, two
        /// engines and a mod library, and the launcher is the thing the user opens.
        ///
        /// This role is why the Doom and Quake engines are not dropped outright.
        /// Publishing GZDoom alone is useless, because the user still has to find an
        /// IWAD and wire it up. Publishing DoomLauncher plus doomretro plus
        /// `doom_complete` plus `doom-wads` is a Doom install that works on first
        /// launch, which is worth handing to someone.
        /// </summary>
        Launcher,

        /// <summary>ROM, IWAD, PAK, MPQ. The half a port cannot legally ship.</summary>
        Data,

        /// <summary>Applied after assembly.</summary>
        Patch,

        /// <summary>
        /// A configuration file WE write, not one anybody hosts.
        ///
        /// This is the only thing that distinguishes a launcher collision from any
        /// other: the launcher, the engine and the data are all public downloads, and
        /// the config is what makes them find each other. DoomLauncher needs to know
        /// where the IWADs landed and which engines to register; QuakeInjector needs
        /// the Quake directory and an engine binary.
        ///
        /// Owner: "all that makes them different is we inject a config or whatever to
        /// make the launcher work." It has no upstream, so its content travels inline
        /// in the feed entry, which is cheap: a config is bytes, not gigabytes.
        /// </summary>
        Config,
    }

    public enum SourceKind
    {
        Unknown,
        InternetArchive,
        GitHub,
        GitLab,
        Itch,
        DirectUrl,

        /// <summary>Already on the machine. Detected, not downloaded.</summary>
        Local,
    }

    /// <summary>
    /// How a part is obtained, which is a different axis from where it lives.
    ///
    /// Taken from `apps.json`'s existing `ingest` field, whose real distribution across
    /// 515 entries is: internet-archive 353, data-folder 52, none 51, place-file 31,
    /// picker 6, steam-data 4, itch 1. Half of those mean "the user provides this",
    /// which no amount of URL modelling covers, and which is exactly the constraint
    /// ports-launcher's README states: the launcher ships port binaries and never
    /// game data.
    /// </summary>
    public enum AcquisitionKind
    {
        /// <summary>Downloaded from <see cref="FeedPart.Locator"/>. The common case.</summary>
        Fetch,

        /// <summary>Nothing to obtain. The port is self-contained.</summary>
        None,

        /// <summary>The user points at a folder they already have.</summary>
        UserFolder,

        /// <summary>The user supplies one named file, typically a ROM or an IWAD.</summary>
        UserFile,

        /// <summary>Prompt the user to choose among several acceptable inputs.</summary>
        UserPick,

        /// <summary>
        /// Located inside an existing Steam install. The one case where data the user
        /// already owns can be staged without asking them for a path.
        /// </summary>
        SteamInstall,

        /// <summary>
        /// Written from content carried in the feed entry itself. Nothing is fetched,
        /// so there is no URL to go stale and no upstream to go dark. This is the
        /// publisher's actual contribution to a launcher collision.
        /// </summary>
        Generated,
    }

    public enum InstallKind
    {
        Unknown,

        /// <summary>Extract and play. ExePicker finds the binary.</summary>
        Portable,

        /// <summary>
        /// Extraction yields a setup program rather than a game.
        ///
        /// Rare enough not to over-build for. The curated uploaders publish repacks and
        /// make a point of extract-and-play, so RepackAdopter's existing behaviour is
        /// adequate: its blocklist filters setup/uninstaller/redist exes, and when that
        /// empties the candidate list it falls back to the unfiltered set so the folder
        /// still gets a Play action. Anything genuinely wrong is one exclude-glob away.
        /// </summary>
        Installer,

        /// <summary>
        /// Extract and play, but the repacker left a note.
        ///
        /// Occasionally a readme carries an extra step. Nothing infers it: the archive
        /// download glob already pulls `*.txt|*.pdf|*.md` alongside the archive
        /// precisely so the note travels with the game, and the plugin's job is to
        /// SHOW it, not to parse it.
        /// </summary>
        PortableWithNotes,
    }
}
