namespace TendrilDeploy.Apps;

using TendrilDeploy;
using TendrilDeploy.Services;

/// <summary>Default Git source for Ivy Tendril (Dockerfile is expected from that repo or your fork).</summary>
public static class TendrilDeployDefaults
{
    public const string DefaultRepoUrl = "https://github.com/ArtemLazarchuk/Ivy-Tendril";
    public const string DefaultBranch = "development";

    /// <summary>Pre-filled draft when no <c>?repo=</c> and no cookie draft.</summary>
    public static DeployDraft InitialDraft => new(
        DefaultRepoUrl,
        DefaultBranch,
        DockerContext: ".",
        DockerfilePath: TendrilDeploymentPaths.DefaultDockerfilePath);
}
