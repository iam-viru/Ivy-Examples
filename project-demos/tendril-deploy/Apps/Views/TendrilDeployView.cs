namespace TendrilDeploy.Apps.Views;

using TendrilDeploy;
using TendrilDeploy.Apps;
using TendrilDeploy.Models;
using TendrilDeploy.Services;

public class TendrilDeployFormModel
{
    [Display(Name = "Server", Order = 1, Prompt = "Select a server")]
    [Required(ErrorMessage = "Select a server")]
    public string ServerId { get; set; } = "";

    public string ProjectId { get; set; } = "";

    [Display(Name = "Service name", Order = 3, Prompt = "e.g. ivy-tendril")]
    [Required(ErrorMessage = "Enter a service name")]
    [MinLength(2, ErrorMessage = "Service name must be at least 2 characters")]
    public string Name { get; set; } = "";

    [Display(Name = "Git repository", Order = 4)]
    [Required(ErrorMessage = "Enter the Git repository URL")]
    public string GitRepo { get; set; } = "";

    [Display(Name = "Branch", Order = 5)]
    public string Branch { get; set; } = "development";

    [Display(Name = "Dockerfile path", Order = 6)]
    public string DockerfilePath { get; set; } = TendrilDeploymentPaths.DefaultDockerfilePath;

    [Display(Name = "Docker context", Order = 7)]
    public string DockerContext { get; set; } = ".";

    [Display(Name = "Anthropic API key (optional)", Order = 8,
        Prompt = "ANTHROPIC_API_KEY from Claude Console — use this **or** “Claude subscription token” below (not both needed). If both are set, the API key takes precedence in Claude Code.")]
    public string AnthropicApiKey { get; set; } = "";

    [Display(Name = "Claude subscription token (optional)", Order = 9,
        Prompt = "Run `claude setup-token` locally (Pro / Max / Team / Enterprise), then paste here — sent as CLAUDE_CODE_OAUTH_TOKEN for headless hosts with no browser login.")]
    public string ClaudeCodeOAuthToken { get; set; } = "";

    [Display(Name = "GitHub token", Order = 10,
        Prompt = "GITHUB_TOKEN for gh / PRs — sent to Sliplane as a secret env var")]
    [Required(ErrorMessage = "Enter your GitHub token")]
    public string GithubToken { get; set; } = "";

    [Display(Name = "OpenAI API key (Codex CLI)", Order = 11,
        Prompt = "OPENAI_API_KEY — optional; enables OpenAI Codex without `codex login` in the container")]
    public string OpenAiApiKey { get; set; } = "";

    [Display(Name = "Gemini API key (Gemini CLI)", Order = 12,
        Prompt = "GEMINI_API_KEY — optional; headless auth for @google/gemini-cli (see Gemini CLI docs)")]
    public string GeminiApiKey { get; set; } = "";

    [Display(Name = "PORT", Order = 13)]
    public string Port { get; set; } = "8000";

    [Display(Name = "TENDRIL_HOME", Order = 14)]
    public string TendrilHome { get; set; } = "/data/tendril";

    [Display(Name = "Volume ID (optional)", Order = 15,
        Prompt = "Sliplane persistent volume — mount at TENDRIL_HOME")]
    public string? VolumeId { get; set; }

    public bool AutoDeploy { get; set; } = true;
    public bool NetworkPublic { get; set; } = true;
    public string NetworkProtocol { get; set; } = "http";
    public string Healthcheck { get; set; } = "/";
    public string? Cmd { get; set; }
}

public class TendrilDeployView : ViewBase
{
    private readonly string _apiToken;
    private readonly DeployDraft _draft;
    private readonly string _defaultServerId;
    private readonly string _defaultProjectId;

    public TendrilDeployView(string apiToken, DeployDraft draft, string defaultServerId = "", string defaultProjectId = "")
    {
        _apiToken = apiToken;
        _draft = draft;
        _defaultServerId = defaultServerId;
        _defaultProjectId = defaultProjectId;
    }

