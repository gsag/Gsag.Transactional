#:package Spectre.Console@0.49.1

using System;
using System.Diagnostics;
using System.IO;
using Spectre.Console;

// Change to repo root to ensure relative paths work correctly
var currentDir = Environment.CurrentDirectory;
var repoRoot = currentDir;

// Verify we're in the repo root by checking for docs/_src/docfx.json
if (!File.Exists(Path.Combine(repoRoot, "docs", "_src", "docfx.json")))
{
    AnsiConsole.MarkupLine("[red]✗ Error: Could not find docs/_src/docfx.json. Please run from repo root.[/]");
    Environment.Exit(1);
}

var docsDir = Path.Combine(repoRoot, "docs");
var docfxConfigPath = Path.Combine(repoRoot, "docs", "_src", "docfx.json");

try
{
    AnsiConsole.MarkupLine("[cyan]\n1/4 Checking if docfx is installed...[/]");

    if (!CheckDocfxInstalled())
    {
        AnsiConsole.MarkupLine("[red]docfx not found. Install it with:[/]");
        AnsiConsole.MarkupLine("[yellow]  dotnet tool install -g docfx[/]");
        Environment.Exit(1);
    }

    AnsiConsole.MarkupLine("[cyan]\n2/4 Cleaning previous output...[/]");
    if (Directory.Exists(docsDir))
    {
        foreach (var dir in Directory.GetDirectories(docsDir))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName != "_src")
            {
                Directory.Delete(dir, true);
            }
        }

        foreach (var file in Directory.GetFiles(docsDir))
        {
            File.Delete(file);
        }
    }

    AnsiConsole.MarkupLine("[cyan]\n3/4 Generating API metadata...[/]");

    if (RunCommand("docfx", $"metadata \"{docfxConfigPath}\"") != 0)
    {
        AnsiConsole.MarkupLine("[red]✗ Error: docfx metadata failed.[/]");
        Environment.Exit(1);
    }

    AnsiConsole.MarkupLine("[cyan]\n4/4 Building documentation...[/]");

    if (RunCommand("docfx", $"\"{docfxConfigPath}\"") != 0)
    {
        AnsiConsole.MarkupLine("[red]✗ Error: docfx build failed.[/]");
        Environment.Exit(1);
    }

    AnsiConsole.MarkupLine($"[green]\n✓ Documentation build completed successfully![/]");
    AnsiConsole.MarkupLine($"[dim]  Output: {Path.GetFullPath(docsDir)}[/]");
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]✗ Error: {ex.Message}[/]");
    Environment.Exit(1);
}

static int RunCommand(string command, string arguments)
{
    try
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };

        using var process = Process.Start(processInfo);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start process: {command}");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(output))
        {
            AnsiConsole.WriteLine(output);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            AnsiConsole.MarkupLine($"[yellow]{error}[/]");
        }

        return process.ExitCode;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗ Error running command '{command}': {ex.Message}[/]");
        return 1;
    }
}

static bool CheckDocfxInstalled()
{
    try
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "docfx",
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null) return false;

        process.WaitForExit();
        return process.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}
