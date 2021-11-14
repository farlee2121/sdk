// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Tools.Internal;
using System.Collections.Immutable;
using FSharp.Compiler.CodeAnalysis;
using Microsoft.FSharp.Core;
using System.IO;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal sealed class FSharpCompilationHandler : IDisposable
    {
        private readonly IReporter _reporter;
        private IDeltaApplier? _deltaApplier;
        public FSharpCompilationHandler(IReporter reporter)
        {
            _reporter = reporter;
        }

        public async ValueTask InitializeAsync(DotNetWatchContext context, CancellationToken cancellationToken)
        {
            Debug.Assert(context.ProjectGraph is not null);

            if (_deltaApplier is null)
            {
                var hotReloadProfile = HotReloadProfileReader.InferHotReloadProfile(context.ProjectGraph, _reporter);
                _deltaApplier = hotReloadProfile switch
                {
                    HotReloadProfile.BlazorWebAssembly => new BlazorWebAssemblyDeltaApplier(_reporter),
                    HotReloadProfile.BlazorHosted => new BlazorWebAssemblyHostedDeltaApplier(_reporter),
                    _ => new DefaultDeltaApplier(_reporter),
                };
            }

            await _deltaApplier.InitializeAsync(context, cancellationToken);
        }

        public async ValueTask<bool> TryHandleFileChange(DotNetWatchContext context, FileItem[] files, CancellationToken cancellationToken)
        {
            Debug.Assert(_deltaApplier != null);

            _reporter.Output($"**cwd:{context.FileSet.Project.RunWorkingDirectory}");
            _reporter.Output($"**cmd:{context.FileSet.Project.RunCommand}");
            _reporter.Output($"**ver:{context.FileSet.Project.TargetFrameworkVersion}");

            //NOTE: i can't directly effect the compilation resources on disk because they are locked by another process
            //var dotnetRebuild = new DotNetBuildFilter(new ReusedFileSetFactory(context.FileSet), new Internal.ProcessRunner(_reporter), _reporter);
            //await dotnetRebuild.ProcessAsync(context, cancellationToken);
            try
            {

                var checker = FSharpChecker.Create(
                    projectCacheSize: FSharpOption<int>.None,
                    keepAssemblyContents: FSharpOption<bool>.None,
                    keepAllBackgroundResolutions: FSharpOption<bool>.None,
#pragma warning disable CS0618 // Type or member is obsolete
                legacyReferenceResolver: FSharpOption<LegacyReferenceResolver>.None,
#pragma warning restore CS0618 // Type or member is obsolete
                tryGetMetadataSnapshot: FSharpOption<FSharpFunc<Tuple<string, DateTime>, FSharpOption<Tuple<object, IntPtr, int>>>>.None,
                    suggestNamesForErrors: FSharpOption<bool>.None,
                    keepAllBackgroundSymbolUses: FSharpOption<bool>.None,
                    enableBackgroundItemKeyStoreAndSemanticClassification: FSharpOption<bool>.None,
                    enablePartialTypeChecking: FSharpOption<bool>.None);

                string projectPath = context.FileSet.Project.ProjectPath;

                string projectDirectory = Path.GetDirectoryName(projectPath) ?? "";
                var projectFiles = Directory.GetFiles(projectDirectory, "*.fs") ?? new string[0];

                var tempPath = Path.Join( Path.GetTempPath(), "HotReload");
                var dllOutput = Path.Join(tempPath, $"{Path.GetFileNameWithoutExtension(projectPath)}.dll");

                var compileAsync = checker.Compile(new string[] { "", "-o", dllOutput, "--debug:full", "-a" }.Concat(projectFiles).ToArray(),
                    //FSharpOption<Tuple<TextWriter, TextWriter>>.None,
                    FSharpOption<string>.None);
                var resultTuple = Microsoft.FSharp.Control.FSharpAsync.RunSynchronously(compileAsync, FSharpOption<int>.None, FSharpOption<CancellationToken>.None);

                //var assembly = resultTuple.Item3.Value;

                //var moduleId = assembly.Modules.First().ModuleVersionId;

                var peReader = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dllOutput));
                var metaReader = peReader.GetMetadataReader();
                var moduleIdHandle = metaReader.GetModuleDefinition().Mvid;
                var moduleId = metaReader.GetGuid(moduleIdHandle);
                var ilUpdate = peReader.GetSectionData(peReader.PEHeaders.SectionHeaders.First().VirtualAddress).GetContent();
                //System.Reflection.PortableExecutable.DebugDirectoryEntry
                
                var pdbPath = Path.ChangeExtension(tempPath, ".pdb");
                var pdbBytes = File.ReadAllBytes(pdbPath);
                //var didPdbReadSucceed = peReader.ReadEmbeddedPortablePdbDebugDirectoryData() //peReader.TryOpenAssociatedPortablePdb(dllOutput, )

                var updatedTypes = metaReader.TypeDefinitions.Select(handle => handle.GetHashCode());
                //var updatedMethodAddresses = metaReader.MethodDefinitions.Select(m => metaReader.GetMethodDefinition(m).RelativeVirtualAddress);
                //var ilUpdate = 

                var updates = ImmutableArray.Create(new CodeAnalysis.ExternalAccess.Watch.Api.WatchHotReloadService.Update(
                    moduleId,
                    ilUpdate,
                    ImmutableArray<byte>.Empty, 
                    pdbBytes.ToImmutableArray(),
                    updatedTypes.ToImmutableArray()));
                //context.ProjectGraph.ProjectNodesTopologicallySorted.First().ProjectInstance.Items; 
                bool success = await _deltaApplier.Apply(context, updates, cancellationToken);
                _reporter.Output($"**apply success: {success}");
            }
            catch (Exception ex)
            {
                _reporter.Output(ex.Message);
                _reporter.Output(ex.ToString());
            }
            return await Task.FromResult(true);
        }

        public void Dispose()
        {
            if (_deltaApplier is not null)
            {
                _deltaApplier.Dispose();
            }
        }

        private class ReusedFileSetFactory : IFileSetFactory
        {
            private readonly FileSet _fileSet;

            public ReusedFileSetFactory(FileSet fileSet)
            {
                _fileSet = fileSet;
            }
            public Task<FileSet> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(_fileSet);
        }
    }
}
