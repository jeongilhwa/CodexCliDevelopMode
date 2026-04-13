using CodexDesk.Tests.TestSupport;
using CodexDesk.Services;
using FluentAssertions;

namespace CodexDesk.Tests.Codex;

[TestClass]
public sealed class CodexWorkspaceLocatorTests
{
    [TestMethod]
    public void FindWorkspaceRoot_should_return_nearest_ancestor_with_agents_file()
    {
        using var tempDirectory = new TemporaryDirectory();
        var rootPath = System.IO.Path.Combine(tempDirectory.Path, "repo");
        var nestedPath = System.IO.Path.Combine(rootPath, "src", "feature", "child");

        Directory.CreateDirectory(nestedPath);
        File.WriteAllText(System.IO.Path.Combine(rootPath, "AGENTS.md"), "# workspace");

        var workspaceRoot = CodexWorkspaceLocator.FindWorkspaceRoot(nestedPath);

        workspaceRoot.Should().Be(rootPath);
    }

    [TestMethod]
    public void FindWorkspaceRoot_should_return_null_when_candidate_is_missing()
    {
        using var tempDirectory = new TemporaryDirectory();
        var missingPath = System.IO.Path.Combine(tempDirectory.Path, "plain", "child");

        var workspaceRoot = CodexWorkspaceLocator.FindWorkspaceRoot(missingPath);

        workspaceRoot.Should().BeNull();
    }
}
