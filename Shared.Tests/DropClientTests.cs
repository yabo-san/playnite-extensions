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
        public void BeginAuth_requests_code_mode_not_the_callback_default()
        {
            // The server defaults to AuthMode.Callback, which redirects to a drop://
            // URI a Playnite plugin cannot own. Asking for code mode is the whole
            // reason this works at all.
            var c = Client(_ => "{\"code\":\"ABCD12\",\"id\":\"client-1\"}");

            var start = c.BeginAuth("Playnite", "Windows");

            Assert.Equal("/api/v1/client/auth/initiate", _paths.Single());
            var sent = Newtonsoft.Json.Linq.JObject.FromObject(_bodies.Single());
            Assert.Equal("code", (string)sent["mode"]);
            Assert.Equal("Playnite", (string)sent["name"]);

            Assert.Equal("ABCD12", start.Code);
            Assert.Equal("client-1", start.ClientId);
            Assert.True(start.UsesCode);
            Assert.Null(start.RedirectUrl);
        }

        [Fact]
        public void CompleteAuth_reads_the_certificate_pair()
        {
            var c = Client(_ => "{\"id\":\"client-1\",\"certificate\":\"CERT\",\"privateKey\":\"KEY\",\"ca\":\"CA\"}");

            var creds = c.CompleteAuth("token-from-websocket");

            Assert.Equal("/api/v1/client/auth/handshake", _paths.Single());
            Assert.True(creds.IsComplete);
            Assert.Equal("CERT", creds.Certificate);
            Assert.Equal("CA", creds.CaCertificate);
        }

        [Fact]
        public void Incomplete_credentials_are_not_reported_complete()
        {
            var c = Client(_ => "{\"id\":\"client-1\",\"certificate\":\"CERT\"}");

            Assert.False(c.CompleteAuth("t").IsComplete);
        }

        [Fact]
        public void Library_maps_a_bare_array_which_is_what_the_endpoint_returns()
        {
            // user/library.get.ts returns `library.entries.map(e => e.game)` — a plain
            // array. Unlike GameVault there is no pagination envelope to unwrap.
            var c = Client(_ => @"[
                {""id"":""g1"",""mName"":""Prey"",""mDescription"":""2006 shooter"",""mCoverObjectId"":""obj1""},
                {""id"":""g2"",""mName"":""Quake II""}
            ]");

            var games = c.Library("auth");

            Assert.Equal("/api/v1/client/user/library", _paths.Single());
            Assert.Equal(2, games.Count);
            Assert.Equal("Prey", games[0].Name);
            Assert.Equal("2006 shooter", games[0].Description);
            Assert.Equal("obj1", games[0].CoverObjectId);
        }

        [Fact]
        public void Library_reads_mDescription_and_falls_back_to_description()
        {
            // The field was renamed upstream in August 2026. A build reading only the
            // old name imports every game with a blank description and no error, which
            // is the failure mode that cost a day on the first Drop deployment.
            var c = Client(_ => @"[{""id"":""g1"",""name"":""Old Shape"",""description"":""legacy field""}]");

            var games = c.Library("auth");

            Assert.Equal("Old Shape", games[0].Name);
            Assert.Equal("legacy field", games[0].Description);
        }

        [Fact]
        public void Library_skips_entries_with_no_id()
        {
            var c = Client(_ => @"[{""mName"":""Nameless""},{""id"":""g1"",""mName"":""Real""}]");

            var games = c.Library("auth");

            Assert.Single(games);
            Assert.Equal("g1", games[0].Id);
        }

        [Fact]
        public void An_empty_library_is_not_an_error()
        {
            Assert.Empty(Client(_ => "[]").Library("auth"));
            Assert.Empty(Client(_ => "").Library("auth"));
        }

        [Fact]
        public void Versions_are_parsed_with_their_index()
        {
            var c = Client(_ => @"[
                {""versionName"":""1.2"",""versionIndex"":2,""platform"":""Windows""},
                {""versionName"":""1.0"",""versionIndex"":0,""platform"":""Windows""}
            ]");

            var versions = c.Versions("auth", "g1");

            Assert.Equal(2, versions.Count);
            Assert.Equal("1.2", versions[0].Name);
            Assert.Equal(2, versions[0].Index);
            Assert.Contains("id=g1", _paths.Single());
        }

        [Fact]
        public void ObjectUrl_builds_an_absolute_art_url_and_ignores_a_trailing_slash()
        {
            var c = Client(_ => "{}");

            Assert.Equal("https://drop.example.com/api/v1/client/object/obj1", c.ObjectUrl("obj1"));
            Assert.Null(c.ObjectUrl(null));
            Assert.Null(c.ObjectUrl("  "));
        }

        [Fact]
        public void Game_ids_are_url_escaped()
        {
            var c = Client(_ => "{}");

            c.Game("auth", "weird id/with slash");

            Assert.DoesNotContain(" ", _paths.Single());
            Assert.Contains("weird%20id%2Fwith%20slash", _paths.Single());
        }
    }
}
