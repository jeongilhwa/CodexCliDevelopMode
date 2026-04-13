using CodexDesk.Tests.TestSupport;
using FluentAssertions;

namespace CodexDesk.Tests.TestSupport;

[TestClass]
public sealed class TemporaryDirectoryTests
{
    [TestMethod]
    public void TemporaryDirectory_should_delete_created_files_when_disposed()
    {
        string directoryPath;
        string filePath;

        using (var temporaryDirectory = new TemporaryDirectory())
        {
            directoryPath = temporaryDirectory.Path;
            filePath = Path.Combine(directoryPath, "created-by-test.txt");

            File.WriteAllText(filePath, "temporary");
            File.Exists(filePath).Should().BeTrue();
        }

        Directory.Exists(directoryPath).Should().BeFalse();
        File.Exists(filePath).Should().BeFalse();
    }
}
