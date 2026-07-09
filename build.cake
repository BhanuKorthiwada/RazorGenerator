using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using IoDirectory = System.IO.Directory;
using IoFile = System.IO.File;
using IoPath = System.IO.Path;

var target = Argument("target", "Verify");
var configuration = Argument("configuration", "Release");
var verbosity = Argument("verbosity", "minimal");
var root = MakeAbsolute(Directory(".")).FullPath;
var artifacts = IoPath.Combine(root, "artifacts");
var checksumsFile = IoPath.Combine(artifacts, "SHA256SUMS.txt");
var toolingSolution = IoPath.Combine(root, "RazorGenerator.Tooling.sln");
var runtimeSolution = IoPath.Combine(root, "RazorGenerator.Runtime.sln");
var coreTestProject = IoPath.Combine(root, "RazorGenerator.Core.Test", "RazorGenerator.Core.Test.csproj");
var coreV3PackagesConfig = IoPath.Combine(root, "RazorGenerator.Core.v3", "packages.config");
var coreTestPackagesConfig = IoPath.Combine(root, "RazorGenerator.Core.Test", "packages.config");
var msBuildProject = IoPath.Combine(root, "RazorGenerator.MsBuild", "RazorGenerator.MsBuild.csproj");
var toolingProject = IoPath.Combine(root, "RazorGenerator.Tooling", "RazorGenerator.Tooling.csproj");
var nugetExe = Argument("nuget-exe", Environment.GetEnvironmentVariable("NUGET_EXE") ?? IoPath.Combine(root, ".tools", "nuget.exe"));
var powershell = Environment.GetEnvironmentVariable("POWERSHELL_EXE") ?? "powershell";
var generatedFileValidator = IoPath.Combine(root, "tools", "validate-generated-files.ps1");
var generatedFileValidatorTests = IoPath.Combine(root, "tools", "tests", "validate-generated-files.tests.ps1");
var vs = FindVisualStudio();
var msbuild = vs.MSBuildPath;
var msbuildVerbosity = ToMSBuildVerbosity(verbosity);

Information("Using MSBuild: {0}", msbuild);
Information("Using VisualStudioVersion={0}", vs.VisualStudioVersion);

Task("Clean")
    .Does(() =>
{
    if (IoDirectory.Exists(artifacts))
    {
        IoDirectory.Delete(artifacts, true);
    }

    IoDirectory.CreateDirectory(artifacts);
});

Task("Restore")
    .Does(() =>
{
    EnsureNuGet();
    NuGetRestore(toolingSolution);
});

Task("RestoreRuntime")
    .Does(() =>
{
    EnsureNuGet();
    NuGetRestore(runtimeSolution);
});

Task("RestoreCoreTest")
    .Does(() =>
{
    EnsureNuGet();
    NuGetRestorePackagesConfig(coreV3PackagesConfig);
    NuGetRestorePackagesConfig(coreTestPackagesConfig);
});

Task("BuildTooling")
    .IsDependentOn("Restore")
    .Does(() =>
{
    // VS 2026 VSSDK validation reports VSSDK1310 for the license file even
    // though the project contributes LICENSE.txt through VSIXSourceItem.
    // Keep the artifact build unblocked and verify the package contents after.
    MSBuild(toolingSolution, "Build", "/p:BypassVsixValidation=true");
});

Task("TestCore")
    .IsDependentOn("RestoreCoreTest")
    .IsDependentOn("BuildTooling")
    .Does(() =>
{
    // The core test project is scoped to the v3 runtime surface and still runs
    // through the legacy xUnit MSBuild target.
    MSBuild(coreTestProject, "Build;Test");
});

Task("Pack")
    .IsDependentOn("BuildTooling")
    .Does(() =>
{
    MSBuild(msBuildProject, "Build");
    RunProcess(
        nugetExe,
        String.Join(" ", new[]
        {
            "pack",
            Quote(msBuildProject),
            "-Properties",
            "Configuration=" + configuration,
            "-OutputDirectory",
            Quote(artifacts),
            "-Symbols",
            "-NonInteractive",
            "-Verbosity",
            "quiet"
        }));
    RequirePackage("RazorGenerator.MsBuild.*.nupkg");
});

Task("Vsix")
    .IsDependentOn("BuildTooling")
    .Does(() =>
{
    MSBuild(toolingProject, "Build", "/p:BypassVsixValidation=true");
    RequireArtifact(IoPath.Combine(artifacts, "RazorGenerator.vsix"));
    RequireZipEntry(IoPath.Combine(artifacts, "RazorGenerator.vsix"), "LICENSE.txt");
});

