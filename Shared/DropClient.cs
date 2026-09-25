using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// The data layer for a Playnite GameLibrary plugin against a Drop server, in the
    /// same shape as Phalcode's GameVault integration: list the library, resolve a
    /// version, hand the install controller somewhere to download from.
    ///
    /// No Playnite dependency and no network of its own, so it is driven from captured
    /// responses in tests. Endpoints are read off `drop-oss/drop` `server/api/v1/client`.
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

        /// <summary>
        /// Starts device-code authentication.
        ///
        /// `mode: "code"` matters. The server defaults to `AuthMode.Callback`, which
        /// redirects to a `drop://` URI that a Playnite plugin cannot own. Code mode
        /// hands back a short code the user types into Drop's web UI instead, and the
        /// token arrives over a websocket. Same pattern as a Plex or Jellyfin sign-in.
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

            var json = JObject.Parse(body ?? "{}");

            return new DropAuthStart
            {
                Code = Value(json, "code"),
                ClientId = Value(json, "id", "clientId"),
                // Present in callback mode; null in code mode, which is the point.
                RedirectUrl = Value(json, "url", "redirect"),
            };
        }

        /// <summary>
        /// Exchanges the token pushed down the websocket for the client's certificate.
        ///
        /// The certificate is the long-lived credential. The day-long session token
        /// documented alongside it is not implemented server side
        /// (`auth/session.post.ts` is an empty handler), so there is no shortcut to
        /// store instead.
        /// </summary>
        public DropCredentials CompleteAuth(string token)
        {
            var body = _post("/api/v1/client/auth/handshake", null, new { token });
            var json = JObject.Parse(body ?? "{}");

            return new DropCredentials
            {
                ClientId = Value(json, "id", "clientId"),
                Certificate = Value(json, "certificate", "cert", "publicKey"),
                PrivateKey = Value(json, "privateKey", "private"),
                CaCertificate = Value(json, "ca", "caCertificate"),
            };
        }

        /// <summary>
        /// The signed-in user's library. This is what GetGames maps over.
        ///
        /// `user/library.get.ts` returns the game objects directly, not a paginated
        /// envelope, so unlike GameVault there is no page loop to write.
        /// </summary>
        public IReadOnlyList<DropGame> Library(string auth)
        {
            var body = _get("/api/v1/client/user/library", auth);
            var parsed = JToken.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);

            // Tolerate an envelope in case the shape changes; the endpoint returns a
            // bare array today.
            var array = parsed as JArray ?? parsed["entries"] as JArray ?? new JArray();

            return array.Select(ToGame).Where(g => g.Id != null).ToList();
        }

        public DropGame Game(string auth, string gameId)
        {
            var body = _get("/api/v1/client/game/" + Uri.EscapeDataString(gameId ?? string.Empty), auth);
            return ToGame(JToken.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body));
        }

        /// <summary>
        /// Versions available for a game, newest first where the server orders them.
        /// The install controller picks one; Playnite has no concept of versions, so
        /// the plugin decides and records which it installed.
        /// </summary>
        public IReadOnlyList<DropVersion> Versions(string auth, string gameId)
        {
            var body = _get("/api/v1/client/game/versions?id=" + Uri.EscapeDataString(gameId ?? string.Empty), auth);
            var parsed = JToken.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
            var array = parsed as JArray ?? parsed["versions"] as JArray ?? new JArray();

            return array
                .Select(v => new DropVersion
                {
                    Name = Value(v, "versionName", "name"),
                    Index = (int?)v["versionIndex"] ?? 0,
                    Platform = Value(v, "platform"),
                })
                .Where(v => v.Name != null)
                .ToList();
        }

        private static DropGame ToGame(JToken t)
        {
            if (t == null || t.Type != JTokenType.Object)
            {
                return new DropGame();
            }

            return new DropGame
            {
                Id = Value(t, "id"),
                Name = Value(t, "mName", "name"),
                // mDescription, not description. The field was renamed upstream in
                // August 2026; an older build reading the old name imports every game
                // with an empty description and no error.
                Description = Value(t, "mDescription", "description"),
                IconObjectId = Value(t, "mIconObjectId", "iconObjectId"),
                CoverObjectId = Value(t, "mCoverObjectId", "coverObjectId"),
                BannerObjectId = Value(t, "mBannerObjectId", "bannerObjectId"),
                ReleaseDate = Value(t, "mReleased", "released"),
            };
        }

        /// <summary>Absolute URL for an object id, for cover and icon art.</summary>
        public string ObjectUrl(string objectId)
        {
            return string.IsNullOrWhiteSpace(objectId)
                ? null
                : _baseUrl + "/api/v1/client/object/" + Uri.EscapeDataString(objectId);
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
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }
            }

            return null;
        }
    }

    public class DropAuthStart
    {
        /// <summary>The short code the user types into Drop's web UI.</summary>
        public string Code { get; set; }

        public string ClientId { get; set; }

        /// <summary>Only set in callback mode. Null is expected here.</summary>
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
            !string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(Certificate)
            && !string.IsNullOrWhiteSpace(PrivateKey);
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
        public string Name { get; set; }
        public int Index { get; set; }
        public string Platform { get; set; }
    }
}
