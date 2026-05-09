param(
    [switch]$LocalAnalyzers,
    [switch]$NoLocalAnalyzers
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "VanillaProfiler-public.sln"
$localProps = Join-Path $root "Directory.Build.props"
$useLocalAnalyzers = $LocalAnalyzers -and -not $NoLocalAnalyzers
if ($useLocalAnalyzers -and -not (Test-Path $localProps)) {
    throw "Local analyzers requested, but Directory.Build.props is not present. That file is intentionally local/private."
}
$analyzerSwitch = if ($useLocalAnalyzers) { "/p:EnableLocalAnalyzers=true" } else { "/p:EnableLocalAnalyzers=false" }

Push-Location $root
try {
    dotnet clean $solution /v:quiet
    dotnet build $solution /v:minimal $analyzerSwitch
}
finally {
    Pop-Location
}
