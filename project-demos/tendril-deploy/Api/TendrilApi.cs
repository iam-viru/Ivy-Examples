namespace TendrilDeploy.Api;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Scalar.AspNetCore;
using TendrilDeploy.Services;

/// <summary>
/// Plugs Tendril API routes + Scalar docs into the pipeline via <see cref="IStartupFilter"/>.
/// Must run before Ivy's middleware, which would otherwise intercept every request.
/// </summary>
public sealed class TendrilApiStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Register REST API endpoints in the main routing table.
            if (app is IEndpointRouteBuilder erb)
                TendrilApi.MapRoutes(erb);

            // Branch /openapi and /swagger BEFORE Ivy's middleware intercepts them.
            app.MapWhen(
                ctx => ctx.Request.Path.StartsWithSegments("/openapi"),
                branch =>
                {
                    branch.UseRouting();
                    branch.UseEndpoints(ep => ep.MapOpenApi());
                });

            app.MapWhen(
                ctx => ctx.Request.Path.StartsWithSegments("/swagger"),
                branch =>
                {
                    branch.UseRouting();
                    branch.UseEndpoints(ep => ep.MapScalarApiReference("/swagger/v1"));
                });

            next(app);
        };
    }
}

/// <summary>
/// REST API for programmatic Tendril management.
///
/// All endpoints (except GET /api/v1/health) require the <c>X-Api-Key</c> header.
/// Set <c>TendrilDeploy:ApiKey</c> in config/secrets to enable the API.
///
/// Endpoints that query Sliplane also require <c>X-Sliplane-Token</c>
/// (your Sliplane API token from Team Settings → API Tokens).
/// </summary>
public static class TendrilApi
{
    private const string ApiKeyHeader      = "X-Api-Key";
    private const string SliplaneKeyHeader = "X-Sliplane-Token";
    private const string ApiKeyConfigKey   = "TendrilDeploy:ApiKey";

