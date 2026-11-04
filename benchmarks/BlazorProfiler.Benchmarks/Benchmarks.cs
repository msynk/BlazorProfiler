using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BlazorProfiler;
using BlazorProfiler.Benchmarks.Components;
using BlazorProfiler.Events;
using BlazorProfiler.Export;
using BlazorProfiler.Pipeline;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

BenchmarkSwitcher.FromAssembly(typeof(BlazorProfiler.Benchmarks.EmitBenchmarks).Assembly).Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));

namespace BlazorProfiler.Benchmarks
{
    /// <summary>Cost of submitting events on the instrumented thread.</summary>
    [MemoryDiagnoser]
    public class EmitBenchmarks
    {
        private ProfilerHost _idle = default!;
        private ProfilerHost _async = default!;
        private ProfilerHost _sync = default!;

        [GlobalSetup]
        public void Setup()
        {
            _idle = new ProfilerHost(new ProfilerOptions { Enabled = true, AutoStartSession = false });
            _async = Create(synchronous: false);
            _sync = Create(synchronous: true);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _idle.Dispose();
            _async.Dispose();
            _sync.Dispose();
        }

        /// <summary>No session recording: collectors bail out before allocating.</summary>
        [Benchmark(Baseline = true)]
        public bool IdleProfiler() => _idle.Emit(NewEvent());

        /// <summary>Recording: correlation, sampling, and enqueue on the emitting thread; processing runs in the background.</summary>
        [Benchmark]
        public bool RecordingAsyncPipeline() => _async.Emit(NewEvent());

        /// <summary>Recording with inline processing: the full pipeline cost per event (filter, redaction, store, aggregates).</summary>
        [Benchmark]
        public bool RecordingFullPipeline() => _sync.Emit(NewEvent());

        private static ParametersSetEvent NewEvent() => new() { ComponentType = "App.Row", Duration = 120 };

        private static ProfilerHost Create(bool synchronous)
        {
            var options = new ProfilerOptions { Enabled = true, AutoStartSession = true };
            options.Pipeline.SynchronousProcessing = synchronous;
            options.Pipeline.QueueCapacity = 1 << 20;
            var host = new ProfilerHost(options);
            host.Start();
            return host;
        }
    }

    /// <summary>Bounded storage under sustained load (at capacity, every append evicts).</summary>
    [MemoryDiagnoser]
    public class StoreBenchmarks
    {
        private BoundedEventStore _store = default!;
        private ProfilerEvent[] _events = default!;
        private int _index;

        [GlobalSetup]
        public void Setup()
        {
            _store = new BoundedEventStore(10_000, long.MaxValue, long.MaxValue);
            _events = Enumerable.Range(0, 20_000).Select(i => (ProfilerEvent)new HttpRequestEvent { Url = "https://x.test/a/" + i, OperationId = i % 50, Timestamp = i }).ToArray();
            foreach (var e in _events)
            {
                _store.Append(e, 0);
            }
        }

        [Benchmark]
        public void AppendWithEviction() => _store.Append(_events[_index++ % _events.Length], 0);
    }

    /// <summary>Redaction of URLs.</summary>
    [MemoryDiagnoser]
    public class RedactionBenchmarks
    {
        private readonly DefaultRedactionPolicy _policy = new(new RedactionOptions());

        [Benchmark]
        public string SanitizeUrl() => _policy.SanitizeUrl("https://api.example.com/v1/users/12345/orders/3f2504e0-4f89-11d3-9a0c-0305e82c3301?page=2&token=secret");

        [Benchmark]
        public string EndpointKey() => _policy.GetEndpointKey("GET", "https://api.example.com/v1/users/12345/orders?page=2");
    }

    /// <summary>Diagnostics, export, and import on a 50,000-event session.</summary>
    [MemoryDiagnoser]
    public class AnalysisBenchmarks
    {
        private ProfilerHost _host = default!;
        private IProfilerSession _session = default!;
        private byte[] _trace = default!;

