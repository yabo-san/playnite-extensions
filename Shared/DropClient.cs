using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// The data layer for a Playnite GameLibrary plugin against a Drop server, in the
    /// same shape as Phalcode's GameVault integration: list the library, resolve a
    /// version, hand the install controller a manifest to download.
    ///
    /// No Playnite dependency and no network of its own, so it is driven from captured
    /// responses in tests. Endpoints are read off `Drop-OSS/drop` `server/api/v1/client`
    /// and `server/api/v1/admin/import`, and the download protocol off
    /// `Drop-OSS/drop-app` `games/src/downloads`.
    ///
    /// Two auth headers exist and they are different things:
    ///   client   `JWT &lt;clientId&gt; &lt;es384-jwt&gt;`   every /client/ call (remote/src/auth.rs)
    ///   admin    `Bearer &lt;api token&gt;`             the /admin/ import calls (acls/index.ts:120)
    /// The caller builds them; this class only forwards the string.
    /// </summary>
    public class DropClient
    {
        private readonly string _baseUrl;
        private readonly Func<string, string, string> _get;   // (path, auth) => body
        private readonly Func<string, string, object, string> _post; // (path, auth, body) => body

        public DropClient(
            string baseUrl,
            Func<string, string, string> get,
            Func<string, string, object, string> post)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            _get = get ?? throw new ArgumentNullException(nameof(get));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        public string BaseUrl => _baseUrl;

        // ---- auth ---------------------------------------------------------------

        /// <summary>
        /// Starts device-code authentication.
        ///
        /// `mode: "code"` matters. The server defaults to `AuthMode.Callback`, which
        /// redirects to a `drop://` URI that a Playnite plugin cannot own. In code mode
        /// `clients/handler.ts:66-74` returns the 7-character code as the whole response
        /// body (a JSON string, not an object), and the client id is only learned later
        /// from the websocket message. Older builds returned an object; both are read.
        /// </summary>
        public DropAuthStart BeginAuth(string clientName, string platform, object capabilities = null)
        {
            var body = _post("/api/v1/client/auth/initiate", null, new
            {
                name = clientName,
                platform,
                capabilities = capabilities ?? new { },
                mode = "code",
            });

            var token = string.IsNullOrWhiteSpace(body) ? JValue.CreateNull() : JToken.Parse(body);
            if (token.Type == JTokenType.String)
            {
                return new DropAuthStart { Code = (string)token };
            }
            if (token is JObject json)
            {
                return new DropAuthStart
                {
                    Code = Value(json, "code"),
                    ClientId = Value(json, "id", "clientId"),
                    RedirectUrl = Value(json, "url", "redirect"),
                };
            }
            return new DropAuthStart();
        }

        /// <summary>
        /// The websocket at `/api/v1/client/auth/code/ws` (header `Authorization: &lt;code&gt;`)
        /// delivers `{"type":"token","value":"&lt;clientId&gt;/&lt;token&gt;"}` once the user
        /// approves the code in Drop's web UI (`clients/handler.ts:140`), or
        /// `{"type":"error","value":"..."}`. This parses one such message.
        /// </summary>
        public static bool TryParseTokenMessage(string message, out string clientId, out string token, out string error)
        {
            clientId = token = error = null;
            if (string.IsNullOrWhiteSpace(message)) return false;
            JObject json;
            try { json = JObject.Parse(message); } catch { return false; }
            var type = Value(json, "type");
            var value = Value(json, "value");
            if (type == "error") { error = value ?? "unknown error"; return false; }
            if (type != "token" || string.IsNullOrEmpty(value)) return false;
            var slash = value.IndexOf('/');
            if (slash <= 0 || slash == value.Length - 1) return false;
            clientId = value.Substring(0, slash);
            token = value.Substring(slash + 1);
            return true;
        }

        /// <summary>
        /// Exchanges the websocket token for the client's certificate pair
        /// (`auth/handshake.post.ts`: body `{clientId, token}`, response
        /// `{private, certificate, id}`). The private key is the long-lived credential:
        /// every later request is a JWT signed with it. There is no session token to
        /// store instead; `auth/session.post.ts` is an empty handler.
        /// </summary>
        public DropCredentials CompleteAuth(string clientId, string token)
        {
            var body = _post("/api/v1/client/auth/handshake", null, new { clientId, token });
            var json = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

            return new DropCredentials
            {
                ClientId = Value(json, "id", "clientId") ?? clientId,
                Certificate = Value(json, "certificate", "cert"),
                PrivateKey = Value(json, "private", "privateKey"),
                CaCertificate = Value(json, "ca", "caCertificate"),
            };
        }

        // ---- library ------------------------------------------------------------

        /// <summary>The signed-in user's library, a bare array (`user/library.get.ts`).</summary>
        public IReadOnlyList<DropGame> Library(string auth)
        {
            var body = _get("/api/v1/client/user/library", auth);
            var parsed = JToken.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
            var array = parsed as JArray ?? parsed["entries"] as JArray ?? new JArray();
            return array.Select(ToGame).Where(g => g.Id != null).ToList();
        }

        public DropGame Game(string auth, string gameId)
        {
            var body = _get("/api/v1/client/game/" + Uri.EscapeDataString(gameId ?? string.Empty), auth);
            return ToGame(JToken.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body));
        }

        /// <summary>
        /// Versions available for a game, newest first (`game/[id]/versions.get.ts`
        /// orders by versionIndex desc). One row per version AND platform; the caller
        /// picks the newest row whose platform is "windows".
        /// </summary>
        public IReadOnlyList<DropVersion> Versions(string auth, string gameId)
        {
            var body = _get("/api/v1/client/game/" + Uri.EscapeDataString(gameId ?? string.Empty) + "/versions", auth);
            var parsed = JToken.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
            var array = parsed as JArray ?? parsed["versions"] as JArray ?? new JArray();

            var list = new List<DropVersion>();
            foreach (var v in array)
            {
                var ver = new DropVersion
                {
                    VersionId = Value(v, "versionId", "id"),
                    Name = Value(v, "displayName", "versionName", "name") ?? Value(v, "versionId"),
                    Index = (int?)v["versionIndex"] ?? list.Count,
                    Platform = (Value(v, "platform") ?? string.Empty).ToLowerInvariant(),
                };
                var size = v["size"];
                if (size != null && size.Type == JTokenType.Object)
                {
                    ver.DownloadBytes = (long?)size["downloadSize"] ?? (long?)size["download"] ?? 0;
                    ver.InstallBytes = (long?)size["installSize"] ?? (long?)size["install"] ?? 0;
                }
                else if (size != null && size.Type == JTokenType.Integer)
                {
                    ver.InstallBytes = (long)size;
                }
                if (ver.VersionId != null || ver.Name != null) list.Add(ver);
            }
            return list;
        }

        /// <summary>
        /// One version's launch table (`game/[id]/version/[versionid]/index.get.ts`).
        /// `launch` is a command line run from the install directory; on Windows the
        /// desktop client runs it through `cmd /C` (process_handlers.rs:43).
        /// </summary>
        public DropVersionDetail VersionDetail(string auth, string gameId, string versionId)
        {
            var body = _get("/api/v1/client/game/" + Uri.EscapeDataString(gameId ?? string.Empty)
                            + "/version/" + Uri.EscapeDataString(versionId ?? string.Empty), auth);
            var json = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var detail = new DropVersionDetail { VersionId = Value(json, "versionId") ?? versionId };
            foreach (var l in json["launches"] as JArray ?? new JArray())
            {
                detail.Launches.Add(new DropLaunch
                {
                    Platform = (Value(l, "platform") ?? string.Empty).ToLowerInvariant(),
                    Name = Value(l, "name"),
                    Command = Value(l, "launch", "command"),
                });
            }
            foreach (var s in json["setups"] as JArray ?? new JArray())
            {
                detail.Setups.Add(new DropLaunch
                {
                    Platform = (Value(s, "platform") ?? string.Empty).ToLowerInvariant(),
                    Name = "setup",
                    Command = Value(s, "launch", "command"),
                });
            }
            return detail;
        }

        /// <summary>Absolute URL for an object id, for cover and icon art.</summary>
        public string ObjectUrl(string objectId)
        {
            return string.IsNullOrWhiteSpace(objectId)
                ? null
                : _baseUrl + "/api/v1/client/object/" + Uri.EscapeDataString(objectId);
        }

        // ---- download -----------------------------------------------------------

        /// <summary>
        /// Depot endpoints (`client/depots/index.get.ts`), each normalised to end in "/".
        /// Chunks live at `&lt;endpoint&gt;content/{gameId}/{versionId}/{chunkId}`
        /// (download_logic.rs:66) and the same JWT header is sent there.
        /// </summary>
        public IReadOnlyList<string> Depots(string auth)
        {
            var body = _get("/api/v1/client/depots", auth);
            var parsed = JToken.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
            var array = parsed as JArray ?? new JArray();
            return array.Select(d => Value(d, "endpoint"))
                        .Where(e => !string.IsNullOrWhiteSpace(e))
                        .Select(e => e.EndsWith("/") ? e : e + "/")
                        .ToList();
        }

        public static string ChunkUrl(string depotEndpoint, string gameId, string versionId, string chunkId)
        {
            var e = depotEndpoint.EndsWith("/") ? depotEndpoint : depotEndpoint + "/";
            return e + "content/" + Uri.EscapeDataString(gameId) + "/" + Uri.EscapeDataString(versionId) + "/" + Uri.EscapeDataString(chunkId);
        }

        /// <summary>
        /// The download plan for a version (`client/game/manifest.get.ts` ->
        /// `createDownloadManifestDetails`): which version owns each file, and per
        /// version the droplet manifest with the AES key and the chunks to fetch.
        /// </summary>
        public DropDownloadInfo Manifest(string auth, string versionId, string previousVersionId = null)
        {
            var path = "/api/v1/client/game/manifest?version=" + Uri.EscapeDataString(versionId ?? string.Empty);
            if (!string.IsNullOrEmpty(previousVersionId)) path += "&previous=" + Uri.EscapeDataString(previousVersionId);
            var body = _get(path, auth);
            return ParseDownloadInfo(body);
        }

        public static DropDownloadInfo ParseDownloadInfo(string body)
        {
            var json = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var info = new DropDownloadInfo
            {
                InstallBytes = (long?)json["installSize"] ?? 0,
                DownloadBytes = (long?)json["downloadSize"] ?? 0,
            };
            foreach (var p in (json["fileList"] as JObject ?? new JObject()).Properties())
            {
                info.FileList[p.Name] = (string)p.Value;
            }
            foreach (var mv in (json["manifests"] as JObject ?? new JObject()).Properties())
            {
                var m = mv.Value as JObject;
                if (m == null) continue;
                var manifest = new DropManifest
                {
                    VersionId = mv.Name,
                    Key = Bytes(m["key"]),
                };
                foreach (var cp in (m["chunks"] as JObject ?? new JObject()).Properties())
                {
                    var c = cp.Value as JObject;
                    if (c == null) continue;
                    var chunk = new DropManifestChunk
                    {
                        Id = cp.Name,
                        Checksum = Value(c, "checksum"),
                        Iv = Bytes(c["iv"]),
                    };
                    foreach (var f in c["files"] as JArray ?? new JArray())
                    {
                        chunk.Files.Add(new DropManifestFile
                        {
                            Filename = Value(f, "filename"),
                            Start = (long?)f["start"] ?? 0,
                            Length = (long?)f["length"] ?? 0,
                            Permissions = (int?)f["permissions"] ?? 0,
                        });
                    }
                    manifest.Chunks.Add(chunk);
                }
                info.Manifests[mv.Name] = manifest;
            }
            return info;
        }

        // ---- admin import (the "Send to Drop" half) -----------------------------

        /// <summary>
        /// Folders in the library root the server has not imported yet
        /// (`admin/import/game/index.get.ts`). Each carries the library it sits in.
        /// </summary>
        public IReadOnlyList<DropUnimported> AdminUnimported(string adminAuth)
        {
            var body = _get("/api/v1/admin/import/game", adminAuth);
            var json = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var list = new List<DropUnimported>();
            foreach (var e in json["unimportedGames"] as JArray ?? new JArray())
            {
                var lib = e["library"] as JObject;
                list.Add(new DropUnimported
                {
                    Path = Value(e, "game"),
                    LibraryId = lib == null ? null : Value(lib, "id"),
                    LibraryName = lib == null ? null : Value(lib, "name"),
                });
            }
            return list;
        }

        /// <summary>`admin/import/game/index.post.ts`: `{library, path, type}` -> `{taskId}`.</summary>
        public string AdminImportGame(string adminAuth, string libraryId, string path, string type = "Game")
        {
            var body = _post("/api/v1/admin/import/game", adminAuth, new { library = libraryId, path, type });
            return Value(JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body), "taskId");
        }

        /// <summary>
        /// `admin/import/version/index.post.ts`. `version.type` is "local" for a folder
        /// under the game's library path; `launches[].launch` is the command line the
        /// client will run from the install root.
        /// </summary>
        public string AdminImportVersion(string adminAuth, string gameId, string versionFolder, string displayName, IEnumerable<DropLaunch> launches)
        {
            var body = _post("/api/v1/admin/import/version", adminAuth, new
            {
                id = gameId,
                version = new { type = "local", identifier = versionFolder, name = versionFolder },
                displayName,
                launches = (launches ?? Enumerable.Empty<DropLaunch>())
                    .Select(l => new { platform = l.Platform ?? "windows", name = l.Name ?? "Play", launch = l.Command })
                    .ToArray(),
                setups = new object[0],
                onlySetup = false,
                delta = false,
                requiredContent = new string[0],
            });
            return Value(JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body), "taskId");
        }

        // ---- parsing helpers ----------------------------------------------------

        private static DropGame ToGame(JToken t)
        {
            if (t == null || t.Type != JTokenType.Object) return new DropGame();
            return new DropGame
            {
                Id = Value(t, "id"),
                Name = Value(t, "mName", "name"),
                // mDescription, not description: the field was renamed upstream in
                // August 2026; an older build reading the old name imports every game
                // with an empty description and no error.
                Description = Value(t, "mDescription", "description"),
                IconObjectId = Value(t, "mIconObjectId", "iconObjectId"),
                CoverObjectId = Value(t, "mCoverObjectId", "coverObjectId"),
                BannerObjectId = Value(t, "mBannerObjectId", "bannerObjectId"),
                ReleaseDate = Value(t, "mReleased", "released"),
            };
        }

        private static byte[] Bytes(JToken t)
        {
            if (t is JArray a) return a.Select(x => (byte)(int)x).ToArray();
            if (t != null && t.Type == JTokenType.String)
            {
                try { return Convert.FromBase64String((string)t); } catch { return null; }
            }
            return null;
        }

        // Drop's field names have drifted (mName/name, mDescription/description), so
        // every read takes the current name first and falls back rather than returning
        // null and silently importing blank games.
        private static string Value(JToken token, params string[] names)
        {
            foreach (var name in names)
            {
                var v = token?[name];
                if (v != null && v.Type != JTokenType.Null && v.Type != JTokenType.Object && v.Type != JTokenType.Array)
                {
                    var s = v.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }
    }

    public class DropAuthStart
    {
        /// <summary>The 7-character code the user types into Drop's web UI.</summary>
        public string Code { get; set; }
        /// <summary>Only present on older servers that returned an object; null in code mode today.</summary>
        public string ClientId { get; set; }
        public string RedirectUrl { get; set; }
        public bool UsesCode => !string.IsNullOrWhiteSpace(Code);
    }

    public class DropCredentials
    {
        public string ClientId { get; set; }
        public string Certificate { get; set; }
        public string PrivateKey { get; set; }
        public string CaCertificate { get; set; }
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Certificate) && !string.IsNullOrWhiteSpace(PrivateKey);
    }

    public class DropGame
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconObjectId { get; set; }
        public string CoverObjectId { get; set; }
        public string BannerObjectId { get; set; }
        public string ReleaseDate { get; set; }
    }

    public class DropVersion
    {
        public string VersionId { get; set; }
        public string Name { get; set; }
        public int Index { get; set; }
        public string Platform { get; set; }
        public long DownloadBytes { get; set; }
        public long InstallBytes { get; set; }
    }

    public class DropLaunch
    {
        public string Platform { get; set; }
        public string Name { get; set; }
        public string Command { get; set; }
    }

    public class DropVersionDetail
    {
        public string VersionId { get; set; }
        public List<DropLaunch> Launches { get; } = new List<DropLaunch>();
        public List<DropLaunch> Setups { get; } = new List<DropLaunch>();
    }

    public class DropDownloadInfo
    {
        /// <summary>filename -> versionId that owns the current copy of it.</summary>
        public Dictionary<string, string> FileList { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>versionId -> manifest with the chunks to fetch for that version.</summary>
        public Dictionary<string, DropManifest> Manifests { get; } = new Dictionary<string, DropManifest>(StringComparer.Ordinal);
        public long InstallBytes { get; set; }
        public long DownloadBytes { get; set; }
    }

    public class DropManifest
    {
        public string VersionId { get; set; }
        /// <summary>AES-128 key for every chunk of this version (droplet manifest `key`, 16 numbers).</summary>
        public byte[] Key { get; set; }
        public List<DropManifestChunk> Chunks { get; } = new List<DropManifestChunk>();
    }

    public class DropManifestChunk
    {
        public string Id { get; set; }
        /// <summary>Hex SHA-256 of the chunk's plaintext.</summary>
        public string Checksum { get; set; }
        public byte[] Iv { get; set; }
        public List<DropManifestFile> Files { get; } = new List<DropManifestFile>();
        public long Length => Files.Sum(f => f.Length);
    }

    public class DropManifestFile
    {
        public string Filename { get; set; }
        public long Start { get; set; }
        public long Length { get; set; }
        public int Permissions { get; set; }
    }

    public class DropUnimported
    {
        public string Path { get; set; }
        public string LibraryId { get; set; }
        public string LibraryName { get; set; }
    }
}
