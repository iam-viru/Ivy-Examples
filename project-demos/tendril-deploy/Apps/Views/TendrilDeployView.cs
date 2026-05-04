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

public class TendrilBasicAuthFormModel
{
    [Display(Name = "Username", Prompt = "e.g. operator")]
    [Required(ErrorMessage = "Username is required")]
    public string Username { get; set; } = "";

    [Display(Name = "Password")]
    [Required(ErrorMessage = "Password is required")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters")]
    public string Password { get; set; } = "";
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
        var copilotGhToken = UseState<string?>(() => SecretPrefill(config, "TendrilDeploy:CopilotGhToken"));

        var basicAuthModel = UseState(() => new TendrilBasicAuthFormModel());
        var basicAuthValidationFailed = UseState(false);
        var basicAuthStepError = UseState<string?>(() => null);

        var reloadCounter = UseState(0);
        var stepIndex = UseState(0);
        /// <summary>
        /// Step 1 hides the main form controls; Ivy form validation relies on mounted inputs,
        /// so we freeze the validated step-0 model and deploy from this snapshot.
        /// </summary>
        var validatedDeployForm = UseState<TendrilDeployFormModel?>(() => null);
        var repoCloneRows = UseState(() => new List<TendrilBootstrapRepoEntry>
        {
            new(Guid.NewGuid(), "")
        });
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

        var (onBasicAuthSubmit, basicAuthFormView, basicAuthValidationView, _) = UseForm(() => basicAuthModel
            .ToForm("Basic auth")
            .Builder(m => m.Password,
                s => s.ToPasswordInput(placeholder: $"At least {TendrilBasicAuthBootstrap.MinPasswordLength} characters"))
            .Required(m => m.Username, m => m.Password));

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
            basicAuthStepError.Set(null);
            deployedService.Set(null);
            validationFailed.Set(false);
            var m = validatedDeployForm.Value;
            if (m == null)
            {
                deployError.Set("Form state expired. Go Back, then continue from step 1 again.");
                stepIndex.Set(0);
                return;
            }

            List<EnvironmentVariable> cloneEnvVars;
            try
            {
                cloneEnvVars = TendrilCloneBootstrap.BuildCloneEnvVars(
                    repoCloneRows.Value
                        .Select(e => new TendrilCloneBootstrap.Row(e.CloneUrl))
                        .ToList());
            }
            catch (ArgumentException ex)
            {
                deployError.Set(ex.Message);
                return;
            }

            var anthropic = (anthropicKey.Value ?? "").Trim();
            var claudeOAuth = (claudeOAuthToken.Value ?? "").Trim();
            var github = (githubToken.Value ?? "").Trim();
            var openAi = (openAiKey.Value ?? "").Trim();
            var gemini = (geminiKey.Value ?? "").Trim();
            var copilotGh = (copilotGhToken.Value ?? "").Trim();

            (string Users, string HashSecret, string JwtSecret) basicAuthEnv;
            try
            {
                basicAuthEnv = TendrilBasicAuthBootstrap.BuildSecrets(
                    basicAuthModel.Value.Username ?? "", basicAuthModel.Value.Password ?? "");
            }
            catch (ArgumentException ex)
            {
                deployError.Set(ex.Message);
                basicAuthStepError.Set(ex.Message);
                basicAuthValidationFailed.Set(true);
                stepIndex.Set(3);
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
                if (github.Length > 0)
                    envVars.Add(new EnvironmentVariable("GITHUB_TOKEN", github, Secret: true));
                if (openAi.Length > 0)
                    envVars.Add(new EnvironmentVariable("OPENAI_API_KEY", openAi, Secret: true));
                if (gemini.Length > 0)
                    envVars.Add(new EnvironmentVariable("GEMINI_API_KEY", gemini, Secret: true));
                if (copilotGh.Length > 0)
                    envVars.Add(new EnvironmentVariable("GH_TOKEN", copilotGh, Secret: true));
                envVars.Add(new EnvironmentVariable("BasicAuth__Users", basicAuthEnv.Users, Secret: true));
                envVars.Add(new EnvironmentVariable("BasicAuth__HashSecret", basicAuthEnv.HashSecret,
                    Secret: true));
                envVars.Add(new EnvironmentVariable("BasicAuth__JwtSecret", basicAuthEnv.JwtSecret,
                    Secret: true));

                envVars.Add(new EnvironmentVariable("PORT", port, Secret: false));
                envVars.Add(new EnvironmentVariable("TENDRIL_HOME", home, Secret: false));
                foreach (var ev in cloneEnvVars)
                    envVars.Add(ev);

                var cmdForService = cloneEnvVars.Count > 0
                    ? TendrilCloneBootstrap.BuildServiceCmdWrapped()
                    : null;

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
                        cmd: string.IsNullOrEmpty(cmdForService)
                            ? (m.Cmd ?? string.Empty)
                            : cmdForService,
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

        var copilotExpandable = new Expandable(
            "GitHub Copilot CLI",
            Layout.Vertical().Gap(3).Width(Size.Full())
                | Text.Muted("Copilot in the terminal authenticates with GitHub (PAT), not a separate Copilot API key.")
                | Text.Markdown(
                    "**Where to get:** GitHub → **Settings → Developer settings** → Personal access tokens (classic or fine-grained). The account needs an active **GitHub Copilot** subscription (or org-assigned Copilot). Grant the scopes the [Copilot CLI docs](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/use-copilot-cli) require for your setup (often overlaps with **repo** / **read:user**). Copy the token and paste it below.")
                | Text.Markdown(
                    "**This form sends:** **`GH_TOKEN`** when non-empty. GitHub’s CLI stack treats **`GH_TOKEN`** like **`GITHUB_TOKEN`**; use the **GitHub — gh CLI** section above for **`GITHUB_TOKEN`**, or paste the **same PAT** here if you only want Copilot-oriented tooling to see **`GH_TOKEN`**.")
                | Text.Muted("Optional.")
                | copilotGhToken.ToPasswordInput(placeholder: "ghp_… or fine-grained PAT").WithField()
                    .Label("GitHub token for Copilot CLI (optional)")
        ).Icon(Icons.Terminal).Width(Size.Full());

        var agentSections = Layout.Vertical().Gap(2).Width(Size.Full())
            | claudeExpandable
            | githubExpandable
            | openAiExpandable
            | geminiExpandable
            | copilotExpandable;

        var basicAuthHintCallout = new Callout(
            Text.Markdown(
                """
                These secrets are added to the **Sliplane service you create** (the repo you deploy), **not** this deploy wizard.
                We set `BasicAuth__Users`, `BasicAuth__HashSecret`, and `BasicAuth__JwtSecret` on that service.

                For login on the deployed app, its `Program.cs` must call `server.UseAuth<BasicAuthProvider>()`.
                """.ReplaceLineEndings("\n")),
            "Basic authentication",
            CalloutVariant.Info);

        object basicAuthSections = Layout.Vertical().Gap(3).Width(Size.Full())
            | basicAuthHintCallout
            | basicAuthFormView
            | (basicAuthValidationFailed.Value
                ? new Callout(basicAuthValidationView, "Please fill required auth fields", CalloutVariant.Error)
                : new Empty())
            | (basicAuthStepError.Value != null
                ? new Callout(basicAuthStepError.Value, variant: CalloutVariant.Error)
                : new Empty());

        var stepperItems = new[]
        {
            new StepperItem("1", stepIndex.Value > 0 ? Icons.Check : null, "Welcome", "Server & name"),
            new StepperItem("2", stepIndex.Value > 1 ? Icons.Check : null, "Secrets", "API keys"),
            new StepperItem("3", stepIndex.Value > 2 ? Icons.Check : null, "Repositories", "Workspaces"),
            new StepperItem("4", deployedService.Value != null ? Icons.Check : null, "Login", "Basic auth"),
        };

        ValueTask OnStepperSelect(Event<Stepper, int> e)
        {
            if (e.Value < stepIndex.Value)
            {
                stepIndex.Set(e.Value);
                if (e.Value == 0)
                {
                    validatedDeployForm.Set(null);
                    repoCloneRows.Set([new TendrilBootstrapRepoEntry(Guid.NewGuid(), "")]);
                }
            }

            return ValueTask.CompletedTask;
        }

        var reposHintCallout = new Callout(
            Text.Markdown(
                """
                Each filled row is one repo cloned when the container starts. In Tendril project settings, **Your repository folder** must be the **path inside the container** (the folder that contains `.git`), not the clone URL.

                **Example**

                - `TENDRIL_HOME` is `/data/tendril`
                - This row is `https://github.com/acme/hello-world`
                - After startup the repo is at `/data/tendril/repos/acme/hello-world`
                - So in **Your repository folder** enter: `/data/tendril/repos/acme/hello-world`
                """.ReplaceLineEndings("\n")),
            "Repositories",
            CalloutVariant.Info);

        object repoSections = Layout.Vertical().Gap(4).Width(Size.Full())
            | Text.H4("Repositories")
            | Text.Block("One URL per row. Leave empty to skip. Add adds another row.")
            | (Layout.Vertical().Gap(2).Width(Size.Full())
                | repoCloneRows.Value.Select(e => (object)new TendrilBootstrapRepoRowView(e.Id, repoCloneRows))
                    .ToArray())
            | new Button("Add").Icon(Icons.Plus).Outline()
                .OnClick(_ =>
                {
                    repoCloneRows.Set(xs =>
                    {
                        var copy = xs.ToList();
                        copy.Add(new TendrilBootstrapRepoEntry(Guid.NewGuid(), ""));
                        return copy;
                    });
                    return ValueTask.CompletedTask;
                });
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

        object titleBlock = stepIndex.Value switch
        {
            0 => Layout.Vertical().Gap(2).AlignContent(Align.Center)
                | Text.H1("Welcome to Ivy Tendril").Align(TextAlignment.Center),
            1 => Layout.Vertical().Gap(2).AlignContent(Align.Center)
                | Text.H1("API keys").Align(TextAlignment.Center),
            2 => Layout.Vertical().Gap(2).AlignContent(Align.Center)
                | Text.H1("Repositories").Align(TextAlignment.Center),
            _ => Layout.Vertical().Gap(2).AlignContent(Align.Center)
                | Text.H1("Protect your deployment").Align(TextAlignment.Center),
        };

        object stepBody = stepIndex.Value switch
        {
            0 => Layout.Vertical().Gap(4).Width(Size.Full())
                | welcomeNoteCallout
                | formView
                | validationBlock,
            1 => Layout.Vertical().Gap(4).Width(Size.Full())
                | secretsHintCallout
                | agentSections,
            2 => Layout.Vertical().Gap(4).Width(Size.Full())
                | reposHintCallout
                | repoSections,
            _ => Layout.Vertical().Gap(4).Width(Size.Full())
                | basicAuthSections,
        };

        object footerRow = stepIndex.Value switch
        {
            0 => Layout.Vertical().Width(Size.Full()).AlignContent(Align.Center)
                | new Button("Get started")
                    .Icon(Icons.ChevronRight, Align.Right)
                    .Primary()
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Full())
                    .Loading(loading)
                    .Disabled(loading)
                    .OnClick(async _ => await AdvanceFromStep0Async()),
            1 => Layout.Horizontal().Width(Size.Full()).Gap(4)
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
                        repoCloneRows.Set([new TendrilBootstrapRepoEntry(Guid.NewGuid(), "")]);
                        return ValueTask.CompletedTask;
                    })
                | new Spacer()
                | new Button("Continue")
                    .Icon(Icons.ChevronRight, Align.Right)
                    .Primary()
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Fraction(0.31f))
                    .OnClick(_ =>
                    {
                        basicAuthStepError.Set(null);
                        stepIndex.Set(2);
                        return ValueTask.CompletedTask;
                    }),
            2 => Layout.Horizontal().Width(Size.Full()).Gap(4)
                | new Button("Back")
                    .Icon(Icons.ChevronLeft)
                    .Variant(ButtonVariant.Outline)
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Fraction(0.31f))
                    .OnClick(_ =>
                    {
                        stepIndex.Set(1);
                        return ValueTask.CompletedTask;
                    })
                | new Spacer()
                | new Button("Continue")
                    .Icon(Icons.ChevronRight, Align.Right)
                    .Primary()
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Fraction(0.31f))
                    .OnClick(async _ =>
                    {
                        basicAuthValidationFailed.Set(false);
                        stepIndex.Set(3);
                        await Task.CompletedTask;
                    }),
            _ => Layout.Horizontal().Width(Size.Full()).Gap(4)
                | new Button("Back")
                    .Icon(Icons.ChevronLeft)
                    .Variant(ButtonVariant.Outline)
                    .Large()
                    .BorderRadius(BorderRadius.Full)
                    .Width(Size.Fraction(0.31f))
                    .OnClick(_ =>
                    {
                        stepIndex.Set(2);
                        return ValueTask.CompletedTask;
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
                    .OnClick(async _ =>
                    {
                        basicAuthStepError.Set(null);
                        basicAuthValidationFailed.Set(false);
                        if (!await onBasicAuthSubmit())
                        {
                            basicAuthValidationFailed.Set(true);
                            return;
                        }

                        await HandleDeploy();
                    }),
        };

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
