using HitMapAggregator.Core.Models;
using HitMapAggregator.Core.Session;

namespace HitMapAggregator.Tests;

public class ChannelSessionTests
{
    [Fact]
    public void Oldest_click_stamp_is_consumed_by_the_snapshot_that_integrates_it()
    {
        var session = new ChannelSession("c1");
        session.Start();

        session.Upsert(1UL, new NormalizedPoint(0.2f, 0.2f, 0L));
        session.BuildSnapshot();
        Assert.True(session.LastSnapshotOldestClickStamp > 0);

        session.BuildSnapshot();
        Assert.Equal(0, session.LastSnapshotOldestClickStamp);
    }

    [Fact]
    public void Upsert_overwrites_the_previous_position()
    {
        var session = new ChannelSession("c1");
        session.Start();

        session.Upsert(42UL, new NormalizedPoint(0.10f, 0.10f, 0L));
        session.Upsert(42UL, new NormalizedPoint(0.90f, 0.90f, 10L));

        Assert.Equal(1, session.PositionCount);

        HeatmapSnapshot snapshot = session.BuildSnapshot();
        Assert.Equal(1, snapshot.TotalVotes);

        Zone zone = Assert.Single(snapshot.Zones);
        Assert.Equal(0.90f, zone.Center.X, 1e-3f);
        Assert.Equal(0.90f, zone.Center.Y, 1e-3f);
    }

    [Fact]
    public void Distinct_users_each_keep_their_own_vote()
    {
        var session = new ChannelSession("c1");
        session.Start();

        for (ulong user = 0; user < 100; user++)
        {
            session.Upsert(user, new NormalizedPoint(0.5f, 0.5f, 0L));
        }

        Assert.Equal(100, session.PositionCount);
        Assert.Equal(100, session.BuildSnapshot().TotalVotes);
    }

    [Fact]
    public void Purge_removes_only_the_expired_positions()
    {
        var session = new ChannelSession("c1")
        {
            Settings = new SessionSettings { PositionTtl = TimeSpan.FromSeconds(10) },
        };
        session.Start();

        session.Upsert(1UL, new NormalizedPoint(0.1f, 0.1f, 0L));
        session.Upsert(2UL, new NormalizedPoint(0.2f, 0.2f, 5_000L));
        session.Upsert(3UL, new NormalizedPoint(0.3f, 0.3f, 9_500L));

        int removed = session.PurgeExpired(nowMs: 15_000L);

        Assert.Equal(1, removed);
        Assert.Equal(2, session.PositionCount);
    }

    [Fact]
    public void Purge_keeps_everything_when_nothing_has_expired()
    {
        var session = new ChannelSession("c1")
        {
            Settings = new SessionSettings { PositionTtl = TimeSpan.FromMinutes(5) },
        };
        session.Start();

        session.Upsert(1UL, new NormalizedPoint(0.1f, 0.1f, 1_000L));
        session.Upsert(2UL, new NormalizedPoint(0.2f, 0.2f, 2_000L));

        Assert.Equal(0, session.PurgeExpired(nowMs: 3_000L));
        Assert.Equal(2, session.PositionCount);
    }

    [Fact]
    public void Reset_empties_the_session()
    {
        var session = new ChannelSession("c1");
        session.Start();
        for (ulong user = 0; user < 50; user++)
        {
            session.Upsert(user, new NormalizedPoint(0.5f, 0.5f, 0L));
        }

        session.Reset();

        Assert.Equal(0, session.PositionCount);
        Assert.Equal(0, session.BuildSnapshot().TotalVotes);
    }

