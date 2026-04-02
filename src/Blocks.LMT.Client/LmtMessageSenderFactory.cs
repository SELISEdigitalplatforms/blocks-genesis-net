using System.Collections.Concurrent;

namespace SeliseBlocks.LMT.Client
{
    public static class LmtMessageSenderFactory
    {
        private static readonly Lock SyncRoot = new();
        private static readonly ConcurrentDictionary<string, SharedSenderRegistration> SharedSenders = new();

        public static ILmtMessageSender Create(LmtOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (LmtTransportHelper.IsRabbitMq(options.ConnectionString))
            {
                return new LmtRabbitMqSender(
                    options.ServiceId,
                    options.ConnectionString,
                    options.MaxRetries,
                    options.MaxFailedBatches);
            }

            return new LmtServiceBusSender(
                options.ServiceId,
                options.ConnectionString,
                options.MaxRetries,
                options.MaxFailedBatches);
        }

        public static ILmtMessageSender CreateShared(LmtOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var key = BuildSharedSenderKey(options);

            lock (SyncRoot)
            {
                if (!SharedSenders.TryGetValue(key, out var registration))
                {
                    registration = new SharedSenderRegistration(Create(options));
                    SharedSenders[key] = registration;
                }
                else
                {
                    registration.ReferenceCount++;
                }

                return new SharedLmtMessageSender(key, registration);
            }
        }

        private static string BuildSharedSenderKey(LmtOptions options)
        {
            return string.Join(
                '|',
                options.ServiceId ?? string.Empty,
                options.ConnectionString ?? string.Empty,
                options.MaxRetries,
                options.MaxFailedBatches,
                LmtTransportHelper.IsRabbitMq(options.ConnectionString) ? "rabbit" : "servicebus");
        }

        private static void Release(string key, SharedSenderRegistration registration)
        {
            lock (SyncRoot)
            {
                registration.ReferenceCount--;
                if (registration.ReferenceCount > 0)
                {
                    return;
                }

                SharedSenders.TryRemove(key, out _);
            }

            registration.Sender.Dispose();
        }

        private sealed class SharedSenderRegistration
        {
            public SharedSenderRegistration(ILmtMessageSender sender)
            {
                Sender = sender;
                ReferenceCount = 1;
            }

            public ILmtMessageSender Sender { get; }
            public int ReferenceCount { get; set; }
        }

        private sealed class SharedLmtMessageSender : ILmtMessageSender
        {
            private readonly string _key;
            private readonly SharedSenderRegistration _registration;
            private bool _disposed;

            public SharedLmtMessageSender(string key, SharedSenderRegistration registration)
            {
                _key = key;
                _registration = registration;
            }

            public Task SendLogsAsync(List<LogData> logs, int retryCount = 0)
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(SharedLmtMessageSender));
                return _registration.Sender.SendLogsAsync(logs, retryCount);
            }

            public Task SendTracesAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0)
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(SharedLmtMessageSender));
                return _registration.Sender.SendTracesAsync(tenantBatches, retryCount);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Release(_key, _registration);
            }
        }
    }
}
