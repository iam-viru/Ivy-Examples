namespace TendrilDeploy.Services;

using System.Globalization;
using System.Linq;

/// <summary>
/// Maps URLs to indexed env vars and a POSIX <c>sh</c> prelude that shallow-clones into <c>TENDRIL_HOME</c> before <c>tendril --web</c>.
/// Target folder is derived as <c>repos/&lt;owner&gt;/&lt;repository&gt;</c> under <c>TENDRIL_HOME</c> (remote default branch, <c>git clone --depth 1</c>).
/// </summary>
public static class TendrilCloneBootstrap
{
    public const string CountEnvKey = "TENDRIL_CLONE_COUNT";

    public static string RepoUrlEnv(int index) =>
        $"TENDRIL_CLONE_{index.ToString(CultureInfo.InvariantCulture)}_URL";

    public static string RepoPathEnv(int index) =>
        $"TENDRIL_CLONE_{index.ToString(CultureInfo.InvariantCulture)}_PATH";

    /// <summary>Only the GitHub remote URL comes from UI; workspace path under <c>TENDRIL_HOME</c> is derived.</summary>
    public sealed record Row(string CloneUrl);

    public static IEnumerable<Row> RowsWithCloneIntent(IEnumerable<Row> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.CloneUrl));

    public static List<Models.EnvironmentVariable> BuildCloneEnvVars(IReadOnlyCollection<Row> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<(string Url, string RelPath)>();

        foreach (var r in RowsWithCloneIntent(rows))
        {
            var u = r.CloneUrl.Trim();
            if (!seen.Add(u))
                continue;

            EnsureGitHubRemoteUrl(u);
            if (!TryDeriveReposRelativePath(u, out var relPath, out var err))
                throw new ArgumentException(err ?? "Could not derive a workspace folder from the GitHub URL.");
            resolved.Add((u, relPath));
        }

        if (resolved.Count == 0)
            return [];

        var list = new List<Models.EnvironmentVariable>
        {
            new(CountEnvKey, resolved.Count.ToString(CultureInfo.InvariantCulture), Secret: false)
        };

        for (var i = 0; i < resolved.Count; i++)
        {
            list.Add(new Models.EnvironmentVariable(RepoUrlEnv(i), resolved[i].Url, Secret: false));
            list.Add(new Models.EnvironmentVariable(RepoPathEnv(i), resolved[i].RelPath, Secret: false));
        }

        return list;
    }

    /// <inheritdoc cref="IsGitHubRepoRemote" />
    public static bool TryDeriveReposRelativePath(string trimmedGitHubUrl, out string relativePath,
        out string? error)
    {
        relativePath = "";
        error = null;
        var t = (trimmedGitHubUrl ?? "").Trim();
        if (t.Length == 0)
        {
            error = "GitHub URL is empty.";
            return false;
        }

        if (!TryParseOwnerRepo(t, out var owner, out var repo))
        {
            error = "Could not read owner/name from URL — use https://github.com/org/repository.";
            return false;
        }

        if (!SafePathSegment(owner) || !SafePathSegment(repo))
        {
            error = "Derived owner or repository name contains invalid path characters.";
            return false;
        }

        relativePath = $"repos/{owner}/{repo}";
        return true;
    }

    private static bool SafePathSegment(string s) =>
        s.Length > 0 && !s.Contains('/', StringComparison.Ordinal) && !s.Contains("..", StringComparison.Ordinal);

    private static bool TryParseOwnerRepo(string t, out string owner, out string repo)
    {
        owner = "";
        repo = "";

        if (t.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            var tail = t["git@github.com:".Length..].TrimStart('/');
            var slash = tail.IndexOf('/');
            if (slash < 1 || slash >= tail.Length - 1)
                return false;
            owner = tail[..slash];
            repo = TrimGitSuffix(tail[(slash + 1)..]);
            return owner.Length > 0 && repo.Length > 0;
        }

        if (t.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
        {
            var tail = t["ssh://git@github.com/".Length..].TrimStart('/');
            var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                return false;
            owner = parts[0];
            repo = TrimGitSuffix(parts[1]);
            return true;
        }

        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathParts.Length < 2)
            return false;

        owner = pathParts[0];
        repo = TrimGitSuffix(pathParts[1]);

        foreach (var p in pathParts)
        {
            if (string.Equals(p, "..", StringComparison.Ordinal))
                return false;
        }

        return owner.Length > 0 && repo.Length > 0;
    }

    private static string TrimGitSuffix(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? n[..^".git".Length] : n;
    }

    public static bool IsGitHubRepoRemote(string url, out string? reason)
    {
        reason = null;
        var t = (url ?? "").Trim();
        if (t.Length == 0)
        {
            reason = "URL is empty.";
            return false;
        }

        if (t.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            var tail = t.Substring("git@github.com:".Length).TrimStart('/');
            if (tail.Replace(".git", "", StringComparison.OrdinalIgnoreCase).Split('/').Length < 2)
            {
                reason =
                    "SSH URL must look like git@github.com:org/repository or git@github.com:org/repository.git.";
                return false;
            }

            return true;
        }

        if (t.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
        {
            var pathPart = t.Substring("ssh://git@github.com/".Length).TrimStart('/');
            var seg = pathPart.Replace(".git", "", StringComparison.OrdinalIgnoreCase)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 2)
            {
                reason = "SSH URL must include owner and repo, e.g. ssh://git@github.com/org/repository.git.";
                return false;
            }

            return true;
        }

        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri))
        {
            reason = "Enter a GitHub URL (https://github.com/org/repo) or SSH (git@github.com:org/repo.git).";
            return false;
        }

        var hostOk = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);
        if (!hostOk || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            reason =
                "Only GitHub clones are supported. Use https://github.com/org/repository or git@github.com:org/repository.git.";
            return false;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            reason = "GitHub URL must include organization (or user) and repository, e.g. https://github.com/org/repo.";
            return false;
        }

        return true;
    }

    internal static void EnsureGitHubRemoteUrl(string trimmedUrl)
    {
        if (!IsGitHubRepoRemote(trimmedUrl, out var reason))
            throw new ArgumentException(reason ?? "Not a GitHub repository URL.");
    }

    /// <summary>POSIX sh prelude: clones then <c>exec tendril --web</c>.</summary>
    public static readonly string ShellStartupPrelude = BuildShellStartupPrelude();

    private static string BuildShellStartupPrelude() =>
        """
set -eu
HOME="${HOMEDIR:-$(getent passwd "$(id -u)" | cut -d: -f6)}"
home="${TENDRIL_HOME:-/data/tendril}"
home="${home%/}"

COUNT_RAW="${TENDRIL_CLONE_COUNT:-0}"
case "${COUNT_RAW}" in ''|'-'*|*[!0-9]*) COUNT=0 ;; *) COUNT="${COUNT_RAW}" ;; esac

i=0
while [ "${i}" -lt "${COUNT}" ]; do
  unk="TENDRIL_CLONE_${i}_URL"
  pnk="TENDRIL_CLONE_${i}_PATH"
  eval "clone_url=\$$unk"
  eval "rel_path=\$$pnk"

  if [ -z "${clone_url}" ] || [ -z "${rel_path}" ]; then
    echo "tendril-bootstrap: clone index ${i}: missing URL or PATH env" >&2
    exit 1
  fi
  case "${rel_path}" in *..*)
    echo "tendril-bootstrap: path must not contain '..': '${rel_path}'" >&2
    exit 1 ;;
  esac

  dest="${home}/${rel_path}"
  dest="${dest#//}"
  case "${dest}" in
    "${home}"|"${home}"/*) ;;
    *)
      echo "tendril-bootstrap: path escapes TENDRIL_HOME: '${rel_path}'" >&2
      exit 1 ;;
  esac

  if [ -d "${dest}/.git" ]; then
    i=$((i + 1))
    continue
  fi

  mkdir -p "$(dirname "${dest}")"
  git clone --depth 1 "${clone_url}" "${dest}"
  i=$((i + 1))
done
exec tendril --web
""".Replace("\r\n", "\n");

    public static string BuildServiceCmdWrapped() =>
        "sh -lc " + QuoteForSingleQuotedShell(ShellStartupPrelude);

    public static string QuoteForSingleQuotedShell(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "''";

        var nl = s.Replace("\r\n", "\n").Replace('\r', '\n');
        return "'" + nl.Replace("'", "'\"'\"'") + "'";
    }
}
