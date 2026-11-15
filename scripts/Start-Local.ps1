param([int]$Port = 0)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not (Test-Path -LiteralPath '.local\settings.json')) { throw 'Run scripts\Initialize-Local.ps1 first.' }
if (-not (Test-Path -LiteralPath 'src\Cohors.Api\wwwroot\index.html')) { throw 'Run scripts\Build-Local.ps1 first.' }
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$configuration = Get-Content -LiteralPath '.local\settings.json' -Raw | ConvertFrom-Json
if ($Port -eq 0) { $Port = $configuration.Local.WebPort }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Choose an unprivileged valid loopback port.' }
$container = $configuration.Local.ContainerName
docker start $container | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The configured SQL container is unavailable; recover it explicitly, do not replace persisted data.' }
$env:COHORS_HOME = $root
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://localhost:$Port"
$env:ASPNETCORE_CONTENTROOT = Join-Path $root 'src\Cohors.Api'
Write-Host "Cohors: http://localhost:$Port | separate account passwords: .local\demo-access.txt"
& $dotnet (Join-Path $root 'src\Cohors.Api\bin\Debug\net10.0\Cohors.Api.dll')
exit $LASTEXITCODE
