# dotnet restore + build + test for the whole solution.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')

Push-Location $root
try {
    & $dotnet restore; if ($LASTEXITCODE -ne 0) { throw 'restore failed' }
    & $dotnet build --no-restore -nologo; if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    & $dotnet test --project tests/TicketShine.Support.Tests --no-build
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
