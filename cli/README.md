# Ivy CLI

Unified command-line tool for Ivy Interactive infrastructure. Manage Sliplane servers, services, projects, and deploy Tendril instances — all from one `ivy-examples` command.

## Installation

```bash
# From source (in cli/src/)
dotnet tool install -g --add-source ./nupkg Ivy.Cli   # global command: ivy-examples

# Or run directly without installing
dotnet run -- <command> [options]
```

## Authentication

### Sliplane commands

```bash
export SLIPLANE_API_KEY=your-sliplane-api-token
# Optional for legacy tokens:
export SLIPLANE_ORG_ID=your-org-id
```

### Tendril commands

```bash
export TENDRIL_BASE_URL=https://your-tendril-deploy.sliplane.app
export TENDRIL_API_KEY=your-internal-api-key   # optional if server has no key set
export SLIPLANE_API_KEY=your-sliplane-token    # also needed for status/servers/projects
```

## Commands

Commands are grouped by project: `ivy-examples sliplane …` for Sliplane resources and `ivy-examples tendril …` for Tendril deployments. CLI-wide settings live under `ivy-examples config …`.

### Sliplane

```bash
ivy-examples sliplane me                                          # current identity

ivy-examples sliplane projects list
ivy-examples sliplane projects create --name my-project
ivy-examples sliplane projects delete --project-id abc123

ivy-examples sliplane servers list
ivy-examples sliplane servers get --server-id srv_123
ivy-examples sliplane servers create --name my-server --instance-type base --location fsn
ivy-examples sliplane servers metrics --server-id srv_123 --range 1h

ivy-examples sliplane services list                               # across all projects
ivy-examples sliplane services list --project-id proj_123
ivy-examples sliplane services get --project-id proj_123 --service-id svc_456
ivy-examples sliplane services create --project-id proj_123 --name my-app --server-id srv_123 --repo https://github.com/org/repo --public
ivy-examples sliplane services deploy --project-id proj_123 --service-id svc_456
ivy-examples sliplane services logs --project-id proj_123 --service-id svc_456
ivy-examples sliplane services pause --project-id proj_123 --service-id svc_456
ivy-examples sliplane services delete --project-id proj_123 --service-id svc_456

ivy-examples sliplane credentials list
ivy-examples sliplane credentials create --name ghcr-creds --type ghcr --username myuser --token ghp_xxx

ivy-examples sliplane oauth list
ivy-examples sliplane oauth get --client-id oauth_123
```

### Tendril

```bash
# List available Sliplane targets
ivy-examples tendril servers
ivy-examples tendril projects

# Deploy a new Tendril instance
ivy-examples tendril deploy \
  --project-id proj_123 \
  --server-id srv_456 \
  --name tendril-artem \
  --username artem \
  --password supersecret \
  --anthropic-key sk-ant-xxx \
  --github-token ghp_xxx \
  --repo https://github.com/Ivy-Interactive/Ivy-Examples

# Check deployment status
ivy-examples tendril status \
  --project-id proj_123 \
  --service-id svc_789
```

## Output

All commands output YAML — easy to read and pipe into other tools.

## Adding new commands

1. Create a new folder under `src/Commands/YourThing/`
2. Add a class inheriting `AsyncCommand<Settings>` (copy any existing command as template)
3. Register it in `Program.cs` under the appropriate top-level branch (`sliplane`, `tendril`, …) with `branch.AddBranch("yourthing", ...)` or `branch.AddCommand<>()`. Add a new top-level branch only when introducing a new project namespace.

### Description style

Keep `WithDescription(...)` strings short, imperative, and consistent. Don't put usage examples there — those belong in this README.

| Verb              | Description pattern         | Example                       |
| ----------------- | --------------------------- | ----------------------------- |
| top-level branch  | `<Action> <project> <scope>`| `Manage Sliplane resources`   |
| sub-branch        | `Manage <resource-plural>`  | `Manage servers`              |
| `list`            | `List <resource-plural>`    | `List servers`                |
| `get`             | `Get <resource>`            | `Get server`                  |
| `create`          | `Create <resource>`         | `Create server`               |
| `update`          | `Update <resource>`         | `Update server`               |
| `delete`          | `Delete <resource>`         | `Delete server`               |
| anything else     | `<Verb> <resource>`         | `Pause service`, `Rescale server` |

Inside a sub-branch, omit the parent name (the path already shows it): write `List servers`, not `List Sliplane servers`.

## Building

```bash
cd cli/src
dotnet build
dotnet run -- --help
```

## Packaging as dotnet tool

```bash
cd cli/src
dotnet pack -c Release -o ../nupkg
dotnet tool install -g --add-source ../nupkg Ivy.Cli   # global command: ivy-examples
ivy-examples --help
```
