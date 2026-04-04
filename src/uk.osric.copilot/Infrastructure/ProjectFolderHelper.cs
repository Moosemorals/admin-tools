namespace uk.osric.copilot.Infrastructure {
    /// <summary>Helpers for enumerating project folders on the local filesystem.</summary>
    internal static class ProjectFolderHelper {
        /// <summary>
        /// Returns the paths of all direct subdirectories under <paramref name="root"/>
        /// that contain a <c>.git</c> directory (i.e. are git repositories).
        /// Returns an empty sequence when <paramref name="root"/> is null, whitespace,
        /// or does not exist.
        /// </summary>
        internal static IEnumerable<string> EnumerateGitRepositories(string? root) {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                return [];
            }
            return Directory.EnumerateDirectories(root)
                .Where(dir => Directory.Exists(Path.Combine(dir, ".git")));
        }
    }
}
