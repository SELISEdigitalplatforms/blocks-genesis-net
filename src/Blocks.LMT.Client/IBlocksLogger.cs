namespace SeliseBlocks.LMT.Client
{
    public enum LmtLogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Critical = 5
    }

    public interface IBlocksLogger
    {
        void Log(LmtLogLevel level, string message, Exception exception = null, params object?[] args);
        void LogTrace(string message, params object?[] args );
        void LogDebug(string message, params object?[] args);
        void LogInformation(string message, params object?[] args);
        void LogWarning(string message, params object?[] args);
        void LogError(string messageTemplate, Exception? exception = null, params object?[] args);
        void LogCritical(string message, Exception exception = null, params object?[] args);
    }
}