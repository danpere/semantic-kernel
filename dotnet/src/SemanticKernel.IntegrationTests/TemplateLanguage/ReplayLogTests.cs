// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Xunit;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;
public class ReplayLogTests
{
    private const string Key = "key";

    [Fact]
    public async Task ItDoesNotLogVariablesAsync()
    {
        // Arrange
        const string input = "template tests";
        const string winner = "SK";
        const string template = "And the winner\n of {{$input}} \nis: {{  $winner }}!";

        var logger = new CollectSkillInvocations(Key);
        var kernel = Kernel.Builder.Build();
        logger.AttachTo(kernel);
        var context = kernel.CreateNewContext();
        context[Key] = Key; // only one call, doesn't matter what the key value is.
        context["input"] = input;
        context["winner"] = winner;

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        var expected = template
            .Replace("{{$input}}", input, StringComparison.OrdinalIgnoreCase)
            .Replace("{{  $winner }}", winner, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, result);
        Assert.Empty(logger.Log);
    }

    [Fact]
    public async Task ItLogsFunctionsAsync()
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        var logger = new CollectSkillInvocations(Key);
        var kernel = Kernel.Builder.Build();
        kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        logger.AttachTo(kernel);
        var context = kernel.CreateNewContext();
        context[Key] = Key; // only one call, doesn't matter what the key value is.
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal("== 123 ok ==", result);
        Assert.Single(logger.Log.Values.SelectMany(l => l.Select(e => e.Output)), "123 ok");
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
        context[Key] = Key; // only one call, doesn't matter what the key value is.
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
        context[Key] = Key; // only one call, doesn't matter what the key value is.
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal("== 123 ok ==", result);
    }

    private static void LoadReplay(IKernel kernel, IReadOnlyDictionary<(string skillName, string functionName, string context), string> log)
    {
        var replay = new SkillReplayer(Key, new Dictionary<string, IReadOnlyList<LoggedSkillInvocation>>
        {
            [Key] = log.Select(e => new LoggedSkillInvocation($"{e.Key.skillName}.{e.Key.functionName}", e.Key.context, e.Value)).ToList()
        });
        replay.AttachTo(kernel);
    }
}
