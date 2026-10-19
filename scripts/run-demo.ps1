<#
.SYNOPSIS
  Build and run the Ticket Shine Support Agent demo at http://127.0.0.1:5080
.PARAMETER LiveAi
  Use Live AI mode (requires ANTHROPIC_API_KEY or `ant auth login`). Default: Template demo, no key needed.
.PARAMETER Reset
  Delete the demo database before running.
#>
param(
    [switch]$LiveAi,
    [switch]$Reset
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')

if ($Reset) {
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $root 'src/TicketShine.Support.Web/App_Data/support.db*')
    Write-Host 'Demo database removed.'
}

$env:Demo__DraftMode = if ($LiveAi) { 'LiveAi' } else { 'Template' }

& $dotnet build (Join-Path $root 'TicketShine.SupportAgent.sln') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host "Starting demo (draft mode: $env:Demo__DraftMode) at http://127.0.0.1:5080 — Ctrl+C to stop."
& $dotnet run --project (Join-Path $root 'src/TicketShine.Support.Web') --no-build --launch-profile demo
