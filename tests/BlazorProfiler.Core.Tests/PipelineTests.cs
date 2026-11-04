using BlazorProfiler.Analysis;
using BlazorProfiler.Api;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;
using BlazorProfiler.Pipeline;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Core.Tests;

public class ClockTests
{
    [Fact]
    public void ConvertsBetweenTicksAndWallClock()
    {
        var clock = new FakeClock();
        Assert.Equal(1.5, clock.ToMilliseconds(1500));
        Assert.Equal(2500, clock.FromMilliseconds(2.5));
        var wall = clock.ToDateTimeOffset(2_000_000);
        Assert.Equal(clock.AnchorTime.AddSeconds(2), wall);
        Assert.Equal(2_000_000, clock.FromDateTimeOffset(wall));
    }

    [Fact]
    public void StopwatchClockIsMonotonic()
    {
        var clock = new StopwatchProfilerClock();
        var a = clock.GetTimestamp();
        var b = clock.GetTimestamp();
        Assert.True(b >= a);
        Assert.True(Math.Abs((clock.ToDateTimeOffset(clock.GetTimestamp()) - DateTimeOffset.UtcNow).TotalSeconds) < 5);
    }
}

public class RedactionTests
{
    private readonly DefaultRedactionPolicy _policy = new(new RedactionOptions());

    [Theory]
    [InlineData("https://user:pass@API.example.com/orders?page=2#frag", "https://api.example.com/orders?page=[redacted]")]
    [InlineData("https://x.test/a?token=abc&sort=asc", "https://x.test/a?token=[redacted]&sort=[redacted]")]
    [InlineData("/api/users/12345/orders/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "/api/users/{id}/orders/{guid}")]
    [InlineData("/files/eyJhbGciOiJIUzI1NiJ9abcdefghijklmnop123", "/files/{token}")]
    [InlineData("/users/someone@example.com", "/users/{email}")]
    [InlineData("/products/shoes", "/products/shoes")]
    [InlineData("", "")]
    public void SanitizesUrls(string input, string expected) => Assert.Equal(expected, _policy.SanitizeUrl(input));

    [Fact]
    public void AllowedQueryParametersAreKeptButSensitiveOnesNeverAre()
    {
        var options = new RedactionOptions();
        options.AllowedQueryParameters.Add("page");
        options.AllowedQueryParameters.Add("api_key");
        var policy = new DefaultRedactionPolicy(options);
        Assert.Equal("/a?page=2&api_key=[redacted]", policy.SanitizeUrl("/a?page=2&api_key=s3cr3t"));
    }

    [Fact]
    public void EndpointKeyGroupsByTemplateAndIgnoresQuery()
    {
        var a = _policy.GetEndpointKey("get", "https://x.test/api/items/1?x=1");
        var b = _policy.GetEndpointKey("GET", "https://x.test/api/items/2?x=2");
        Assert.Equal("GET https://x.test/api/items/{id}", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RedactsConfiguredInteropIdentifiers()
    {
        var options = new RedactionOptions();
        options.RedactedJSIdentifierPrefixes.Add("secret.");
        var policy = new DefaultRedactionPolicy(options);
        Assert.Equal(DefaultRedactionPolicy.RedactedValue, policy.SanitizeIdentifier("secret.fn"));
        Assert.Equal("public.fn", policy.SanitizeIdentifier("public.fn"));
    }

    [Fact]
    public void HostRedactsUrlsBeforeStorageAndKeepsHash()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(new HttpRequestEvent { Method = "GET", Url = "https://x.test/api/a?secret=1", Timestamp = Ev.T(0) });
        host.Emit(new HttpRequestEvent { Method = "GET", Url = "https://x.test/api/a?secret=2", Timestamp = Ev.T(1) });
        var events = session.GetEvents(new EventQuery()).OfType<HttpRequestEvent>().ToList();
        Assert.All(events, e => Assert.DoesNotContain("secret=1", e.Url, StringComparison.Ordinal));
        Assert.All(events, e => Assert.DoesNotContain("secret=2", e.Url, StringComparison.Ordinal));
        Assert.NotEqual(events[0].UrlHash, events[1].UrlHash);
        Assert.Equal(events[0].Endpoint, events[1].Endpoint);
    }
}

public class SamplingAndFilterTests
{
    [Fact]
    public void SamplesAtConfiguredRateWithWeights()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Sampling.Rates[ProfilerEventKind.ParametersSet] = 0.25);
        for (var i = 0; i < 1000; i++)
        {
            host.Emit(Ev.Parameters("A", i));
        }

        var stored = session.GetEvents(new EventQuery()).OfType<ParametersSetEvent>().ToList();
        Assert.Equal(250, stored.Count);
        Assert.All(stored, e => Assert.Equal(4, e.SampleWeight));
        Assert.Equal(1000, session.GetSummary().Components.Single().ParametersSets.Count);
        Assert.True(session.GetSummary().ContainsSampledData);
        Assert.Equal(750, host.GetHealth().SampledOut);
    }

    [Fact]
    public void NeverSamplesOperationsOrErrors()
    {
        var (host, session, _) = TestHost.WithSession(o =>
        {
            o.Sampling.Rates[ProfilerEventKind.EventHandler] = 0;
            o.Sampling.Rates[ProfilerEventKind.ParametersSet] = 0;
        });
        host.Emit(Ev.Handler("A", "Click", 0, 1, 1));
        var failed = Ev.Parameters("A", 1);
        failed.Outcome = ProfilerOutcome.Error;
        host.Emit(failed);
        host.Emit(Ev.Parameters("A", 2));
        Assert.Equal(2, session.GetStatistics().AcceptedEvents);
    }

    [Fact]
    public void ExcludesProfilerComponentsAndConfiguredUrls()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Filters.ExcludedUrlFragments.Add("/health"));
        host.Emit(Ev.Parameters("BlazorProfiler.Browser.BlazorProfilerClient", 0));
        host.Emit(Ev.Http("https://x.test/health", 0, 1));
        host.Emit(Ev.Parameters("App.Counter", 0));
        Assert.Equal(1, session.GetStatistics().AcceptedEvents);
        Assert.Equal(2, host.GetHealth().Filtered);
    }

    [Fact]
    public void CustomPredicateCanDropEvents()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Filters.Predicate = e => e.Kind != ProfilerEventKind.JSInterop);
        host.Emit(Ev.Interop("x", 0));
        host.Emit(Ev.Parameters("A", 0));
        Assert.Equal(1, session.GetStatistics().AcceptedEvents);
    }
}

