param([switch]$SkipBrowser)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet test (Join-Path $root 'tests\Cohors.Tests\Cohors.Tests.csproj') --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false `
    --verbosity minimal --logger 'trx;LogFileName=acceptance.trx' --results-directory (Join-Path $root '.local\test-results')
if ($LASTEXITCODE -ne 0) { throw '.NET/domain/SQL acceptance failed.' }
if (-not $SkipBrowser) {
    $url = if ($env:COHORS_URL) { $env:COHORS_URL.TrimEnd('/') } else { 'http://localhost:5187' }
    $uri = [Uri]$url
    if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') { throw 'Local browser acceptance requires an explicit HTTP loopback URL.' }
    $ready = Invoke-RestMethod -Uri "$url/health/ready" -TimeoutSec 10
    if ($ready.status -ne 'ready') { throw 'Start the local Cohors application before browser acceptance.' }
    npm --prefix (Join-Path $root 'src\Cohors.Web') run test:e2e
    if ($LASTEXITCODE -ne 0) { throw 'Browser acceptance failed.' }
}
