#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [ValidateSet('Preview','PhysicalRetention')][string]$Profile = 'Preview',
    [int]$Port = 5197
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -eq 'true') { throw 'This is the explicit local browser runner, not GitHub Actions.' }
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Backup-Local.ps1') -ApplicationDirectory $ApplicationDirectory
$application = Assert-RecoveryPath $ApplicationDirectory
$artifacts = Assert-RecoveryPath $ArtifactDirectory -New
if (-not (Test-Path -LiteralPath (Join-Path $application 'wwwroot\index.html'))) {
    throw 'Publish the current API with its built web assets before browser acceptance.'
}
if ($Port -lt 1024 -or $Port -gt 65535 -or $Port -eq 5187) { throw 'Use a separate unprivileged browser-test port, never the existing 5187 preview.' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } finally { $listener.Stop() }
Initialize-RecoveryRuntime $application
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$config = Get-Content -LiteralPath (Join-Path $root '.local\settings.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 30
$sql = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($config.ConnectionStrings.Cohors)
if ($sql.DataSource -cnotmatch '^(127\.0\.0\.1|localhost),[1-9][0-9]{3,4}$') { throw 'Browser acceptance requires the explicit existing loopback SQL server.' }
$database = 'Cohors_Test_' + [Guid]::NewGuid().ToString('N')
$sql.set_InitialCatalog('master')
$sql.set_Pooling($false)
$master = [Microsoft.Data.SqlClient.SqlConnection]::new($sql.ConnectionString)
$runtime = Join-Path $artifacts 'runtime'
$private = Join-Path $runtime '.local'
$settingsPath = Join-Path $private 'settings.json'
$accessPath = Join-Path $private 'demo-access.txt'
$created = $false
$apiProcess = $null
$passed = $false
$started = [DateTimeOffset]::UtcNow.ToString('O')
$assemblyNames = @('Cohors.Api.dll','Cohors.Application.dll','Cohors.Domain.dll','Cohors.Infrastructure.dll','Cohors.Agents.dll')
$applicationFiles = [ordered]@{}
$files = @($assemblyNames | ForEach-Object { Join-Path $application $_ }) +
    @(Get-ChildItem -LiteralPath (Join-Path $application 'wwwroot') -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName })
