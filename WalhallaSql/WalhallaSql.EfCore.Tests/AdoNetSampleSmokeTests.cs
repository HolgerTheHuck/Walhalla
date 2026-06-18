using System;
using System.Diagnostics;
using System.IO;

namespace WalhallaSql.EfCore.Tests;

public sealed class AdoNetSampleSmokeTests
{
    [Fact]
    [Trait("Category", "ADOEmbeddedSmoke")]
    public void Embedded_ado_sample_runs_reproducibly()
    {
        var repoRoot = FindRepositoryRoot();
        var sampleProject = Path.Combine(repoRoot, "WalhallaSql.AdoNet.Sample", "WalhallaSql.AdoNet.Sample.csproj");

        Assert.True(File.Exists(sampleProject), $"AdoNet-Sample-Projekt nicht gefunden: {sampleProject}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{sampleProject}\" --no-restore",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("WalhallaSql.AdoNet.Sample konnte nicht gestartet werden.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        var exited = process.WaitForExit(60_000);
        Assert.True(exited, $"AdoNet-Sample hat Timeout �berschritten.\nStdout: {stdout}\nStderr: {stderr}");
        Assert.True(process.ExitCode == 0,
            $"AdoNet-Sample sollte mit ExitCode 0 enden, war aber: {process.ExitCode}\nStdout: {stdout}\nStderr: {stderr}");

        Assert.Contains("UPDATE affected rows: 1", stdout, StringComparison.Ordinal);
        Assert.Contains("Users (Id >= 1):", stdout, StringComparison.Ordinal);
        Assert.Contains("1 | Ada Lovelace | 30", stdout, StringComparison.Ordinal);
        Assert.Contains("2 | Alan Turing | 42", stdout, StringComparison.Ordinal);
        Assert.Contains("Scalar result for Id=1: Ada Lovelace", stdout, StringComparison.Ordinal);
        Assert.Contains("Transaction inserted row exists: True", stdout, StringComparison.Ordinal);
        Assert.Contains("DatabasePath:", stdout, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WalhallaSql.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository-Root mit WalhallaSql.sln konnte nicht gefunden werden.");
    }
}
