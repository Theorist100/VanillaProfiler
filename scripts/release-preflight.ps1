param(
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

function Get-SingleXmlValue([xml]$Xml, [string]$XPath, [string]$Description) {
    $node = $Xml.SelectSingleNode($XPath)
    if ($node -eq $null) {
        throw "Missing $Description ($XPath)"
    }
    return $node.InnerText
}

function Normalize-VersionText([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    $text = $Value.Trim()
    $plus = $text.IndexOf("+", [StringComparison]::Ordinal)
    if ($plus -ge 0) { $text = $text.Substring(0, $plus) }
    while ($text.EndsWith(".0", [StringComparison]::Ordinal)) {
        $text = $text.Substring(0, $text.Length - 2)
    }
    return $text
}

function Assert-VersionEqual([string]$Expected, [string]$Actual, [string]$Label) {
    if ((Normalize-VersionText $Expected) -ne (Normalize-VersionText $Actual)) {
        throw "$Label mismatch: expected '$Expected', got '$Actual'"
    }
}

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "VanillaProfiler-public.sln"
$csprojPath = Join-Path $root "VanillaProfiler.csproj"
$publishPath = Join-Path $root "Properties\PublishConfiguration.xml"
$thumbnailPath = Join-Path $root "Properties\thumbnail.png"

[xml]$csproj = Get-Content $csprojPath
[xml]$publish = Get-Content $publishPath
$sourceVersion = Get-SingleXmlValue $csproj "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Version']" "csproj Version"
$publishVersionNode = $publish.SelectSingleNode("/Publish/ModVersion")
if ($publishVersionNode -eq $null) { throw "Missing Publish/ModVersion" }
$publishVersion = $publishVersionNode.GetAttribute("Value")
if ([string]::IsNullOrWhiteSpace($publishVersion)) { throw "Publish/ModVersion is missing Value" }
Assert-VersionEqual $sourceVersion $publishVersion "Publish ModVersion"

Push-Location $root
try {
    if (-not $SkipBuild) {
        dotnet clean $solution /v:quiet /p:Configuration=$Configuration
        dotnet build $solution /v:minimal /p:Configuration=$Configuration /p:EnableLocalAnalyzers=false
    }

    $buildDir = Join-Path $root "bin\$Configuration\net48"
    $dllPath = Join-Path $buildDir "VanillaProfiler.dll"
    if (-not (Test-Path $dllPath)) {
        throw "Built DLL not found: $dllPath"
    }

    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString()
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
    Assert-VersionEqual $sourceVersion $assemblyVersion "Assembly version"
    Assert-VersionEqual $sourceVersion $versionInfo.FileVersion "File version"
    Assert-VersionEqual $sourceVersion $versionInfo.ProductVersion "Product version"

    $releaseRoot = Join-Path $root "artifacts\release"
    $stageDir = Join-Path $releaseRoot "VanillaProfiler-$sourceVersion"
    if (Test-Path $stageDir) {
        $resolvedStage = (Resolve-Path $stageDir).Path
        $resolvedReleaseRoot = if (Test-Path $releaseRoot) { (Resolve-Path $releaseRoot).Path } else { $releaseRoot }
        if (-not $resolvedStage.StartsWith($resolvedReleaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove staging path outside artifacts/release: $resolvedStage"
        }
        Remove-Item -LiteralPath $stageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stageDir | Out-Null

    Copy-Item -LiteralPath $dllPath -Destination (Join-Path $stageDir "VanillaProfiler.dll")
    Copy-Item -LiteralPath $publishPath -Destination (Join-Path $stageDir "PublishConfiguration.xml")
    Copy-Item -LiteralPath $thumbnailPath -Destination (Join-Path $stageDir "thumbnail.png")
    Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $stageDir "README.md")
    Copy-Item -LiteralPath (Join-Path $root "USER_GUIDE.md") -Destination (Join-Path $stageDir "USER_GUIDE.md")
    Copy-Item -LiteralPath (Join-Path $root "CHANGELOG.md") -Destination (Join-Path $stageDir "CHANGELOG.md")

    $unexpected = Get-ChildItem -Path $stageDir -Recurse -File |
        Where-Object {
            $_.Extension -in @(".pdb", ".mdb", ".tmp", ".bak", ".local", ".user")
        }
    if ($unexpected) {
        $list = ($unexpected | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Unexpected staging artifact(s):$([Environment]::NewLine)$list"
    }

    $publishText = $publish.OuterXml
    foreach ($required in @("VanillaProfiler.log", "CSII_Report_*.txt", "CSII_Report_*.zip")) {
        if ($publishText.IndexOf($required, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Publish text does not mention required output '$required'"
        }
    }

    $manifestPath = Join-Path $stageDir "release-manifest.txt"
    $files = Get-ChildItem -Path $stageDir -File | Sort-Object Name
    $manifest = @(
        "Version: $sourceVersion"
        "Configuration: $Configuration"
        "Source DLL: $dllPath"
        "AssemblyVersion: $assemblyVersion"
        "FileVersion: $($versionInfo.FileVersion)"
        "ProductVersion: $($versionInfo.ProductVersion)"
        "Files:"
    ) + ($files | ForEach-Object { "  $($_.Name)  $($_.Length) bytes" })
    Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8

    Write-Host "Release preflight OK"
    Write-Host "Version: $sourceVersion"
    Write-Host "Staged: $stageDir"
}
finally {
    Pop-Location
}
