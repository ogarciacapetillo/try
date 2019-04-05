﻿using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.DotNet.Try.Markdown;
using MLS.Agent.Markdown;
using MLS.Protocol;
using MLS.Protocol.Diagnostics;
using MLS.Protocol.Execution;
using WorkspaceServer;
using WorkspaceServer.Servers.Roslyn;

namespace MLS.Agent.CommandLine
{
    public static class VerifyCommand
    {
        public static async Task<int> Do(
            VerifyOptions options,
            IConsole console,
            Func<IDirectoryAccessor> getDirectoryAccessor,
            PackageRegistry packageRegistry,
            StartupOptions startupOptions = null)
        {
            var directoryAccessor = getDirectoryAccessor();
            var markdownProject = new MarkdownProject(
                directoryAccessor, 
                packageRegistry,
                startupOptions);
            var returnCode = 0;
            var workspaceServer = new Lazy<RoslynWorkspaceServer>(() => new RoslynWorkspaceServer(packageRegistry));

            var markdownFiles = markdownProject.GetAllMarkdownFiles().ToArray();

            if (markdownFiles.Length == 0)
            {
                console.Error.Write($"No markdown files found under {options.RootDirectory}");
                return -1;
            }

            foreach (var markdownFile in markdownFiles)
            {
                var fullName = directoryAccessor.GetFullyQualifiedPath(markdownFile.Path).FullName;

                console.Out.WriteLine();
                console.Out.WriteLine(fullName);
                console.Out.WriteLine(new string('-', fullName.Length));

                var codeLinkBlocks = await markdownFile.GetCodeLinkBlocks();

                var sessions = codeLinkBlocks.GroupBy(block => block.Options?.Session);

                foreach (var session in sessions)
                {
                    if (session.Select(block => block.ProjectOrPackageName()).Distinct().Count() != 1)
                    {
                        SetError();
                        console.Out.WriteLine($"Session cannot span projects or packages: --session {session.Key}");
                        continue;
                    }

                    foreach (var codeLinkBlock in session)
                    {
                        ReportCodeLinkageResults(codeLinkBlock);
                    }

                    Console.ResetColor();

                    if (!session.Any(block => block.Diagnostics.Any()))
                    {
                        var (buffersToInclude, filesToInclude) = await markdownFile.GetIncludes(directoryAccessor);
                        await ReportCompileResults(session, markdownFile, filesToInclude, buffersToInclude);
                    }

                    Console.ResetColor();
                }
            }

            return returnCode;

            void SetError()
            {
                Console.ForegroundColor = ConsoleColor.Red;
                returnCode = 1;
            }

            async Task ReportCompileResults(IGrouping<string, CodeLinkBlock> session, MarkdownFile markdownFile, Dictionary<string, Workspace.File[]> filesToInclude, Dictionary<string, Workspace.Buffer[]> buffersToInclude)
            {
                console.Out.WriteLine($"\n  Compiling samples for session \"{session.Key}\"\n");

                var editableCodeBlocks = session.Where(b => b.Options.Editable).ToList();

                var projectOrPackageName = session
                    .Select(b => b.ProjectOrPackageName())
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

                var buffers = editableCodeBlocks
                              .Select(block => block.GetBufferAsync(directoryAccessor, markdownFile))
                              .ToList();

                var files = new List<Workspace.File>();

                if (filesToInclude.TryGetValue("global", out var globalIncludes))
                {
                    files.AddRange(globalIncludes);
                }

                if (!string.IsNullOrWhiteSpace(session.Key) && filesToInclude.TryGetValue(session.Key, out var sessionIncludes))
                {
                    files.AddRange(sessionIncludes);
                }

                if (buffersToInclude.TryGetValue("global", out var globalSessionBuffersToInclude))
                {
                    buffers.AddRange(globalSessionBuffersToInclude);
                }

                if (!string.IsNullOrWhiteSpace(session.Key) && buffersToInclude.TryGetValue(session.Key, out var localSessionBuffersToInclude))
                {
                    buffers.AddRange(localSessionBuffersToInclude);
                }

                var workspace = new Workspace(
                    workspaceType: projectOrPackageName,
                    files: files.ToArray(),
                    buffers: buffers.ToArray());

                var result = await workspaceServer.Value.Compile(new WorkspaceRequest(workspace));

                var projectDiagnostics = result.GetFeature<ProjectDiagnostics>()
                                               .Where(e => e.Severity == DiagnosticSeverity.Error)
                                               .ToArray();
                if (projectDiagnostics.Any())
                {
                    SetError();

                    console.Out.WriteLine($"    Build failed for project {projectOrPackageName}");

                    foreach (var diagnostic in projectDiagnostics)
                    {
                        console.Out.WriteLine($"\t\t{diagnostic.Location}: {diagnostic.Message}");
                    }
                }
                else
                {
                    var symbol = !result.Succeeded
                                     ? "X"
                                     : "✓";

                    if (result.Succeeded)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        console.Out.WriteLine($"    {symbol}  No errors found within samples for session \"{session.Key}\"");
                    }
                    else
                    {
                        SetError();

                        console.Out.WriteLine($"    {symbol}  Errors found within samples for session \"{session.Key}\"");

                        foreach (var diagnostic in result.GetFeature<Diagnostics>())
                        {
                            console.Out.WriteLine($"\t\t{diagnostic.Message}");
                        }
                    }
                }
            }

            void ReportCodeLinkageResults(CodeLinkBlock codeLinkBlock)
            {
                var diagnostics = codeLinkBlock.Diagnostics.ToArray();

                Console.ResetColor();

                console.Out.WriteLine("  Checking Markdown...");

                if (diagnostics.Any())
                {
                    SetError();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                }

                var blockOptions = (LocalCodeLinkBlockOptions) codeLinkBlock.Options;

                var sourceFile =
                    blockOptions?.SourceFile != null
                        ? directoryAccessor.GetFullyQualifiedPath(blockOptions.SourceFile).FullName
                        : "UNKNOWN";

                var project = blockOptions?.Project?.FullName ?? "UNKNOWN";

                var symbol = diagnostics.Any()
                                 ? "X"
                                 : "✓";

                console.Out.WriteLine($"    {symbol}  Line {codeLinkBlock.Line + 1}:\t{sourceFile} (in project {project})");

                foreach (var diagnostic in diagnostics)
                {
                    console.Out.WriteLine($"\t\t{diagnostic}");
                }
            }
        }
    }
}