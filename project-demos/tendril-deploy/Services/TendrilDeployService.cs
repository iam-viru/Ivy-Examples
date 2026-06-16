namespace TendrilDeploy.Services;

using System.Text.RegularExpressions;
using TendrilDeploy.Api;
using TendrilDeploy.Models;
using TendrilDeploy.Apps;

/// <summary>
/// Encapsulates the core Tendril → Sliplane deployment logic so it can be called
/// from both the Ivy UI wizard (<see cref="Apps.Views.TendrilDeployView"/>) and the
/// REST API (<c>POST /api/v1/tendrils</c>).
/// </summary>
public class TendrilDeployService
{
    private readonly SliplaneApiClient _client;
    private readonly GitHubDockerfilePathResolver _dockerfileResolver;

    public TendrilDeployService(
        SliplaneApiClient client,
        GitHubDockerfilePathResolver dockerfileResolver)
    {
        _client = client;
        _dockerfileResolver = dockerfileResolver;
    }

    /// <summary>
    /// Creates a Tendril service on Sliplane from a <see cref="DeployRequest"/>.
    /// Throws <see cref="ArgumentException"/> for bad input and
    /// <see cref="InvalidOperationException"/> for Sliplane API failures.
    /// </summary>
    public async Task<DeployResponse> DeployAsync(DeployRequest req, CancellationToken cancellationToken = default)
    {
        // ── 1. Build clone env vars ───────────────────────────────────────
        List<EnvironmentVariable> cloneEnvVars;
        try
        {
            cloneEnvVars = TendrilCloneBootstrap.BuildCloneEnvVars(
                req.Repos
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => new TendrilCloneBootstrap.Row(r.Trim()))
                    .ToList());
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Repos: {ex.Message}", ex);
        }

