param(
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "",
    [string]$LiteOutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$configFile = Join-Path $repoRoot "NuGet.Config"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$scanScript = Join-Path $PSScriptRoot "scan-release.ps1"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\AIHubRouter-$Runtime"
}
if ([string]::IsNullOrWhiteSpace($LiteOutputDirectory)) {
    $LiteOutputDirectory = Join-Path $repoRoot "artifacts\AIHubRouter-$Runtime-lite"
}

function Reset-PublishDirectory([string]$directory) {
    $fullPath = [IO.Path]::GetFullPath($directory).TrimEnd('\', '/')
    $artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish directories must stay inside the repository artifacts directory."
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Invoke-ReleaseScan([string[]]$targets) {
    if ($targets.Count -eq 0) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $scanScript
        if ($LASTEXITCODE -ne 0) {
            throw "Release security scan failed."
        }
        return
    }

    foreach ($target in $targets) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $scanScript -Path $target
        if ($LASTEXITCODE -ne 0) {
            throw "Release security scan failed."
        }
    }
}

Invoke-ReleaseScan @()
Reset-PublishDirectory $OutputDirectory
Reset-PublishDirectory $LiteOutputDirectory

dotnet restore (Join-Path $repoRoot "AIHubRouter.sln") --configfile $configFile
dotnet run --project (Join-Path $repoRoot "tests\AIHubRouter.Core.Tests\AIHubRouter.Core.Tests.csproj") --no-restore -c Release
dotnet restore (Join-Path $repoRoot "src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj") `
    --configfile $configFile `
    -r $Runtime
dotnet publish (Join-Path $repoRoot "src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj") `
    --no-restore `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDirectory

dotnet publish (Join-Path $repoRoot "src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj") `
    --no-restore `
    -c Release `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $LiteOutputDirectory

$portableExe = Join-Path $OutputDirectory "AIHubRouter.exe"
$liteExe = Join-Path $LiteOutputDirectory "AIHubRouter.exe"
$scanTargets = @(
    $portableExe,
    $liteExe,
    (Join-Path $repoRoot "src\AIHubRouter.WinForms\bin\Release\net10.0-windows\$Runtime\AIHubRouter.dll"),
    (Join-Path $repoRoot "src\AIHubRouter.Core\bin\Release\net10.0\AIHubRouter.Core.dll")
)
$missingScanTargets = @($scanTargets | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missingScanTargets.Count -gt 0) {
    throw "A required release scan target is missing."
}
Invoke-ReleaseScan $scanTargets

$unexpectedFiles = @(
    Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Where-Object FullName -ne $portableExe
    Get-ChildItem -LiteralPath $LiteOutputDirectory -Recurse -File | Where-Object FullName -ne $liteExe
)
$unexpectedDirectories = @(
    Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Directory
    Get-ChildItem -LiteralPath $LiteOutputDirectory -Recurse -Directory
)
if ($unexpectedFiles.Count -gt 0 -or $unexpectedDirectories.Count -gt 0) {
    throw "A publish directory contains files other than AIHubRouter.exe."
}

Write-Host "Portable build: $OutputDirectory"
Write-Host "Lite build: $LiteOutputDirectory"
