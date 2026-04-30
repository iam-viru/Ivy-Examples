# Tendril on Sliplane (demo)

Ivy app in the same spirit as [`sliplane-deploy`](../sliplane-deploy): sign in with **Sliplane**, pick a server, and **create a Git-based service**. This UI does **not** collect Anthropic/GitHub secrets — set **ANTHROPIC_API_KEY** and **GITHUB_TOKEN** in the Sliplane service environment after deploy (or edit the service there).

Default Git source: **[ArtemLazarchuk/Ivy-Tendril](https://github.com/ArtemLazarchuk/Ivy-Tendril)** branch `development`, Dockerfile **[`.github/docker/Dockerfile.tendril`](https://github.com/ArtemLazarchuk/Ivy-Tendril/tree/development/.github/docker)** (same layout as upstream [Ivy-Tendril](https://github.com/Ivy-Interactive/Ivy-Tendril)).

This folder contains **only** the Ivy deploy UI.

## Flow

1. Run `dotnet run` or `dotnet watch` in this folder (defaults to **port 5021** in `Properties/launchSettings.json`).
   - Port in use: `dotnet watch --find-available-port`, or stop the other process.
2. **Sign in with Sliplane** (or set `Sliplane:ApiToken` in user secrets / config).
3. Confirm **Git repository**, **branch**, Dockerfile path, and Docker context (defaults match the fork above).
4. Optionally set **Volume ID** for persistent storage at **TENDRIL_HOME**.
5. **Deploy** — then add runtime secrets in Sliplane if needed.

The Sliplane API ([ctrl.sliplane.io](https://ctrl.sliplane.io)) rejects environment variables with empty keys or values. This app only sends **PORT** and **TENDRIL_HOME** (+ any resolver extras such as **IVY_APP_DIR** when applicable).

### Pre-fill from `?repo=`

`/tendril-deploy-app?repo=https://github.com/ArtemLazarchuk/Ivy-Tendril/tree/development`

## Configuration

| Key | Purpose |
|-----|--------|
| `Sliplane:ApiToken` | Optional static API token (otherwise OAuth session) |
| `Sliplane:ManageServicesUrl` | Optional override for the “Manage services” link |

For **Tendril** itself, see [Ivy-Tendril](https://github.com/Ivy-Interactive/Ivy-Tendril).
