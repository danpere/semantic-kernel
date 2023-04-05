// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;
using Xunit;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;
public class ReplayLogTests
{
    [Fact]
    public async Task ItDoesNotLogVariablesAsync()
    {
        // Arrange
        const string input = "template tests";
        const string winner = "SK";
        const string template = "And the winner\n of {{$input}} \nis: {{  $winner }}!";

        var logger = new FunctionLogger();
        var kernel = Kernel.Builder.WithLogger(logger).Build();
        var context = kernel.CreateNewContext();
        context["input"] = input;
        context["winner"] = winner;

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        var expected = template
            .Replace("{{$input}}", input, StringComparison.OrdinalIgnoreCase)
            .Replace("{{  $winner }}", winner, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, result);
        Assert.Empty(logger.openCalls);
        Assert.Empty(logger.log);
    }

    [Fact]
    public async Task ItLogsFunctionsAsync()
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        var logger = new FunctionLogger();
        var kernel = Kernel.Builder.WithLogger(logger).Build();
        kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        var context = kernel.CreateNewContext();
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal("== 123 ok ==", result);
        Assert.Single(logger.log.Values, "123 ok");
    }

    [Fact]
    public async Task ItReplaysFunctionsAsync()
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        const string nonsense = "not the real function result";

        var kernel = Kernel.Builder.Build();
        kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        LoadReplay(kernel, new Dictionary<(string skillName, string functionName, string context), string>
        {
            [(skillName: "my", functionName: "check123", context: "123")] = nonsense,
        });
        var context = kernel.CreateNewContext();
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal($"== {nonsense} ==", result);
    }

    [Fact]
    public async Task ItReplaysOnlyLoggedFunctionCallsAsync()
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        const string nonsense = "not the real function result";

        var kernel = Kernel.Builder.Build();
        kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        LoadReplay(kernel, new Dictionary<(string skillName, string functionName, string context), string>
        {
            [(skillName: "my", functionName: "check123", context: "321")] = nonsense,
        });
        var context = kernel.CreateNewContext();
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal("== 123 ok ==", result);
    }

    internal class FunctionLogger : ILogger
    {
        private readonly object _locker = new object();
        public readonly Dictionary<EventId, (string skillName, string functionName, string context)> openCalls = new();
        public readonly Dictionary<(string skillName, string functionName, string context), string> log = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel == LogLevel.Trace;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Trace) { return; }
            if (eventId.Name == null || !eventId.Name.StartsWith("Invoke ", StringComparison.Ordinal)) { return; }

            string message = formatter(state, exception);
            if (message.StartsWith("Invoking SKFunction ", StringComparison.Ordinal))
            {
                // Parse out function name and context.
                var match = new Regex("^Invoking SKFunction ([^.])*\\.([^ ]*) with context (.*)$").Match(message);
                if (!match.Success) { return; }
                lock (this._locker)
                {
                    this.openCalls[eventId] = (
                        skillName: match.Groups[1].Value,
                        functionName: match.Groups[2].Value,
                        context: match.Groups[3].Value);
                }
            }
            else if (message.StartsWith("Result: ", StringComparison.Ordinal))
            {
                lock (this._locker)
                {
                    if (!this.openCalls.TryGetValue(eventId, out var callInfo)) { return; }
                    string result = message.Substring("Result: ".Length);
                    this.log[callInfo] = result;
                    this.openCalls.Remove(eventId);
                }
            }
        }
    }

    private static void LoadReplay(IKernel kernel, IReadOnlyDictionary<(string skillName, string functionName, string context), string> log)
    {
        foreach (var logEntry in log.GroupBy(kvp => (kvp.Key.skillName, kvp.Key.functionName), kvp => (kvp.Key.context, result: kvp.Value)))
        {
            ISKFunction fallback = kernel.Skills.GetFunction(logEntry.Key.skillName, logEntry.Key.functionName);

            var loggedIOPairs = logEntry.ToDictionary(t => t.context, t => t.result);
#pragma warning disable CA2000 // Dispose objects before losing scope
            kernel.RegisterCustomFunction(logEntry.Key.skillName, new SKFunction(
                SKFunction.DelegateTypes.ContextSwitchInSKContextOutTaskSKContext,
                (Func<SKContext, Task<SKContext>>)(async (context) =>
                {
                    if (loggedIOPairs.TryGetValue(context.Variables.Input, out string? result))
                    {
                        context.Variables.Update(result);
                        return context;
                    }
                    return await fallback.InvokeAsync(context);
                }),
                (fallback as SKFunction)?.Parameters ?? Array.Empty<ParameterView>(),
                logEntry.Key.skillName,
                logEntry.Key.functionName,
                fallback.Description,
                fallback.IsSemantic));
#pragma warning restore CA2000 // Dispose objects before losing scope
        }
    }
}
