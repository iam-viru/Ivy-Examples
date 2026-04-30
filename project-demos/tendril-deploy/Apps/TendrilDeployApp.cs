namespace TendrilDeploy.Apps;

using TendrilDeploy.Apps.Views;
using TendrilDeploy.Models;
using TendrilDeploy.Services;

/// <summary>
/// One-click Tendril deploy to Sliplane from Git (default fork with <c>.github/docker/Dockerfile.tendril</c>).
/// <c>?repo=</c> is captured by <see cref="RepoCaptureFilter"/> the same way as sliplane-deploy.
/// </summary>
[App(
    id: "tendril-deploy-app",
    icon: Icons.Container,
    title: "Tendril on Sliplane",
    isVisible: true)]
public class TendrilDeployApp : ViewBase
{
    public override object? Build()
    {
        var config = UseService<IConfiguration>();
        var auth = UseService<IAuthService>();
        var draftStore = UseService<DeploymentDraftStore>();
        var args = UseArgs<TendrilDeployArgs>();
        var client = UseService<SliplaneApiClient>();

        var draftState = UseState<DeployDraft?>(() =>
            args is not null
                ? DeploymentDraftStore.ParseGitHubUrl(args.Repo)
                : draftStore.LastDraft ?? TendrilDeployDefaults.InitialDraft);

        var firstServerQuery = UseQuery<SliplaneServer?, (string, string)>(
            key: ("tendril-default-server", config["Sliplane:ApiToken"] ?? auth.GetAuthSession().AuthToken?.AccessToken ?? string.Empty),
            fetcher: async (key, ct) => (await client.GetServersAsync(key.Item2)).FirstOrDefault());

        var ivyProjectQuery = UseQuery<SliplaneProject?, (string, bool)>(
            key: (config["Sliplane:ApiToken"] ?? auth.GetAuthSession().AuthToken?.AccessToken ?? string.Empty, draftState.Value is not null),
            fetcher: async (key, ct) =>
            {
                var (token, needIvy) = key;
                if (!needIvy) return null;
                var projects = await client.GetProjectsAsync(token);
                var ivy = projects.FirstOrDefault(p => p.Name.Equals("Ivy", StringComparison.OrdinalIgnoreCase));
                return ivy ?? await client.CreateProjectAsync(token, "Ivy");
            });

        var firstProjectQuery = UseQuery<SliplaneProject?, (string, string)>(
            key: ("tendril-default-project", config["Sliplane:ApiToken"] ?? auth.GetAuthSession().AuthToken?.AccessToken ?? string.Empty),
            fetcher: async (key, ct) => (await client.GetProjectsAsync(key.Item2)).FirstOrDefault());

        var serverLookupPreload = UseQuery<Option<string>?, (string, string?, int)>(
            key: ("tendril-server-lookup", string.IsNullOrEmpty(firstServerQuery.Value?.Id ?? "") ? null : firstServerQuery.Value!.Id, 0),
            fetcher: async _ =>
            {
                var currentServerId = firstServerQuery.Value?.Id ?? "";
                return string.IsNullOrEmpty(currentServerId)
                    ? null
                    : new Option<string>(firstServerQuery.Value!.Name, currentServerId);
            });

        var projectLookupPreload = UseQuery<Option<string>?, (string, string?, int)>(
            key: ("tendril-project-lookup", string.IsNullOrEmpty((draftState.Value is not null ? ivyProjectQuery.Value?.Id : firstProjectQuery.Value?.Id) ?? "") ? null : (draftState.Value is not null ? ivyProjectQuery.Value?.Id : firstProjectQuery.Value?.Id), 0),
            fetcher: async _ =>
            {
                var currentNeedIvyProject = draftState.Value is not null;
                var currentProjectId = (currentNeedIvyProject ? ivyProjectQuery.Value?.Id : firstProjectQuery.Value?.Id) ?? "";
                return string.IsNullOrEmpty(currentProjectId)
                    ? null
                    : new Option<string>((currentNeedIvyProject ? ivyProjectQuery.Value?.Name : firstProjectQuery.Value?.Name) ?? "Ivy", currentProjectId);
            });

        var session = auth.GetAuthSession();
        var apiToken = config["Sliplane:ApiToken"]
                       ?? session.AuthToken?.AccessToken
                       ?? string.Empty;
        var draft = draftState.Value ?? TendrilDeployDefaults.InitialDraft;
        var needIvyProject = draftState.Value is not null;
        var ivyProject = needIvyProject ? ivyProjectQuery.Value : null;
        var preServerId = firstServerQuery.Value?.Id ?? "";
        var preProjectId = (needIvyProject ? ivyProject?.Id : firstProjectQuery.Value?.Id) ?? "";

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            return Layout.Center()
                | (Layout.Vertical().AlignContent(Align.Center).Gap(6)
                    | Text.H2("Deploy Tendril to Sliplane")
                    | Text.Muted($"Default repository: {TendrilDeployDefaults.DefaultRepoUrl}")
                    | Text.Muted("Sign in to deploy. Git/Docker settings use user-secrets; the next screen asks for server, name, and API keys."));
        }

        var serversReady = !firstServerQuery.Loading || firstServerQuery.Value != null;
        var projectsReady = needIvyProject
            ? (!ivyProjectQuery.Loading || ivyProjectQuery.Value != null)
            : (!firstProjectQuery.Loading || firstProjectQuery.Value != null);
        var serverLkpReady = string.IsNullOrEmpty(preServerId) || !serverLookupPreload.Loading || serverLookupPreload.Value != null;
        var projectLkpReady = string.IsNullOrEmpty(preProjectId) || !projectLookupPreload.Loading || projectLookupPreload.Value != null;

        if (!serversReady || !projectsReady || !serverLkpReady || !projectLkpReady)
            return Layout.Center() | Text.Muted("Loading…");

        var needDefaults = true;
        var defaultServerId = needDefaults ? preServerId : (firstServerQuery.Value?.Id ?? "");
        var defaultProjectId = needDefaults ? preProjectId : ((needIvyProject ? ivyProject?.Id : firstProjectQuery.Value?.Id) ?? "");

        return new TendrilDeployView(apiToken, draft, defaultServerId, defaultProjectId);
    }
}

/// <summary>Navigation with <c>?repo=</c> to pre-fill Git source.</summary>
public record TendrilDeployArgs(string Repo);
