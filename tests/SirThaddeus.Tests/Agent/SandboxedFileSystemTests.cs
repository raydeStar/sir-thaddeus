using SirThaddeus.Agent.Pipeline;
using Xunit;

namespace SirThaddeus.Tests;

public class SandboxedFileSystemTests
{
    // ─── InMemory ─────────────────────────────────────────────────────

    [Fact]
    public void InMemory_WriteAndRead_RoundTrips()
    {
        var fs = new InMemorySandboxedFileSystem();
        fs.WriteFile("notes/readme.txt", "hello");
        Assert.True(fs.FileExists("notes/readme.txt"));
        Assert.Equal("hello", fs.ReadFile("notes/readme.txt"));
    }

    [Fact]
    public void InMemory_ReadMissing_Throws()
    {
        var fs = new InMemorySandboxedFileSystem();
        Assert.Throws<FileNotFoundException>(() => fs.ReadFile("nope.txt"));
    }

    [Fact]
    public void InMemory_ListFiles_FiltersPrefix()
    {
        var fs = new InMemorySandboxedFileSystem();
        fs.WriteFile("a/one.txt", "1");
        fs.WriteFile("a/two.txt", "2");
        fs.WriteFile("b/three.txt", "3");

        var aFiles = fs.ListFiles("a");
        Assert.Equal(2, aFiles.Count);
        Assert.All(aFiles, f => Assert.StartsWith("a/", f));
    }

    [Fact]
    public void InMemory_Delete_RemovesFile()
    {
        var fs = new InMemorySandboxedFileSystem();
        fs.WriteFile("temp.txt", "data");
        Assert.True(fs.FileExists("temp.txt"));
        fs.DeleteFile("temp.txt");
        Assert.False(fs.FileExists("temp.txt"));
    }

    [Fact]
    public void InMemory_PathTraversal_Blocked()
    {
        var fs = new InMemorySandboxedFileSystem();
        Assert.Throws<ArgumentException>(() => fs.WriteFile("../../etc/passwd", "bad"));
    }

    [Fact]
    public void InMemory_NormalizesSlashes()
    {
        var fs = new InMemorySandboxedFileSystem();
        fs.WriteFile("dir\\file.txt", "ok");
        Assert.True(fs.FileExists("dir/file.txt"));
        Assert.Equal("ok", fs.ReadFile("dir/file.txt"));
    }

    // ─── TempDirectory ───────────────────────────────────────────────

    [Fact]
    public void TempDir_WriteRead_RoundTrips()
    {
        using var fs = new TempDirectorySandboxedFileSystem();
        fs.WriteFile("data/test.txt", "content");
        Assert.True(fs.FileExists("data/test.txt"));
        Assert.Equal("content", fs.ReadFile("data/test.txt"));
    }

    [Fact]
    public void TempDir_ListFiles_Returns()
    {
        using var fs = new TempDirectorySandboxedFileSystem();
        fs.WriteFile("a.txt", "1");
        fs.WriteFile("sub/b.txt", "2");

        var all = fs.ListFiles();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void TempDir_Delete_Removes()
    {
        using var fs = new TempDirectorySandboxedFileSystem();
        fs.WriteFile("tmp.txt", "x");
        fs.DeleteFile("tmp.txt");
        Assert.False(fs.FileExists("tmp.txt"));
    }

    [Fact]
    public void TempDir_PathEscape_Blocked()
    {
        using var fs = new TempDirectorySandboxedFileSystem();
        Assert.Throws<UnauthorizedAccessException>(() => fs.WriteFile("../../escape.txt", "bad"));
    }

    [Fact]
    public void TempDir_Dispose_CleansUp()
    {
        string root;
        using (var fs = new TempDirectorySandboxedFileSystem())
        {
            root = fs.RootDirectory;
            fs.WriteFile("data.txt", "something");
            Assert.True(Directory.Exists(root));
        }
        Assert.False(Directory.Exists(root));
    }
}
