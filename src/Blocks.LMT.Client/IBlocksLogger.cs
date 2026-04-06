namespace SeliseBlocks.LMT.Client
{
    /// <summary>
    /// Log level enumeration for structured logging.
    /// </summary>
    public enum LmtLogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Critical = 5
    }

    /// <summary>
    /// Interface for centralized logging with automatic batching and multi-transport support.
    /// </summary>
    public interface IBlocksLogger : IDisposable
    {
        /// <summary>
        /// Logs a message with the specified log level.
        /// </summary>
        void Log(LmtLogLevel level, string message, Exception? exception = null, params object?[] args);

        /// <summary>
        /// Logs a trace-level message.
        /// </summary>
        void LogTrace(string message, params object?[] args);

        /// <summary>
        /// Logs a debug-level message.
        /// </summary>
        void LogDebug(string message, params object?[] args);

        /// <summary>
        /// Logs an information-level message.
        /// </summary>
        void LogInformation(string message, params object?[] args);

        /// <summary>
        /// Logs a warning-level message.
        /// </summary>
        void LogWarning(string message, params object?[] args);

        /// <summary>
        /// Logs an error-level message with optional exception.
        /// </summary>
        void LogError(string messageTemplate, Exception? exception = null, params object?[] args);

        /// <summary>
        /// Logs a critical-level message with an exception.
        /// </summary>
        void LogCritical(string message, Exception? exception = null, params object?[] args);
    }
}