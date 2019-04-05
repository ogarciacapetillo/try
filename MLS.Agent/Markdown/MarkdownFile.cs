﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Html;
using Microsoft.DotNet.Try.Markdown;
using MLS.Protocol.Execution;

namespace MLS.Agent.Markdown
{
    public class MarkdownFile
    {
        public MarkdownFile(
            RelativeFilePath path,
            MarkdownProject project)
        {
            Path = path;
            Project = project;
        }

        public RelativeFilePath Path { get; }

        public MarkdownProject Project { get; }

        public async Task<IEnumerable<CodeLinkBlock>> GetCodeLinkBlocks()
        {
            var pipeline = Project.GetMarkdownPipelineFor(Path);

            var document = Markdig.Markdown.Parse(
                ReadAllText(),
                pipeline);

            var blocks = document
                         .OfType<CodeLinkBlock>()
                         .OrderBy(c => c.Order)
                         .ToList();

            await Task.WhenAll(blocks.Select(b => b.InitializeAsync()));

            return blocks;
        }

        public async Task<IEnumerable<CodeLinkBlock>> GetEditableCodeLinkBlocks()
        {
            var blocks = (await GetCodeLinkBlocks()).Where(b => b.Options.Editable);
            return blocks;
        }

        public async Task<IEnumerable<CodeLinkBlock>> GetReadOnlyCodeLinkBlocks()
        {
            var blocks = (await GetCodeLinkBlocks()).Where(b => !b.Options.Editable);
            return blocks;
        }

        public async Task<IHtmlContent> ToHtmlContentAsync()
        {
            var pipeline = Project.GetMarkdownPipelineFor(Path);
            var html = await pipeline.RenderHtmlAsync(ReadAllText());
            return new HtmlString(html);
        }

        public string ReadAllText() =>
            Project.DirectoryAccessor.ReadAllText(Path);

        internal async Task<(Dictionary<string, Workspace.Buffer[]> buffers,  Dictionary<string, Workspace.File[]> files)> GetIncludes(IDirectoryAccessor directoryAccessor)
        {
            var buffersToIncludeBySession = new Dictionary<string, Workspace.Buffer[]>(StringComparer.InvariantCultureIgnoreCase);

            var contentBuildersByBufferBySession = new Dictionary<string, Dictionary<BufferId, StringBuilder>>(StringComparer.InvariantCultureIgnoreCase);

            var filesToIncludeBySession = new Dictionary<string, Workspace.File[]>(StringComparer.InvariantCultureIgnoreCase);

            var contentBuildersByFileBySession = new Dictionary<string, Dictionary<string, StringBuilder>>(StringComparer.InvariantCultureIgnoreCase);

            var blocks = await GetReadOnlyCodeLinkBlocks();

            foreach (var block in blocks)
            {
                var sessionId = string.IsNullOrWhiteSpace(block.Options.Session) ? "global" : block.Options.Session;
                var filePath = block.Options.DestinationFile ?? new RelativeFilePath($"./generated_include_file_{sessionId}.cs");
                var absolutePath = directoryAccessor.GetFullyQualifiedPath(filePath).FullName;

                if (string.IsNullOrWhiteSpace(block.Options.Region))
                {
                    if (!contentBuildersByFileBySession.TryGetValue(sessionId, out var sessionFileBuffers))
                    {
                        sessionFileBuffers = new Dictionary<string, StringBuilder>(StringComparer.InvariantCultureIgnoreCase);
                        contentBuildersByFileBySession[sessionId] = sessionFileBuffers;
                    }

                    if (!sessionFileBuffers.TryGetValue(absolutePath, out var fileBuffer))
                    {
                        fileBuffer = new StringBuilder();
                        sessionFileBuffers[absolutePath] = fileBuffer;
                    }

                    fileBuffer.AppendLine(block.SourceCode);
                }
                else
                {
                    var bufferId = new BufferId(absolutePath, block.Options.Region);
                    if (!contentBuildersByBufferBySession.TryGetValue(sessionId, out var sessionFileBuffers))
                    {
                        sessionFileBuffers = new Dictionary<BufferId, StringBuilder>();
                        contentBuildersByBufferBySession[sessionId] = sessionFileBuffers;
                    }

                    if (!sessionFileBuffers.TryGetValue(bufferId, out var bufferContentBuilder))
                    {
                        bufferContentBuilder = new StringBuilder();
                        sessionFileBuffers[bufferId] = bufferContentBuilder;
                    }

                    bufferContentBuilder.AppendLine(block.SourceCode);
                }
            }

            foreach (var (sessionId, contentBuildersByBuffer) in contentBuildersByBufferBySession)
            {
                buffersToIncludeBySession[sessionId] = contentBuildersByBuffer
                    .Select(contentBuilder => new Workspace.Buffer(
                        contentBuilder.Key,
                        contentBuilder.Value.ToString())
                    ).ToArray();
            }

            foreach (var (sessionId, contentBuildersByFile) in contentBuildersByFileBySession)
            {
                filesToIncludeBySession[sessionId] = contentBuildersByFile
                    .Select(fileBuffer => new Workspace.File(
                            fileBuffer.Key,
                            fileBuffer.Value.ToString()
                        )
                    ).ToArray();
            }

            return (buffersToIncludeBySession, filesToIncludeBySession);
        }
    }
}