public class StoreTests
{
    [Fact]
    public void EnforcesMaxEvents()
    {
        var store = new BoundedEventStore(100, long.MaxValue, long.MaxValue);
        for (var i = 0; i < 1000; i++)
        {
            store.Append(Ev.Parameters("A", i), 0);
        }

        Assert.Equal(100, store.Count);
        Assert.Equal(900, store.EvictedCount);
        Assert.Equal(1000, store.TotalAppended);
        Assert.Equal(Ev.T(900), store.ToArray()[0].Timestamp);
    }

    [Fact]
    public void EnforcesMemoryLimit()
    {
        var limit = 10_000;
        var store = new BoundedEventStore(100_000, limit, long.MaxValue);
        for (var i = 0; i < 10_000; i++)
        {
            store.Append(Ev.Http("https://x.test/" + new string('a', i % 50), i, 1), 0);
            Assert.True(store.EstimatedBytes <= limit);
        }

        Assert.True(store.Count > 0);
        Assert.Equal(store.ToArray().Sum(e => (long)e.EstimateSize()), store.EstimatedBytes);
    }

    [Fact]
    public void EvictsByAge()
    {
        var store = new BoundedEventStore(1000, long.MaxValue, Ev.D(1000));
        store.Append(Ev.Parameters("A", 0), Ev.T(0));
        store.Append(Ev.Parameters("A", 500), Ev.T(500));
        store.Append(Ev.Parameters("A", 2000), Ev.T(2000));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void CursorReadsReturnEachEventOnceAndSurviveEviction()
    {
        var store = new BoundedEventStore(10, long.MaxValue, long.MaxValue);
        for (var i = 0; i < 5; i++)
        {
            store.Append(Ev.Parameters("A", i), 0);
        }

        var first = store.GetAfter(0, 3, out var cursor);
        Assert.Equal(3, first.Count);
        Assert.Equal(3, cursor);
        var second = store.GetAfter(cursor, 100, out cursor);
        Assert.Equal(2, second.Count);
        Assert.Equal(5, cursor);

        for (var i = 0; i < 20; i++)
        {
            store.Append(Ev.Parameters("A", 100 + i), 0);
        }

        var third = store.GetAfter(cursor, 100, out cursor);
        Assert.Equal(10, third.Count);
        Assert.Equal(25, cursor);
        Assert.Empty(store.GetAfter(cursor, 100, out _));
    }

    [Fact]
    public void CursorsFromBeforeAClearReturnEveryEventAppendedAfterIt()
    {
        var store = new BoundedEventStore(1000, long.MaxValue, long.MaxValue);
        for (var i = 0; i < 10; i++)
        {
            store.Append(Ev.Parameters("Before", i), 0);
        }

        store.GetAfter(0, 100, out var cursor);
        Assert.Equal(10, cursor);
        store.Clear();

        // More events arrive after the clear than the old cursor value: none may be skipped or mistaken for old ones.
        for (var i = 0; i < 40; i++)
        {
            store.Append(Ev.Parameters("After", i), 0);
        }

        var page = store.GetAfter(cursor, 100, out var next);
        Assert.Equal(40, page.Count);
        Assert.All(page, e => Assert.Equal("After", ((ParametersSetEvent)e).ComponentType));
        Assert.Equal(store.LastSequence, next);
        Assert.True(store.ClearedSequence > cursor);
        Assert.Equal(40, store.TotalAppended);
        Assert.Empty(store.GetAfter(next, 100, out _));
    }

    [Fact]
    public void CursorAheadOfTheStoreRestartsFromTheOldestEvent()
    {
        var store = new BoundedEventStore(1000, long.MaxValue, long.MaxValue);
        for (var i = 0; i < 5; i++)
        {
            store.Append(Ev.Parameters("A", i), 0);
        }

        Assert.Equal(5, store.GetAfter(5000, 100, out var cursor).Count);
        Assert.Equal(5, cursor);
    }

    [Fact]
    public void ReplaceSwapsTheStoredInstanceAndTheOperationIndex()
    {
        var store = new BoundedEventStore(10, long.MaxValue, long.MaxValue);
        var original = Ev.Http("/a", 0, 1, operation: 3);
        store.Append(original, 0);
        store.Append(Ev.Parameters("A", 1), 0);
        var replacement = Ev.Http("/a", 0, 1, operation: 3);
        Assert.True(store.Replace(original, replacement));
        Assert.Same(replacement, store.ToArray()[0]);
        Assert.Same(replacement, Assert.Single(store.GetOperation(3)));
        Assert.False(store.Replace(original, replacement));
    }

    [Fact]
    public void OperationIndexTracksEviction()
    {
        var store = new BoundedEventStore(50, long.MaxValue, long.MaxValue);
        for (var i = 0; i < 200; i++)
        {
            store.Append(Ev.Parameters("A", i, operation: i % 2 == 0 ? 1 : 2), 0);
        }

        Assert.Equal(25, store.GetOperation(1).Count);
        Assert.Equal(25, store.GetOperation(2).Count);
        Assert.Empty(store.GetOperation(3));
        store.Clear();
        Assert.Empty(store.GetOperation(1));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void QueryFiltersByKindTimeAndComponent()
    {
        var store = new BoundedEventStore(100, long.MaxValue, long.MaxValue);
        store.Append(Ev.Parameters("A", 0), 0);
        store.Append(Ev.Parameters("B", 10), 0);
        store.Append(Ev.Http("/x", 20, 5, origin: "A"), 0);
        Assert.Equal(2, store.Query(new EventQuery { ComponentType = "A" }).Count);
        Assert.Single(store.Query(new EventQuery { Kinds = new HashSet<ProfilerEventKind> { ProfilerEventKind.HttpRequest } }));
        Assert.Equal(2, store.Query(new EventQuery { From = Ev.T(5) }).Count);
        Assert.Single(store.Query(new EventQuery { Limit = 1 }));
    }
}

public class SessionTests
{
    [Fact]
    public void SessionsDoNotLeakEventsAcrossCircuits()
    {
        var host = TestHost.Create();
        var a = host.StartSession(new ProfilerSessionOptions { CircuitId = "A" });
        var b = host.StartSession(new ProfilerSessionOptions { CircuitId = "B" });
        var all = host.StartSession();

        host.Emit(new CircuitEvent { CircuitId = "A", Change = CircuitChange.Opened });
        host.Emit(new CircuitEvent { CircuitId = "B", Change = CircuitChange.Opened });
        host.Emit(Ev.Runtime(0, 1000, 10));
        host.Emit(new MarkerEvent { Name = "process-level marker" });

        Assert.All(a.GetEvents(new EventQuery()), e => Assert.True(e.CircuitId is "A" || e is RuntimeSampleEvent));
        Assert.All(b.GetEvents(new EventQuery()), e => Assert.True(e.CircuitId is "B" || e is RuntimeSampleEvent));
        Assert.Equal(2, a.GetStatistics().AcceptedEvents);
        Assert.Equal(4, all.GetStatistics().AcceptedEvents);
    }

    [Fact]
    public void PauseResumeAndStopControlRecording()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Ev.Parameters("A", 0));
        session.Pause();
        Assert.False(host.IsRecording);
        Assert.False(host.Emit(Ev.Parameters("A", 1)));
        session.Resume();
        host.Emit(Ev.Parameters("A", 2));
        session.Stop();
        Assert.False(host.Emit(Ev.Parameters("A", 3)));

        var events = session.GetEvents(new EventQuery());
        Assert.Equal(2, events.OfType<ParametersSetEvent>().Count());
        Assert.Contains(events, e => e is MarkerEvent { MarkerType: MarkerType.SessionPaused });
        Assert.Contains(events, e => e is MarkerEvent { MarkerType: MarkerType.SessionResumed });
        Assert.Equal(ProfilerSessionState.Stopped, session.State);
        Assert.Throws<InvalidOperationException>(session.Pause);
    }

