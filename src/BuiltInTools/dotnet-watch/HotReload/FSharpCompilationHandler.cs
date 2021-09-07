// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Tools.Internal;

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
            return await Task.FromResult(false);
        }

        public void Dispose()
        {
            if (_deltaApplier is not null)
            {
                _deltaApplier.Dispose();
            }
        }
    }
}
