namespace SeliseBlocks.LMT.Client
{
    public class TraceData
    {
        public DateTime Timestamp { get; set; }
        public string TraceId { get; set; } = string.Empty;
        public string SpanId { get; set; } = string.Empty;
        public string ParentSpanId { get; set; } = string.Empty;
        public string ParentId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string ActivitySourceName { get; set; } = string.Empty;
        public string OperationName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double Duration { get; set; }
        public Dictionary<string, object?> Attributes { get; set; } = new();
        public string Status { get; set; } = string.Empty;
        public string StatusDescription { get; set; } = string.Empty;
        public Dictionary<string, string> Baggage { get; set; } = new();
        public string ServiceName { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
    }

    public class FailedTraceBatch
    {
        public Dictionary<string, List<TraceData>> TenantBatches { get; set; } = new();
        public int RetryCount { get; set; }
        public DateTime NextRetryTime { get; set; }
    }
}
