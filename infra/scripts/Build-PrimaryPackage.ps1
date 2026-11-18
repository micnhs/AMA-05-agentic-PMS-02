param(
    [Parameter(Mandatory)][string]$ApplicationRoot,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $IsLinux) {
    throw 'Package building is confined to the authorized hosted Linux lane.'
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$application = [IO.Path]::GetFullPath($ApplicationRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'A bound build must use a fresh output directory.' }
if (Test-Path -LiteralPath (Join-Path $application '.local/settings.json')) { throw 'Never package existing local settings.' }
$appSha = (& git -C $application rev-parse HEAD).Trim()
$infraSha = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $appSha -cne $env:APP_COMMIT -or $infraSha -cne $env:GITHUB_SHA) {
    throw 'Actual checkouts differ from their immutable build bindings.'
}
function Assert-Exit([string]$Name) {
    if ($LASTEXITCODE -ne 0) { throw "$Name failed." }
}
$bundle = Join-Path $output 'bundle'
New-Item -ItemType Directory -Path $bundle, (Join-Path $bundle 'images'), (Join-Path $bundle 'functions'),
    (Join-Path $bundle 'wheels') | Out-Null
Set-Location $application
$expectedSdk = (Get-Content -LiteralPath (Join-Path $application 'global.json') -Raw | ConvertFrom-Json).sdk.version
$actualSdk = (& dotnet --version).Trim()
Assert-Exit 'Bound application SDK resolution'
if ($actualSdk -cne $expectedSdk) { throw 'Package build must use the exact SDK from the bound application global.json.' }
& npm --prefix (Join-Path $application 'src/Cohors.Web') ci --no-fund
Assert-Exit 'Locked frontend install'
& npm --prefix (Join-Path $application 'src/Cohors.Web') run build
Assert-Exit 'Actual React build'
foreach ($hostName in @('Api', 'AgentHost', 'Bootstrap')) {
    $folder = @{ Api = 'api'; AgentHost = 'agents'; Bootstrap = 'bootstrap' }[$hostName]
    & dotnet restore (Join-Path $application "src/Cohors.$hostName/Cohors.$hostName.csproj") --locked-mode --verbosity minimal
    Assert-Exit "$hostName locked dependency restore"
    & dotnet publish (Join-Path $application "src/Cohors.$hostName/Cohors.$hostName.csproj") -c Release `
        --no-restore -o (Join-Path $bundle $folder) --disable-build-servers -m:1 `
        -p:UseSharedCompilation=false --verbosity minimal
    Assert-Exit "$hostName private executable publish"
}
& dotnet publish (Join-Path $root 'infra/bootstrap/Cohors.InfraSql/Cohors.InfraSql.csproj') -c Release `
    -o (Join-Path $bundle 'tools/sql') `
    "-p:CohorsApplicationRoot=$application" --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
Assert-Exit 'MI-only infrastructure permission client publish'
$runtimeInfo = @(& dotnet --list-runtimes)
Assert-Exit 'Actual installed runtime inventory'
$coreRuntimes = @($runtimeInfo | ForEach-Object {
    if ($_ -cmatch '^Microsoft\.NETCore\.App (10\.0\.\d+) \[(.+)\]$') {
        [pscustomobject]@{ Version = [Version]$Matches[1]; Root = $Matches[2] }
    }
})
if ($coreRuntimes.Count -lt 1) { throw 'The bound SDK did not supply a .NET 10 runtime.' }
$core = $coreRuntimes | Sort-Object Version -Descending | Select-Object -First 1
$runtimeVersion = $core.Version.ToString()
$dotnetRoot = Split-Path (Split-Path $core.Root -Parent) -Parent
$runtime = Join-Path $bundle 'tools/dotnet'
New-Item -ItemType Directory -Path $runtime, (Join-Path $runtime 'host/fxr'), (Join-Path $runtime 'shared') | Out-Null
foreach ($file in @('dotnet', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $dotnetRoot $file) -Destination $runtime
}
Copy-Item -LiteralPath (Join-Path $dotnetRoot "host/fxr/$runtimeVersion") -Destination (Join-Path $runtime 'host/fxr') -Recurse
foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
    $target = Join-Path $runtime "shared/$framework"
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $dotnetRoot "shared/$framework/$runtimeVersion") -Destination $target -Recurse
}
@{ contract = 'cohors-private-dotnet-runtime-1'; version = $runtimeVersion; sdkVersion = $actualSdk } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtime 'runtime-binding.json') -Encoding utf8NoBOM
& (Join-Path $runtime 'dotnet') (Join-Path $bundle 'tools/sql/Cohors.InfraSql.dll') --describe |
    Set-Content -LiteralPath (Join-Path $bundle 'sql-contract-build.json') -Encoding utf8NoBOM
Assert-Exit 'Actual compiled migration resource binding'
& pwsh -NoProfile -File (Join-Path $application 'scripts/Publish-Functions.ps1')
Assert-Exit 'Existing authoritative Flex packager'
foreach ($hostName in @('workflows', 'operations')) {
    Copy-Item -LiteralPath (Join-Path $application ".local/publish/cohors-$hostName.zip") -Destination (Join-Path $bundle 'functions')
}
if (-not (Get-Command skopeo -ErrorAction SilentlyContinue)) {
    & sudo apt-get update -qq
    Assert-Exit 'Hosted package tool catalog'
    & sudo apt-get install -y -qq skopeo
    Assert-Exit 'Hosted OCI inspection tool'
}
& docker buildx create --name cohors-package-builder --driver docker-container --use
Assert-Exit 'Isolated hosted OCI builder'
try {
    foreach ($hostName in @('api', 'agents')) {
        $project = if ($hostName -eq 'api') { 'Api' } else { 'AgentHost' }
        $archive = Join-Path $bundle "images/$hostName.oci.tar"
        & docker buildx build --platform linux/amd64 --provenance=false --sbom=false `
            --file (Join-Path $application "src/Cohors.$project/Dockerfile") `
            --output "type=oci,dest=$archive" $application
        Assert-Exit "$hostName OCI build"
        $digest = (& skopeo inspect --format '{{.Digest}}' "oci-archive:$archive").Trim()
        Assert-Exit "$hostName actual OCI digest"
        if ($digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'OCI inspection did not return a real manifest digest.' }
        $digest | Set-Content -LiteralPath (Join-Path $bundle "images/$hostName.digest") -Encoding utf8NoBOM
    }
} finally {
    & docker buildx rm cohors-package-builder
    Assert-Exit 'Owned hosted builder cleanup'
}
& python -m pip download --only-binary=:all: --dest (Join-Path $bundle 'wheels') `
    -r (Join-Path $root 'infra/bootstrap/requirements-nva.txt')
Assert-Exit 'Linux Python 3.12 proxy dependency bundle'
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $root 'infra') -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($root, $source.FullName)
    if ($relative -match '(^|[/\\])(obj|bin|__pycache__)([/\\]|$)') { continue }
    $destination = Join-Path $bundle $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source.FullName -Destination $destination
}
& python -B (Join-Path $PSScriptRoot 'primary_package.py') --root $bundle --application-root $application --application-commit $appSha `
    --infrastructure-commit $infraSha --run-id $env:GITHUB_RUN_ID --zip (Join-Path $output 'cohors-primary-release.zip')
Assert-Exit 'Complete package and generated metadata binding'
Write-Output 'Bound package prepared. No ACR push, Azure login, schema call or deployment occurred.'
