[CmdletBinding()]
param(
    [string] $Target = "Verify",
    [string] $Configuration = "Release",
    [string] $Verbosity = "minimal",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $CakeArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsDir = Join-Path $repoRoot ".tools"
$cakeExe = Join-Path $toolsDir "dotnet-cake.exe"
$nugetExe = Join-Path $toolsDir "nuget.exe"
$buildScript = Join-Path $repoRoot "build.cake"

if (-not (Test-Path $buildScript)) {
    throw "Cannot find build.cake at $buildScript"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is required to bootstrap Cake. Install .NET SDK 8.0 or newer and retry."
}

if (-not (Test-Path $cakeExe)) {
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    & dotnet tool install Cake.Tool --tool-path $toolsDir
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install Cake.Tool into $toolsDir"
    }
}

if (-not (Test-Path $nugetExe)) {
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    $nugetUrl = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $nugetUrl -OutFile $nugetExe
}

& $cakeExe $buildScript "--target=$Target" "--configuration=$Configuration" "--verbosity=$Verbosity" "--nuget-exe=$nugetExe" @CakeArgs
if ($LASTEXITCODE -ne 0) {
    throw "Cake build failed with exit code $LASTEXITCODE"
}
