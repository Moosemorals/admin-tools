// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Infrastructure {
    internal static class ProjectFolderValidator {
        /// <summary>
        /// Validates that <paramref name="path"/> is an allowed project folder:
        /// it must be a direct child of <paramref name="projectFoldersPath"/> and
        /// must contain a <c>.git</c> directory.
        /// </summary>
        /// <returns>The canonical absolute path, or <c>null</c> if validation fails.</returns>
        internal static string? Validate(string projectFoldersPath, string path) {
            var root = projectFoldersPath;
            if (string.IsNullOrWhiteSpace(root)) {
                return null;
            }

            // Resolve to canonical absolute paths to prevent path-traversal attacks.
            string canonical;
            try {
                canonical = Path.GetFullPath(path);
            } catch {
                return null;
            }

            // Append the separator so that prefix matching cannot accidentally match a sibling
            // folder whose name starts with the root folder's name.
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

            if (!canonical.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) {
                return null;
            }

            // Only direct children are allowed — reject paths with an extra separator.
            var relative = canonical[rootFull.Length..];
            if (relative.Contains(Path.DirectorySeparatorChar)) {
                return null;
            }

            if (!Directory.Exists(canonical)) {
                return null;
            }
            if (!Directory.Exists(Path.Combine(canonical, ".git"))) {
                return null;
            }

            return canonical;
        }
    }
}
