// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.TemplateEngine;
using Microsoft.SemanticKernel.TemplateEngine.Blocks;
using Xunit;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;
public class LoggingPromptTemplateEngineTests
{
    [Fact]
    public async Task ItDoesNotLogVariablesAsync()
    {
        // Arrange
        const string input = "template tests";
        const string winner = "SK";
        const string template = "And the winner\n of {{$input}} \nis: {{  $winner }}!";

        var loggingEngine = new LoggingPromptTemplateEngine();
        var kernel = Kernel.Builder.WithPromptTemplateEngine(loggingEngine).Build();
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
        Assert.Empty(loggingEngine.Log);
    }

    [Fact]
    public async Task ItLogsFunctionsAsync()
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        var loggingEngine = new LoggingPromptTemplateEngine();
        var kernel = Kernel.Builder.WithPromptTemplateEngine(loggingEngine).Build();
        kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        var context = kernel.CreateNewContext();
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal("== 123 ok ==", result);
        Assert.Single(loggingEngine.Log, new KeyValuePair<string, string>("my.check123 $call", "123 ok"));
    }

    [Fact]
    public async Task ItDoesNotReplayVariablesAsync()
    {
        // Arrange
        const string input = "template tests";
        const string winner = "SK";
        const string template = "And the winner\n of {{$input}} \nis: {{  $winner }}!";

        var replayEngine = new ReplayPromptTemplateEngine(new Dictionary<string, string>
        {
            ["input"] = "not " + input,
            ["winner"] = "not " + winner,
        });
        var kernel = Kernel.Builder.WithPromptTemplateEngine(replayEngine).Build();
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
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ItReplaysFunctionsAsync(bool provideSkill)
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        const string nonsense = "not the real function result";
        var replayEngine = new ReplayPromptTemplateEngine(new Dictionary<string, string>
        {
            ["my.check123 $call"] = nonsense,
        });
        var kernel = Kernel.Builder.WithPromptTemplateEngine(replayEngine).Build();
        if (provideSkill)
        {
            kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        }
        var context = kernel.CreateNewContext();
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal($"== {nonsense} ==", result);
    }

    internal class LoggingPromptTemplateEngine : PromptTemplateEngine
    {
        public Dictionary<string, string> Log { get; } = new Dictionary<string, string>();

        public LoggingPromptTemplateEngine(ILogger? logger = null) : base(logger ?? NullLogger.Instance)
        {
        }

        protected override async Task<string> RenderDynamicBlockAsync(ICodeRendering dynamicBlock, SKContext context, string blockContent)
        {
            return this.Log[blockContent] = await base.RenderDynamicBlockAsync(dynamicBlock, context, blockContent);
        }
    }

    internal class ReplayPromptTemplateEngine : PromptTemplateEngine
    {
        public Dictionary<string, string> Log { get; }

        public ReplayPromptTemplateEngine(Dictionary<string, string> replayLog, ILogger? logger = null) : base(logger ?? NullLogger.Instance)
        {
            this.Log = replayLog;
        }

        protected override Task<string> RenderDynamicBlockAsync(ICodeRendering dynamicBlock, SKContext context, string blockContent)
        {
            if (this.Log.TryGetValue(blockContent, out var result))
            {
                return Task.FromResult(result);
            }

            return base.RenderDynamicBlockAsync(dynamicBlock, context, blockContent);
        }
    }
}