    [Fact]
    public void DisposeReleasesDataAndRemovesSession()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Ev.Parameters("A", 0));
        session.Dispose();
        Assert.Equal(ProfilerSessionState.Disposed, session.State);
        Assert.Null(host.GetSession(session.Id));
        Assert.Equal(0, session.GetStatistics().StoredEvents);
        Assert.Throws<ObjectDisposedException>(() => session.AddBookmark("x"));
    }

    [Fact]
    public void SessionLimitIsEnforced()
    {
        var host = TestHost.Create(configure: o => o.Retention.MaxSessions = 2);
        host.StartSession();
        var second = host.StartSession();
        Assert.Throws<InvalidOperationException>(() => host.StartSession());
        Assert.True(host.RemoveSession(second.Id));
        host.StartSession();
    }

    [Fact]
    public void BookmarksAreRecorded()
    {
        var (_, session, _) = TestHost.WithSession();
        var marker = session.AddBookmark("before click");
        Assert.Contains(session.GetEvents(new EventQuery()), e => ReferenceEquals(e, marker));
        Assert.Single(((ProfilerSession)session).Bookmarks);
    }

    [Fact]
    public void ClearResetsStoreAndAggregates()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Ev.Parameters("A", 0));
        session.Clear();
        Assert.Equal(0, session.GetStatistics().StoredEvents);
        Assert.Empty(session.GetSummary().Components);
    }

    [Fact]
    public void EventPagesSignalAResetAfterClear()
    {
        var (host, session, _) = TestHost.WithSession();
        var api = new ProfilerApi(host, "test");
        for (var i = 0; i < 10; i++)
        {
            host.Emit(Ev.Parameters("A", i));
        }

        var first = api.GetEvents(ProfilerApiAccess.Full, session.Id, 0, 100);
        Assert.False(first.Reset);
        Assert.Equal(10, first.Events.Count);

        session.Clear();
        var empty = api.GetEvents(ProfilerApiAccess.Full, session.Id, first.Cursor, 100);
        Assert.True(empty.Reset);
        Assert.False(empty.Gap);
        Assert.Empty(empty.Events);

        // Once the client has the post-clear cursor, later pages are ordinary increments.
        host.Emit(Ev.Parameters("B", 20));
        var next = api.GetEvents(ProfilerApiAccess.Full, session.Id, empty.Cursor, 100);
        Assert.False(next.Reset);
        Assert.Equal("B", ((ParametersSetEvent)Assert.Single(next.Events)).ComponentType);

        // A client that missed the clear entirely still learns about it.
        var stale = api.GetEvents(ProfilerApiAccess.Full, session.Id, first.Cursor, 100);
        Assert.True(stale.Reset);
        Assert.Single(stale.Events);
    }

    [Fact]
    public void SuppressionsRequireAccessToAllCircuits()
    {
        var (host, _, _) = TestHost.WithSession();
        var api = new ProfilerApi(host, "test");
        var ownCircuitsOnly = new ProfilerApiAccess { CanViewAllCircuits = false, CanControl = true, OwnedCircuits = new HashSet<string> { "c1" } };

        var suppress = Assert.Throws<ProfilerApiException>(() => api.Suppress(ownCircuitsOnly, "BP3001:*", SuppressionKind.Dismissed, null));
        Assert.Equal(403, suppress.StatusCode);
        Assert.Empty(host.Diagnostics.Suppressions.GetAll());

        api.Suppress(ProfilerApiAccess.Full, "BP3001:x", SuppressionKind.Expected, null);
        var unsuppress = Assert.Throws<ProfilerApiException>(() => api.Unsuppress(ownCircuitsOnly, "BP3001:x"));
        Assert.Equal(403, unsuppress.StatusCode);
        Assert.Single(host.Diagnostics.Suppressions.GetAll());
    }

    [Fact]
    public void DisabledProfilerRejectsSessions()
    {
        var host = new ProfilerHost(new ProfilerOptions { Enabled = false });
        Assert.False(host.IsEnabled);
        host.Start();
        Assert.Empty(host.GetSessions());
        Assert.Throws<InvalidOperationException>(() => host.StartSession());
    }
}