        // ── 2. Build BasicAuth secrets ────────────────────────────────────
        (string UsersValue, string HashSecretBase64, string JwtSecretBase64) basicAuth;
        try
        {
            basicAuth = TendrilBasicAuthBootstrap.BuildSecrets(
                req.BasicAuthUsername ?? "",
                req.BasicAuthPassword ?? "");
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"BasicAuth: {ex.Message}", ex);
        }

        // ── 3. Resolve Dockerfile path via GitHub ─────────────────────────
        var resolution = await _dockerfileResolver.ResolveAsync(
            req.GitRepo,
            req.Branch,
            req.DockerfilePath,
            req.DockerContext,
            cancellationToken);

        // ── 4. Assemble env vars ──────────────────────────────────────────
        var port = string.IsNullOrWhiteSpace(req.Port) ? "8000" : req.Port.Trim();
        var home = string.IsNullOrWhiteSpace(req.TendrilHome) ? "/data/tendril" : req.TendrilHome.Trim();

        var envVars = new List<EnvironmentVariable>();

        // AdditionalEnv from Dockerfile resolver (e.g. IVY_APP_DIR for monorepos)
        foreach (var e in resolution.AdditionalEnv ?? [])
        {
            var k = (e.Key ?? "").Trim();
            var v = (e.Value ?? "").Trim();
            if (k.Length > 0 && v.Length > 0)
                envVars.Add(new EnvironmentVariable(k, v, e.Secret));
        }

        // Coding agent secrets
        AddSecret(envVars, "ANTHROPIC_API_KEY",      req.AnthropicApiKey);
        AddSecret(envVars, "CLAUDE_CODE_OAUTH_TOKEN", req.ClaudeCodeOAuthToken);
        AddSecret(envVars, "GITHUB_TOKEN",            req.GitHubToken);
        AddSecret(envVars, "OPENAI_API_KEY",          req.OpenAiApiKey);
        AddSecret(envVars, "GEMINI_API_KEY",          req.GeminiApiKey);
        AddSecret(envVars, "GH_TOKEN",                req.CopilotGhToken);

        // BasicAuth secrets
        envVars.Add(new EnvironmentVariable("BasicAuth__Users",      basicAuth.UsersValue,       Secret: true));
        envVars.Add(new EnvironmentVariable("BasicAuth__HashSecret", basicAuth.HashSecretBase64,  Secret: true));
        envVars.Add(new EnvironmentVariable("BasicAuth__JwtSecret",  basicAuth.JwtSecretBase64,   Secret: true));

        // Non-secret config
        envVars.Add(new EnvironmentVariable("PORT",         port, Secret: false));
        envVars.Add(new EnvironmentVariable("TENDRIL_HOME", home, Secret: false));
        foreach (var ev in cloneEnvVars)
            envVars.Add(ev);

        // ── 5. Determine service CMD ──────────────────────────────────────
        var cmd = cloneEnvVars.Count > 0
            ? TendrilCloneBootstrap.BuildServiceCmdWrapped()
            : null;

        // ── 6. Volume mounts ──────────────────────────────────────────────
        // If no volume ID was provided, auto-create one on the selected server.
        var volumeId = req.VolumeId?.Trim();
        if (string.IsNullOrWhiteSpace(volumeId))
        {
            var volumeName = AutoDataVolumeName(req.ServiceName);
            var created = await _client.CreateVolumeAsync(req.SliplaneApiToken, req.ServerId, volumeName);
            volumeId = created.Id;
        }

        List<(string VolumeId, string MountPath)>? volumes = null;
        if (!string.IsNullOrWhiteSpace(volumeId))
            volumes = [(volumeId, home)];

        // ── 7. Create the Sliplane service ────────────────────────────────
        var service = await _client.CreateServiceAsync(
            req.SliplaneApiToken,
            req.ProjectId,
            ServiceRequestFactory.BuildCreateRequest(
                name:            req.ServiceName,
                serverId:        req.ServerId,
                gitRepo:         req.GitRepo,
                branch:          req.Branch,
                dockerfilePath:  resolution.DockerfilePath,
                dockerContext:   resolution.DockerContext,
                autoDeploy:      req.AutoDeploy,
                networkPublic:   true,
                networkProtocol: "http",
                cmd:             cmd,
                healthcheck:     "/",
                env:             envVars,
                volumeMounts:    volumes));

        if (service == null)
            throw new InvalidOperationException("Sliplane returned an empty response after service creation.");

        var domain = service.Domains?.FirstOrDefault(d => !d.IsCustom)?.Domain
                  ?? service.Domains?.FirstOrDefault()?.Domain;
        var serviceUrl = domain != null ? $"https://{domain}" : null;

        return new DeployResponse
        {
            ServiceId   = service.Id   ?? "",
            ServiceName = service.Name ?? req.ServiceName,
            ProjectId   = req.ProjectId,
            ServiceUrl  = serviceUrl,
        };
    }

    // ── Discovery helpers ─────────────────────────────────────────────────

    public async Task<List<ServerInfo>> GetServersAsync(string sliplaneToken, CancellationToken ct = default)
    {
        var servers = await _client.GetServersAsync(sliplaneToken);
        return servers.Select(s => new ServerInfo { Id = s.Id, Name = s.Name }).ToList();
    }

    public async Task<List<ProjectInfo>> GetProjectsAsync(string sliplaneToken, CancellationToken ct = default)
    {
        var projects = await _client.GetProjectsAsync(sliplaneToken);
        return projects.Select(p => new ProjectInfo { Id = p.Id, Name = p.Name }).ToList();
    }

    public async Task<ServiceStatusResponse?> GetServiceStatusAsync(
        string sliplaneToken, string projectId, string serviceId, CancellationToken ct = default)
    {
        var svc = await _client.GetServiceAsync(sliplaneToken, projectId, serviceId);
        if (svc == null) return null;

        var domain = svc.Domains?.FirstOrDefault(d => !d.IsCustom)?.Domain
                  ?? svc.Domains?.FirstOrDefault()?.Domain;

        return new ServiceStatusResponse
        {
            ServiceId   = svc.Id   ?? serviceId,
            ServiceName = svc.Name ?? "",
            Status      = svc.Status ?? "unknown",
            ServiceUrl  = domain != null ? $"https://{domain}" : null,
        };
    }

    private static void AddSecret(List<EnvironmentVariable> list, string key, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length > 0)
            list.Add(new EnvironmentVariable(key, v, Secret: true));
    }

    /// <summary>
    /// Sliplane volume names should be URL-safe-ish; keep alphanumerics, dot, underscore, hyphen.
    /// </summary>
    public static string AutoDataVolumeName(string serviceName)
    {
        var raw = (serviceName ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0)
            return "tendril-data";

        var slug = Regex.Replace(raw, @"[^a-z0-9._-]+", "-", RegexOptions.CultureInvariant);
        slug = Regex.Replace(slug, "-{2,}", "-", RegexOptions.CultureInvariant).Trim('-');
        if (slug.Length == 0)
            slug = "tendril";

        return $"{slug}-data";
    }
}