Task("Checksums")
    .IsDependentOn("Pack")
    .IsDependentOn("Vsix")
    .Does(() =>
{
    WriteChecksums(checksumsFile);
    RequireArtifact(checksumsFile);
});

Task("TestGeneratedFileValidator")
    .Does(() =>
{
    RunPowerShell(generatedFileValidatorTests);
});

Task("ValidateGeneratedFiles")
    .IsDependentOn("TestGeneratedFileValidator")
    .Does(() =>
{
    RunPowerShell(generatedFileValidator, "-RootPath", root);
});

Task("BuildRuntime")
    .IsDependentOn("RestoreRuntime")
    .Does(() =>
{
    MSBuild(runtimeSolution, "Build");
});

Task("Verify")
    .IsDependentOn("Clean")
    .IsDependentOn("Restore")
    .IsDependentOn("BuildTooling")
    .IsDependentOn("BuildRuntime")
    .IsDependentOn("TestCore")
    .IsDependentOn("ValidateGeneratedFiles")
    .IsDependentOn("Checksums");

RunTarget(target);

void EnsureNuGet()
{
    if (IoFile.Exists(nugetExe))
    {
        return;
    }

    throw new Exception("Could not locate NuGet.exe at " + nugetExe + ". Run build.ps1 or set NUGET_EXE.");
}

void NuGetRestore(string solution)
{
    RunProcess(
        nugetExe,
        String.Join(" ", new[]
        {
            "restore",
            Quote(solution),
            "-NonInteractive",
            "-Verbosity",
            "quiet"
        }));
}

void NuGetRestorePackagesConfig(string packagesConfig)
{
    RunProcess(
        nugetExe,
        String.Join(" ", new[]
        {
            "restore",
            Quote(packagesConfig),
            "-PackagesDirectory",
            Quote(IoPath.Combine(root, "packages")),
            "-NonInteractive",
            "-Verbosity",
            "quiet"
        }));
}

void MSBuild(string projectOrSolution, string targets, params string[] extraArgs)
{
    var args = new List<string>
    {
        Quote(projectOrSolution),
        "/m",
        "/nologo",
        "/t:" + targets,
        "/v:" + msbuildVerbosity,
        "/p:Configuration=" + configuration,
        "/p:VisualStudioVersion=" + vs.VisualStudioVersion
    };

    args.AddRange(extraArgs.Where(a => !String.IsNullOrWhiteSpace(a)));
    RunProcess(msbuild, String.Join(" ", args));
}

void RunPowerShell(string scriptPath, params string[] scriptArgs)
{
    var args = new List<string>
    {
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        Quote(scriptPath)
    };

    args.AddRange(scriptArgs.Select(a => a == root ? Quote(a) : a));
    RunProcess(powershell, String.Join(" ", args));
}

void RequireArtifact(string path)
{
    if (!IoFile.Exists(path))
    {
        throw new Exception("Expected artifact was not produced: " + path);
    }
}

