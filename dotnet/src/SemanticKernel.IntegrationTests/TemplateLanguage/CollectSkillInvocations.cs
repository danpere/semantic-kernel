// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Orchestration;
using System.Linq;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;

/// <summary>
///     Log of a single invocation of a skill. Note that SemanticKernel skills in general
///     can map an arbitrary SKContext to another arbitrary SKContext,
///     so this does not work in general.
/// </summary>
/// <param name="Name">The name of the skill, namespaced with a dot between the skill and function name: <c>$"{SkillName}.{FunctionName}"</c></param>
/// <param name="Input">The default string in the context when the function was called.</param>
/// <param name="Output">The default string in the context when the function returned.</param>
public record LoggedSkillInvocation(string Name, string Input, string Output);

/// <summary>
///     Collects the input/output pairs for all skill invocations made while making a request through an IKernel.
///     Requests are identified by the SKContext variable named <see cref="CorrelationKey" />;
///     if that variable is unset, nothing will be logged. Call <see cref="LogFor(string)" /> with the value of
///     that variable to get all logged skill invocations since the last time <see cref="LogFor(string)" /> was called with the same input.
/// </summary>
internal class CollectSkillInvocations : SkillProxy
{
    private readonly ConcurrentDictionary<string, List<LoggedSkillInvocation>> _log = new();

    public string CorrelationKey { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<LoggedSkillInvocation>> Log => this._log.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<LoggedSkillInvocation>)kv.Value);

    /// <summary>
    ///     Returns the list of all logged skill invocations made with the correlation key <paramref name="key" />, and clears that log.
    /// </summary>
    /// <param name="key">Value of the SKContext variable named <see cref="CorrelationKey" /> for the skill invocations.</param>
    public IReadOnlyList<LoggedSkillInvocation>? LogFor(string key)
    {
        if (this._log.Remove(key, out var logForKey))
        {
            return logForKey;
        }
        else
        {
            return null;
        }
    }

    public CollectSkillInvocations(string correlationKey)
    {
        this.CorrelationKey = correlationKey;
    }

    protected override async Task<SKContext> ProxyInvocationAsync(ISKFunction skFunc, SKContext? context = null, CompleteRequestSettings? settings = null, ILogger? log = null, CancellationToken? cancel = null)
    {
        string input = context?.Result ?? string.Empty;
        var result = await skFunc.InvokeAsync(context, settings, log, cancel);

        if (context?.Variables.Get(this.CorrelationKey, out string correlationKey) ?? false)
        {
            if (!this._log.TryGetValue(correlationKey, out var logForKey))
            {
                this._log[correlationKey] = logForKey = new();
            }
            logForKey.Add(new LoggedSkillInvocation($"{skFunc.SkillName}.{skFunc.Name}", input, result.Result));
        }

        return result;
    }
}