public class HostTests
{
    [Fact]
    public void EmitWithoutRecordingSessionIsRejected()
    {
        var host = TestHost.Create();
        Assert.False(host.IsRecording);
        Assert.False(host.Emit(Ev.Parameters("A", 0)));
        Assert.Equal(0, host.GetHealth().Emitted);
    }

    [Fact]
    public async Task QueueOverflowIsCountedAndReportedAsMarker()
    {
        using var blocker = new BlockingProcessor();
        var options = new ProfilerOptions { Enabled = true, AutoStartSession = false };
        options.Pipeline.QueueCapacity = 16;
        await using var host = new ProfilerHost(options, new FakeClock(), processors: [blocker]);
        var session = host.StartSession();

        var accepted = 0;
        for (var i = 0; i < 200; i++)
        {
            if (host.Emit(Ev.Parameters("A", i)))
            {
                accepted++;
            }
        }

        blocker.Release();
        await host.FlushAsync();
        host.Emit(Ev.Parameters("A", 1000));
        await host.FlushAsync();

        var health = host.GetHealth();
        Assert.True(health.DroppedQueueFull > 0);
        Assert.Equal(200 - accepted, health.DroppedQueueFull);
        var markers = session.GetEvents(new EventQuery()).OfType<MarkerEvent>().Where(m => m.MarkerType == MarkerType.EventsDropped).ToList();
        Assert.Equal(health.DroppedQueueFull, markers.Sum(m => m.Count));
    }

