namespace SirThaddeus.Tests;

internal static class DocumentFixturePaths
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.GetFiles(dir.FullName, "*.sln").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    public static string Resolve(string fileName)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests", "Fixtures", "Documents", fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture '{fileName}' not found.", path);
        }

        return path;
    }
}
