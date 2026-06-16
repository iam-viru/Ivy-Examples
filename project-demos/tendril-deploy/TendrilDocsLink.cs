namespace TendrilDeploy;

/// <summary>
/// Resolves the Scalar UI URL for the Tendril Deploy OpenAPI docs (opened from the app shell footer menu).
/// </summary>
internal static class TendrilDocsLink
{
    internal const string ScalarPath = "/swagger/v1";

    /// <summary>
    /// Optional full URL to the docs UI (Scalar). When set, used as-is.<br/>
    /// Otherwise builds from <c>TendrilDeploy:PublicOrigin</c> + <see cref="ScalarPath"/>, then falls back to listening URLs.
    /// </summary>
    internal static string ResolveScalarUrl(IConfiguration configuration)
    {
        var explicitUrl = configuration["TendrilDeploy:DocsUrl"]?.Trim();
        if (!string.IsNullOrEmpty(explicitUrl))
            return explicitUrl;

        var origin = configuration["TendrilDeploy:PublicOrigin"]?.Trim();
        if (!string.IsNullOrEmpty(origin))
            return origin.TrimEnd('/') + ScalarPath;

        var urls = configuration["Urls"]
                   ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
                   ?? "http://localhost:5000";
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(first))
            first = "http://localhost:5000";

        var listen = first.Replace("*", "localhost", StringComparison.Ordinal);
        return listen.TrimEnd('/') + ScalarPath;
    }
}
