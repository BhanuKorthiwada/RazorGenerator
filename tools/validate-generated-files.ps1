[CmdletBinding()]
param(
    [Parameter()]
    [string]$RootPath = ".",

    [Parameter()]
    [switch]$StrictTimestamps
)

<#
.SYNOPSIS
Validates committed RazorGenerator-generated C# files for Razor views.

.DESCRIPTION
Scans a repository or project tree for `.cshtml` files that look like RazorGenerator
inputs. A file is validated when either:

- a project file declares `Generator=RazorGenerator` for the `.cshtml`, or
- a known generated output already exists next to it or under `obj\CodeGen`.

Supported generated output patterns:

- `View.generated.cs`
- `View.cs`
- `obj\CodeGen\relative\path\View.cshtml.cs`

By default the script only checks that at least one expected generated file exists.
Use `-StrictTimestamps` to also fail when a source `.cshtml` is newer than one of its
discovered generated outputs.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\tools\validate-generated-files.ps1

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\tools\validate-generated-files.ps1 -RootPath .\samples\PrecompiledMvcLibrary -StrictTimestamps
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedRoot = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RootPath).Path)
$excludedSegmentSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(".git", ".vs", "bin", "obj", "packages", "artifacts") | ForEach-Object { [void]$excludedSegmentSet.Add($_) }

function Test-ContainsRazorGeneratorMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $reader = [System.IO.File]::OpenText($FilePath)
    try {
        for ($lineIndex = 0; $lineIndex -lt 8 -and -not $reader.EndOfStream; $lineIndex++) {
            $line = $reader.ReadLine()
            if ($line -match "@\*\s*Generator\s*:") {
                return $true
            }
        }
    }
    finally {
        $reader.Dispose()
    }

    return $false
}

function Test-IsExcludedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $relativePath = $FilePath.Substring($resolvedRoot.Length).TrimStart('\', '/')
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $false
    }

    foreach ($segment in ($relativePath -split "[\\/]")) {
        if ($excludedSegmentSet.Contains($segment)) {
            return $true
        }
    }

    return $false
}

function Get-ProjectRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    return $FilePath.Substring($resolvedRoot.Length).TrimStart('\', '/')
}

function Add-UniquePath {
    param(
        [System.Collections.Generic.List[string]]$Paths,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $Paths.Contains($normalizedPath)) {
        $Paths.Add($normalizedPath)
    }
}

function Get-ProjectDeclaredGeneratedOutputs {
    $projectOutputMap = @{}
    $projectFiles = Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File -Filter *.csproj |
        Where-Object { -not (Test-IsExcludedPath -FilePath $_.FullName) }

    foreach ($projectFile in $projectFiles) {
        [xml]$projectXml = Get-Content -LiteralPath $projectFile.FullName
        $namespaceManager = New-Object System.Xml.XmlNamespaceManager($projectXml.NameTable)
        $namespaceManager.AddNamespace("msb", $projectXml.DocumentElement.NamespaceURI)

        $itemNodes = $projectXml.SelectNodes("//msb:Content | //msb:None | //msb:EmbeddedResource", $namespaceManager)
        foreach ($itemNode in $itemNodes) {
            $includePath = $itemNode.Include
            if ([string]::IsNullOrWhiteSpace($includePath) -or -not $includePath.EndsWith(".cshtml", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $generatorNode = $itemNode.SelectSingleNode("./msb:Generator", $namespaceManager)
            if ($null -eq $generatorNode -or $generatorNode.InnerText -ne "RazorGenerator") {
                continue
            }

            $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $projectFile.DirectoryName $includePath))
            $declaredOutputs = $projectOutputMap[$sourcePath]
            if ($null -eq $declaredOutputs) {
                $declaredOutputs = [System.Collections.Generic.List[string]]::new()
                $projectOutputMap[$sourcePath] = $declaredOutputs
            }

            $lastGenOutputNode = $itemNode.SelectSingleNode("./msb:LastGenOutput", $namespaceManager)
            if ($null -ne $lastGenOutputNode -and -not [string]::IsNullOrWhiteSpace($lastGenOutputNode.InnerText)) {
                Add-UniquePath -Paths $declaredOutputs -Path (Join-Path (Split-Path -Parent $sourcePath) $lastGenOutputNode.InnerText.Trim())
            }
        }
    }

    return $projectOutputMap
}

function Get-ExpectedGeneratedPaths {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$SourceFile,

        [Parameter()]
        [string[]]$ProjectDeclaredOutputs = @()
    )

    $relativePath = Get-ProjectRelativePath -FilePath $SourceFile.FullName
    $sourceDirectory = $SourceFile.DirectoryName
    $sourceFileNameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($SourceFile.Name)
    $expectedPaths = [System.Collections.Generic.List[string]]::new()

    foreach ($declaredOutput in $ProjectDeclaredOutputs) {
        Add-UniquePath -Paths $expectedPaths -Path $declaredOutput
    }

    Add-UniquePath -Paths $expectedPaths -Path ([System.IO.Path]::Combine($sourceDirectory, "$sourceFileNameWithoutExtension.generated.cs"))
    Add-UniquePath -Paths $expectedPaths -Path ([System.IO.Path]::Combine($sourceDirectory, "$sourceFileNameWithoutExtension.cs"))

    $codeGenRelativePath = [System.IO.Path]::Combine("obj", "CodeGen", $relativePath) + ".cs"
    Add-UniquePath -Paths $expectedPaths -Path ([System.IO.Path]::Combine($resolvedRoot, $codeGenRelativePath))

    return $expectedPaths.ToArray()
}