        [GlobalSetup]
        public void Setup()
        {
            var options = new ProfilerOptions { Enabled = true, AutoStartSession = false };
            options.Pipeline.SynchronousProcessing = true;
            _host = new ProfilerHost(options);
            _session = _host.StartSession();
            var random = new Random(42);
            long t = _host.Clock.GetTimestamp();
            for (var i = 0; i < 50_000; i++)
            {
                t += 2_000;
                var operation = i / 25;
                ProfilerEvent e = (i % 25) switch
                {
                    0 => new EventHandlerEvent { ComponentType = "App.Page" + (i % 7), MethodName = "Click", AttributeName = "onclick", OperationId = operation, Duration = random.Next(10_000, 2_000_000) },
                    1 or 2 => new HttpRequestEvent { Method = "GET", Url = $"https://api.test/items/{random.Next(100)}", OperationId = operation, Duration = random.Next(100_000, 5_000_000), StatusCode = 200 },
                    3 => new RenderBatchEvent { UpdatedComponentCount = random.Next(1, 200), OperationId = operation, Duration = random.Next(10_000, 500_000) },
                    4 => new JSInteropEvent { Identifier = "chart.plot", OperationId = operation, Duration = 5_000 },
                    _ => new ComponentRenderEvent { ComponentType = "App.Row" + (i % 40), ComponentInstanceId = i % 300, Duration = random.Next(1_000, 100_000), OutputUnchanged = i % 3 == 0, Source = InstrumentationSource.ComponentInstrumentation },
                };
                e.Timestamp = t;
                _host.Emit(e);
            }

            using var stream = new MemoryStream();
            new CompressedTraceExporter().ExportAsync(TraceDocumentBuilder.Create(_session, options), stream).GetAwaiter().GetResult();
            _trace = stream.ToArray();
        }

        [GlobalCleanup]
        public void Cleanup() => _host.Dispose();

        [Benchmark]
        public int AnalyzeAllRules() => _host.Diagnostics.Analyze(_session).Findings.Count;

        [Benchmark]
        public int Summary() => _session.GetSummary().Components.Count;

        [Benchmark]
        public long ExportCompressed()
        {
            using var stream = new MemoryStream();
            new CompressedTraceExporter().ExportAsync(TraceDocumentBuilder.Create(_session, _host.Options), stream).GetAwaiter().GetResult();
            return stream.Length;
        }

        [Benchmark]
        public int ImportCompressed()
        {
            using var stream = new MemoryStream(_trace);
            return TraceImporter.ReadAsync(stream).GetAwaiter().GetResult().Events.Count;
        }
    }

    /// <summary>
    /// Rendering overhead: re-rendering a list of 200 child components with ComponentBase, with ProfiledComponentBase while
    /// the profiler is idle, and while it records (framework metrics, activities, and instrumentation).
    /// </summary>
    [MemoryDiagnoser]
    public class RenderBenchmarks
    {
        private BenchRenderer _plain = default!;
        private BenchRenderer _profiledIdle = default!;
        private BenchRenderer _profiledRecording = default!;
        private BenchRenderer _plainRecording = default!;
        private ServiceProvider _idleServices = default!;
        private ServiceProvider _recordingServices = default!;
        private int _version;

        [Params(200)]
        public int Count { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _idleServices = Services(record: false);
            _recordingServices = Services(record: true);
            _plain = new BenchRenderer(new ServiceCollection().BuildServiceProvider(), typeof(PlainList));
            _profiledIdle = new BenchRenderer(_idleServices.CreateScope().ServiceProvider, typeof(ProfiledList));
            _profiledRecording = new BenchRenderer(_recordingServices.CreateScope().ServiceProvider, typeof(ProfiledList));
            _plainRecording = new BenchRenderer(_recordingServices.CreateScope().ServiceProvider, typeof(PlainList));
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _idleServices.Dispose();
            _recordingServices.Dispose();
        }

        [Benchmark(Baseline = true)]
        public Task ComponentBase_NoProfiler() => _plain.Render(Count, ++_version);

        [Benchmark]
        public Task ComponentBase_ProfilerRecording() => _plainRecording.Render(Count, ++_version);

        [Benchmark]
        public Task ProfiledComponentBase_ProfilerIdle() => _profiledIdle.Render(Count, ++_version);

        [Benchmark]
        public Task ProfiledComponentBase_ProfilerRecording() => _profiledRecording.Render(Count, ++_version);

        private static ServiceProvider Services(bool record)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddBlazorProfiler(o =>
            {
                o.Enabled = true;
                o.AutoStartSession = record;
                o.Collectors.Runtime = false;
                o.Retention.MaxEvents = 200_000;
            });
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IProfiler>();
            return provider;
        }

        private sealed class BenchRenderer : Renderer
        {
            private readonly Type _root;
            private int _rootId = -1;

            public BenchRenderer(IServiceProvider services, Type root)
                : base(services, NullLoggerFactory.Instance)
            {
                _root = root;
            }

            public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

            public Task Render(int count, int version) => Dispatcher.InvokeAsync(() =>
            {
                if (_rootId < 0)
                {
                    _rootId = AssignRootComponentId(InstantiateComponent(_root));
                }

                return RenderRootComponentAsync(_rootId, ParameterView.FromDictionary(new Dictionary<string, object?> { ["Count"] = count, ["Version"] = version }));
            });

            protected override void HandleException(Exception exception) => throw exception;

            protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        }
    }
}
