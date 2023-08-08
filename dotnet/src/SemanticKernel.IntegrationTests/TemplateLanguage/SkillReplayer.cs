// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Orchestration;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;

internal class SkillReplayer : SkillProxy
{
    // Dictionary is from correlation key -> function name -> input -> output
    private readonly ConcurrentDictionary<string, Dictionary<string, Dictionary<string, string>>> _log = new();

    public SkillReplayer(string correlationKey, IReadOnlyDictionary<string, IReadOnlyList<LoggedSkillInvocation>> log)
    {
        this.CorrelationKey = correlationKey;

        foreach ((string key, var entries) in log)
        {
            this._log[key] = entries.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Input, e => e.Output));
        }
    }

    public string CorrelationKey { get; }

    protected override Task<SKContext> ProxyInvocationAsync(ISKFunction skFunc, SKContext? context = null, CompleteRequestSettings? settings = null, ILogger? log = null, CancellationToken? cancel = null)
    {
        if (context?.Variables.Get(this.CorrelationKey, out string key) ?? false)
        {
            if (this._log.TryGetValue(key, out var logForKey)
                && logForKey.TryGetValue($"{skFunc.SkillName}.{skFunc.Name}", out var logForFunc)
                && logForFunc.TryGetValue(context.Result, out string? result))
            {
                context.Variables.Update(result);
                return Task.FromResult(context);
            }
        }

        return skFunc.InvokeAsync(context, settings, log, cancel);
    }
}

