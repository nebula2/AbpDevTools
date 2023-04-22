﻿using CliFx.Infrastructure;
using Spectre.Console;
using System.Diagnostics;

namespace AbpDevTools.Commands;

[Command("migrate", Description = "Runs all .DbMigrator projects in folder recursively.")]
public class MigrateCommand : ICommand
{
    [CommandParameter(0, IsRequired = false, Description = "Working directory to run build. Probably project or solution directory path goes here. Default: . (Current Directory)")]
    public string WorkingDirectory { get; set; }

    [CommandOption("no-build", Description = "Skipts build before running. Passes '--no-build' parameter to dotnet run.")]
    public bool NoBuild { get; set; }

    protected readonly List<RunningProjectItem> runningProjects = new();

    protected IConsole console;

    public async ValueTask ExecuteAsync(IConsole console)
    {
        this.console = console;
        if (string.IsNullOrEmpty(WorkingDirectory))
        {
            WorkingDirectory = Directory.GetCurrentDirectory();
        }

        var dbMigrators = Directory.EnumerateFiles(WorkingDirectory, "*.csproj", SearchOption.AllDirectories)
           .Where(x => x.EndsWith("DbMigrator.csproj"))
           .Select(x => new FileInfo(x))
           .ToList();

        var cancellationToken = console.RegisterCancellationHandler();

        await console.Output.WriteLineAsync($"{dbMigrators.Count} db migrator(s) found.");

        var commandPostFix = NoBuild ? " --no-build" : string.Empty;

        foreach (var dbMigrator in dbMigrators)
        {
            var process = Process.Start(new ProcessStartInfo("dotnet", $"run --project {dbMigrator.FullName}" + commandPostFix)
            {
                WorkingDirectory = Path.GetDirectoryName(dbMigrator.FullName),
                RedirectStandardOutput = true,
            });

            runningProjects.Add(new RunningProjectItem
            {
                Name = dbMigrator.Name,
                Process = process,
                Status = "Running..."
            });
        }

        await console.Output.WriteAsync("Waiting for db migrators to finish...");
        cancellationToken.Register(KillRunningProcesses);

        await RenderStatusAsync();

        await console.Output.WriteLineAsync("Migrations finished.");

        KillRunningProcesses();
    }

    private async Task RenderStatusAsync()
    {
        var table = new Table().Border(TableBorder.Ascii);

        AnsiConsole.WriteLine(Environment.NewLine);
        await AnsiConsole.Live(table)
            .StartAsync(async ctx =>
            {
                table.AddColumn("Project");
                table.AddColumn("Status");
                
                UpdateTable(table);
                ctx.UpdateTarget(table);

                foreach (var runningProject in runningProjects)
                {
                    runningProject.Process.OutputDataReceived += (sender, args) =>
                    {
                        if (args?.Data != null && args.Data.Length < 90)
                        {
                            runningProject.Status = args.Data[args.Data.IndexOf(']')..].Replace('[', '\0').Replace(']', '\0');
                            UpdateTable(table);
                            ctx.UpdateTarget(table);
                        }
                    };
                    runningProject.Process.BeginOutputReadLine();
                }

                await Task.WhenAll(runningProjects.Select(x => x.Process.WaitForExitAsync()));
            });
    }

    private void UpdateTable(Table table)
    {
        table.Rows.Clear();
        foreach (var runningProject in runningProjects)
        {
            table.AddRow(
                runningProject.Name,
                runningProject.Status);
        }
    }

    protected void KillRunningProcesses()
    {
        console.Output.WriteLine($"- Killing running {runningProjects.Count} processes...");
        foreach (var project in runningProjects)
        {
            project.Process.Kill(entireProcessTree: true);

            project.Process.WaitForExit();
        }
    }
}
