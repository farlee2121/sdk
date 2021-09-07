// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Watcher.Internal;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal class HotReload : IDisposable
    {
        private readonly StaticFileHandler _staticFileHandler;
        private readonly ScopedCssFileHandler _scopedCssFileHandler;
        private readonly CompilationHandler _compilationHandler;
        private readonly FSharpCompilationHandler _fSharpCompilationHandler;
        private CompilerFamily _compilerFamily;

        public HotReload(ProcessRunner processRunner, IReporter reporter)
        {
            _staticFileHandler = new StaticFileHandler(reporter);
            _scopedCssFileHandler = new ScopedCssFileHandler(reporter);
            _compilationHandler = new CompilationHandler(reporter);
            _fSharpCompilationHandler = new FSharpCompilationHandler(reporter);
        }

        private enum CompilerFamily
        {
            Roslyn,
            FSharp
        }

        private CompilerFamily ClassifyProjectCompiler(DotNetWatchContext dotNetWatchContext)
        {
            if (dotNetWatchContext.FileSet.Project.ProjectPath.EndsWith(".fsproj")) return CompilerFamily.FSharp;
            else return CompilerFamily.Roslyn;
        }

        public async ValueTask InitializeAsync(DotNetWatchContext dotNetWatchContext, CancellationToken cancellationToken)
        {
            //TODO: this probably prevents multi-language solutions. We need to modify the roslyn handler to be picky about what projects it can handle
            //      so we avoid build issues from roslyn trying to handle fsproj. Then we could change this to await both initializations
            // - It may be a good idea to share a list of roslyn project extensions between that and DotNetBuildFilter

            _compilerFamily = ClassifyProjectCompiler(dotNetWatchContext);
            await (_compilerFamily switch
            {
                CompilerFamily.Roslyn => _compilationHandler.InitializeAsync(dotNetWatchContext, cancellationToken),
                CompilerFamily.FSharp => _fSharpCompilationHandler.InitializeAsync(dotNetWatchContext, cancellationToken),
                _ => ValueTask.CompletedTask,
            });
        }

        public async ValueTask<bool> TryHandleFileChange(DotNetWatchContext context, FileItem[] files, CancellationToken cancellationToken)
        {
            HotReloadEventSource.Log.HotReloadStart(HotReloadEventSource.StartType.Main);

            var fileHandlerResult = false;
            for (var i = files.Length - 1; i >= 0; i--)
            {
                var file = files[i];
                if (await _staticFileHandler.TryHandleFileChange(context, file, cancellationToken) ||
                    await _scopedCssFileHandler.TryHandleFileChange(context, file, cancellationToken))
                {
                    fileHandlerResult = true;
                }
            }

            fileHandlerResult |= await (_compilerFamily switch
            {
                CompilerFamily.Roslyn => _compilationHandler.TryHandleFileChange(context, files, cancellationToken),
                CompilerFamily.FSharp => _fSharpCompilationHandler.TryHandleFileChange(context, files, cancellationToken),
                _ => ValueTask.FromResult(true),
            });

            HotReloadEventSource.Log.HotReloadEnd(HotReloadEventSource.StartType.Main);
            return fileHandlerResult;
        }

        public void Dispose()
        {
            _compilationHandler.Dispose();
        }
    }
}
