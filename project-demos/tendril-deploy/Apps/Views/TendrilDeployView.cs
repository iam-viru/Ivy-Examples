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
                AutoDeploy = true,
                NetworkPublic = true,
                NetworkProtocol = "http",
                Healthcheck = "/",
            };
        });

        var anthropicKey = UseState<string?>(() => SecretPrefill(config, "TendrilDeploy:AnthropicApiKey"));
        var claudeOAuthToken = UseState<string?>(() => SecretPrefill(config, "TendrilDeploy:ClaudeCodeOAuthToken"));
        var githubToken = UseState<string?>(() => SecretPrefill(config, "TendrilDeploy:GithubToken"));
        var openAiKey = UseState<string?>(() => SecretPrefill(config, "TendrilDeploy:OpenAiApiKey"));
        var geminiKey = UseState<string?>(() => SecretPrefill(config, "TendrilDeploy:GeminiApiKey"));

        var reloadCounter = UseState(0);
        var stepIndex = UseState(0);
        /// <summary>
        /// Step 1 hides the main form controls; Ivy form validation relies on mounted inputs,
        /// so we freeze the validated step-0 model and deploy from this snapshot.
        /// </summary>
        var validatedDeployForm = UseState<TendrilDeployFormModel?>(() => null);
        var deployedService = UseState<(string ProjectId, SliplaneService Service)?>(() => null);
        var deployError = UseState<string?>(() => null);
        var validationFailed = UseState(false);
        var isDeploying = UseState(false);

        var (onSubmit, formView, validationView, loading) = UseForm(() => model.ToForm("Deploy Tendril")
            .Place(m => m.ServerId, m => m.Name)
            .Builder(m => m.ServerId,
                s => s.ToAsyncSelectInput(QueryServers, LookupServer, placeholder: "Search server…"))
            .Builder(m => m.Name, s => s.ToTextInput().Placeholder("e.g. ivy-tendril"))
            .Remove(m => m.ProjectId, m => m.GitRepo, m => m.Branch, m => m.DockerfilePath, m => m.DockerContext,
                m => m.Port, m => m.TendrilHome, m => m.VolumeId,
                m => m.AutoDeploy, m => m.NetworkPublic, m => m.NetworkProtocol,
                m => m.Healthcheck, m => m.Cmd)
            .Required(m => m.ProjectId, m => m.Name, m => m.ServerId, m => m.GitRepo));

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

        async ValueTask AdvanceFromStep0Async()
        {
            validationFailed.Set(false);
            if (!await onSubmit())
            {
                validationFailed.Set(true);
                validatedDeployForm.Set(null);
                return;
            }

            validatedDeployForm.Set(CloneFormModel(model.Value));
            stepIndex.Set(1);
        }

        async ValueTask HandleDeploy()
        {
            deployError.Set(null);
            deployedService.Set(null);
            validationFailed.Set(false);
            var m = validatedDeployForm.Value;
            if (m == null)
            {
                deployError.Set("Form state expired. Go Back, then continue from step 1 again.");
                stepIndex.Set(0);
                return;
            }


            var anthropic = (anthropicKey.Value ?? "").Trim();
            var claudeOAuth = (claudeOAuthToken.Value ?? "").Trim();
            var github = (githubToken.Value ?? "").Trim();
            var openAi = (openAiKey.Value ?? "").Trim();
            var gemini = (geminiKey.Value ?? "").Trim();

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
                if (github.Length > 0)
                    envVars.Add(new EnvironmentVariable("GITHUB_TOKEN", github, Secret: true));
                if (openAi.Length > 0)
                    envVars.Add(new EnvironmentVariable("OPENAI_API_KEY", openAi, Secret: true));
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

        var claudeExpandable = new Expandable(
            "Claude (Anthropic) — Claude Code",
            Layout.Vertical().Gap(3).Width(Size.Full())
                | Text.Muted("Claude Code inside the container.")
                | Text.Markdown(
                    "**API key:** create one in [Claude Console](https://console.anthropic.com/) → API keys. Pasted value is sent to Sliplane as **`ANTHROPIC_API_KEY`**.")
                | Text.Markdown(
                    "**Subscription token (no API billing):** on your laptop run **`claude setup-token`** (needs Pro / Max / Team / Enterprise), paste the token here → **`CLAUDE_CODE_OAUTH_TOKEN`**. If both key and token are set, Claude Code prefers the **API key**.")
                | Text.Muted("Leave blank to configure later in the Sliplane service env.")
                | anthropicKey.ToPasswordInput(placeholder: "sk-ant-api03-…").WithField().Label("Anthropic API key (optional)")
                | claudeOAuthToken.ToPasswordInput(placeholder: "from claude setup-token").WithField().Label("Claude subscription token (optional)")
        ).Open().Icon(Icons.Bot).Width(Size.Full());

        var githubExpandable = new Expandable(
            "GitHub — gh CLI",
            Layout.Vertical().Gap(3).Width(Size.Full())
                | Text.Muted("Repos, PRs, branches via GitHub CLI.")
                | Text.Markdown(
                    "**Where to get:** GitHub → **Settings → Developer settings** → Personal access tokens (classic or fine-grained). Scopes: at minimum **repo** if Tendril should push branches/open PRs.")
                | Text.Markdown("**Sliplane env:** **`GITHUB_TOKEN`** (secret). The container runs `gh auth` using this variable.")
                | Text.Muted("Optional — add later in Sliplane if you skip it here.")
                | githubToken.ToPasswordInput(placeholder: "ghp_… or fine-grained PAT").WithField().Label("GitHub token (optional)")
        ).Icon(Icons.Github).Width(Size.Full());

        var openAiExpandable = new Expandable(
            "OpenAI Codex CLI",
            Layout.Vertical().Gap(3).Width(Size.Full())
                | Text.Muted("@openai/codex in the container image.")
                | Text.Markdown(
                    "**Where to get:** [OpenAI Platform](https://platform.openai.com/api-keys) → API keys.")
                | Text.Markdown(
                    "**Sliplane env:** **`OPENAI_API_KEY`**. Avoids running interactive **`codex login`** in the container.")
                | Text.Muted("Optional.")
                | openAiKey.ToPasswordInput(placeholder: "sk-…").WithField().Label("OpenAI API key (optional)")
        ).Icon(Icons.Code).Width(Size.Full());

        var geminiExpandable = new Expandable(
            "Google Gemini CLI",
            Layout.Vertical().Gap(3).Width(Size.Full())
                | Text.Muted("@google/gemini-cli in the container image.")
                | Text.Markdown(
                    "**Where to get:** Google AI Studio / Gemini API key (see [Gemini CLI docs](https://github.com/google-gemini/gemini-cli) for the exact variable names for your version).")
                | Text.Markdown("**This form sends:** **`GEMINI_API_KEY`** when non-empty.")
                | Text.Muted("Optional.")
                | geminiKey.ToPasswordInput(placeholder: "Gemini API key").WithField().Label("Gemini API key (optional)")
        ).Icon(Icons.Sparkles).Width(Size.Full());

        var agentSections = Layout.Vertical().Gap(2).Width(Size.Full())
            | claudeExpandable
            | githubExpandable
            | openAiExpandable
            | geminiExpandable;

        var stepperItems = new[]
        {
            new StepperItem("1", stepIndex.Value > 0 ? Icons.Check : null, "Welcome", "Server & name"),
            new StepperItem("2", deployedService.Value != null ? Icons.Check : null, "Secrets", "API keys"),
        };

        ValueTask OnStepperSelect(Event<Stepper, int> e)
        {
            if (e.Value < stepIndex.Value)
            {
                stepIndex.Set(e.Value);
                if (e.Value == 0)
                    validatedDeployForm.Set(null);
            }

            return ValueTask.CompletedTask;
        }

        var welcomeNoteCallout = new Callout(
            Layout.Vertical().Gap(3)
                | Text.Block(
                    "Ivy Tendril is a coding orchestrator powered by agents like Claude Code, Codex, Gemini, or Copilot. "
                    + "It is built to help you ship a lot of work quickly on your own infrastructure.")
                | Text.Block("Please be aware that Tendril can consume tokens rapidly.").Bold(),
            "Note",
            CalloutVariant.Info);

        var secretsHintCallout = new Callout(
            Text.Markdown(
                "**Optional** keys for your agents—each is a **secret** env var on Sliplane. "
                + "Empty fields are ignored. Repo, Dockerfile, **PORT**, **TENDRIL_HOME**, and volume come from config or **`?repo=`**, not below."),
            "Secrets",
            CalloutVariant.Info);

        object validationBlock = validationFailed.Value
            ? new Callout(validationView, "Please fix the following", CalloutVariant.Error)
            : new Empty();

        var titleBlock = stepIndex.Value == 0
            ? (object)(Layout.Vertical().Gap(2).AlignContent(Align.Center)
                | Text.H1("Welcome to Ivy Tendril").Align(TextAlignment.Center))
            : (object)(Layout.Vertical().Gap(2).AlignContent(Align.Center)
                | Text.H1("API keys").Align(TextAlignment.Center));

        var stepBody = stepIndex.Value == 0
            ? (object)(Layout.Vertical().Gap(4).Width(Size.Full())
                | welcomeNoteCallout
                | formView
                | validationBlock)
            : (object)(Layout.Vertical().Gap(4).Width(Size.Full())
                | secretsHintCallout
                | agentSections);

        object footerRow = stepIndex.Value == 0
            ? (object)(Layout.Vertical().Width(Size.Full()).AlignContent(Align.Center)
                | new Button("Get started")
                    .Icon(Icons.ChevronRight, Align.Right)
                    .Primary()
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Full())
                    .Loading(loading)
                    .Disabled(loading)
                    .OnClick(async _ => await AdvanceFromStep0Async()))
            : (object)(Layout.Horizontal().Width(Size.Full()).Gap(4)
                | new Button("Back")
                    .Icon(Icons.ChevronLeft)
                    .Variant(ButtonVariant.Outline)
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Fraction(0.31f))
                    .OnClick(_ =>
                    {
                        stepIndex.Set(0);
                        validatedDeployForm.Set(null);
                    })
                | new Spacer()
                | new Button("Deploy")
                    .Icon(Icons.Rocket, Align.Right)
                    .Primary()
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Fraction(0.31f))
                    .Loading(loading || isDeploying.Value)
                    .Disabled(loading || isDeploying.Value)
                    .OnClick(async _ => await HandleDeploy()));

        var stepperRow = Layout.Vertical().Width(Size.Full()).AlignContent(Align.Center)
            | new Stepper(OnStepperSelect, stepIndex.Value, stepperItems).Width(Size.Full());

        var mainFlow = Layout.Vertical().Width(Size.Full()).Gap(4).AlignContent(Align.Stretch)
            | stepperRow
            | titleBlock
            | stepBody
            | footerRow;

        var pageBody = mainFlow;

        if (isDeploying.Value && deployedService.Value == null)
        {
            pageBody = pageBody
                | new Callout(
                    Layout.Vertical().Gap(3)
                        | Text.Block("Creating Tendril service on Sliplane…").Bold()
                        | new Progress().Indeterminate().Goal("Please wait…"),
                    "Deploying",
                    CalloutVariant.Info);
        }
        else if (deployedService.Value is { } deployed)
        {
            pageBody = pageBody
                | new TendrilDeployStatusView(_apiToken, deployed.ProjectId, deployed.Service);
        }

        if (deployError.Value != null)
            pageBody = pageBody | new Callout(deployError.Value, variant: CalloutVariant.Error);

        var pageColumn = Layout.Vertical()
            .Width(Size.Fraction(0.52f))
            .Gap(2)
            .Padding(new Thickness(16, 16, 16, 16))
            .AlignContent(Align.Stretch)
            | pageBody;

        var manageServicesUrl = config["Sliplane:ManageServicesUrl"]?.Trim();
        if (string.IsNullOrEmpty(manageServicesUrl))
            manageServicesUrl = "https://ivy-sliplane-management.sliplane.app/";
        var manageBtn = new Button("Manage services")
            .Link().Url(manageServicesUrl)
            .Outline().Large().BorderRadius(BorderRadius.Full);
        var manageFloat = new FloatingPanel(manageBtn, Align.BottomRight).Offset(new Thickness(0, 0, 20, 10));

        return new Fragment(
            Layout.TopCenter()
                .Padding(new Thickness(0, 12, 0, 0))
                | pageColumn,
            manageFloat);
    }

    private static TendrilDeployFormModel CloneFormModel(TendrilDeployFormModel src) => new()
    {
        ServerId = src.ServerId,
        ProjectId = src.ProjectId,
        Name = src.Name,
        GitRepo = src.GitRepo,
        Branch = src.Branch,
        DockerfilePath = src.DockerfilePath,
        DockerContext = src.DockerContext,
        Port = src.Port,
        TendrilHome = src.TendrilHome,
        VolumeId = src.VolumeId,
        AutoDeploy = src.AutoDeploy,
        NetworkPublic = src.NetworkPublic,
        NetworkProtocol = src.NetworkProtocol,
        Healthcheck = src.Healthcheck,
        Cmd = src.Cmd,
    };

    private static string? SecretPrefill(IConfiguration c, string key)
    {
        var v = c[key]?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
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
