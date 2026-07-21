using SirThaddeus.Harness.Suites;

namespace SirThaddeus.Tests.Harness;

public sealed class SuiteLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "thaddeus-suite-loader-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadSuite_AcceptsBoundedBinaryFileFixture()
    {
        WriteSuiteTest("""
            {
              "id": "binary_fixture",
              "name": "Binary fixture",
              "user_message": "Read the attached document.",
              "state_setup": {
                "files": [
                  { "path": "documents/sample.bin", "content_base64": "AAECAw==" }
                ]
              }
            }
            """);

        var suite = new SuiteLoader().LoadSuite(_root, "documents");

        Assert.Equal("AAECAw==", suite.Tests.Single().StateSetup.Files.Single().ContentBase64);
    }

    [Fact]
    public void LoadSuite_RejectsInvalidBinaryFileFixtureBeforeExecution()
    {
        WriteSuiteTest("""
            {
              "id": "invalid_binary_fixture",
              "name": "Invalid binary fixture",
              "user_message": "Read the attached document.",
              "state_setup": {
                "files": [
                  { "path": "documents/sample.bin", "content_base64": "not base64" }
                ]
              }
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new SuiteLoader().LoadSuite(_root, "documents"));

        Assert.Contains("Invalid state_setup file fixture", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteSuiteTest(string json)
    {
        var suiteDirectory = Path.Combine(_root, "documents");
        Directory.CreateDirectory(suiteDirectory);
        File.WriteAllText(Path.Combine(suiteDirectory, "case.json"), json);
    }
}