void RequirePackage(string searchPattern)
{
    var packages = IoDirectory.GetFiles(artifacts, searchPattern)
        .Where(path => !path.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    if (packages.Length == 0)
    {
        throw new Exception("Expected package artifact was not produced: " + searchPattern);
    }
}

void RequireZipEntry(string zipPath, string entryName)
{
    using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
    {
        if (archive.GetEntry(entryName) == null)
        {
            throw new Exception(zipPath + " does not contain " + entryName);
        }
    }
}

void WriteChecksums(string outputPath)
{
    var files = IoDirectory.GetFiles(artifacts)
        .Where(path => !String.Equals(path, outputPath, StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => IoPath.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (files.Length == 0)
    {
        throw new Exception("No artifacts were produced for checksum generation.");
    }

    var lines = files.Select(path => ComputeSha256(path) + "  " + IoPath.GetFileName(path)).ToArray();
    IoFile.WriteAllLines(outputPath, lines);
}

string ComputeSha256(string path)
{
    using (var stream = IoFile.OpenRead(path))
    using (var sha256 = SHA256.Create())
    {
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
    }
}

void RunProcess(string fileName, string arguments)
{
    Information("> {0} {1}", fileName, arguments);

    var startInfo = new ProcessStartInfo(fileName, arguments)
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    using (var process = Process.Start(startInfo))
    {
        process.OutputDataReceived += (sender, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                Information(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (sender, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                Error(eventArgs.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception(fileName + " failed with exit code " + process.ExitCode);
        }
    }
}

string CaptureProcess(string fileName, string arguments)
{
    var startInfo = new ProcessStartInfo(fileName, arguments)
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    using (var process = Process.Start(startInfo))
    {
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception(fileName + " " + arguments + " failed: " + error);
        }

        return output.Trim();
    }
}

VisualStudioBuild FindVisualStudio()
{
    var overrideMsBuild = Environment.GetEnvironmentVariable("MSBUILD_EXE");
    if (!String.IsNullOrWhiteSpace(overrideMsBuild) && IoFile.Exists(overrideMsBuild))
    {
        return new VisualStudioBuild(overrideMsBuild, InferVisualStudioVersion(overrideMsBuild));
    }

    var vswhere = Environment.GetEnvironmentVariable("VSWHERE");
    if (String.IsNullOrWhiteSpace(vswhere))
    {
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!String.IsNullOrWhiteSpace(programFilesX86))
        {
            vswhere = IoPath.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        }
    }

    if (!String.IsNullOrWhiteSpace(vswhere) && IoFile.Exists(vswhere))
    {
        var fromVsWhere = TryFindWithVsWhere(vswhere);
        if (fromVsWhere != null)
        {
            return fromVsWhere;
        }
    }

    var roots = new[]
    {
        Environment.GetEnvironmentVariable("ProgramFiles"),
        Environment.GetEnvironmentVariable("ProgramFiles(x86)")
    }
    .Where(p => !String.IsNullOrWhiteSpace(p))
    .Distinct(StringComparer.OrdinalIgnoreCase);

    foreach (var programFilesRoot in roots)
    {
        foreach (var major in new[] { "18", "17" })
        {
            foreach (var edition in new[] { "Enterprise", "Professional", "Community", "BuildTools" })
            {
                var candidate = IoPath.Combine(programFilesRoot, "Microsoft Visual Studio", major, edition, "MSBuild", "Current", "Bin", "MSBuild.exe");
                if (IoFile.Exists(candidate))
                {
                    return new VisualStudioBuild(candidate, major + ".0");
                }
            }
        }
    }

    throw new Exception("Could not locate MSBuild from Visual Studio 2026 or 2022. Install VS Build Tools with MSBuild, or set MSBUILD_EXE.");
}

VisualStudioBuild TryFindWithVsWhere(string vswhere)
{
    foreach (var range in new[] { "[18.0,19.0)", "[17.0,18.0)" })
    {
        var installationPath = CaptureProcess(vswhere, "-latest -products * -requires Microsoft.Component.MSBuild -version " + range + " -property installationPath");
        if (String.IsNullOrWhiteSpace(installationPath))
        {
            continue;
        }

        var msbuildPath = IoPath.Combine(installationPath, "MSBuild", "Current", "Bin", "MSBuild.exe");
        if (IoFile.Exists(msbuildPath))
        {
            return new VisualStudioBuild(msbuildPath, range.StartsWith("[18.") ? "18.0" : "17.0");
        }
    }

    return null;
}

string InferVisualStudioVersion(string msbuildPath)
{
    var normalized = msbuildPath.Replace('/', '\\');
    if (normalized.IndexOf("\\Microsoft Visual Studio\\18\\", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        return "18.0";
    }

    if (normalized.IndexOf("\\Microsoft Visual Studio\\17\\", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        return "17.0";
    }

    return "18.0";
}

string ToMSBuildVerbosity(string cakeVerbosity)
{
    switch ((cakeVerbosity ?? String.Empty).ToLowerInvariant())
    {
        case "quiet":
            return "quiet";
        case "diagnostic":
            return "diag";
        case "detailed":
            return "detailed";
        case "normal":
            return "normal";
        default:
            return "minimal";
    }
}

string Quote(string value)
{
    return "\"" + value.Replace("\"", "\\\"") + "\"";
}

public sealed class VisualStudioBuild
{
    public VisualStudioBuild(string msBuildPath, string visualStudioVersion)
    {
        MSBuildPath = msBuildPath;
        VisualStudioVersion = visualStudioVersion;
    }

    public string MSBuildPath { get; private set; }
    public string VisualStudioVersion { get; private set; }
}
