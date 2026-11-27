using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using SeliseBlocks.LMT.Client;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis;

public class MongoDBTraceExporter : BaseProcessor<Activity>
{
    private readonly string _serviceName;
    private readonly ConcurrentQueue<TraceData> _batch;
    private readonly Timer _timer;
    private readonly IMongoDatabase? _database;
    private readonly int _batchSize;
    private readonly LmtServiceBusSender? _serviceBusSender;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private bool _disposed;
    private int _disposeState;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    public MongoDBTraceExporter(
        string serviceName,
        int batchSize = 1000,
        IBlocksSecret? blocksSecret = null)
    {
        _serviceName = serviceName;
        _batchSize = batchSize;

        var interval = TimeSpan.FromSeconds(3);
        _batch = new ConcurrentQueue<TraceData>();

        var connectionString = blocksSecret?.TraceConnectionString ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _database = LmtConfiguration.GetMongoDatabase(connectionString, LmtConfiguration.TraceDatabaseName);
        }

        _serviceBusSender = TryCreateTracesSender(serviceName);

        _timer = new Timer(_ => TrackedFlush(), null, interval, interval);
    }

    private bool IsDisposing => Volatile.Read(ref _disposeState) != 0;

    // Runs a flush that Dispose can wait for. A flush that starts after Dispose has begun does nothing, and a flush that loses the
    // race with the semaphore being disposed is swallowed instead of becoming an unhandled exception on a thread-pool thread.
    private void TrackedFlush()
    {
        if (IsDisposing) return;
        var task = Task.Run(async () =>
        {
            try { await FlushBatchAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        });
        _inFlight?.TryAdd(task, 0);
        _ = task.ContinueWith(t => _inFlight?.TryRemove(t, out _), TaskScheduler.Default);
    }

    private static LmtServiceBusSender? TryCreateTracesSender(string serviceName)
    {
        var connectionString = LmtConfigurationProvider.GetServiceBusConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var maxRetries = LmtConfigurationProvider.GetMaxRetries();
        var maxFailedBatches = LmtConfigurationProvider.GetMaxFailedBatches();

        return new LmtServiceBusSender(serviceName, connectionString, maxRetries, maxFailedBatches);
    }

    public override void OnEnd(Activity data)
    {
        if (IsDisposing) return;

        var endTime = data.StartTimeUtc.Add(data.Duration);
        var tenantId = Baggage.GetBaggage("TenantId") ?? BlocksConstants.Miscellaneous;
        tenantId = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : BlocksConstants.Miscellaneous;

        var traceData = new TraceData
        {
            Timestamp = endTime,
            TraceId = data.TraceId.ToString(),
            SpanId = data.SpanId.ToString(),
            ParentSpanId = data.ParentSpanId.ToString(),
            ParentId = data.ParentId?.ToString() ?? string.Empty,
            Kind = data.Kind.ToString(),
            ActivitySourceName = data.Source.Name,
            OperationName = data.DisplayName,
            StartTime = data.StartTimeUtc,
            EndTime = endTime,
            Duration = data.Duration.TotalMilliseconds,
            Attributes = data.TagObjects?.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
            ) ?? [],
            Status = data.Status.ToString(),
            StatusDescription = data.StatusDescription ?? string.Empty,
            Baggage = GetBaggageItems(),
            ServiceName = _serviceName,
            TenantId = tenantId
        };

        _batch.Enqueue(traceData);

        if (_batch.Count >= _batchSize)
        {
            TrackedFlush();
        }
    }

    private static Dictionary<string, string> GetBaggageItems()
    {
        var baggage = new Dictionary<string, string>();
        foreach (var baggageItem in Baggage.Current)
        {
            baggage[baggageItem.Key] = baggageItem.Value;
        }
        return baggage;
    }

    private Task FlushBatchAsync() => IsDisposing ? Task.CompletedTask : FlushCoreAsync();

    private async Task FlushCoreAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var tenantBatches = new Dictionary<string, List<TraceData>>();

            while (_batch.TryDequeue(out var traceData))
            {
                if (!tenantBatches.ContainsKey(traceData.TenantId))
                {
                    tenantBatches[traceData.TenantId] = [];
                }
                tenantBatches[traceData.TenantId].Add(traceData);
            }

            if (tenantBatches.Count == 0)
                return;

            if (_serviceBusSender != null)
            {
                await _serviceBusSender.SendTracesAsync(tenantBatches).ConfigureAwait(false);
                return;
            }

            if (_database != null)
            {
                await SaveToMongoDBAsync(tenantBatches).ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveToMongoDBAsync(Dictionary<string, List<TraceData>> tenantBatches)
    {
        foreach (var tenantBatch in tenantBatches)
        {
            var collection = _database!.GetCollection<BsonDocument>(tenantBatch.Key);

            try
            {
                var bsonDocuments = tenantBatch.Value.Select(ConvertToBsonDocument).ToList();
                await collection.InsertManyAsync(bsonDocuments).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to insert trace batch for tenant '{TenantId}'.", tenantBatch.Key);
            }
        }
    }

    private static BsonDocument ConvertToBsonDocument(TraceData traceData)
    {
        return new BsonDocument
        {
            { "Timestamp", traceData.Timestamp },
            { "TraceId", traceData.TraceId },
            { "SpanId", traceData.SpanId },
            { "ParentSpanId", traceData.ParentSpanId },
            { "ParentId", traceData.ParentId },
            { "Kind", traceData.Kind },
            { "ActivitySourceName", traceData.ActivitySourceName },
            { "OperationName", traceData.OperationName },
            { "StartTime", traceData.StartTime },
            { "EndTime", traceData.EndTime },
            { "Duration", traceData.Duration },
            {
                "Attributes",
                new BsonDocument(
                    traceData.Attributes.ToDictionary(
                        kvp => kvp.Key,
                        kvp => BsonValue.Create(kvp.Value)
                    )
                )
            },
            { "Status", traceData.Status },
            { "StatusDescription", traceData.StatusDescription },
            { "Baggage", new BsonDocument(traceData.Baggage) },
            { "ServiceName", traceData.ServiceName },
            { "TenantId", traceData.TenantId }
        };
    }

    protected override void Dispose(bool disposing)
    {
        // The flag goes first, so a second Dispose, a timer tick and OnEnd all see it at once.
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        if (disposing)
        {
            // Dispose the timer and wait for a callback that is running, then wait for the flushes still in flight.
            using (var timerDone = new ManualResetEvent(false))
            {
                if (_timer.Dispose(timerDone))
                    timerDone.WaitOne(TimeSpan.FromSeconds(10));
            }

            try { Task.WaitAll(_inFlight?.Keys.ToArray() ?? [], TimeSpan.FromSeconds(10)); }
            catch (AggregateException) { }

            // The final flush still runs, before the semaphore goes.
            FlushCoreAsync().GetAwaiter().GetResult();
            _semaphore.Dispose();
            _serviceBusSender?.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