function Get-ExistingGeneratedFiles {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$SourceFile,

        [Parameter()]
        [string[]]$ProjectDeclaredOutputs = @()
    )

    $expectedPaths = Get-ExpectedGeneratedPaths -SourceFile $SourceFile -ProjectDeclaredOutputs $ProjectDeclaredOutputs
    $existingPaths = [System.Collections.Generic.List[string]]::new()

    foreach ($expectedPath in $expectedPaths) {
        if (Test-Path -LiteralPath $expectedPath -PathType Leaf) {
            $existingPaths.Add([System.IO.Path]::GetFullPath($expectedPath))
        }
    }

    return $existingPaths.ToArray()
}

$projectDeclaredOutputsBySource = Get-ProjectDeclaredGeneratedOutputs
$sourceFiles = Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File -Filter *.cshtml |
    Where-Object { -not (Test-IsExcludedPath -FilePath $_.FullName) } |
    Sort-Object FullName

$validatedSources = 0
$missingOutputFailures = [System.Collections.Generic.List[object]]::new()
$staleOutputFailures = [System.Collections.Generic.List[object]]::new()

foreach ($sourceFile in $sourceFiles) {
    $projectDeclaredOutputs = @(
        @($projectDeclaredOutputsBySource[$sourceFile.FullName]) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $hasMarker = Test-ContainsRazorGeneratorMarker -FilePath $sourceFile.FullName
    $existingGeneratedFiles = @(
        @(
            Get-ExistingGeneratedFiles -SourceFile $sourceFile -ProjectDeclaredOutputs $projectDeclaredOutputs
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    $shouldValidate = $projectDeclaredOutputs.Count -gt 0 -or $existingGeneratedFiles.Count -gt 0
    if (-not $shouldValidate) {
        continue
    }

    $validatedSources++
    $relativeSourcePath = Get-ProjectRelativePath -FilePath $sourceFile.FullName

    if ($existingGeneratedFiles.Count -eq 0) {
        $missingOutputFailures.Add([pscustomobject]@{
            Source = $relativeSourcePath
            Marker = $hasMarker
            ExpectedPaths = Get-ExpectedGeneratedPaths -SourceFile $sourceFile -ProjectDeclaredOutputs $projectDeclaredOutputs
        })
        continue
    }

    if ($StrictTimestamps.IsPresent) {
        foreach ($generatedFilePath in $existingGeneratedFiles) {
            $generatedFile = Get-Item -LiteralPath $generatedFilePath
            if ($sourceFile.LastWriteTimeUtc -gt $generatedFile.LastWriteTimeUtc) {
                $staleOutputFailures.Add([pscustomobject]@{
                    Source = $relativeSourcePath
                    Generated = $generatedFilePath.Substring($resolvedRoot.Length).TrimStart('\', '/')
                    SourceUtc = $sourceFile.LastWriteTimeUtc
                    GeneratedUtc = $generatedFile.LastWriteTimeUtc
                })
            }
        }
    }
}

$hasFailures = $missingOutputFailures.Count -gt 0 -or $staleOutputFailures.Count -gt 0

if ($missingOutputFailures.Count -gt 0) {
    Write-Host "Missing generated files:" -ForegroundColor Red
    foreach ($failure in $missingOutputFailures) {
        Write-Host ("  - {0}" -f $failure.Source) -ForegroundColor Red
        foreach ($expectedPath in $failure.ExpectedPaths) {
            $displayPath = $expectedPath.Substring($resolvedRoot.Length).TrimStart('\', '/')
            Write-Host ("      expected: {0}" -f $displayPath) -ForegroundColor Red
        }
    }
}

if ($staleOutputFailures.Count -gt 0) {
    Write-Host "Stale generated files:" -ForegroundColor Red
    foreach ($failure in $staleOutputFailures) {
        Write-Host ("  - {0}" -f $failure.Source) -ForegroundColor Red
        Write-Host ("      generated: {0}" -f $failure.Generated) -ForegroundColor Red
        Write-Host ("      source utc:    {0:o}" -f $failure.SourceUtc) -ForegroundColor Red
        Write-Host ("      generated utc: {0:o}" -f $failure.GeneratedUtc) -ForegroundColor Red
    }
}

if ($hasFailures) {
    Write-Host ("Validation failed. Checked {0} RazorGenerator source file(s)." -f $validatedSources) -ForegroundColor Red
    exit 1
}

Write-Host ("Validation passed. Checked {0} RazorGenerator source file(s)." -f $validatedSources) -ForegroundColor Green
exit 0
