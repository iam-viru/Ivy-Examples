namespace TendrilDeploy.Api;

/// <summary>Returned when a Tendril service is successfully created (<c>201 Created</c>).</summary>
public sealed class DeployResponse
{
    /// <summary>Sliplane service ID of the newly created service.</summary>
    public string ServiceId { get; init; } = "";

    /// <summary>Service name as it appears in Sliplane.</summary>
    public string ServiceName { get; init; } = "";

    /// <summary>Sliplane project ID the service belongs to.</summary>
    public string ProjectId { get; init; } = "";

    /// <summary>
    /// Public URL of the deployed Tendril instance.
    /// May be null immediately after creation — Sliplane assigns the domain during the first build.
    /// </summary>
    public string? ServiceUrl { get; init; }
}

/// <summary>Returned for all error responses (4xx / 5xx).</summary>
public sealed class ErrorResponse
{
    /// <summary>Human-readable description of what went wrong.</summary>
    public string Error { get; init; } = "";
}

/// <summary>A Sliplane server the service can be deployed to.</summary>
public sealed class ServerInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>A Sliplane project that can contain services.</summary>
public sealed class ProjectInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>Status of a deployed Tendril service.</summary>
public sealed class ServiceStatusResponse
{
    public string ServiceId { get; init; } = "";
    public string ServiceName { get; init; } = "";

    /// <summary>Current deployment status, e.g. <c>running</c>, <c>building</c>, <c>stopped</c>.</summary>
    public string Status { get; init; } = "";

    /// <summary>Public URL of the service, if already assigned.</summary>
    public string? ServiceUrl { get; init; }
}
