namespace MetaVoiceType.Tests;

public sealed class ManagedCodePolicyTests
{
    [Fact]
    public void RepositoryContainsNoApplicationAuthoredNativeSourcesOrBindings()
    {
        string root = FindRepositoryRoot();
        string[] bannedExtensions = [".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".vcxproj"];
        string[] bannedFiles = ["CMakeLists.txt"];
        var offending = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => bannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                || bannedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(offending);

        var bindings = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("[DllImport(", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("[LibraryImport(", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(bindings);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
