using System.Text.Json;
using BlazorProfiler.Browser;
using BlazorProfiler.Events;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorProfiler.Tests;

public class DevToolsTrackTests
{
    [Fact]
    public async Task DeliversOnlyTheSubscribedCircuitsEventsAndNothingWithoutSubscribers()
    {
        await using var f = ProfilerFixture.Create();
        var processor = f.Root.GetRequiredService<DevToolsTrackProcessor>();
        Assert.False(processor.IsActive);
        f.Host.Emit(new HttpRequestEvent { Method = "GET", Url = "https://x.test/before", Timestamp = f.Host.Clock.GetTimestamp(), CircuitId = "a" });

        using var circuitA = processor.Subscribe("a");
        using var all = processor.Subscribe(null);
        Assert.True(processor.IsActive);
        f.Host.Emit(new HttpRequestEvent { Method = "GET", Url = "https://x.test/a", Timestamp = f.Host.Clock.GetTimestamp(), CircuitId = "a" });
        f.Host.Emit(new HttpRequestEvent { Method = "GET", Url = "https://x.test/b", Timestamp = f.Host.Clock.GetTimestamp(), CircuitId = "b" });
        f.Host.Emit(new RuntimeSampleEvent { Timestamp = f.Host.Clock.GetTimestamp() });
        await f.Host.FlushAsync();

        Assert.Equal(["https://x.test/a"], circuitA.Drain(100).Cast<HttpRequestEvent>().Select(e => e.Url));
        Assert.Equal(2, all.Drain(100).Count);
        Assert.Empty(all.Drain(100));
    }

    [Fact]
    public async Task SerializesTrackEntriesAndMarkersForTheBrowserClock()
    {
        await using var f = ProfilerFixture.Create();
        var clock = f.Host.Clock;
        var now = clock.GetTimestamp();
        var events = new List<ProfilerEvent>
        {
            new ComponentRenderEvent { ComponentType = "App.Pages.Counter", ComponentInstanceId = 3, Timestamp = now, Duration = clock.FromMilliseconds(12), Reason = RenderReason.EventHandler, RenderNumber = 2, OutputUnchanged = true },
            new HttpRequestEvent { Method = "GET", Url = "https://x.test/api", Endpoint = "GET /api", Timestamp = now, Duration = clock.FromMilliseconds(5), Outcome = ProfilerOutcome.Error },
            new NavigationEvent { Route = "/orders", Timestamp = now },
        };

        var json = DevToolsTrackProcessor.Serialize(events, clock, clockOffsetMs: 100);
        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);

        var render = entries[0];
        Assert.Equal("Counter", render[0].GetString());
        Assert.Equal("Components", render[1].GetString());
        Assert.Equal("primary-dark", render[4].GetString());
        Assert.Equal(12, render[3].GetDouble() - render[2].GetDouble(), 2);
        var expectedStart = (clock.ToDateTimeOffset(now) - DateTimeOffset.UnixEpoch).TotalMilliseconds - 100;
        Assert.Equal(expectedStart, render[2].GetDouble(), 1);
        Assert.Contains(render[6].EnumerateArray(), p => p[0].GetString() == "Reason" && p[1].GetString() == "EventHandler");
        Assert.Contains(render[6].EnumerateArray(), p => p[0].GetString() == "Output" && p[1].GetString()!.StartsWith("identical", StringComparison.Ordinal));

        Assert.Equal("error", entries[1][4].GetString());
        Assert.Equal(string.Empty, entries[2][1].GetString());
        Assert.Contains("/orders", entries[2][0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DropsEventsBeyondTheQueueLimit()
    {
        var processor = new DevToolsTrackProcessor();
        using var subscription = processor.Subscribe(null);
        for (var i = 0; i < DevToolsTrackProcessor.MaxQueued + 10; i++)
        {
            processor.Process(new JSInteropEvent { Identifier = "x" });
        }

        Assert.Equal(10, subscription.Dropped);
        Assert.Equal(DevToolsTrackProcessor.MaxQueued, subscription.Drain(int.MaxValue).Count);
    }
}
