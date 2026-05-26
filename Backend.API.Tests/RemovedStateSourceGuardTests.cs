using System;
using System.Collections.Generic;
using System.IO;
using Shouldly;

namespace Backend.API.Tests;

public sealed class RemovedStateSourceGuardTests
{
    [Test]
    public void Active_sources_do_not_reference_removed_process_state_contracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scannedRoots = new[]
        {
            "Backend.API",
            "Backend.DTOs",
            "Backend.Interfaces",
            "Backend.ProcessState.Contracts",
            "Backend.Repositories",
            "Backend.Services",
            "Backend.Utilities",
            "Backend.Database.Relational/Entities",
            "Backend.Database.MigrationService",
            "Backend.State.DAL",
            "Backend.Plugins.Harness",
            "Backend.Plugins.Communication",
            "UI/src",
            "UI/e2e",
            "IntegrationTests/WorkflowBuilder.IntegrationTests",
            "PerformanceTests"
        };
        var forbiddenTerms = new[]
        {
            "global" + "State",
            "Global" + "State",
            "Global " + "State",
            "required" + "Global" + "State",
            "With" + "Global" + "State",
            "I" + "Global" + "State",
            "Plugin" + "Global" + "State"
        };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".ts",
            ".html",
            ".json",
            ".js",
            ".md"
        };

        var matches = new List<string>();
        foreach (var root in scannedRoots)
        {
            var fullRoot = Path.Combine(repositoryRoot, root.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file)) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var term in forbiddenTerms)
                {
                    if (text.Contains(term, StringComparison.Ordinal))
                    {
                        matches.Add($"{Path.GetRelativePath(repositoryRoot, file)} contains {term}");
                    }
                }
            }
        }

        matches.ShouldBeEmpty();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Backend.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
