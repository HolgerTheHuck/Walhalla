using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WalhallaSql.EfCore.Tests;

internal static class LocalNuGetConsumerTestHarness
{
    internal static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LayeredSql.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository-Root mit LayeredSql.sln konnte nicht gefunden werden.");
    }

    internal static void RebuildLocalFeed(string repoRoot)
    {
        var buildScript = Path.Combine(repoRoot, "scripts", "build-local-nuget-feed.ps1");
        Assert.True(File.Exists(buildScript), $"Build-Skript fuer lokalen Feed nicht gefunden: {buildScript}");

        RunProcess(
            GetPowerShellExecutable(),
            $"-ExecutionPolicy Bypass -File \"{buildScript}\"",
            repoRoot,
            timeoutMs: 180_000,
            stepName: "Lokalen NuGet-Feed bauen");
    }

    internal static void RestoreAndBuildProject(string projectPath, string nuGetConfigPath, string repoRoot, string displayName)
    {
        Assert.True(File.Exists(projectPath), $"{displayName}-Projekt nicht gefunden: {projectPath}");
        Assert.True(File.Exists(nuGetConfigPath), $"NuGet.config fuer {displayName} nicht gefunden: {nuGetConfigPath}");

        var packageCachePath = PrepareConsumerPackageCache(repoRoot, displayName);

        RunProcess(
            "dotnet",
            $"restore \"{projectPath}\" --configfile \"{nuGetConfigPath}\" --packages \"{packageCachePath}\"",
            repoRoot,
            timeoutMs: 180_000,
            stepName: $"{displayName} restore");

        RunProcess(
            "dotnet",
            $"build \"{projectPath}\" --no-restore --property:RestorePackagesPath=\"{packageCachePath}\"",
            repoRoot,
            timeoutMs: 180_000,
            stepName: $"{displayName} build");
    }

    internal static ProcessResult RunDotNetProject(string projectPath, string repoRoot, string displayName)
    {
        return RunProcess(
            "dotnet",
            $"run --project \"{projectPath}\" --no-build",
            repoRoot,
            timeoutMs: 180_000,
            stepName: $"{displayName} run");
    }

    internal static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory, int timeoutMs, string stepName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"{stepName} konnte nicht gestartet werden.");

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();

        var exited = process.WaitForExit(timeoutMs);
        Assert.True(exited, $"{stepName} hat Timeout überschritten.\nStdout: {stdOut}\nStderr: {stdErr}");
        Assert.True(
            process.ExitCode == 0,
            $"{stepName} sollte mit ExitCode 0 enden, war aber: {process.ExitCode}\nStdout: {stdOut}\nStderr: {stdErr}");

        return new ProcessResult(stdOut, stdErr);
    }

    private static string PrepareConsumerPackageCache(string repoRoot, string displayName)
    {
        var safeName = new string(displayName
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray())
            .Trim('-');
        var packageRoot = Path.Combine(repoRoot, ".artifacts", "consumer-packages", safeName);

        if (Directory.Exists(packageRoot))
        {
            Directory.Delete(packageRoot, recursive: true);
        }

        Directory.CreateDirectory(packageRoot);
        return packageRoot;
    }

    private static string GetPowerShellExecutable()
    {
        return OperatingSystem.IsWindows() ? "powershell" : "pwsh";
    }

    internal sealed record ProcessResult(string StdOut, string StdErr);
}
