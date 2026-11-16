using Azure;
using Cohors.Bootstrap;

namespace Cohors.Bootstrap.Tests;

public sealed class BootstrapGuardTests
{
    private static readonly string[] SqlArgs =
        ["sql", "--expect-server", "synthetic-sql.database.windows.net", "--expect-database", "cohors"];
    private static readonly string[] SearchArgs =
        ["search", "--expect-endpoint", "https://synthetic-search.search.windows.net", "--expect-index", "cohors-knowledge"];
    private const string Connection =
        "Server=tcp:synthetic-sql.database.windows.net,1433;Database=cohors;Encrypt=True;TrustServerCertificate=False";

    private static string? Environment(string key) => key switch
    {
        "ConnectionStrings__Cohors" => Connection,
        "Search__Endpoint" => "https://synthetic-search.search.windows.net/",
        "Search__Index" => "cohors-knowledge",
        _ => null
    };

    [Fact]
    public void WriteModeMustBeExplicitAndCannotBeDuplicated()
    {
        Assert.False(BootstrapRequest.Parse(SqlArgs, Environment).Apply);
        Assert.False(BootstrapRequest.Parse(SearchArgs, Environment).Apply);
        Assert.True(BootstrapRequest.Parse([.. SqlArgs, "--apply"], Environment).Apply);
        Assert.Throws<BootstrapException>(() => BootstrapRequest.Parse([.. SqlArgs, "--apply", "--apply"], Environment));
        Assert.Throws<BootstrapException>(() => BootstrapRequest.Parse(["sql", "--apply"], Environment));
        Assert.Throws<BootstrapException>(() => BootstrapRequest.Parse([.. SearchArgs, "--reset"], Environment));
    }

    [Theory]
    [InlineData("ConnectionStrings__Cohors", "Server=tcp:other.database.windows.net,1433;Database=cohors;Encrypt=True")]
    [InlineData("ConnectionStrings__Cohors", "Server=tcp:synthetic-sql.database.windows.net,1433;Database=wrong;Encrypt=True")]
    [InlineData("Cohors__SqlAuthentication", "Local")]
    [InlineData("Search__Endpoint", "https://other.search.windows.net")]
    [InlineData("Search__Index", "other-index")]
    public async Task InvalidTargetsAreRejectedBeforeAnyServiceCall(string key, string value)
    {
        var calls = 0;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var args = key.StartsWith("Search__", StringComparison.Ordinal) ? SearchArgs : SqlArgs;
        var exit = await Program.Run([.. args, "--apply"], name => name == key ? value : Environment(name),
            (_, _) => { calls++; return Task.FromResult("unexpected"); }, output, errors);
        Assert.NotEqual(0, exit);
        Assert.Equal(0, calls);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("synthetic-secret", errors.ToString());
    }

    [Theory]
    [InlineData("https://user:synthetic-secret@synthetic-search.search.windows.net")]
    [InlineData("https://synthetic-search.search.windows.net/?token=synthetic-secret")]
    [InlineData("http://synthetic-search.search.windows.net")]
    [InlineData("https://synthetic-search.search.windows.net.evil.example")]
    [InlineData("https://a--b.search.windows.net")]
    [InlineData("https://a-b.search.windows.net")]
    public void UnsafeOriginsCannotBeApprovedUsingExpectedTarget(string endpoint)
    {
        string[] args = ["search", "--expect-endpoint", endpoint, "--expect-index", "cohors-knowledge", "--apply"];
        Assert.Throws<BootstrapException>(() => BootstrapRequest.Parse(args,
            name => name == "Search__Endpoint" ? endpoint : Environment(name)));
    }

    [Theory]
    [InlineData(";User ID=synthetic-user;Password=synthetic-secret")]
    [InlineData(";Integrated Security=True")]
    [InlineData(";Encrypt=False")]
    [InlineData(";TrustServerCertificate=True")]
    [InlineData(";Authentication=Active Directory Default")]
    [InlineData(";HostNameInCertificate=other.database.windows.net")]
    [InlineData(";Failover Partner=other.database.windows.net")]
    [InlineData(";ServerCertificate=synthetic-secret.cer")]
    [InlineData(";Unknown=synthetic-secret")]
    public async Task SqlCredentialsAndTlsOrTargetOverridesFailClosed(string suffix)
    {
        var calls = 0;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exit = await Program.Run([.. SqlArgs, "--apply"],
            name => name == "ConnectionStrings__Cohors" ? Connection + suffix : Environment(name),
            (_, _) => { calls++; return Task.FromResult("unexpected"); }, output, errors);
        Assert.NotEqual(0, exit);
        Assert.Equal(0, calls);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("synthetic-secret", errors.ToString());
    }

    [Fact]
    public async Task BackendErrorsRemainFailuresWithoutEchoingResponsePayloads()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exit = await Program.Run(SearchArgs, Environment,
            (_, _) => Task.FromException<string>(new RequestFailedException(403, "token=synthetic-secret")),
            output, errors);
        Assert.Equal(1, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains("search_http_status=403", errors.ToString());
        Assert.DoesNotContain("synthetic-secret", errors.ToString());
    }
}
