using Ivy.Auth.Sliplane;
using TendrilDeploy.Api;
using TendrilDeploy.Apps;
using TendrilDeploy.Services;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

var server = new Server();

server.Services.AddHttpClient("Ivy", client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "Ivy-TendrilDeploy/1.0");
});

server.Services.AddHttpClient("GitHubRaw", client =>
{
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Ivy-TendrilDeploy/1.0");
});

server.Services.AddSingleton<GitHubDockerfilePathResolver>();
server.Services.AddSingleton(server.Configuration);
server.Services.AddHttpContextAccessor();
server.Services.AddScoped<DeploymentDraftStore>();
server.Services.AddScoped<SliplaneApiClient>();

Server.ConfigureAuthCookieOptions = options =>
{
    options.Expires = DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(30));
};

server.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RepoCaptureFilter>();
server.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TendrilApiStartupFilter>();
server.Services.AddScoped<TendrilDeployService>();
server.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title   = "Tendril Deploy API";
        doc.Info.Version = "v1";
        doc.Info.Description =
            "Programmatic Tendril instance deployment on Sliplane. " +
            "Authenticate via the X-Api-Key header (set TendrilDeploy:ApiKey in config).";
        return Task.CompletedTask;
    });
});

#if DEBUG
server.UseHotReload();
#endif
server.AddAppsFromAssembly();
server.AddConnectionsFromAssembly();

server.UseAuth<SliplaneAuthProvider>();

var appShellSettings = new AppShellSettings()
    .DefaultApp<TendrilDeployApp>()
    .UseTabs(preventDuplicates: true);
server.UseAppShell(appShellSettings);

await server.RunAsync();