    public override object? Build()
    {
        var client = UseService<SliplaneApiClient>();
        var config = UseService<IConfiguration>();
        var dockerfileResolver = UseService<GitHubDockerfilePathResolver>();

        var model = UseState(() =>
        {
            var ui = TendrilDeploySettingsReader.Read(
                config,
                _draft,
                TendrilDeployDefaults.DefaultBranch,
                TendrilDeploymentPaths.DefaultDockerfilePath);
            return new TendrilDeployFormModel
            {
                ServerId = _defaultServerId,
                ProjectId = _defaultProjectId,
                GitRepo = ui.GitRepo,
                Branch = ui.Branch,
                DockerContext = ui.DockerContext,
                DockerfilePath = ui.DockerfilePath,
                Name = DeriveServiceName(ui.GitRepo, ui.DockerContext),
                Port = ui.Port,
                TendrilHome = ui.TendrilHome,
                VolumeId = ui.VolumeId,
                AnthropicApiKey = config["TendrilDeploy:AnthropicApiKey"]?.Trim() ?? "",
                ClaudeCodeOAuthToken = config["TendrilDeploy:ClaudeCodeOAuthToken"]?.Trim() ?? "",
                GithubToken = config["TendrilDeploy:GithubToken"]?.Trim() ?? "",
                OpenAiApiKey = config["TendrilDeploy:OpenAiApiKey"]?.Trim() ?? "",
                GeminiApiKey = config["TendrilDeploy:GeminiApiKey"]?.Trim() ?? "",
                AutoDeploy = true,
                NetworkPublic = true,
                NetworkProtocol = "http",
                Healthcheck = "/",
            };
        });

        var reloadCounter = UseState(0);
        var deployedService = UseState<(string ProjectId, SliplaneService Service)?>(() => null);
        var deployError = UseState<string?>(() => null);
        var validationFailed = UseState(false);
        var isDeploying = UseState(false);

        var (onSubmit, formView, validationView, loading) = UseForm(() => model.ToForm("Deploy Tendril")
            .Place(m => m.ServerId, m => m.Name)
            .Builder(m => m.ServerId,
                s => s.ToAsyncSelectInput(QueryServers, LookupServer, placeholder: "Search server…"))
            .Builder(m => m.Name, s => s.ToTextInput().Placeholder("e.g. ivy-tendril"))
            .Builder(m => m.AnthropicApiKey, b => b.ToPasswordInput(placeholder: "sk-ant-… or leave empty if using subscription token"))
            .Builder(m => m.ClaudeCodeOAuthToken, b => b.ToPasswordInput(placeholder: "from `claude setup-token` (optional)"))
            .Builder(m => m.GithubToken, b => b.ToPasswordInput(placeholder: "ghp_… or fine-grained PAT"))
            .Builder(m => m.OpenAiApiKey, b => b.ToPasswordInput(placeholder: "sk-… (optional)"))
            .Builder(m => m.GeminiApiKey, b => b.ToPasswordInput(placeholder: "optional — Gemini API key"))
            .Remove(m => m.ProjectId, m => m.GitRepo, m => m.Branch, m => m.DockerfilePath, m => m.DockerContext,
                m => m.Port, m => m.TendrilHome, m => m.VolumeId,
                m => m.AutoDeploy, m => m.NetworkPublic, m => m.NetworkProtocol,
                m => m.Healthcheck, m => m.Cmd)
            .Required(m => m.ProjectId, m => m.Name, m => m.ServerId, m => m.GitRepo, m => m.GithubToken));

        QueryResult<Option<string>[]> QueryServers(IViewContext ctx, string q) =>
            ctx.UseQuery<Option<string>[], (string, string, int)>(
                key: ("tendril-deploy-servers", q, reloadCounter.Value),
                fetcher: async _ =>
                    (await client.GetServersAsync(_apiToken))
                        .Where(s => string.IsNullOrEmpty(q) || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                        .Take(20)
                        .Select(s => new Option<string>(s.Name, s.Id))
                        .ToArray());

        QueryResult<Option<string>?> LookupServer(IViewContext ctx, string? id) =>
            ctx.UseQuery<Option<string>?, (string, string?, int)>(
                key: ("tendril-deploy-server-lookup", id, reloadCounter.Value),
                fetcher: async _ =>
                {
                    if (string.IsNullOrEmpty(id)) return null;
                    var s = (await client.GetServersAsync(_apiToken)).FirstOrDefault(x => x.Id == id);
                    return s is null ? null : new Option<string>(s.Name, s.Id);
                });

        _ = QueryServers(Context, "");
        _ = LookupServer(Context, model.Value.ServerId);

        async ValueTask HandleDeploy()
        {
            deployError.Set(null);
            deployedService.Set(null);
            validationFailed.Set(false);
            if (!await onSubmit())
            {
                validationFailed.Set(true);
                return;
            }

            var m = model.Value;

            var anthropic = (m.AnthropicApiKey ?? "").Trim();
            var claudeOAuth = (m.ClaudeCodeOAuthToken ?? "").Trim();
            var github = (m.GithubToken ?? "").Trim();
            if (string.IsNullOrEmpty(anthropic) && string.IsNullOrEmpty(claudeOAuth))
            {
                deployError.Set(
                    "Provide either an Anthropic API key or a Claude subscription token (`claude setup-token` → CLAUDE_CODE_OAUTH_TOKEN). "
                    + "GitHub token must also be non-empty. Sliplane rejects empty env values.");
                return;
            }

            if (string.IsNullOrEmpty(github))
            {
                deployError.Set(
                    "GitHub token must be non-empty (spaces-only is treated as empty). Sliplane rejects empty env values.");
                return;
            }

            isDeploying.Set(true);
            try
            {
                var resolution = await dockerfileResolver.ResolveAsync(
                    m.GitRepo, m.Branch, m.DockerfilePath, m.DockerContext);

                var envVars = new List<EnvironmentVariable>();
                foreach (var e in resolution.AdditionalEnv ?? [])
                {
                    var k = (e.Key ?? "").Trim();
                    var v = (e.Value ?? "").Trim();
                    if (k.Length > 0 && v.Length > 0)
                        envVars.Add(new EnvironmentVariable(k, v, e.Secret));
                }

                var port = string.IsNullOrWhiteSpace(m.Port) ? "8000" : m.Port.Trim();
                var home = string.IsNullOrWhiteSpace(m.TendrilHome) ? "/data/tendril" : m.TendrilHome.Trim();

                if (anthropic.Length > 0)
                    envVars.Add(new EnvironmentVariable("ANTHROPIC_API_KEY", anthropic, Secret: true));
                if (claudeOAuth.Length > 0)
                    envVars.Add(new EnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", claudeOAuth, Secret: true));
                envVars.Add(new EnvironmentVariable("GITHUB_TOKEN", github, Secret: true));
                var openAi = (m.OpenAiApiKey ?? "").Trim();
                if (openAi.Length > 0)
                    envVars.Add(new EnvironmentVariable("OPENAI_API_KEY", openAi, Secret: true));
                var gemini = (m.GeminiApiKey ?? "").Trim();
                if (gemini.Length > 0)
                    envVars.Add(new EnvironmentVariable("GEMINI_API_KEY", gemini, Secret: true));
                envVars.Add(new EnvironmentVariable("PORT", port, Secret: false));
                envVars.Add(new EnvironmentVariable("TENDRIL_HOME", home, Secret: false));

                List<(string VolumeId, string MountPath)>? volumes = null;
                if (!string.IsNullOrWhiteSpace(m.VolumeId))
                    volumes = [(m.VolumeId.Trim(), home)];

                var service = await client.CreateServiceAsync(_apiToken, m.ProjectId,
                    ServiceRequestFactory.BuildCreateRequest(
                        name: m.Name,
                        serverId: m.ServerId,
                        gitRepo: m.GitRepo,
                        branch: m.Branch,
                        dockerfilePath: resolution.DockerfilePath,
                        dockerContext: resolution.DockerContext,
                        autoDeploy: m.AutoDeploy,
                        networkPublic: m.NetworkPublic,
                        networkProtocol: m.NetworkProtocol,
                        cmd: m.Cmd ?? string.Empty,
                        healthcheck: m.Healthcheck,
                        env: envVars,
                        volumeMounts: volumes));

                if (service != null)
                    deployedService.Set((m.ProjectId, service));
            }
            catch (Exception ex)
            {
                deployError.Set(ex.Message);
            }
            finally
            {
                isDeploying.Set(false);
            }
        }

        var calloutNoDockerfile = new Callout(
            Text.Markdown(
                "**Git, Docker, PORT, TENDRIL_HOME, volume** come from **dotnet user-secrets** (`TendrilDeploy:*`) and app defaults — they are not shown in this form. " +
                "Change them with `dotnet user-secrets set …` or use a `?repo=` GitHub URL (see README). " +
                "Image: **[ArtemLazarchuk/Ivy-Tendril](https://github.com/ArtemLazarchuk/Ivy-Tendril)** → [`.github/docker/Dockerfile.tendril`](https://github.com/ArtemLazarchuk/Ivy-Tendril/tree/development/.github/docker)."),
            variant: CalloutVariant.Warning).Width(Size.Full());

        var headerSection = Layout.Vertical().AlignContent(Align.Center).Gap(4)
            | Text.H1("Deploy Tendril to Sliplane")
            | Text.Lead(
                "Pick server, service name, GitHub token, and **either** an Anthropic API key **or** a Claude subscription token from `claude setup-token`. "
                + "Optional: OpenAI (Codex) and Gemini API keys. Build settings use user-secrets / defaults.");

        var actionsRow = Layout.Vertical()
            | (Layout.Horizontal().AlignContent(Align.Center)
                | new Button("Deploy").Primary().Large()
                    .Loading(loading || isDeploying.Value).Disabled(loading || isDeploying.Value)
                    .Width(Size.Fraction(0.5f))
                    .OnClick(async _ => await HandleDeploy()))
            | (validationFailed.Value
                ? new Callout(validationView, "Please fix the following", CalloutVariant.Error)
                : validationView);

        var cardContent = Layout.Vertical()
            | headerSection
            | calloutNoDockerfile
            | new Separator()
            | formView
            | new Spacer()
            | actionsRow;

        if (isDeploying.Value && deployedService.Value == null)
        {
            cardContent = cardContent
                | new Separator()
                | new Callout(
                    Layout.Vertical()
                        | Text.Block("Creating Tendril service on Sliplane…").Bold()
                        | new Progress().Indeterminate().Goal("Please wait…"),
                    "Deploying",
                    CalloutVariant.Info);
        }
        else if (deployedService.Value is { } deployed)
        {
            cardContent = cardContent
                | new Separator()
                | new TendrilDeployStatusView(_apiToken, deployed.ProjectId, deployed.Service);
        }

        if (deployError.Value != null)
            cardContent = cardContent | new Callout(deployError.Value, variant: CalloutVariant.Error);

        var card = new Card(cardContent).Width(Size.Fraction(0.42f));
        var manageServicesUrl = config["Sliplane:ManageServicesUrl"]?.Trim();
        if (string.IsNullOrEmpty(manageServicesUrl))
            manageServicesUrl = "https://ivy-sliplane-management.sliplane.app/";
        var manageBtn = new Button("Manage services")
            .Link().Url(manageServicesUrl)
            .Outline().Large().BorderRadius(BorderRadius.Full);
        var manageFloat = new FloatingPanel(manageBtn, Align.BottomRight).Offset(new Thickness(0, 0, 20, 10));

        return new Fragment(Layout.Center() | card, manageFloat);
    }

    private static string DeriveServiceName(string repoUrl, string dockerContext = ".")
    {
        var source = (!string.IsNullOrWhiteSpace(dockerContext) && dockerContext != ".")
            ? dockerContext
            : repoUrl;

        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var seg = source.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        if (seg.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) seg = seg[..^4];
        return string.IsNullOrWhiteSpace(seg) ? string.Empty : seg.ToLowerInvariant();
    }
}