    [Fact]
    public async Task SustainedProductionStaysWithinMemoryLimits()
    {
        var options = new ProfilerOptions { Enabled = true, AutoStartSession = false };
        options.Retention.MaxEvents = 1_000;
        options.Retention.MaxMemoryBytes = 128 * 1024;
        await using var host = new ProfilerHost(options);
        var session = host.StartSession();
        for (var i = 0; i < 100_000; i++)
        {
            host.Emit(new HttpRequestEvent { Method = "GET", Url = "https://x.test/items/" + i, Timestamp = host.Clock.GetTimestamp() });
            if (i % 5_000 == 0)
            {
                await host.FlushAsync();
            }
        }

        await host.FlushAsync();
        var stats = session.GetStatistics();
        Assert.True(stats.StoredEvents <= 1_000);
        Assert.True(stats.StoredBytes <= 128 * 1024);
        Assert.True(stats.EvictedEvents > 0);
    }

    [Fact]
    public async Task ConcurrentEmittersAreAllAccountedFor()
    {
        var options = new ProfilerOptions { Enabled = true, AutoStartSession = false };
        options.Pipeline.QueueCapacity = 1_000_000;
        options.Retention.MaxEvents = 1_000_000;
        options.Retention.MaxMemoryBytes = 1L << 31;
        await using var host = new ProfilerHost(options);
        var session = host.StartSession();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                host.Emit(new ParametersSetEvent { ComponentType = "T" + t, Timestamp = host.Clock.GetTimestamp() });
            }
        })));

        await host.FlushAsync();
        var health = host.GetHealth();
        Assert.Equal(80_000, health.Emitted);
        Assert.Equal(80_000, health.Processed + health.DroppedQueueFull);
        Assert.Equal(health.Processed, session.GetStatistics().AcceptedEvents);
        var ids = session.GetEvents(new EventQuery()).Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task FailingCollectorDoesNotAffectHostAndIsReported()
    {
        var throwing = new ThrowingCollector();
        var recording = new RecordingCollector();
        await using var host = TestHost.Create(collectors: [throwing, recording]);
        var session = host.StartSession();

        Assert.Equal(CollectorState.Failed, throwing.GetStatus().State);
        Assert.Contains("boom", throwing.GetStatus().LastError, StringComparison.Ordinal);
        Assert.Equal(CollectorState.Running, recording.GetStatus().State);
        Assert.True(recording.Publish(Ev.Parameters("A", 0)));
        Assert.Equal(1, recording.GetStatus().EventsEmitted);

        session.Stop();
        Assert.Equal(1, recording.Stops);
        Assert.Equal(CollectorState.Stopped, recording.GetStatus().State);
    }

    [Fact]
    public async Task CollectorsStartWithFirstRecordingSessionAndStopWhenIdle()
    {
        var collector = new RecordingCollector();
        await using var host = TestHost.Create(collectors: [collector]);
        Assert.Equal(0, collector.Starts);
        var a = host.StartSession();
        var b = host.StartSession();
        Assert.Equal(1, collector.Starts);
        a.Stop();
        Assert.Equal(0, collector.Stops);
        b.Pause();
        Assert.Equal(1, collector.Stops);
        b.Resume();
        Assert.Equal(2, collector.Starts);
    }

    [Fact]
    public async Task CollectorsRunWhileAnySessionRecordsUnderConcurrentStateChanges()
    {
        var collector = new RecordingCollector();
        await using var host = TestHost.Create(collectors: [collector]);
        for (var i = 0; i < 300; i++)
        {
            // One session stops while another starts: the stop must never be applied after the start.
            var closing = host.StartSession();
            using var barrier = new Barrier(2);
            IProfilerSession? opened = null;
            await Task.WhenAll(
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    closing.Stop();
                }),
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    opened = host.StartSession();
                }));

            Assert.True(host.IsRecording);
            Assert.Equal(CollectorState.Running, collector.GetStatus().State);

            host.RemoveSession(closing.Id);
            host.RemoveSession(opened!.Id);
            Assert.False(host.IsRecording);
            Assert.Equal(CollectorState.Stopped, collector.GetStatus().State);
        }
    }

    [Fact]
    public async Task FlushCompletesDuringAndAfterDisposal()
    {
        var host = new ProfilerHost(new ProfilerOptions { Enabled = true, AutoStartSession = false }, new FakeClock());
        host.StartSession();
        host.Emit(Ev.Parameters("A", 0));
        var during = host.FlushAsync();
        await host.DisposeAsync();
        await during.WaitAsync(TimeSpan.FromSeconds(10));
        await host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DisposeStopsCollectorsAndSessions()
    {
        var collector = new RecordingCollector();
        var host = new ProfilerHost(new ProfilerOptions { Enabled = true }, collectors: [collector]);
        host.Start();
        var session = host.GetSessions().Single();
        await host.DisposeAsync();
        Assert.Equal(1, collector.Stops);
        Assert.Equal(ProfilerSessionState.Disposed, session.State);
        Assert.Throws<ObjectDisposedException>(() => host.StartSession());
    }

    [Fact]
    public void HealthReportsOverhead()
    {
        var (host, _, _) = TestHost.WithSession();
        for (var i = 0; i < 256; i++)
        {
            host.Emit(Ev.Parameters("A", i));
        }

        var health = host.GetHealth();
        Assert.Equal(256, health.Processed);
        Assert.True(health.ProcessingMs >= 0);
        Assert.True(health.EmitMicrosecondsPerEvent >= 0);
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        var options = new ProfilerOptions();
        options.Sampling.Rates[ProfilerEventKind.JSInterop] = 2;
        Assert.Throws<ArgumentException>(() => new ProfilerHost(options));
    }

    [Fact]
    public void ProfilesApplyDefaults()
    {
        var low = new ProfilerOptions().UseProfile(ProfilerProfile.LowOverhead);
        Assert.Equal(0.25, low.Sampling.Rates[ProfilerEventKind.ParametersSet]);
        Assert.False(low.Instrumentation.TrackRenderOutput);
        var detailed = new ProfilerOptions().UseProfile(ProfilerProfile.Detailed);
        Assert.Empty(detailed.Sampling.Rates);
        Assert.True(detailed.Instrumentation.TrackRenderOutput);
    }
}

