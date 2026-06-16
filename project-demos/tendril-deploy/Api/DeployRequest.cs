namespace TendrilDeploy.Api;

using TendrilDeploy.Apps;

/// <summary>Request body for <c>POST /api/v1/tendrils</c>.</summary>
public sealed class DeployRequest
{
    // ── Sliplane ───────────────────────────────────────────────────────────

    /// <summary>
    /// Your Sliplane API token (Bearer).
    /// Found in Sliplane → Team Settings → API Tokens.
    /// </summary>
    public string SliplaneApiToken { get; set; } = "";

    /// <summary>
    /// Sliplane project ID where the service will be created.
    /// Use <c>GET /api/v1/projects</c> to list your projects.
    /// </summary>
    public string ProjectId { get; set; } = "";

    /// <summary>
    /// Sliplane server ID where the service will run.
    /// Use <c>GET /api/v1/servers</c> to list available servers.
    /// </summary>
    public string ServerId { get; set; } = "";

    /// <summary>Name for the new Sliplane service, e.g. <c>tendril-artem</c>.</summary>
    public string ServiceName { get; set; } = "";

    // ── Source repo ────────────────────────────────────────────────────────

    /// <summary>
    /// GitHub repo that contains the Tendril Dockerfile.
    /// Defaults to the official Ivy-Tendril repo if omitted.
    /// </summary>
    public string GitRepo { get; set; } = TendrilDeployDefaults.DefaultRepoUrl;

    /// <summary>Branch to build from. Defaults to <c>development</c>.</summary>
    public string Branch { get; set; } = TendrilDeployDefaults.DefaultBranch;

    // ── Workspace repos ────────────────────────────────────────────────────

    /// <summary>
    /// GitHub repos to automatically clone into the container on startup.
    /// Each repo ends up at <c>TENDRIL_HOME/repos/owner/repo-name</c>.
    /// Leave empty to skip cloning.
    /// </summary>
    public List<string> Repos { get; set; } = [];

    // ── Agent API keys (all optional) ─────────────────────────────────────

    /// <summary>Anthropic API key → env var <c>ANTHROPIC_API_KEY</c>.</summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>
    /// Claude subscription token (from <c>claude setup-token</c>) → <c>CLAUDE_CODE_OAUTH_TOKEN</c>.
    /// Use this instead of an API key if you have a Pro/Max/Team plan.
    /// </summary>
    public string? ClaudeCodeOAuthToken { get; set; }

    /// <summary>GitHub personal access token → <c>GITHUB_TOKEN</c>. Used by the <c>gh</c> CLI.</summary>
    public string? GitHubToken { get; set; }

    /// <summary>OpenAI API key → <c>OPENAI_API_KEY</c>. Used by the Codex CLI.</summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>Google Gemini API key → <c>GEMINI_API_KEY</c>.</summary>
    public string? GeminiApiKey { get; set; }

    /// <summary>GitHub token for GitHub Copilot CLI → <c>GH_TOKEN</c>.</summary>
    public string? CopilotGhToken { get; set; }

    // ── Login credentials for the deployed Tendril app ────────────────────

    /// <summary>Username for logging into the deployed Tendril web UI.</summary>
    public string BasicAuthUsername { get; set; } = "";

    /// <summary>Password for the Tendril web UI (minimum 8 characters).</summary>
    public string BasicAuthPassword { get; set; } = "";

    // ── Optional overrides ─────────────────────────────────────────────────

    /// <summary>Container port. Default: <c>8000</c>.</summary>
    public string Port { get; set; } = "8000";

    /// <summary>
    /// Path inside the container where Tendril stores its data.
    /// Default: <c>/data/tendril</c>. Mount a Sliplane volume here to persist data across redeploys.
    /// </summary>
    public string TendrilHome { get; set; } = "/data/tendril";

    /// <summary>
    /// Sliplane persistent volume ID to attach at <c>TendrilHome</c>.
    /// If omitted, a new volume is created on <see cref="ServerId"/> (name derived from <see cref="ServiceName"/>).
    /// </summary>
    public string? VolumeId { get; set; }

    /// <summary>
    /// Dockerfile path in the source repository.
    /// Default: <c>.github/docker/Dockerfile.tendril</c>.
    /// </summary>
    public string DockerfilePath { get; set; } = TendrilDeploymentPaths.DefaultDockerfilePath;

    /// <summary>Docker build context path. Default: <c>.</c>.</summary>
    public string DockerContext { get; set; } = ".";

    /// <summary>Enable automatic redeploy when the source repo is pushed to. Default: <c>true</c>.</summary>
    public bool AutoDeploy { get; set; } = true;
}