    public static IEndpointRouteBuilder MapRoutes(IEndpointRouteBuilder app)
    {
        // ── Health ──────────────────────────────────────────────────────
        app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }))
            .WithName("Health")
            .WithSummary("Health check")
            .WithDescription("Returns 200 OK when the API is running. No authentication required.");

        const string apiKeyNote = "\n\n**Headers required:** `X-Api-Key` — must match `TendrilDeploy:ApiKey` in config.";
        const string sliplaneNote = "\n\n**Headers required:** `X-Api-Key` + `X-Sliplane-Token` (Sliplane → Team Settings → API Tokens).";

        // ── Servers ─────────────────────────────────────────────────────
        app.MapGet("/api/v1/servers", ListServersAsync)
            .WithName("ListServers")
            .WithSummary("List Sliplane servers")
            .WithDescription(
                "Returns all servers in your Sliplane account. " +
                "Copy the `id` to use as `serverId` when deploying." + sliplaneNote)
            .Produces<List<ServerInfo>>()
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── Projects ────────────────────────────────────────────────────
        app.MapGet("/api/v1/projects", ListProjectsAsync)
            .WithName("ListProjects")
            .WithSummary("List Sliplane projects")
            .WithDescription(
                "Returns all projects in your Sliplane account. " +
                "Copy the `id` to use as `projectId` when deploying." + sliplaneNote)
            .Produces<List<ProjectInfo>>()
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── Deploy ──────────────────────────────────────────────────────
        app.MapPost("/api/v1/tendrils", DeployAsync)
            .WithName("DeployTendril")
            .WithSummary("Deploy a new Tendril instance")
            .WithDescription(
                "Creates a Tendril service on Sliplane. " +
                "Builds from the specified GitHub repo (defaults to the official Ivy-Tendril repo), " +
                "injects your API keys as secrets, optionally clones workspace repos on startup, " +
                "and sets up basic-auth login for the Tendril web UI. " +
                "Returns the service ID once Sliplane accepts the request — the build takes a few minutes." + apiKeyNote)
            .Produces<DeployResponse>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces<ErrorResponse>(StatusCodes.Status500InternalServerError);

        // ── Service status ───────────────────────────────────────────────
        app.MapGet("/api/v1/tendrils/{projectId}/{serviceId}", GetServiceStatusAsync)
            .WithName("GetServiceStatus")
            .WithSummary("Get status of a deployed Tendril service")
            .WithDescription(
                "Returns the current status and URL of a Tendril service. " +
                "Poll this after deploying to know when the build is done and the service is running." + sliplaneNote)
            .Produces<ServiceStatusResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    // ── Handlers ─────────────────────────────────────────────────────────

    private static async Task<IResult> ListServersAsync(
        HttpContext ctx, TendrilDeployService svc, IConfiguration cfg, CancellationToken ct)
    {
        if (Guard(ctx, cfg, out var err, requireSliplane: true)) return err!;
        var token = ctx.Request.Headers[SliplaneKeyHeader].ToString().Trim();
        var servers = await svc.GetServersAsync(token, ct);
        return Results.Ok(servers);
    }

    private static async Task<IResult> ListProjectsAsync(
        HttpContext ctx, TendrilDeployService svc, IConfiguration cfg, CancellationToken ct)
    {
        if (Guard(ctx, cfg, out var err, requireSliplane: true)) return err!;
        var token = ctx.Request.Headers[SliplaneKeyHeader].ToString().Trim();
        var projects = await svc.GetProjectsAsync(token, ct);
        return Results.Ok(projects);
    }

    private static async Task<IResult> DeployAsync(
        DeployRequest req, HttpContext ctx, TendrilDeployService svc, IConfiguration cfg, CancellationToken ct)
    {
        if (Guard(ctx, cfg, out var err)) return err!;

        var errors = ValidateDeploy(req);
        if (errors.Count > 0)
            return Err400(string.Join("; ", errors));

        try
        {
            var response = await svc.DeployAsync(req, ct);
            return Results.Json(response, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex) { return Err400(ex.Message); }
        catch (Exception ex)         { return Err500(ex.Message); }
    }

    private static async Task<IResult> GetServiceStatusAsync(
        string projectId, string serviceId,
        HttpContext ctx, TendrilDeployService svc, IConfiguration cfg, CancellationToken ct)
    {
        if (Guard(ctx, cfg, out var err, requireSliplane: true)) return err!;
        var token = ctx.Request.Headers[SliplaneKeyHeader].ToString().Trim();
        var status = await svc.GetServiceStatusAsync(token, projectId, serviceId, ct);
        return status is null
            ? Results.Json(new ErrorResponse { Error = $"Service '{serviceId}' not found in project '{projectId}'." },
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(status);
    }

    // ── Auth guard ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true (and sets <paramref name="result"/>) when the request should be rejected.
    /// Returns false when the request is allowed to proceed.
    /// </summary>
    private static bool Guard(HttpContext ctx, IConfiguration cfg, out IResult? result, bool requireSliplane = false)
    {
        result = null;
        var configuredKey = cfg[ApiKeyConfigKey]?.Trim();
        if (string.IsNullOrEmpty(configuredKey))
        {
            result = Results.Json(
                new ErrorResponse { Error = "API is disabled. Set TendrilDeploy:ApiKey in configuration to enable it." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
            return true;
        }

        if (!ctx.Request.Headers.TryGetValue(ApiKeyHeader, out var supplied)
            || !string.Equals(supplied.ToString().Trim(), configuredKey, StringComparison.Ordinal))
        {
            result = Results.Json(
                new ErrorResponse { Error = "Invalid or missing X-Api-Key header." },
                statusCode: StatusCodes.Status401Unauthorized);
            return true;
        }

        if (requireSliplane && string.IsNullOrWhiteSpace(ctx.Request.Headers[SliplaneKeyHeader]))
        {
            result = Results.Json(
                new ErrorResponse { Error = "Missing X-Sliplane-Token header." },
                statusCode: StatusCodes.Status401Unauthorized);
            return true;
        }

        return false;
    }

    private static List<string> ValidateDeploy(DeployRequest req)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(req.SliplaneApiToken))  e.Add("sliplaneApiToken is required.");
        if (string.IsNullOrWhiteSpace(req.ProjectId))         e.Add("projectId is required.");
        if (string.IsNullOrWhiteSpace(req.ServerId))          e.Add("serverId is required.");
        if (string.IsNullOrWhiteSpace(req.ServiceName))       e.Add("serviceName is required.");
        if (string.IsNullOrWhiteSpace(req.BasicAuthUsername)) e.Add("basicAuthUsername is required.");
        if (string.IsNullOrWhiteSpace(req.BasicAuthPassword)) e.Add("basicAuthPassword is required.");
        return e;
    }

    private static IResult Err400(string msg) =>
        Results.Json(new ErrorResponse { Error = msg }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Err500(string msg) =>
        Results.Json(new ErrorResponse { Error = msg }, statusCode: StatusCodes.Status500InternalServerError);

}