public class CorrelationTests
{
    [Fact]
    public void AmbientOperationIsAppliedToEvents()
    {
        var (host, session, _) = TestHost.WithSession();
        var previous = ProfilerContext.Push(new OperationFrame(42, OperationKind.Interaction, CorrelationKind.ExecutionContext) { CircuitId = "c1" });
        try
        {
            host.Emit(Ev.Http("/a", 0, 1));
        }
        finally
        {
            ProfilerContext.Restore(previous);
        }

        var http = session.GetEvents(new EventQuery()).OfType<HttpRequestEvent>().Single();
        Assert.Equal(42, http.OperationId);
        Assert.Equal(CorrelationKind.ExecutionContext, http.Correlation);
        Assert.Equal("c1", http.CircuitId);
        Assert.Null(ProfilerContext.Current);
    }

    [Fact]
    public void ComponentFramesInheritOperationAndAddOrigin()
    {
        var (host, session, _) = TestHost.WithSession();
        var outer = ProfilerContext.Push(new OperationFrame(7, OperationKind.Interaction, CorrelationKind.ExecutionContext));
        var inner = ProfilerContext.Push(ProfilerContext.CreateComponentFrame("App.List", 3, LifecycleMethod.OnInitializedAsync, 0));
        host.Emit(Ev.Http("/a", 0, 1));
        host.Emit(Ev.Interop("x", 0));
        ProfilerContext.Restore(inner);
        ProfilerContext.Restore(outer);

        var http = session.GetEvents(new EventQuery()).OfType<HttpRequestEvent>().Single();
        Assert.Equal(7, http.OperationId);
        Assert.Equal("App.List", http.OriginComponentType);
        Assert.Equal(3, http.OriginComponentInstanceId);
        Assert.Equal(LifecycleMethod.OnInitializedAsync, http.OriginLifecycleMethod);
        Assert.Equal("App.List", session.GetEvents(new EventQuery()).OfType<JSInteropEvent>().Single().OriginComponentType);
    }

