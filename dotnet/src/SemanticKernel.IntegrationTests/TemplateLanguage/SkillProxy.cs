// Copyright (c) Microsoft. All rights reserved.

using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Orchestration;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;

/// <summary>
///     Supports intercepting skill invocations to perform some computation on every invocation.
/// </summary>
internal abstract class SkillProxy
{
    /// <summary>
    ///     Proxy all skills <strong>currently</strong> in the kernel, notably not skills added after this call.
    ///     The subtype of this abstract class determines what behavior that proxy has.
    /// </summary>
    /// <param name="kernel">Kernel to modify all current skills of to go through this.</param>
    public void AttachTo(IKernel kernel)
    {
        SKFunctionProxy.ProxyAllSkills(kernel, this.ProxyInvocationAsync);
    }

    protected abstract Task<SKContext> ProxyInvocationAsync(ISKFunction skFunc, SKContext? context = null, CompleteRequestSettings? settings = null, ILogger? log = null, CancellationToken? cancel = null);
}
