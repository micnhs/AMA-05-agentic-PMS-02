#requires -Version 7.6
param([switch]$SkipBrowser)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet test (Join-Path $root 'tests\Cohors.Tests\Cohors.Tests.csproj') --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false `
    --verbosity minimal --logger 'trx;LogFileName=acceptance.trx' --results-directory (Join-Path $root '.local\test-results')
if ($LASTEXITCODE -ne 0) { throw '.NET/domain/SQL acceptance failed.' }
& $dotnet test (Join-Path $root 'tests\Cohors.Bootstrap.Tests\Cohors.Bootstrap.Tests.csproj') --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false `
    --verbosity minimal --logger 'trx;LogFileName=bootstrap.trx' --results-directory (Join-Path $root '.local\bootstrap-test-results')
if ($LASTEXITCODE -ne 0) { throw '.NET/bootstrap tests failed.' }
if (-not $SkipBrowser) {
    $run = [Guid]::NewGuid().ToString('N')
    $application = Join-Path $root ".local\ui-package-$run"
    & $dotnet publish (Join-Path $root 'src\Cohors.Api\Cohors.Api.csproj') --configuration Debug --no-build --no-restore --output $application --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The current browser-test application could not be published.' }
    & (Join-Path $PSScriptRoot 'Test-Ui.ps1') -ApplicationDirectory $application `
        -ArtifactDirectory (Join-Path $root ".local\ui-results-$run") -Profile Preview
}
