using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ivy.Cli.Infrastructure;

/// <summary>
/// Base settings for all Sliplane commands.
/// Key priority: --api-key flag → SLIPLANE_API_KEY env var → ~/.ivy/config.json → interactive prompt.
/// </summary>
public class ApiSettings : CommandSettings
{
    [CommandOption("--api-key <KEY>")]
    [Description("Sliplane API key (or set SLIPLANE_API_KEY env var, or run once to save)")]
    public string? ApiKey { get; init; }

    [CommandOption("--org-id <ORG_ID>")]
    [Description("Organization ID for legacy tokens (or set SLIPLANE_ORG_ID env var)")]
    public string? OrgId { get; init; }

    public SliplaneClient CreateClient()
    {
        var key = ConfigStore.Resolve(
            flagValue: ApiKey,
            envVar:    Environment.GetEnvironmentVariable("SLIPLANE_API_KEY"),
            configKey: "sliplane_api_key",
            label:     "Sliplane API key",
            hint:      "Find it at [link]https://sliplane.io[/] → Team Settings → API Tokens",
            isSecret:  true);

        var org = OrgId
            ?? Environment.GetEnvironmentVariable("SLIPLANE_ORG_ID")
            ?? ConfigStore.Get("sliplane_org_id");

        return new SliplaneClient(key, org);
    }
}

/// <summary>
/// Base settings for all Tendril commands.
/// URL and key are resolved from flags → env vars → config file → interactive prompt.
/// </summary>
public class TendrilApiSettings : CommandSettings
{
    [CommandOption("--tendril-url <URL>")]
    [Description("Base URL of the tendril-deploy instance (or set TENDRIL_BASE_URL env var)")]
    public string? TendrilUrl { get; init; }

    [CommandOption("--tendril-api-key <KEY>")]
    [Description("API key for tendril-deploy (or set TENDRIL_API_KEY env var). Optional if server has no key.")]
    public string? TendrilApiKey { get; init; }

    public TendrilClient CreateTendrilClient()
    {
        var url = ConfigStore.Resolve(
            flagValue: TendrilUrl,
            envVar:    Environment.GetEnvironmentVariable("TENDRIL_BASE_URL"),
            configKey: "tendril_base_url",
            label:     "Tendril base URL",
            hint:      "e.g. [yellow]https://ivy-tendril-deployment.sliplane.app[/]",
            isSecret:  false);

        // API key is optional for Tendril — don't prompt, just check flag/env/config
        var key = TendrilApiKey
            ?? Environment.GetEnvironmentVariable("TENDRIL_API_KEY")
            ?? ConfigStore.Get("tendril_api_key");

        return new TendrilClient(url, key);
    }
}

