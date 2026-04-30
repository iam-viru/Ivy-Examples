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

    [Display(Name = "Anthropic API key", Order = 8,
        Prompt = "ANTHROPIC_API_KEY (Claude) — sent to Sliplane as a secret env var")]
    [Required(ErrorMessage = "Enter your Anthropic API key")]
    public string AnthropicApiKey { get; set; } = "";

    [Display(Name = "GitHub token", Order = 9,
        Prompt = "GITHUB_TOKEN for gh / PRs — sent to Sliplane as a secret env var")]
    [Required(ErrorMessage = "Enter your GitHub token")]
    public string GithubToken { get; set; } = "";

    [Display(Name = "PORT", Order = 10)]
    public string Port { get; set; } = "8000";

    [Display(Name = "TENDRIL_HOME", Order = 11)]
    public string TendrilHome { get; set; } = "/data/tendril";

    [Display(Name = "Volume ID (optional)", Order = 12,
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
                GithubToken = config["TendrilDeploy:GithubToken"]?.Trim() ?? "",
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

        var (onSubmit, formView, validationView, loading) = UseForm(() => model.Value.ToForm("Deploy Tendril")
            .Place(m => m.ServerId, m => m.Name)
            .Builder(m => m.ServerId,
                s => s.ToAsyncSelectInput(QueryServers, LookupServer, placeholder: "Search server…"))
            .Builder(m => m.Name, s => s.ToTextInput().Placeholder("e.g. ivy-tendril"))
            .Builder(m => m.GitRepo, s => s.ToTextInput().Placeholder("https://github.com/owner/repo"))
            .Builder(m => m.Branch, s => s.ToTextInput())
            .Builder(m => m.DockerfilePath, s => s.ToTextInput().Placeholder(TendrilDeploymentPaths.DefaultDockerfilePath))
            .Builder(m => m.DockerContext, s => s.ToTextInput().Placeholder("."))
            .Builder(m => m.AnthropicApiKey, b => b.ToPasswordInput(placeholder: "sk-ant-api03-…"))
            .Builder(m => m.GithubToken, b => b.ToPasswordInput(placeholder: "ghp_… or fine-grained PAT"))
            .Builder(m => m.Port, b => b.ToTextInput())
            .Builder(m => m.TendrilHome, b => b.ToTextInput())
            .Builder(m => m.VolumeId, b => b.ToTextInput().Placeholder("optional"))
            .Remove(m => m.ProjectId, m => m.AutoDeploy, m => m.NetworkPublic, m => m.NetworkProtocol,
                m => m.Healthcheck, m => m.Cmd)
            .Required(m => m.ProjectId, m => m.Name, m => m.ServerId, m => m.GitRepo, m => m.AnthropicApiKey,
                m => m.GithubToken));

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
            var github = (m.GithubToken ?? "").Trim();
            if (string.IsNullOrEmpty(anthropic) || string.IsNullOrEmpty(github))
            {
                deployError.Set(
                    "Anthropic API key and GitHub token must be non-empty (spaces-only is treated as empty). "
                    + "Sliplane rejects empty env values.");
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

                envVars.Add(new EnvironmentVariable("ANTHROPIC_API_KEY", anthropic, Secret: true));
                envVars.Add(new EnvironmentVariable("GITHUB_TOKEN", github, Secret: true));
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
                "Default image: **[ArtemLazarchuk/Ivy-Tendril](https://github.com/ArtemLazarchuk/Ivy-Tendril)** branch `development`, Dockerfile **[`.github/docker/Dockerfile.tendril`](https://github.com/ArtemLazarchuk/Ivy-Tendril/tree/development/.github/docker)**. " +
                "**Secrets** below are sent to Sliplane as env vars on deploy. Optionally pre-fill Git/PORT/TENDRIL_HOME and keys from **dotnet user-secrets** (`TendrilDeploy:*` — see README)."),
            variant: CalloutVariant.Warning).Width(Size.Full());

        var headerSection = Layout.Vertical().AlignContent(Align.Center).Gap(4)
            | Text.H1("Deploy Tendril to Sliplane")
            | Text.Lead("Enter runtime secrets here so the container gets Claude and GitHub CLI working without interactive login.");

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
