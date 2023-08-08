// Copyright (c) Microsoft. All rights reserved.

using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;

internal class SKFunctionProxy : ISKFunction
{
    public delegate Task<SKContext> InvokeProxy(
        ISKFunction function,
        SKContext? context = null,
        CompleteRequestSettings? settings = null,
        ILogger? log = null,
        CancellationToken? cancel = null);

    private IReadOnlySkillCollection? _skillCollection;

    public ISKFunction Inner { get; }

    public InvokeProxy Wrapper { get; }

    public string Name => this.Inner.Name;

    public string SkillName => this.Inner.SkillName;

    public string Description => this.Inner.Description;

    public bool IsSemantic => this.Inner.IsSemantic;

    public CompleteRequestSettings RequestSettings => this.Inner.RequestSettings;

    public SKFunctionProxy(ISKFunction inner, InvokeProxy wrapper)
    {
        this.Inner = inner;
        this.Wrapper = wrapper;
    }

    public FunctionView Describe()
    {
        return this.Inner.Describe();
    }

    public Task<SKContext> InvokeAsync(string input, SKContext? context = null, CompleteRequestSettings? settings = null, ILogger? log = null, CancellationToken? cancel = null)
    {
        // Copied from SKFunction.
        if (context == null)
        {
            var cToken = cancel ?? default;
            log ??= NullLogger.Instance;
            context = new SKContext(
                new ContextVariables(""),
                NullMemory.Instance,
                this._skillCollection,
                log,
                cToken);
        }

        context.Variables.Update(input);

        return this.InvokeAsync(context, settings, log, cancel);
    }

    public Task<SKContext> InvokeAsync(SKContext? context = null, CompleteRequestSettings? settings = null, ILogger? log = null, CancellationToken? cancel = null)
    {
        return this.Wrapper(this.Inner, context, settings, log, cancel);
    }

    public ISKFunction SetDefaultSkillCollection(IReadOnlySkillCollection skills)
    {
        this._skillCollection = skills;
        return this.Inner.SetDefaultSkillCollection(skills);
    }

    public ISKFunction SetAIService(Func<ITextCompletion> serviceFactory)
    {
        return this.Inner.SetAIService(serviceFactory);
    }

    public ISKFunction SetAIConfiguration(CompleteRequestSettings settings)
    {
        return this.Inner.SetAIConfiguration(settings);
    }

    public static void ProxyAllSkills(IKernel kernel, InvokeProxy wrapperFunction)
    {
        var functionsView = kernel.Skills.GetFunctionsView();
        foreach ((string skillName, var skillFunctions) in functionsView.NativeFunctions.Concat(functionsView.SemanticFunctions))
        {
            foreach (var function in skillFunctions)
            {
                ISKFunction skFunc = kernel.Skills.GetFunction(function.SkillName, function.Name);
                kernel.RegisterCustomFunction(skFunc.SkillName,
                    new SKFunctionProxy(skFunc, wrapperFunction));
            }
        }
    }

    public static ISKFunction[] ProxySkills(InvokeProxy wrapperFunction, params ISKFunction[] skills)
    {
        return skills.Select(sk => new SKFunctionProxy(sk, wrapperFunction)).ToArray();
    }
}