    [Fact]
    public async Task AmbientOperationFlowsIntoContinuationsWithoutLeaking()
    {
        var (host, session, _) = TestHost.WithSession();

        async Task HandlerAsync()
        {
            ProfilerContext.Push(new OperationFrame(99, OperationKind.Interaction, CorrelationKind.ExecutionContext));
            await Task.Yield();
            host.Emit(Ev.Http("/inside", 0, 1));
        }

        await HandlerAsync();
        host.Emit(Ev.Http("/outside", 0, 1));

        var events = session.GetEvents(new EventQuery()).OfType<HttpRequestEvent>().ToList();
        Assert.Equal(99, events.Single(e => e.Url == "/inside").OperationId);
        Assert.Equal(0, events.Single(e => e.Url == "/outside").OperationId);
    }

    [Fact]
    public void ExplicitValuesAreNotOverwritten()
    {
        var (host, session, _) = TestHost.WithSession();
        var previous = ProfilerContext.Push(new OperationFrame(1, OperationKind.Interaction, CorrelationKind.ExecutionContext));
        var e = Ev.Http("/a", 0, 1, operation: 5);
        e.Correlation = CorrelationKind.Explicit;
        host.Emit(e);
        ProfilerContext.Restore(previous);
        var stored = session.GetEvents(new EventQuery()).OfType<HttpRequestEvent>().Single();
        Assert.Equal(5, stored.OperationId);
        Assert.Equal(CorrelationKind.Explicit, stored.Correlation);
    }
}

public class BrowserMergeTests
{
    [Fact]
    public void ResourceTimingEnrichesMatchingHttpRequestInsteadOfDuplicating()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Ev.Http("https://x.test/api/data", 100, 50));
        host.Emit(new BrowserResourceEvent { Url = "https://x.test/api/data", InitiatorType = "fetch", Timestamp = Ev.T(120), Duration = Ev.D(40), TransferSize = 1234, FromCache = false });
        host.Emit(new BrowserResourceEvent { Url = "https://x.test/img.png", InitiatorType = "img", Timestamp = Ev.T(120), Duration = Ev.D(40), TransferSize = 10 });

        var events = session.GetEvents(new EventQuery());
        var http = events.OfType<HttpRequestEvent>().Single();
        Assert.Equal(1234, http.ResponseBytes);
        Assert.Equal(ValueQuality.Measured, http.ResponseBytesQuality);
        Assert.False(http.FromCache);
        Assert.Single(events.OfType<BrowserResourceEvent>());
        Assert.Equal(1234, session.GetSummary().Endpoints.Single().ResponseBytes);
    }

    [Fact]
    public void ResourceTimingReplacesTheStoredRequestAndUpdatesAggregates()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Ev.Http("https://x.test/api/data", 100, 50, operation: 7));
        var original = session.GetEvents(new EventQuery()).OfType<HttpRequestEvent>().Single();
        host.Emit(new BrowserResourceEvent { Url = "https://x.test/api/data", InitiatorType = "fetch", Timestamp = Ev.T(110), Duration = Ev.D(40), TransferSize = 0, DecodedBodySize = 500, FromCache = true });

        // Stored events are immutable (readers may be serializing them): the enriched request is a copy.
        var enriched = session.GetEvents(new EventQuery()).OfType<HttpRequestEvent>().Single();
        Assert.NotSame(original, enriched);
        Assert.Null(original.FromCache);
        Assert.True(enriched.FromCache);
        Assert.Equal(original.Id, enriched.Id);
        Assert.Same(enriched, session.GetOperationEvents(7).Single());

        var endpoint = session.GetSummary().Endpoints.Single();
        Assert.Equal(1, endpoint.CacheHits);
    }

    [Fact]
    public void ServerEventsAreNotMergedWithBrowserEntries()
    {
        var (host, session, _) = TestHost.WithSession();
        var http = Ev.Http("https://x.test/api/data", 100, 50);
        http.CircuitId = "c";
        host.Emit(http);
        host.Emit(new BrowserResourceEvent { Url = "https://x.test/api/data", InitiatorType = "fetch", Timestamp = Ev.T(110), TransferSize = 5 });
        Assert.Single(session.GetEvents(new EventQuery()).OfType<BrowserResourceEvent>());
    }
}

