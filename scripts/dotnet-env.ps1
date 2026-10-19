# Find a dotnet with the .NET 10 SDK: prefer the per-user install (dotnet-install), then dotnet on PATH.
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
) | Where-Object { $_ -and (Test-Path $_) }

$script:dotnet = $null
foreach ($c in $candidates) {
    if ((& $c --list-sdks) -match '^10\.') { $script:dotnet = $c; break }
}
if (-not $script:dotnet) {
    throw '.NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
}
$env:DOTNET_ROOT = Split-Path -Parent $script:dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Write-Host "Using $script:dotnet"