    [Fact]
    public void Anonymous_viewers_are_filtered_at_aggregation_and_the_toggle_is_reversible()
    {
        var session = new ChannelSession("c1");
        session.Start();

        for (ulong i = 0; i < 3; i++)
        {
            session.Upsert(UserKey.WithAnonymousFlag(i, isAnonymous: false), new NormalizedPoint(0.3f, 0.4f, 0L));
        }

        for (ulong i = 0; i < 2; i++)
        {
            session.Upsert(UserKey.WithAnonymousFlag(i, isAnonymous: true), new NormalizedPoint(0.7f, 0.6f, 0L));
        }

        Assert.Equal(5, session.PositionCount);

        session.Settings = session.Settings with { AllowAnonymous = true };
        Assert.Equal(5, session.BuildSnapshot().TotalVotes);

        session.Settings = session.Settings with { AllowAnonymous = false };
        Assert.Equal(3, session.BuildSnapshot().TotalVotes);

        // Rien n'a ete detruit a l'ingestion : rebasculer le reglage recupere tout.
        session.Settings = session.Settings with { AllowAnonymous = true };
        Assert.Equal(5, session.BuildSnapshot().TotalVotes);
        Assert.Equal(5, session.PositionCount);
    }

    [Fact]
    public void Anonymous_and_identified_keys_never_collide()
    {
        var session = new ChannelSession("c1");
        session.Start();

        session.Upsert(UserKey.WithAnonymousFlag(7UL, isAnonymous: false), new NormalizedPoint(0.2f, 0.2f, 0L));
        session.Upsert(UserKey.WithAnonymousFlag(7UL, isAnonymous: true), new NormalizedPoint(0.8f, 0.8f, 0L));

        Assert.Equal(2, session.PositionCount);
    }

    [Fact]
    public void A_session_starts_closed_and_ignores_clicks()
    {
        var session = new ChannelSession("c1");

        Assert.False(session.IsRunning);
        Assert.False(session.Upsert(1UL, new NormalizedPoint(0.5f, 0.5f, 0L)));
        Assert.Equal(0, session.PositionCount);
    }

    [Fact]
    public void Stopping_closes_the_collection_without_erasing_anything()
    {
        var session = new ChannelSession("c1");
        session.Start();
        session.Upsert(1UL, new NormalizedPoint(0.3f, 0.3f, 0L));
        session.Upsert(2UL, new NormalizedPoint(0.7f, 0.7f, 0L));

        session.Stop();

        // Refuse les nouveaux clics...
        Assert.False(session.Upsert(3UL, new NormalizedPoint(0.5f, 0.5f, 0L)));
        Assert.False(session.Upsert(1UL, new NormalizedPoint(0.9f, 0.9f, 0L)));

        // ...mais conserve les anciens : arreter et vider sont deux gestes distincts.
        Assert.Equal(2, session.PositionCount);
        Assert.Equal(2, session.BuildSnapshot().TotalVotes);
    }

    [Fact]
    public void Restarting_resumes_on_top_of_the_previous_votes()
    {
        var session = new ChannelSession("c1");
        session.Start();
        session.Upsert(1UL, new NormalizedPoint(0.3f, 0.3f, 0L));
        session.Stop();

        session.Start();

        Assert.True(session.Upsert(2UL, new NormalizedPoint(0.7f, 0.7f, 0L)));
        Assert.Equal(2, session.PositionCount);
    }

    [Fact]
    public void Reset_works_whether_the_session_runs_or_not()
    {
        var session = new ChannelSession("c1");
        session.Start();
        session.Upsert(1UL, new NormalizedPoint(0.3f, 0.3f, 0L));
        session.Stop();

        session.Reset();

        Assert.Equal(0, session.PositionCount);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void Registry_hands_back_the_same_session_for_a_given_channel()
    {
        var registry = new ChannelSessionRegistry();

        ChannelSession first = registry.GetOrCreate("abc");
        ChannelSession second = registry.GetOrCreate("abc");

        Assert.Same(first, second);
        Assert.NotSame(first, registry.GetOrCreate("def"));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Registry_removes_a_channel()
    {
        var registry = new ChannelSessionRegistry();
        registry.GetOrCreate("abc");

        Assert.True(registry.Remove("abc"));
        Assert.False(registry.Remove("abc"));
        Assert.False(registry.TryGet("abc", out _));
        Assert.Equal(0, registry.Count);
    }
}
