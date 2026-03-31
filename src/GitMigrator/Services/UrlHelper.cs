namespace GitMigrator.Services;

/// <summary>
/// Helpers for normalising git remote URLs so that HTTPS and SSH variants of the same
/// repository can be matched against each other.
/// </summary>
public static class UrlHelper
{
    /// <summary>
    /// Normalise a git URL to a canonical, lowercase HTTPS URL (without .git suffix and
    /// without embedded credentials) so that different URL forms of the same repo compare equal.
    ///
    /// Examples:
    ///   git@github.com:user/repo.git  →  https://github.com/user/repo
    ///   https://token@github.com/user/repo.git  →  https://github.com/user/repo
    ///   ssh://git@github.com/user/repo  →  https://github.com/user/repo
    /// </summary>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        url = url.Trim();

        // Strip .git suffix
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // SCP-style SSH: git@host:path/to/repo  →  https://host/path/to/repo
        if (!url.Contains("://") && url.Contains('@') && url.Contains(':'))
        {
            var atIdx = url.IndexOf('@');
            var colonIdx = url.IndexOf(':', atIdx);
            if (colonIdx > atIdx)
            {
                var host = url[(atIdx + 1)..colonIdx];
                var path = url[(colonIdx + 1)..].TrimStart('/');
                url = $"https://{host}/{path}";
            }
        }

        // ssh:// or git+ssh:// → https://
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("git+ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var schemeEnd = url.IndexOf("://") + 3;
            var rest = url[schemeEnd..];
            // Strip optional "git@" user info
            var atIdx = rest.IndexOf('@');
            var slashIdx = rest.IndexOf('/');
            if (atIdx >= 0 && (slashIdx < 0 || atIdx < slashIdx))
                rest = rest[(atIdx + 1)..];
            url = $"https://{rest}";
        }

        // https:// with embedded credentials → strip them
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var afterScheme = url[8..];
            var atIdx = afterScheme.IndexOf('@');
            var firstSlash = afterScheme.IndexOf('/');
            if (atIdx >= 0 && (firstSlash < 0 || atIdx < firstSlash))
                url = "https://" + afterScheme[(atIdx + 1)..];
        }

        return url.ToLowerInvariant();
    }

    /// <summary>
    /// Builds an authenticated HTTPS clone URL by embedding the token as the password.
    /// For GitHub: https://TOKEN@github.com/user/repo.git
    /// For GitLab: https://oauth2:TOKEN@gitlab.com/user/repo.git
    /// </summary>
    public static string WithGitHubToken(string httpsUrl, string token)
    {
        if (string.IsNullOrEmpty(token))
            return httpsUrl;

        if (!httpsUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return httpsUrl;

        return "https://" + token + "@" + httpsUrl[8..];
    }

    /// <summary>GitLab uses oauth2 as the username for PAT authentication.</summary>
    public static string WithGitLabToken(string httpsUrl, string token)
    {
        if (string.IsNullOrEmpty(token))
            return httpsUrl;

        if (!httpsUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return httpsUrl;

        return "https://oauth2:" + token + "@" + httpsUrl[8..];
    }

    /// <summary>Strip embedded credentials from an HTTPS URL (for storing in git config).</summary>
    public static string StripCredentials(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        var afterScheme = url[8..];
        var atIdx = afterScheme.IndexOf('@');
        var firstSlash = afterScheme.IndexOf('/');
        if (atIdx >= 0 && (firstSlash < 0 || atIdx < firstSlash))
            return "https://" + afterScheme[(atIdx + 1)..];

        return url;
    }
}
