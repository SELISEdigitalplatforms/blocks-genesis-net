using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace XUnitTest.Logging;

/// <summary>
/// Contract tests for the internal static log classes. Every public static
/// method is invoked once with an enabled logger, asserting that exactly one
/// record is emitted and that it carries the event id and level declared on
/// the <see cref="LoggerMessageAttribute"/> when one is present, and once with
/// <see cref="NullLogger"/> to cover the IsEnabled-false path.
/// </summary>
public class GeneratedLogContractTests
{
    [Theory]
    [InlineData("Blocks.Genesis.AzureMessageWorkerLog, Blocks.Genesis")]
    [InlineData("Blocks.Genesis.HttpServiceLog, Blocks.Genesis")]
    [InlineData("Blocks.Genesis.ConfigureAzureServiceBusLog, Blocks.Genesis")]
    [InlineData("Blocks.Genesis.Health.GenesisHealthPingLog, Blocks.Genesis")]
    [InlineData("SeliseBlocks.LMT.Client.LmtServiceBusSenderLog, Blocks.LMT.Client")]
    public void LogMethods_ShouldEmitDeclaredEventIdAndLevel_AndSkipWhenDisabled(string typeName)
    {
        var logType = Type.GetType(typeName);
        Assert.NotNull(logType);

        var methods = logType!
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().FirstOrDefault()?.ParameterType == typeof(ILogger))
            .ToList();
        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            var recorder = new RecordingLogger();
            method.Invoke(null, BuildArguments(method, recorder));

            var record = Assert.Single(recorder.Records);
            var declared = method.GetCustomAttribute<LoggerMessageAttribute>();
            if (declared is not null)
            {
                Assert.Equal(declared.EventId, record.EventId.Id);
                Assert.Equal(declared.Level, record.Level);
            }

            // Disabled logger: the generated guard must skip formatting entirely.
            method.Invoke(null, BuildArguments(method, NullLogger.Instance));
        }
    }

    private static object?[] BuildArguments(MethodInfo method, ILogger logger)
    {
        return [.. method.GetParameters().Select(p => SynthesizeArgument(p.ParameterType, logger))];
    }

    private static object? SynthesizeArgument(Type type, ILogger logger)
    {
        if (type == typeof(ILogger)) return logger;
        if (type == typeof(string)) return "value";
        if (type == typeof(int)) return 1;
        if (type == typeof(long)) return 2L;
        if (type == typeof(double)) return 1.5d;
        if (type == typeof(bool)) return true;
        if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(1);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.UtcNow;
        if (typeof(Exception).IsAssignableFrom(type)) return new InvalidOperationException("boom");
        throw new NotSupportedException($"No synthesized value for parameter type {type}");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, eventId, formatter(state, exception)));
        }
    }
}
