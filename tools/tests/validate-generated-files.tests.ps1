[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$scriptPath = Join-Path $repoRoot "tools\validate-generated-files.ps1"

function New-TestRoot {
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("razorgenerator-validator-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    return $tempRoot
}

function New-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrEmpty($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $Content -NoNewline
}

function Invoke-Validator {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter()]
        [switch]$StrictTimestamps
    )

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RootPath $RootPath @(
        if ($StrictTimestamps) { "-StrictTimestamps" }
    ) 2>&1

    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = [string]::Join([Environment]::NewLine, $output)
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        $Actual,

        [Parameter(Mandatory = $true)]
        $Expected,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "{0} Expected: {1}; Actual: {2}" -f $Message, $Expected, $Actual
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSubstring,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if ($Actual -notlike "*$ExpectedSubstring*") {
        throw "{0} Missing substring: {1}`nActual output:`n{2}" -f $Message, $ExpectedSubstring, $Actual
    }
}

$testRoots = [System.Collections.Generic.List[string]]::new()

try {
    $root = New-TestRoot
    $testRoots.Add($root)
    New-File -Path (Join-Path $root "Views\Home\Index.cshtml") -Content "@* Generator: MvcView *@`n<h1>Index</h1>"
    New-File -Path (Join-Path $root "Views\Home\Index.generated.cs") -Content "// generated"
    $result = Invoke-Validator -RootPath $root
    Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "Marker + generated sibling should pass."
    Assert-Contains -Actual $result.Output -ExpectedSubstring "Validation passed" -Message "Pass output should be clear."

    $root = New-TestRoot
    $testRoots.Add($root)
    $projectPath = Join-Path $root "Sample.csproj"
    New-File -Path $projectPath -Content @"
<Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup>
    <Content Include="Views\Home\Missing.cshtml">
      <Generator>RazorGenerator</Generator>
      <LastGenOutput>Missing.generated.cs</LastGenOutput>
    </Content>
  </ItemGroup>
</Project>
"@
    New-File -Path (Join-Path $root "Views\Home\Missing.cshtml") -Content "@* Generator: MvcView *@`n<h1>Missing</h1>"
    $result = Invoke-Validator -RootPath $root
    Assert-Equal -Actual $result.ExitCode -Expected 1 -Message "Project-declared RazorGenerator source without output should fail."
    Assert-Contains -Actual $result.Output -ExpectedSubstring "Missing generated files" -Message "Missing output failure should be reported."

    $root = New-TestRoot
    $testRoots.Add($root)
    New-File -Path (Join-Path $root "Views\Home\Legacy.cshtml") -Content "<h1>Legacy</h1>"
    New-File -Path (Join-Path $root "Views\Home\Legacy.cs") -Content "// generated legacy"
    $result = Invoke-Validator -RootPath $root
    Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "Existing .cs sibling should opt a view into validation."

    $root = New-TestRoot
    $testRoots.Add($root)
    $sourcePath = Join-Path $root "Views\Home\Freshness.cshtml"
    $generatedPath = Join-Path $root "Views\Home\Freshness.generated.cs"
    New-File -Path $sourcePath -Content "@* Generator: MvcView *@`n<h1>Freshness</h1>"
    New-File -Path $generatedPath -Content "// generated"
    (Get-Item -LiteralPath $generatedPath).LastWriteTimeUtc = [datetime]::Parse("2026-01-01T00:00:00Z")
    (Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc = [datetime]::Parse("2026-01-02T00:00:00Z")
    $result = Invoke-Validator -RootPath $root -StrictTimestamps
    Assert-Equal -Actual $result.ExitCode -Expected 1 -Message "Strict timestamp mode should fail stale generated files."
    Assert-Contains -Actual $result.Output -ExpectedSubstring "Stale generated files" -Message "Stale output failure should be reported."

    $root = New-TestRoot
    $testRoots.Add($root)
    New-File -Path (Join-Path $root "Views\Home\MsBuildOnly.cshtml") -Content "@* Generator: MvcView *@`n<h1>MsBuild</h1>"
    New-File -Path (Join-Path $root "obj\CodeGen\Views\Home\MsBuildOnly.cshtml.cs") -Content "// generated by msbuild"
    $result = Invoke-Validator -RootPath $root
    Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "obj\\CodeGen output should satisfy validation."

    Write-Host "All validator tests passed."
}
finally {
    foreach ($testRoot in $testRoots) {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}
