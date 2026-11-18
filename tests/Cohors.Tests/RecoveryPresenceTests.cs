using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
[Trait("Category", "RecoveryPresence")]
public sealed class RecoveryPresenceTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task OriginalPresenceReadsActualCurrentAndHistoricalSqlWithoutChangingItsCut()
    {
        Assert.Equal(0L, Assert.IsType<long>(await RecoveryFixture.Scalar(fixture.ConnectionString,
            "SELECT COUNT_BIG(*) FROM dbo.Aggregates WHERE Kind COLLATE Latin1_General_100_CI_AS LIKE N'eudamed%'")));
        var empty = await RecoveryFixture.Tables(fixture.ConnectionString);
        await VerifyPresence([]);
        Assert.Equal(empty, await RecoveryFixture.Tables(fixture.ConnectionString));
        var data = await EudamedRecoveryData.Prepare(fixture);
        var before = await RecoveryFixture.Tables(fixture.ConnectionString);
        string[] missing = [data.Originals["historical-pdf"], data.Originals["approved-snapshot"]];
        await VerifyPresence(missing);
        Assert.Equal(before, await RecoveryFixture.Tables(fixture.ConnectionString));
        foreach (var hash in missing)
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await fixture.Objects.ReadBytes(hash))));
    }

    private async Task VerifyPresence(string[] missing)
    {
        var home = RecoveryFixture.RepositoryRoot();
        var configuration = typeof(RecoveryFixture).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COHORS_RECOVERY_PWSH") ?? "pwsh")
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["COHORS_PRESENCE_SCRIPT"] = Path.Combine(home, "scripts", "Backup-Local.ps1");
        start.Environment["COHORS_PRESENCE_APP"] = Path.Combine(home, "src", "Cohors.Api", "bin", configuration, "net10.0");
        start.Environment["COHORS_PRESENCE_CONNECTION"] = fixture.ConnectionString;
        start.Environment["COHORS_PRESENCE_OBJECTS"] = fixture.ObjectDirectory;
        start.Environment["COHORS_PRESENCE_MISSING"] = JsonSerializer.Serialize(missing);
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("""
            $ErrorActionPreference = 'Stop'
            trap {
                $code = [regex]::Match($_.Exception.Message, '^recovery_[a-z_]+:').Value.TrimEnd(':')
                if (-not $code) { $code = 'unexpected' }
                [Console]::Error.WriteLine('RECOVERY_NATIVE_PRESENCE_FAILURE=' + $code + ';type=' +
                    $_.Exception.GetType().FullName + ';line=' + $_.InvocationInfo.ScriptLineNumber)
                exit 1
            }
            . $env:COHORS_PRESENCE_SCRIPT
            Initialize-RecoveryRuntime $env:COHORS_PRESENCE_APP
            $connection = Open-RecoverySql @{ connectionString = $env:COHORS_PRESENCE_CONNECTION }
            $root = $env:COHORS_PRESENCE_OBJECTS
            try {
                $missing = ConvertFrom-Json -InputObject $env:COHORS_PRESENCE_MISSING -NoEnumerate
                if ($missing -isnot [array]) { throw 'Explicit synthetic original list required.' }
                Assert-RecoveryEudamedOriginals $connection $root
                if ($missing.Count -eq 0) {
                    $state = Get-RecoveryState $connection @{ connectionString = $env:COHORS_PRESENCE_CONNECTION }
                    $emptyTables = @($state.tables | Where-Object count -eq 0)
                    if ($state.tables.Count -ne 8 -or -not $emptyTables.Count -or
                        @($emptyTables | Where-Object { $_.sha256 -cne [Cohors.Domain.Canonical]::HashText('[]') }).Count) {
                        throw 'Empty authoritative tables must retain their complete explicit JSON-array hashes.'
                    }
                }
                foreach ($hash in $missing) {
                    if ($hash -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid synthetic original binding.' }
                    $path = [IO.Path]::Combine($root, $hash)
                    $bytes = [IO.File]::ReadAllBytes($path)
                    if ([Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes)) -cne $hash) {
                        throw 'Synthetic original does not match its exact address.'
                    }
                    [IO.File]::Delete($path)
                    try {
                        $rejected = $false
                        try { Assert-RecoveryEudamedOriginals $connection $root }
                        catch {
                            if (-not $_.Exception.Message.StartsWith('recovery_reference_coverage:', [StringComparison]::Ordinal)) { throw }
                            $rejected = $true
                        }
                        if (-not $rejected) { throw 'Missing historical original was not rejected.' }
                    } finally { [IO.File]::WriteAllBytes($path, $bytes) }
                    Assert-RecoveryEudamedOriginals $connection $root
                }
            } finally { $connection.Dispose() }
            Write-Output ('RECOVERY_NATIVE_PRESENCE=Verified; missing=' + $missing.Count)
            """)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native original-presence probe did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var output = await stdout + await stderr;
        var diagnostic = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("RECOVERY_NATIVE_PRESENCE_FAILURE=", StringComparison.Ordinal));
        Assert.True(process.ExitCode == 0,
            $"Native original-presence probe failed (exit {process.ExitCode}); {diagnostic ?? "no controlled diagnostic"}. Unfiltered output is not published.");
        Assert.Contains("RECOVERY_NATIVE_PRESENCE=Verified; missing=" + missing.Length, output);
    }
}
