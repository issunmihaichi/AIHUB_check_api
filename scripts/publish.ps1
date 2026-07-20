param(
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "",
    [string]$LiteOutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$configFile = Join-Path $repoRoot "NuGet.Config"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\AIHubRouter-$Runtime"
}
if ([string]::IsNullOrWhiteSpace($LiteOutputDirectory)) {
    $LiteOutputDirectory = Join-Path $repoRoot "artifacts\AIHubRouter-$Runtime-lite"
}

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

Write-Host "Portable build: $OutputDirectory"
Write-Host "Lite build: $LiteOutputDirectory"
