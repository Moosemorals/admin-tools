// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Tests.Unit {
    using NUnit.Framework;
    using uk.osric.copilot.Infrastructure;

    [TestFixture]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class ProjectFolderValidatorTests {
        private string _tempRoot = null!;
        private string _projectDir = null!;

        [SetUp]
        public void SetUp() {
            _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            _projectDir = Path.Combine(_tempRoot, "MyProject");
            Directory.CreateDirectory(Path.Combine(_projectDir, ".git"));
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        [Test]
        public void Validate_ReturnsCanonicalPath_WhenPathIsValidGitRepo() {
            var result = ProjectFolderValidator.Validate(_tempRoot, _projectDir);

            Assert.That(result, Is.EqualTo(Path.GetFullPath(_projectDir)));
        }

        [Test]
        public void Validate_ReturnsNull_WhenPathIsOutsideRoot() {
            var outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(outside, ".git"));
            try {
                var result = ProjectFolderValidator.Validate(_tempRoot, outside);
                Assert.That(result, Is.Null);
            } finally {
                if (Directory.Exists(outside)) {
                    Directory.Delete(outside, recursive: true);
                }
            }
        }

        [Test]
        public void Validate_ReturnsNull_WhenPathIsNestedChildOfRoot() {
            var nested = Path.Combine(_projectDir, "subdir");
            Directory.CreateDirectory(Path.Combine(nested, ".git"));

            var result = ProjectFolderValidator.Validate(_tempRoot, nested);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Validate_ReturnsNull_WhenDirectoryDoesNotExist() {
            var missing = Path.Combine(_tempRoot, "Missing");

            var result = ProjectFolderValidator.Validate(_tempRoot, missing);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Validate_ReturnsNull_WhenDirectoryHasNoGitFolder() {
            var noGit = Path.Combine(_tempRoot, "NoGit");
            Directory.CreateDirectory(noGit);

            var result = ProjectFolderValidator.Validate(_tempRoot, noGit);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Validate_ReturnsNull_WhenRootIsEmpty() {
            var result = ProjectFolderValidator.Validate(string.Empty, _projectDir);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Validate_ReturnsNull_WhenPathTraversesOutsideRootWithDotDot() {
            var traversal = _projectDir + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "..";

            var result = ProjectFolderValidator.Validate(_tempRoot, traversal);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Validate_ReturnsNull_WhenRootSiblingWithSamePrefixIsSupplied() {
            // e.g. root = /tmp/abc, sibling = /tmp/abcEvil — must not match
            var siblingRoot = _tempRoot + "Evil";
            var siblingProject = Path.Combine(siblingRoot, "MyProject");
            Directory.CreateDirectory(Path.Combine(siblingProject, ".git"));
            try {
                var result = ProjectFolderValidator.Validate(_tempRoot, siblingProject);
                Assert.That(result, Is.Null);
            } finally {
                if (Directory.Exists(siblingRoot)) {
                    Directory.Delete(siblingRoot, recursive: true);
                }
            }
        }
    }
}
