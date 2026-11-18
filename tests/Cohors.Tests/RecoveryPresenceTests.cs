using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
[Trait("Category", "RecoveryPresence")]
public sealed class RecoveryPresenceTests(SqlFixture fixture)
{
    [Fact]
    public async Task OriginalPresenceReadsActualCurrentAndHistoricalSqlWithoutChangingItsCut()
    {
        var data = await EudamedRecoveryData.Prepare(fixture);
        var before = await RecoveryFixture.Tables(fixture.ConnectionString);
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
        start.Environment["COHORS_PRESENCE_MISSING"] = string.Join(",",
            data.Originals["historical-pdf"], data.Originals["approved-snapshot"]);
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("""
            $ErrorActionPreference = 'Stop'
            . $env:COHORS_PRESENCE_SCRIPT
            Initialize-RecoveryRuntime $env:COHORS_PRESENCE_APP
            $connection = Open-RecoverySql @{ connectionString = $env:COHORS_PRESENCE_CONNECTION }
            $root = $env:COHORS_PRESENCE_OBJECTS
            try {
                Assert-RecoveryEudamedOriginals $connection $root
                foreach ($hash in $env:COHORS_PRESENCE_MISSING.Split(',')) {
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
            Write-Output 'RECOVERY_NATIVE_PRESENCE=Verified'
            """)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native original-presence probe did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, "Native original-presence probe failed; unfiltered child output is not published.");
        Assert.Contains("RECOVERY_NATIVE_PRESENCE=Verified", output);
        Assert.Equal(before, await RecoveryFixture.Tables(fixture.ConnectionString));
        foreach (var hash in new[] { data.Originals["historical-pdf"], data.Originals["approved-snapshot"] })
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await fixture.Objects.ReadBytes(hash))));
    }
}
