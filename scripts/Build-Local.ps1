param([switch]$SkipWeb)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$web = Join-Path $root 'src\Cohors.Web'
if (-not $SkipWeb) {
    if (-not (Test-Path -LiteralPath 'src\Cohors.Web\node_modules')) {
        npm --prefix $web ci --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency restore failed.' }
    }
    npm --prefix $web run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
}
& $dotnet restore (Join-Path $root 'Cohors.slnx') --locked-mode --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Locked .NET restore failed; do not replace package versions silently.' }
& $dotnet build (Join-Path $root 'Cohors.slnx') --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw '.NET build failed.' }
Write-Host 'Build complete. Frontend assets are generated before the API build, never concurrently.'
