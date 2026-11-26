namespace XUnitTest.Ai;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that needs a real model endpoint of one provider style. It is skipped, not failed,
/// when the <c>TEST_MODEL_*</c> variables do not describe an endpoint of that style, so a CI run without an endpoint stays green.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute(string style)
    {
        if (!TryGetEndpoint(style, out _))
        {
            Skip = $"No real '{style}' model endpoint is configured: set TEST_MODEL_STYLE={style}, TEST_MODEL_ENDPOINT, TEST_MODEL_KEY and TEST_MODEL_NAME to run it.";
        }
    }

    /// <summary>Reads the endpoint settings of the given style from the environment; false when any is missing or the style differs.</summary>
    public static bool TryGetEndpoint(string style, out (string Endpoint, string Key, string Name, string? ApiVersion) endpoint)
    {
        var actualStyle = Environment.GetEnvironmentVariable("TEST_MODEL_STYLE");
        var url = Environment.GetEnvironmentVariable("TEST_MODEL_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("TEST_MODEL_KEY");
        var name = Environment.GetEnvironmentVariable("TEST_MODEL_NAME");
        if (!string.Equals(actualStyle, style, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
        {
            endpoint = default;
            return false;
        }

        endpoint = (url.TrimEnd('/'), key, name, Environment.GetEnvironmentVariable("TEST_MODEL_API_VERSION"));
        return true;
    }
}