public class AggregateTests
{
    [Fact]
    public void SummarizesComponentsHandlersEndpointsAndTimeSeries()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(new ComponentCreatedEvent { ComponentType = "A", ComponentInstanceId = 1, Timestamp = Ev.T(0) });
        host.Emit(Ev.Render("A", 1, 2, instance: 1, unchanged: true));
        host.Emit(Ev.Render("A", 2, 4, instance: 1, unchanged: false));
        host.Emit(Ev.Parameters("A", 3, 1, ParameterChangeState.ChangedByReferenceOnly, InstrumentationSource.ComponentInstrumentation, ["OnClick"]));
        host.Emit(Ev.Handler("A", "Click", 10, 5, 1));
        host.Emit(Ev.Batch(16, 3, 4, 1));
        host.Emit(Ev.Http("https://x.test/a/1", 20, 10, status: 500));
        host.Emit(Ev.Http("https://x.test/a/2", 1500, 30));
        host.Emit(new ComponentChildrenEvent { ComponentType = "A", ComponentInstanceId = 1, ChildInstanceIds = [2, 3], ChildTypes = ["B", "B"], Timestamp = Ev.T(30) });

        var summary = session.GetSummary();
        var a = summary.Components.Single(c => c.ComponentType == "A");
        Assert.True(a.Instrumented);
        Assert.Equal(1, a.Created);
        Assert.Equal(2, a.Renders.Count);
        Assert.Equal(6, a.Renders.TotalMs, 3);
        Assert.Equal(1, a.RendersWithUnchangedOutput);
        Assert.Equal(1, a.ParametersChangedByReferenceOnly);
        Assert.Equal(1, a.ReferenceOnlyParameters["OnClick"]);
        Assert.Single(summary.Handlers);
        var endpoint = summary.Endpoints.Single();
        Assert.Equal("GET https://x.test/a/{id}", endpoint.Endpoint);
        Assert.Equal(2, endpoint.Durations.Count);
        Assert.Equal(1, endpoint.Failures);
        Assert.Equal(4, summary.RenderBatches.MaxUpdatedComponents);
        Assert.Equal(3, summary.Instances.Count);
        Assert.Equal(1, summary.Instances.Single(i => i.InstanceId == 2).ParentInstanceId);
        Assert.Contains(summary.TimeSeries, p => p.HttpRequests == 1 && p.Second == 1);
    }

    [Fact]
    public void FullInstanceTableEvictsDisposedInstancesAndOtherwiseStopsTracking()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Retention.MaxTrackedInstances = 16);
        for (var i = 1; i <= 17; i++)
        {
            host.Emit(new ComponentCreatedEvent { ComponentType = "A", ComponentInstanceId = i, Timestamp = Ev.T(i) });
        }

        var ids = session.GetSummary().Instances.Select(x => x.InstanceId).ToList();
        Assert.Equal(16, ids.Count);
        Assert.DoesNotContain(17, ids);

        host.Emit(new ComponentDisposedEvent { ComponentType = "A", ComponentInstanceId = 3, Timestamp = Ev.T(20) });
        host.Emit(new ComponentCreatedEvent { ComponentType = "A", ComponentInstanceId = 18, Timestamp = Ev.T(21) });
        ids = session.GetSummary().Instances.Select(x => x.InstanceId).ToList();
        Assert.Equal(16, ids.Count);
        Assert.Contains(18, ids);
        Assert.DoesNotContain(3, ids);
    }

    [Fact]
    public void DurationStatsEstimatesPercentiles()
    {
        var stats = new DurationStats();
        for (var i = 1; i <= 100; i++)
        {
            stats.Add(i);
        }

        Assert.Equal(100, stats.Count);
        Assert.Equal(100, stats.MaxTicks);
        Assert.InRange(stats.Percentile(50), 40, 60);
        Assert.InRange(stats.Percentile(95), 90, 100);
    }

    [Fact]
    public void AggregationKeysAreBounded()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Retention.MaxAggregateKeys = 16);
        for (var i = 0; i < 100; i++)
        {
            host.Emit(Ev.Parameters("Type" + i, i));
        }

        var components = session.GetSummary().Components;
        Assert.Equal(17, components.Count);
        Assert.Contains(components, c => c.ComponentType == SessionAggregates.OverflowKey);
    }
}
