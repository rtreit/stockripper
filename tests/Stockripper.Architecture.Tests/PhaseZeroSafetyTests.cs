namespace Stockripper.Architecture.Tests;

public sealed class PhaseZeroSafetyTests
{
    private static readonly string[] ForbiddenProductionText =
    [
        "api.alpaca.markets",
        "paper-api.alpaca.markets",
        "Alpaca",
        "HttpClient",
        "System.Net.Http",
        "ClientWebSocket",
        "SocketsHttpHandler",
        "SubmitOrder",
        "PlaceOrder"
    ];

    [Fact]
    public void Production_source_contains_no_real_broker_order_surface()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(sourceRoot, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".csproj", StringComparison.Ordinal)
                || path.EndsWith(".json", StringComparison.Ordinal)
                || path.EndsWith(".proto", StringComparison.Ordinal)
                || path.EndsWith(".razor", StringComparison.Ordinal)
                || path.EndsWith(".xml", StringComparison.Ordinal)
                || path.EndsWith(".yml", StringComparison.Ordinal)
                || path.EndsWith(".yaml", StringComparison.Ordinal));

        var findings = sourceFiles
            .SelectMany(path => ForbiddenProductionText
                .Where(text => File.ReadAllText(path).Contains(text, StringComparison.Ordinal))
                .Select(text => $"{Path.GetRelativePath(sourceRoot, path)} contains {text}."))
            .ToArray();

        Assert.Empty(findings);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stockripper.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
