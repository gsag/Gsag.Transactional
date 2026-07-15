#:package Spectre.Console@0.49.1

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Spectre.Console;

const string DocsSourceDir = "docs/_src";
const string DocfxConfigFile = "docfx.json";

var repoRoot = Environment.CurrentDirectory;
var docfxConfigPath = Path.Combine(repoRoot, DocsSourceDir, DocfxConfigFile);
var docsDir = Path.Combine(repoRoot, "docs");

AnsiConsole.Write(new FigletText("DocFX Build").Color(Color.Cyan1));
AnsiConsole.WriteLine();

// Verify repo root
if (!File.Exists(docfxConfigPath))
{
    AnsiConsole.Write(new Panel("[red]✗ Error: Could not find docs/_src/docfx.json[/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Red));
    AnsiConsole.MarkupLine("[dim]\nPlease run from repo root.[/]");
    Environment.Exit(1);
}

var steps = new List<(string Name, Func<bool> Action)>
{
    ("Checking docfx installation", () => VerifyDocfx()),
    ("Cleaning previous output", () => CleanDocs(docsDir)),
    ("Generating API metadata", () => RunDocfx("metadata")),
    ("Building documentation", () => RunDocfx("build")),
};

try
{
    AnsiConsole.MarkupLine("[dim]Starting documentation build...[/]\n");

    foreach (var (stepName, action) in steps)
    {
        var step = $"[bold cyan][/] {stepName}";

        bool result = AnsiConsole.Status()
            .Start(step, _ => action());

        if (!result)
        {
            throw new InvalidOperationException($"Step failed: {stepName}");
        }
    }

    // Success summary
    var table = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Cyan1)
        .AddColumn("[cyan]✓ Status[/]")
        .AddColumn("[cyan]Result[/]")
        .AddRow("[cyan]Build[/]", "[cyan]Completed Successfully[/]")
        .AddRow("[dim]Output[/]", $"[dim]{Path.GetFullPath(docsDir)}[/]");

    AnsiConsole.Write(new Panel(table)
        .Header("[cyan bold] Documentation Build Complete [/]")
        .BorderColor(Color.Cyan1)
        .Padding(1, 1));
}
catch (Exception ex)
{
    AnsiConsole.Write(new Panel($"[red]{ex.Message}[/]")
        .Header("[red bold] Build Failed [/]")
        .BorderColor(Color.Red)
        .Padding(1, 1));
    Environment.Exit(1);
}

bool VerifyDocfx()
{
    try
    {
        var exitCode = RunCommand("dotnet", "tool run docfx -- --version", silent: true);
        return exitCode == 0;
    }
    catch
    {
        AnsiConsole.MarkupLine("[red]docfx not found. Install with:[/]");
        AnsiConsole.MarkupLine("[yellow]  dotnet tool restore[/]");
        return false;
    }
}

bool CleanDocs(string docsDir)
{
    try
    {
        if (!Directory.Exists(docsDir)) return true;

        // Remove all dirs except _src
        Directory.GetDirectories(docsDir)
            .Where(d => Path.GetFileName(d) != "_src")
            .ToList()
            .ForEach(d => Directory.Delete(d, recursive: true));

        // Remove all files in root
        Directory.GetFiles(docsDir)
            .ToList()
            .ForEach(File.Delete);

        return true;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error cleaning docs: {ex.Message}[/]");
        return false;
    }
}

bool RunDocfx(string command)
{
    var args = command == "metadata"
        ? $"tool run docfx -- metadata \"{docfxConfigPath}\""
        : $"tool run docfx -- \"{docfxConfigPath}\"";

    var exitCode = RunCommand("dotnet", args);
    return exitCode == 0;
}

int RunCommand(string command, string arguments, bool silent = false)
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
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (!silent)
        {
            if (!string.IsNullOrWhiteSpace(output))
                AnsiConsole.WriteLine(output);
            if (!string.IsNullOrWhiteSpace(error))
                AnsiConsole.MarkupLine($"[yellow]{error}[/]");
        }

        return process.ExitCode;
    }
    catch (Exception ex)
    {
        if (!silent)
            AnsiConsole.MarkupLine($"[red]✗ Error: {ex.Message}[/]");
        return 1;
    }
}