foreach ($file in $files) {
    $applicationFiles[[IO.Path]::GetRelativePath($application, $file)] = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
}
$sourceFiles = [ordered]@{}
foreach ($file in @('scripts\Test-Ui.ps1','src\Cohors.Web\e2e\pilot.spec.ts','src\Cohors.Web\playwright.config.ts')) {
    $sourceFiles[$file] = (Get-FileHash -LiteralPath (Join-Path $root $file)).Hash.ToLowerInvariant()
}
$previous = @{}
foreach ($name in @('COHORS_URL', 'COHORS_TEST_ACCESS_FILE', 'COHORS_UI_ARTIFACT_DIR')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}
New-Item -ItemType Directory -Path $private | Out-Null
try {
    $master.Open()
    Invoke-RecoverySql $master "CREATE DATABASE [$database]"
    $created = $true
    $sql.set_InitialCatalog($database)
    $config.ConnectionStrings.Cohors = $sql.ConnectionString
    $config.Cohors.AutoMigrate = $true
    $config.Cohors.Seed = $true
    $config.Cohors.LocalOperations = $true
    $config.Cohors.PublicSources = $false
    $config.Cohors.ObjectStore = 'Local'
    $config.Cohors.Search = 'Local'
    $physical = $Profile -eq 'PhysicalRetention'
    $config.Retention = @{ PhysicalMode = if ($physical) { 'SyntheticLocal' } else { 'Disabled' } }
    $access = [IO.File]::ReadAllText((Join-Path $root '.local\demo-access.txt'))
    foreach ($product in @('ortho','cardio')) {
        $secret = 'Pilot!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(12))
        $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
        $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($secret, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
        $username = "pms-$product.demo"
        $config.Authentication.Users = @($config.Authentication.Users) + @{
            Id = [Guid]::NewGuid().ToString(); Username = $username; Name = "Synthetic $product-scoped reviewer"
            Role = 'PmsSpecialist'; Products = @($product); Salt = [Convert]::ToBase64String($salt); Hash = [Convert]::ToBase64String($hash)
        }
        $access += "`n$username : $secret`n"
    }
    [IO.File]::WriteAllText($settingsPath, (ConvertTo-Json -InputObject $config -Depth 30))
    [IO.File]::WriteAllText($accessPath, $access)
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.ArgumentList.Add((Join-Path $application 'Cohors.Api.dll'))
    $start.WorkingDirectory = $application
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['COHORS_HOME'] = $runtime
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Testing'
    $start.Environment['ASPNETCORE_CONTENTROOT'] = $application
    $start.Environment['ASPNETCORE_URLS'] = "http://localhost:$Port"
    $apiProcess = [Diagnostics.Process]::Start($start)
    $stdout = $apiProcess.StandardOutput.ReadToEndAsync()
    $stderr = $apiProcess.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($apiProcess.HasExited) { throw 'The owned browser-test API exited before readiness; inspect its private log.' }
        try {
            $response = Invoke-RestMethod -Uri "http://localhost:$Port/health/ready" -TimeoutSec 5
            $ready = $response.status -eq 'ready'
        } catch [Net.Http.HttpRequestException] { $ready = $false }
        catch [Net.WebException] { $ready = $false }
        catch [OperationCanceledException] { $ready = $false }
        if ($ready) { break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'The owned browser-test API did not become ready.' }
    $env:COHORS_URL = "http://localhost:$Port"
    $env:COHORS_TEST_ACCESS_FILE = $accessPath
    $env:COHORS_UI_ARTIFACT_DIR = $artifacts
    $project = if ($physical) { 'physical-retention' } else { 'preview' }
    & npm --prefix (Join-Path $root 'src\Cohors.Web') run test:e2e -- "--project=$project" *> (Join-Path $artifacts 'private-browser.log')
    if ($LASTEXITCODE -ne 0) { throw 'Current browser acceptance failed; private diagnostics and JUnit were preserved.' }
    [xml]$junit = Get-Content -LiteralPath (Join-Path $artifacts 'e2e-results.xml') -Raw
    $cases = $junit.SelectNodes('//testcase')
    $expected = if ($physical) { 1 } else { 6 }
    if ($cases.Count -ne $expected -or $junit.SelectNodes('//testcase/skipped | //testcase/failure | //testcase/error').Count) {
        throw 'Browser acceptance requires every named scenario of the explicitly selected profile, without failures or skips.'
    }
    foreach ($file in $applicationFiles.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $application $file)).Hash.ToLowerInvariant() -cne $applicationFiles[$file]) {
            throw 'Published application or browser assets changed during acceptance.'
        }
    }
    foreach ($file in $sourceFiles.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $root $file)).Hash.ToLowerInvariant() -cne $sourceFiles[$file]) {
            throw 'Browser test source changed during acceptance.'
        }
    }
    $assemblies = [ordered]@{}
    foreach ($name in $assemblyNames) {
        $assemblies[$name] = $applicationFiles[$name]
    }
    $report = [ordered]@{
        scope = 'Isolated local synthetic Replay browser; local outbox/watchdog emulation, not separate Functions/Azure/live AI'
        profile = $Profile; retentionPhysicalMode = $config.Retention.PhysicalMode
        startedUtc = $started; finishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sourceCommit = (& git -C $root rev-parse HEAD); sourceTreeDirty = @(& git -C $root status --porcelain --untracked-files=normal).Count -gt 0
        sourceFiles = $sourceFiles; applicationFiles = $applicationFiles
        applicationAssemblies = $assemblies; testCount = $cases.Count
        tests = @($cases | ForEach-Object { $_.name }); database = $database
        originalDemoUnchanged = $true; ownApiStopped = $false; ownDatabaseRemoved = $false
    }
    $passed = $true
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    if ($apiProcess) {
        if (-not $apiProcess.HasExited) { $apiProcess.Kill($true) }
        $apiProcess.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $artifacts 'private-api.log'), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        $apiProcess.Dispose()
    }
    if ($created) {
        if ($master.State -ne [Data.ConnectionState]::Open) { $master.Open() }
        Invoke-RecoverySql $master "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]"
        if ((Invoke-RecoverySql $master "SELECT COUNT(*) FROM sys.databases WHERE name=N'$database'" -Scalar) -ne 0) {
            throw 'Owned browser-test database cleanup is incomplete.'
        }
    }
    $master.Dispose()
    [IO.File]::Delete($settingsPath)
    [IO.File]::Delete($accessPath)
    $objects = Join-Path $private 'blobs'
    if (Test-Path -LiteralPath $objects) {
        $objects = Assert-RecoveryPath $objects
        [IO.Directory]::Delete($objects, $true)
    }
    if ($passed) {
        $report.ownApiStopped = $true
        $report.ownDatabaseRemoved = $true
        [IO.File]::WriteAllText((Join-Path $artifacts 'browser-results.json'), (ConvertTo-Json -InputObject $report -Depth 20))
        Write-Output "Current $Profile browser acceptance: $expected/$expected passed, none skipped in this explicit profile; owned API/database stopped and removed. Existing 5187 demo unchanged."
    }
}
