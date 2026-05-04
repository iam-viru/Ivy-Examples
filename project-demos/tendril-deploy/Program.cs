using Ivy.Auth.Sliplane;
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
