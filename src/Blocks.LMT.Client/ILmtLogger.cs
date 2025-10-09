namespace Blocks.LMT.Client
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

    public interface ILmtLogger
    {
        void Log(LmtLogLevel level, string message, Exception exception = null, Dictionary<string, object> properties = null);
        void LogTrace(string message, Dictionary<string, object> properties = null);
        void LogDebug(string message, Dictionary<string, object> properties = null);
        void LogInformation(string message, Dictionary<string, object> properties = null);
        void LogWarning(string message, Dictionary<string, object> properties = null);
        void LogError(string message, Exception exception = null, Dictionary<string, object> properties = null);
        void LogCritical(string message, Exception exception = null, Dictionary<string, object> properties = null);
    }
}