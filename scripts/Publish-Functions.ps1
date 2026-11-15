[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
foreach ($hostName in @('Workflows', 'Operations')) {
    $project = Join-Path $root "src\Cohors.$hostName\Cohors.$hostName.csproj"
    $output = Join-Path $root ".local\publish\$hostName"
    & $dotnet publish $project -c Release -o $output --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "$hostName publish failed." }
    $metadata = Get-Content -LiteralPath (Join-Path $output 'functions.metadata') -Raw | ConvertFrom-Json
    $required = if ($hostName -eq 'Workflows') { @('DispatchOutbox', 'CohorsOutboxV1', 'ProcessOutbox', 'WorkflowLive', 'WorkflowReady') }
        else { @('DeadlineWatchdog', 'OperationsLive', 'OperationsReady') }
    foreach ($name in $required) {
        if ($name -notin $metadata.name) { throw "$hostName function metadata missing $name." }
    }
    foreach ($file in @('host.json', 'worker.config.json', 'extensions.json', '.azurefunctions\Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader.dll', "Cohors.$hostName.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $file))) { throw "$hostName output missing $file." }
    }
    $zip = Join-Path $root ".local\publish\cohors-$($hostName.ToLowerInvariant()).zip"
    if (Test-Path -LiteralPath $zip) { [System.IO.File]::Delete($zip) }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($output, $zip)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($file in @('host.json', 'functions.metadata', 'worker.config.json', 'extensions.json', '.azurefunctions/Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader.dll', "Cohors.$hostName.dll")) {
            if ($null -eq $archive.GetEntry($file)) { throw "$hostName package missing $file." }
        }
    } finally { $archive.Dispose() }
    Write-Host "$hostName code package and trigger metadata prepared. Not deployed."
}
