namespace TendrilDeploy.Services;

/// <summary>Non-secret deploy form defaults from <c>IConfiguration</c> (e.g. dotnet user-secrets under <c>TendrilDeploy:*</c>).</summary>
public readonly record struct TendrilDeployUiDefaults(
    string GitRepo,
    string Branch,
    string DockerfilePath,
    string DockerContext,
    string Port,
    string TendrilHome,
    string? VolumeId);

public static class TendrilDeploySettingsReader
{
    /// <summary>Fills UI defaults: config value wins when non-empty after trim; otherwise <paramref name="draft"/> / built-in fallbacks.</summary>
    public static TendrilDeployUiDefaults Read(
        IConfiguration config,
        DeployDraft draft,
        string defaultBranch,
        string defaultDockerfilePath)
    {
        string Co(string key, string fallback)
        {
            var v = config[key]?.Trim();
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        string? CoOpt(string key)
        {
            var v = config[key]?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        var branchFb = string.IsNullOrWhiteSpace(draft.Branch) ? defaultBranch : draft.Branch.Trim();
        var ctxFb = string.IsNullOrWhiteSpace(draft.DockerContext) ? "." : draft.DockerContext.Trim();
        var dfFb = string.IsNullOrWhiteSpace(draft.DockerfilePath) ? defaultDockerfilePath : draft.DockerfilePath.Trim();
        var repoFb = string.IsNullOrWhiteSpace(draft.RepoUrl) ? "" : draft.RepoUrl.Trim();

        return new TendrilDeployUiDefaults(
            GitRepo: Co("TendrilDeploy:GitRepo", repoFb),
            Branch: Co("TendrilDeploy:Branch", branchFb),
            DockerfilePath: Co("TendrilDeploy:DockerfilePath", dfFb),
            DockerContext: Co("TendrilDeploy:DockerContext", ctxFb),
            Port: Co("TendrilDeploy:Port", "8000"),
            TendrilHome: Co("TendrilDeploy:TendrilHome", "/data/tendril"),
            VolumeId: CoOpt("TendrilDeploy:VolumeId"));
    }
}
