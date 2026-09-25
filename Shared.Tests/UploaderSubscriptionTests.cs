using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    public class UploaderSubscriptionTests
    {
        private static ArchiveItem Item(string id, string mediaType = "software")
        {
            return new ArchiveItem { Identifier = id, Title = id, MediaType = mediaType, SizeBytes = 1 };
        }

        private static SubscriptionRefresher Returning(params ArchiveItem[] items)
        {
            return new SubscriptionRefresher(_ => items.ToList());
        }

        [Fact]
        public void First_refresh_treats_everything_as_new_and_nothing_as_vanished()
        {
            var sub = new UploaderSubscription { Uploader = "example-uploader" };

            var r = Returning(Item("a"), Item("b")).Refresh(sub);

            Assert.Equal(2, r.New.Count);
            Assert.Empty(r.Vanished);
            Assert.Equal(2, sub.Seen.Count);
            Assert.NotNull(sub.LastChecked);
        }

        [Fact]
        public void Second_refresh_surfaces_only_what_arrived_since()
        {
            // The whole point of subscribing to an account rather than to items.
            var sub = new UploaderSubscription { Uploader = "another-uploader" };
            Returning(Item("a"), Item("b")).Refresh(sub);

            var r = Returning(Item("a"), Item("b"), Item("c")).Refresh(sub);

            Assert.Single(r.New);
            Assert.Equal("c", r.New[0].Identifier);
            Assert.Equal(3, r.All.Count);
        }

        [Fact]
        public void An_item_that_disappears_is_reported_as_vanished()
        {
            // Usually a takedown, which makes it an early warning that a local copy
            // may now be the only one.
            var sub = new UploaderSubscription { Uploader = "someone" };
            Returning(Item("kept"), Item("pulled")).Refresh(sub);

            var r = Returning(Item("kept")).Refresh(sub);

            Assert.Single(r.Vanished);
            Assert.Equal("pulled", r.Vanished[0]);
        }

        [Fact]
        public void A_failed_refresh_does_not_mark_anything_seen()
        {
            // Otherwise the next successful refresh silently swallows real new items.
            var sub = new UploaderSubscription { Uploader = "someone" };
            var refresher = new SubscriptionRefresher(_ => throw new TimeoutException("network down"));

            var r = refresher.Refresh(sub);

            Assert.True(r.Failed);
            Assert.Contains("network down", r.Error);
            Assert.Empty(sub.Seen);
            Assert.Null(sub.LastChecked);
        }

        [Fact]
        public void SoftwareOnly_filters_a_dirty_creator_query()
        {
            // The real Infidelity results: romhacks mixed with a podcast and a 1757
            // devotional text, because `creator:` is free text, not an account.
            var sub = new UploaderSubscription { Uploader = "Infidelity", ByCreator = true, SoftwareOnly = true };

            var r = new SubscriptionRefresher(_ => new List<ArchiveItem>
            {
                Item("metroid-snes"),
                Item("infidelity-records-dnb-podcast-9", "audio"),
                Item("miscellaneous-devotions-1757", "texts"),
                Item("super-mario-bros-sram", "movies"),
            }).Refresh(sub);

            Assert.Single(r.All);
            Assert.Equal("metroid-snes", r.All[0].Identifier);
        }

        [Fact]
        public void Uploader_subscriptions_do_not_filter_by_default()
        {
            var sub = new UploaderSubscription { Uploader = "someone" };

            var r = Returning(Item("a"), Item("b", "image")).Refresh(sub);

            Assert.Equal(2, r.All.Count);
        }

        [Fact]
        public void Describe_shows_which_field_is_queried()
        {
            Assert.Equal("uploader:example-uploader",
                new UploaderSubscription { Uploader = "example-uploader" }.Describe());
            Assert.Equal("creator:Infidelity",
                new UploaderSubscription { Uploader = "Infidelity", ByCreator = true }.Describe());
        }

        [Fact]
        public void Seen_matching_is_case_insensitive()
        {
            // archive.org identifiers are lowercase by convention but not by rule, and
            // a case flip must not re-announce an item as new.
            var sub = new UploaderSubscription { Uploader = "someone" };
            Returning(Item("Some-Item")).Refresh(sub);

            var r = Returning(Item("some-item")).Refresh(sub);

            Assert.Empty(r.New);
        }

        [Fact]
        public void RefreshAll_skips_disabled_subscriptions()
        {
            var subs = new[]
            {
                new UploaderSubscription { Uploader = "on" },
                new UploaderSubscription { Uploader = "off", Enabled = false },
            };

            var results = Returning(Item("x")).RefreshAll(subs);

            Assert.Single(results);
            Assert.Equal("on", results[0].Subscription.Uploader);
        }


        [Fact]
        public void A_subscription_records_what_it_produces_and_where_it_lands()
        {
            // The field the old shell script lacked. It had one DEST, so routing was
            // expressed by keeping four divergent copies of the script.
            var repacks = new UploaderSubscription
            {
                Uploader = "example-uploader",
                Produces = ArtifactKind.PcRepack,
                Destination = @"/mnt/media/games/pc",
            };
            var romhacks = new UploaderSubscription
            {
                Uploader = "Infidelity", ByCreator = true, SoftwareOnly = true,
                Produces = ArtifactKind.Rom,
                Destination = @"/mnt/media/games/0 ROMS/snes",
            };
            var portdata = new UploaderSubscription
            {
                Uploader = "another-uploader",
                Produces = ArtifactKind.GameData,
            };

            Assert.Equal(ArtifactKind.PcRepack, repacks.Produces);
            Assert.Equal(ArtifactKind.Rom, romhacks.Produces);
            Assert.Equal(ArtifactKind.GameData, portdata.Produces);

            // Three sources, three consumers, and none of them share a directory.
            Assert.NotEqual(repacks.Destination, romhacks.Destination);
            Assert.Null(portdata.Destination);   // staged into a port, not filed as a game
        }

        [Fact]
        public void PcRepack_is_the_default_so_an_unconfigured_source_behaves_as_before()
        {
            Assert.Equal(ArtifactKind.PcRepack, new UploaderSubscription { Uploader = "x" }.Produces);
        }

        [Fact]
        public void An_empty_uploader_is_reported_not_queried()
        {
            var refresher = new SubscriptionRefresher(_ => throw new InvalidOperationException("must not be called"));

            var r = refresher.Refresh(new UploaderSubscription { Uploader = "  " });

            Assert.True(r.Failed);
            Assert.Contains("no uploader", r.Error);
        }
    }
}
