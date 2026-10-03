using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    public class DropClientTests
    {
        private readonly List<string> _paths = new List<string>();
        private readonly List<object> _bodies = new List<object>();

        private DropClient Client(Func<string, string> respond)
        {
            return new DropClient(
                "https://drop.example.com/",
                (path, auth) => { _paths.Add(path); return respond(path); },
                (path, auth, body) => { _paths.Add(path); _bodies.Add(body); return respond(path); });
        }

        [Fact]
        public void BeginAuth_requests_code_mode_and_reads_the_bare_string_the_server_returns()
        {
            // clients/handler.ts:66-74 returns the code itself as the response body in
            // code mode. The client id is not known until the websocket message.
            var c = Client(_ => "\"ABCD123\"");

            var start = c.BeginAuth("Playnite", "Windows");

            Assert.Equal("/api/v1/client/auth/initiate", _paths.Single());
            var sent = Newtonsoft.Json.Linq.JObject.FromObject(_bodies.Single());
            Assert.Equal("code", (string)sent["mode"]);
            Assert.Equal("Playnite", (string)sent["name"]);
            Assert.Equal("ABCD123", start.Code);
            Assert.Null(start.ClientId);
            Assert.True(start.UsesCode);
        }

        [Fact]
        public void BeginAuth_reads_an_unquoted_plain_text_code()
        {
            // Live drop.example.com, 2026-09-27: 200 text/html with the bare code, no
            // quotes. A code starting with a digit made JToken.Parse throw
            // "Input string '9C86E0F' is not a valid number".
            var start = Client(_ => "9C86E0F\n").BeginAuth("Playnite", "Windows");

            Assert.Equal("9C86E0F", start.Code);
            Assert.True(start.UsesCode);
        }

        [Fact]
        public void BeginAuth_still_reads_the_older_object_shape()
        {
            var start = Client(_ => "{\"code\":\"ABCD12\",\"id\":\"client-1\"}").BeginAuth("Playnite", "Windows");
            Assert.Equal("ABCD12", start.Code);
            Assert.Equal("client-1", start.ClientId);
        }

        [Fact]
        public void The_websocket_token_message_carries_client_id_and_token_joined_by_a_slash()
        {
            Assert.True(DropClient.TryParseTokenMessage("{\"type\":\"token\",\"value\":\"client-1/tok-2\"}", out var id, out var tok, out var err));
            Assert.Equal("client-1", id);
            Assert.Equal("tok-2", tok);
            Assert.Null(err);

            Assert.False(DropClient.TryParseTokenMessage("{\"type\":\"error\",\"value\":\"Request timed out.\"}", out _, out _, out err));
            Assert.Equal("Request timed out.", err);

            Assert.False(DropClient.TryParseTokenMessage("not json", out _, out _, out _));
            Assert.False(DropClient.TryParseTokenMessage("{\"type\":\"token\",\"value\":\"noslash\"}", out _, out _, out _));
        }

        [Fact]
        public void CompleteAuth_sends_client_id_and_token_and_reads_the_certificate_pair()
        {
            // auth/handshake.post.ts: body {clientId, token}; response {private, certificate, id}.
            var c = Client(_ => "{\"id\":\"client-1\",\"certificate\":\"CERT\",\"private\":\"KEY\"}");

            var creds = c.CompleteAuth("client-1", "token-from-websocket");

            Assert.Equal("/api/v1/client/auth/handshake", _paths.Single());
            var sent = Newtonsoft.Json.Linq.JObject.FromObject(_bodies.Single());
            Assert.Equal("client-1", (string)sent["clientId"]);
            Assert.Equal("token-from-websocket", (string)sent["token"]);
            Assert.True(creds.IsComplete);
            Assert.Equal("CERT", creds.Certificate);
            Assert.Equal("KEY", creds.PrivateKey);
        }

        [Fact]
        public void Incomplete_credentials_are_not_reported_complete()
        {
            var c = Client(_ => "{\"id\":\"client-1\",\"certificate\":\"CERT\"}");
            Assert.False(c.CompleteAuth("client-1", "t").IsComplete);
        }

        [Fact]
        public void Library_maps_a_bare_array_which_is_what_the_endpoint_returns()
        {
            var c = Client(_ => "[{\"id\":\"g1\",\"mName\":\"Doom\",\"mCoverObjectId\":\"cov\"},{\"id\":\"g2\",\"mName\":\"Quake\"}]");

            var games = c.Library("auth");

            Assert.Equal("/api/v1/client/user/library", _paths.Single());
            Assert.Equal(2, games.Count);
            Assert.Equal("Doom", games[0].Name);
            Assert.Equal("cov", games[0].CoverObjectId);
        }

        [Fact]
        public void Library_reads_mDescription_and_falls_back_to_description()
        {
            var c = Client(_ => "[{\"id\":\"g1\",\"name\":\"A\",\"mDescription\":\"new\"},{\"id\":\"g2\",\"name\":\"B\",\"description\":\"old\"}]");
            var games = c.Library("auth");
            Assert.Equal("new", games[0].Description);
            Assert.Equal("old", games[1].Description);
        }

        [Fact]
        public void Library_skips_entries_with_no_id()
        {
            var games = Client(_ => "[{\"mName\":\"orphan\"},{\"id\":\"g1\",\"mName\":\"ok\"}]").Library("auth");
            Assert.Single(games);
        }

        [Fact]
        public void An_empty_library_is_not_an_error()
        {
            Assert.Empty(Client(_ => "[]").Library("auth"));
            Assert.Empty(Client(_ => "").Library("auth"));
        }

        [Fact]
        public void Versions_come_from_the_per_game_path_with_platform_and_sizes()
        {
            var c = Client(_ => "[{\"versionId\":\"v2\",\"displayName\":\"1.1\",\"versionIndex\":1,\"platform\":\"Windows\",\"size\":{\"downloadSize\":10,\"installSize\":20}},"
                             + "{\"versionId\":\"v1\",\"versionIndex\":0,\"platform\":\"linux\"}]");

            var versions = c.Versions("auth", "g 1");

            Assert.Equal("/api/v1/client/game/g%201/versions", _paths.Single());
            Assert.Equal(2, versions.Count);
            Assert.Equal("windows", versions[0].Platform);
            Assert.Equal("1.1", versions[0].Name);
            Assert.Equal(10, versions[0].DownloadBytes);
            Assert.Equal("v1", versions[1].Name);
        }

        [Fact]
        public void VersionDetail_reads_launches_and_setups()
        {
            var c = Client(_ => "{\"versionId\":\"v2\",\"launches\":[{\"platform\":\"Windows\",\"name\":\"Play\",\"launch\":\"game.exe -skipintro\"}],\"setups\":[{\"platform\":\"Windows\",\"launch\":\"setup.exe\"}]}");
            var d = c.VersionDetail("auth", "g1", "v2");
            Assert.Equal("/api/v1/client/game/g1/version/v2", _paths.Single());
            Assert.Single(d.Launches);
            Assert.Equal("windows", d.Launches[0].Platform);
            Assert.Equal("game.exe -skipintro", d.Launches[0].Command);
            Assert.Single(d.Setups);
        }

        [Fact]
        public void Depots_are_normalised_to_end_in_a_slash_and_chunk_urls_build_from_them()
        {
            var depots = Client(_ => "[{\"endpoint\":\"https://depot.example.com/d1\"},{\"endpoint\":\"https://drop.example.com/api/v1/client/\"}]").Depots("auth");
            Assert.Equal(new[] { "https://depot.example.com/d1/", "https://drop.example.com/api/v1/client/" }, depots);
            Assert.Equal("https://depot.example.com/d1/content/g1/v%202/c1", DropClient.ChunkUrl("https://depot.example.com/d1", "g1", "v 2", "c1"));
        }

        [Fact]
        public void Manifest_parses_the_download_plan_with_keys_as_byte_arrays()
        {
            var body = "{\"fileList\":{\"game.exe\":\"v2\",\"data.pak\":\"v1\"},\"installSize\":30,\"downloadSize\":12,"
                     + "\"manifests\":{\"v2\":{\"version\":\"2\",\"size\":30,\"key\":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16],"
                     + "\"chunks\":{\"c1\":{\"checksum\":\"ab\",\"iv\":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"
                     + "\"files\":[{\"filename\":\"game.exe\",\"start\":0,\"length\":12,\"permissions\":0}]}}}}}";
            var info = Client(_ => body).Manifest("auth", "v2");

            Assert.Equal("/api/v1/client/game/manifest?version=v2", _paths.Single());
            Assert.Equal("v2", info.FileList["game.exe"]);
            Assert.Equal(12, info.DownloadBytes);
            var m = info.Manifests["v2"];
            Assert.Equal(16, m.Key.Length);
            Assert.Equal(9, m.Key[8]);
            var chunk = m.Chunks.Single();
            Assert.Equal("c1", chunk.Id);
            Assert.Equal(12, chunk.Length);
            Assert.Equal("game.exe", chunk.Files[0].Filename);
        }

        [Fact]
        public void ObjectUrl_builds_an_absolute_art_url_and_ignores_a_trailing_slash()
        {
            var c = Client(_ => "");
            Assert.Equal("https://drop.example.com/api/v1/client/object/obj1", c.ObjectUrl("obj1"));
            Assert.Null(c.ObjectUrl(null));
            Assert.Null(c.ObjectUrl("  "));
        }

        [Fact]
        public void Game_ids_are_url_escaped()
        {
            Client(_ => "{}").Game("auth", "a b/c");
            Assert.Equal("/api/v1/client/game/a%20b%2Fc", _paths.Single());
        }

        [Fact]
        public void Admin_import_calls_send_the_shapes_the_server_validates()
        {
            var c = Client(p => p.EndsWith("/import/game") && _bodies.Count == 0
                ? "{\"unimportedGames\":[{\"game\":\"My Game\",\"library\":{\"id\":\"lib1\",\"name\":\"main\"}}]}"
                : "{\"taskId\":\"t1\"}");

            var pending = c.AdminUnimported("Bearer x");
            Assert.Single(pending);
            Assert.Equal("My Game", pending[0].Path);
            Assert.Equal("lib1", pending[0].LibraryId);

            Assert.Equal("t1", c.AdminImportGame("Bearer x", "lib1", "My Game"));
            var g = Newtonsoft.Json.Linq.JObject.FromObject(_bodies[0]);
            Assert.Equal("lib1", (string)g["library"]);
            Assert.Equal("Game", (string)g["type"]);

            Assert.Equal("t1", c.AdminImportVersion("Bearer x", "g1", "v20260925", "2026-09-25",
                new[] { new DropLaunch { Platform = "windows", Name = "Play", Command = "game.exe" } }));
            var v = Newtonsoft.Json.Linq.JObject.FromObject(_bodies[1]);
            Assert.Equal("local", (string)v["version"]["type"]);
            Assert.Equal("v20260925", (string)v["version"]["identifier"]);
            Assert.Equal("game.exe", (string)v["launches"][0]["launch"]);
            Assert.False((bool)v["delta"]);
        }
    }
}
