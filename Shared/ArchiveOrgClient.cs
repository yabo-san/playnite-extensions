using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// Talks to archive.org's two public endpoints. Deliberately has no Playnite
    /// dependency so it can be exercised standalone, the same way GithubLauncher's
    /// ListParser can.
    ///
    /// It does NOT use the `ia` CLI. That tool being absent from a machine is the
    /// entire cause of INC0044907, where a nightly sync exited at its own guard 392
    /// times over six months. Both endpoints below are plain HTTPS and need nothing
    /// installed.
    /// </summary>
    public class ArchiveOrgClient
    {
        private const string SearchEndpoint = "https://archive.org/advancedsearch.php";
        private const string MetadataEndpoint = "https://archive.org/metadata/";

        private readonly Func<string, string> _get;

        public ArchiveOrgClient() : this(DefaultGet) { }

        /// <summary>Takes the fetcher so tests can drive it from captured responses.</summary>
        public ArchiveOrgClient(Func<string, string> get)
        {
            _get = get ?? throw new ArgumentNullException(nameof(get));
        }

        private static string DefaultGet(string url)
        {
            // Playnite targets net462, whose default is SSL3/TLS1.0. archive.org
            // refuses both, so without this every request fails with a bare
            // "connection closed" that looks like a network fault.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "playnite-internetarchive";
            request.Timeout = 60000;

            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Every item published by one source. A source is either an uploader handle
        /// or an email; both are accepted because archive.org indexes them in the same
        /// field, and the curated seed genuinely contains a mix.
        ///
        /// Handles are preferred in anything that ships: an uploader's email is
        /// someone else's personal data, and these repos are public.
        /// </summary>
        public IReadOnlyList<ArchiveItem> ItemsByUploader(string uploader)
        {
            return Search("uploader:" + Quote(uploader));
        }

        /// <summary>
        /// Items by `creator:` rather than `uploader:`. Kept separate on purpose.
        ///
        /// `creator:` is a free-text credit field, not an account, so it matches
        /// anything sharing that name. The curated source "Infidelity" returns SNES
        /// romhacks alongside a drum-and-bass podcast and an 1800s ordination sermon.
        /// A caller using this needs to filter; a caller using ItemsByUploader does not.
        /// </summary>
        public IReadOnlyList<ArchiveItem> ItemsByCreator(string creator)
        {
            return Search("creator:" + Quote(creator));
        }

        private IReadOnlyList<ArchiveItem> Search(string query)
        {
            var url = SearchEndpoint
                + "?q=" + Uri.EscapeDataString(query)
                + "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=item_size&fl%5B%5D=mediatype"
                + "&rows=3000&output=json";

            var docs = JObject.Parse(_get(url))["response"]?["docs"] as JArray;
            if (docs == null)
            {
                return new List<ArchiveItem>();
            }

            return docs
                .Select(d => new ArchiveItem
                {
                    Identifier = Str(d["identifier"]),
                    Title = Str(d["title"]),
                    SizeBytes = (long?)d["item_size"] ?? 0,
                    MediaType = Str(d["mediatype"]),
                })
                .Where(i => !string.IsNullOrWhiteSpace(i.Identifier))
                .ToList();
        }

        /// <summary>
        /// Availability and file list for one item.
        ///
        /// A removed item still resolves: archive.org keeps the identifier and answers
        /// with `is_dark` and no `metadata` block. That is the only way to tell "taken
        /// down" from "never existed", and it is how four unrecoverable items were
        /// found during a real audit. Both cases return IsDark, with Exists distinguishing them.
        /// </summary>
        public ArchiveItemDetail Detail(string identifier)
        {
            var json = JObject.Parse(_get(MetadataEndpoint + Uri.EscapeDataString(identifier)));

            var metadata = json["metadata"] as JObject;
            if (metadata == null)
            {
                // No metadata block. Two very different situations share that shape:
                // archive.org holds the item and is withholding it (`is_dark: true`,
                // plus housekeeping keys like `d1`/`dir`/`created`), or the identifier
                // was never used at all and the document is empty.
                //
                // The distinction is the whole point of this call. A dark item means a
                // copy on disk is now the only copy; a missing one means nothing.
                var known = json.HasValues;

                return new ArchiveItemDetail
                {
                    Identifier = identifier,
                    Exists = known,
                    // Only meaningful when the item exists. Reporting an unknown
                    // identifier as "dark" would invent a takedown that never happened.
                    IsDark = known,
                };
            }

            var files = (json["files"] as JArray) ?? new JArray();

            return new ArchiveItemDetail
            {
                Identifier = identifier,
                Exists = true,
                IsDark = false,
                Title = (string)metadata["title"],
                Uploader = (string)metadata["uploader"],
                // Distinct from `curation`, which carries a routine
                // "[curator]validator@archive.org[/curator]" stamp on effectively every
                // item and means nothing. This flag is the one that restricts access.
                AccessRestricted = metadata["access-restricted-item"] != null,
                Files = files
                    .Select(f => new ArchiveFile
                    {
                        Name = (string)f["name"],
                        SizeBytes = ParseSize((string)f["size"]),
                        Format = (string)f["format"],
                    })
                    .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                    .ToList(),
            };
        }

        // archive.org returns file sizes as strings, and omits the field entirely on
        // derived files, so a miss is 0 rather than an error.
        private static long ParseSize(string raw)
        {
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        /// <summary>
        /// Coerces a search field to a string.
        ///
        /// The search index is Solr, and several of its fields are multi-valued: an
        /// item with two titles returns a JSON ARRAY where every other item returns a
        /// string. Casting blind throws "Can not convert Array to String" on the first
        /// such item, and in a real catalogue that lands inside the first page of
        /// results. Take the first element and move on; nothing here needs the rest.
        /// </summary>
        private static string Str(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Array)
            {
                var first = token.FirstOrDefault();
                return first == null ? null : (string)first;
            }

            return (string)token;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", string.Empty) + "\"";
        }
    }

    public class ArchiveItem
    {
        public string Identifier { get; set; }
        public string Title { get; set; }
        public long SizeBytes { get; set; }
        public string MediaType { get; set; }

        /// <summary>Title when archive.org has one, otherwise the identifier.</summary>
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Title) ? Identifier : Title;
    }

    public class ArchiveItemDetail
    {
        public string Identifier { get; set; }
        public bool Exists { get; set; }
        public bool IsDark { get; set; }
        public string Title { get; set; }
        public string Uploader { get; set; }
        public bool AccessRestricted { get; set; }
        public IReadOnlyList<ArchiveFile> Files { get; set; } = new List<ArchiveFile>();
    }

    public class ArchiveFile
    {
        public string Name { get; set; }
        public long SizeBytes { get; set; }
        public string Format { get; set; }
    }
}
