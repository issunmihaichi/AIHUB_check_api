param(
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "",
    [string]$LiteOutputDirectory = "",
    [string[]]$AdditionalScanTarget = @()
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$configFile = Join-Path $repoRoot "NuGet.Config"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\', '/')
$scanScript = Join-Path $PSScriptRoot "scan-release.ps1"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot "AIHubRouter-$Runtime"
}
if ([string]::IsNullOrWhiteSpace($LiteOutputDirectory)) {
    $LiteOutputDirectory = Join-Path $artifactsRoot "AIHubRouter-$Runtime-lite"
}

function Get-SafeArtifactPath([string]$directory) {
    $fullPath = [IO.Path]::GetFullPath($directory).TrimEnd('\', '/')
    $artifactsPrefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish directories must stay inside the repository artifacts directory."
    }

    return $fullPath
}

function Remove-SafeArtifactDirectory([string]$directory) {
    $fullPath = Get-SafeArtifactPath $directory
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
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

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Assert-SingleExecutable([string]$directory, [string]$expectedExecutable) {
    $unexpectedFiles = @(
        Get-ChildItem -LiteralPath $directory -Recurse -File |
            Where-Object FullName -ne $expectedExecutable
    )
    $unexpectedDirectories = @(Get-ChildItem -LiteralPath $directory -Recurse -Directory)
    if ($unexpectedFiles.Count -gt 0 -or $unexpectedDirectories.Count -gt 0) {
        throw "A publish directory contains files other than AIHubRouter.exe."
    }
}

function Install-StagedDirectories(
    [string]$portableStage,
    [string]$liteStage,
    [string]$portableDestination,
    [string]$liteDestination,
    [string]$stagingRoot
) {
    $portableBackup = Join-Path $stagingRoot "backup-portable"
    $liteBackup = Join-Path $stagingRoot "backup-lite"
    $portableBackedUp = $false
    $liteBackedUp = $false
    $portableInstalled = $false
    $liteInstalled = $false

    try {
        if (Test-Path -LiteralPath $portableDestination) {
            Move-Item -LiteralPath $portableDestination -Destination $portableBackup
            $portableBackedUp = $true
        }
        if (Test-Path -LiteralPath $liteDestination) {
            Move-Item -LiteralPath $liteDestination -Destination $liteBackup
            $liteBackedUp = $true
        }

        Move-Item -LiteralPath $portableStage -Destination $portableDestination
        $portableInstalled = $true
        Move-Item -LiteralPath $liteStage -Destination $liteDestination
        $liteInstalled = $true
    }
    catch {
        if ($portableInstalled) {
            Remove-SafeArtifactDirectory $portableDestination
        }
        if ($liteInstalled) {
            Remove-SafeArtifactDirectory $liteDestination
        }
        if ($portableBackedUp) {
            Move-Item -LiteralPath $portableBackup -Destination $portableDestination
        }
        if ($liteBackedUp) {
            Move-Item -LiteralPath $liteBackup -Destination $liteDestination
        }
        throw
    }

    if ($portableBackedUp) {
        Remove-SafeArtifactDirectory $portableBackup
    }
    if ($liteBackedUp) {
        Remove-SafeArtifactDirectory $liteBackup
    }
}

$OutputDirectory = Get-SafeArtifactPath $OutputDirectory
$LiteOutputDirectory = Get-SafeArtifactPath $LiteOutputDirectory
if ($OutputDirectory.Equals($LiteOutputDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Portable and lite publish directories must be different."
}

Invoke-ReleaseScan @()

$stagingRoot = Get-SafeArtifactPath (Join-Path $artifactsRoot (".stage-" + [Guid]::NewGuid().ToString("N")))
$portableStage = Join-Path $stagingRoot "portable"
$liteStage = Join-Path $stagingRoot "lite"
New-Item -ItemType Directory -Path $portableStage, $liteStage -Force | Out-Null

try {
    Invoke-DotNet @(
        "restore",
        (Join-Path $repoRoot "AIHubRouter.sln"),
        "--configfile", $configFile
    )
    Invoke-DotNet @(
        "run",
        "--project", (Join-Path $repoRoot "tests\AIHubRouter.Core.Tests\AIHubRouter.Core.Tests.csproj"),
        "--no-restore",
        "-c", "Release"
    )
    Invoke-DotNet @(
        "restore",
        (Join-Path $repoRoot "src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj"),
        "--configfile", $configFile,
        "-r", $Runtime
    )
    Invoke-DotNet @(
        "publish",
        (Join-Path $repoRoot "src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj"),
        "--no-restore",
        "-c", "Release",
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $portableStage
    )

    Invoke-DotNet @(
        "publish",
        (Join-Path $repoRoot "src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj"),
        "--no-restore",
        "-c", "Release",
        "-r", $Runtime,
        "--self-contained", "false",
        "-p:PublishSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $liteStage
    )

    $portableStageExe = Join-Path $portableStage "AIHubRouter.exe"
    $liteStageExe = Join-Path $liteStage "AIHubRouter.exe"
    $scanTargets = @(
        $portableStageExe,
        $liteStageExe,
        (Join-Path $repoRoot "src\AIHubRouter.WinForms\bin\Release\net10.0-windows\$Runtime\AIHubRouter.dll"),
        (Join-Path $repoRoot "src\AIHubRouter.Core\bin\Release\net10.0\AIHubRouter.Core.dll")
    ) + $AdditionalScanTarget
    $missingScanTargets = @($scanTargets | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missingScanTargets.Count -gt 0) {
        throw "A required release scan target is missing."
    }
    Invoke-ReleaseScan $scanTargets

    Assert-SingleExecutable $portableStage $portableStageExe
    Assert-SingleExecutable $liteStage $liteStageExe
    Install-StagedDirectories `
        $portableStage `
        $liteStage `
        $OutputDirectory `
        $LiteOutputDirectory `
        $stagingRoot
}
finally {
    Remove-SafeArtifactDirectory $stagingRoot
}

Write-Host "Portable build: $OutputDirectory"
Write-Host "Lite build: $LiteOutputDirectory"
