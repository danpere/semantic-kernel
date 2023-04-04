// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SemanticFunctions;
using Microsoft.SemanticKernel.SkillDefinition;
using Microsoft.SemanticKernel.TemplateEngine;
using Xunit;

namespace SemanticKernel.IntegrationTests.TemplateLanguage;
public class ProxySkillTests
{
    [Fact]
    public async Task ItDoesNotLogVariablesAsync()
    {
        // Arrange
        const string input = "template tests";
        const string winner = "SK";
        const string template = "And the winner\n of {{$input}} \nis: {{  $winner }}!";

        var kernel = new LoggingKernel(Kernel.Builder.Build());
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
        Assert.Empty(kernel.Log);
    }

    [Fact]
    public async Task ItLogsFunctionsAsync()
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        var kernel = new LoggingKernel(Kernel.Builder.Build());
        kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        var context = kernel.CreateNewContext();
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal("== 123 ok ==", result);
        Assert.Single(kernel.Log.Values, "123 ok");
    }

    [Fact]
    public async Task ItReplaysFunctionsAsync()
    {
        // Arrange
        const string template = "== {{my.check123 $call}} ==";
        const string nonsense = "not the real function result";

        var kernel = new ReplayKernel(Kernel.Builder.Build(), new Dictionary<(MethodInfo method, object[] args), object?>(new MethodArgsEqualityComparer())
        {
            [(method: typeof(PromptTemplateEngineTests.MySkill).GetMethod(nameof(PromptTemplateEngineTests.MySkill.MyFunction))!,
              args: new object[] { "123" })] = nonsense,
        });
        kernel.ImportSkill(new PromptTemplateEngineTests.MySkill(), "my");
        var context = kernel.CreateNewContext();
        context["call"] = "123";

        // Act
        var result = await kernel.PromptTemplateEngine.RenderAsync(template, context);

        // Assert
        Assert.Equal($"== {nonsense} ==", result);
    }

    public class SkillProxyKernel : IKernel
    {
        private readonly IKernel _kernel;
        private readonly ModuleBuilder _moduleBuilder;

        public SkillProxyKernel(IKernel kernel)
        {
            this._kernel = kernel;

            AssemblyName aName = new AssemblyName("SkillProxy_" + this.GetHashCode());
            var ab = AssemblyBuilder.DefineDynamicAssembly(aName, AssemblyBuilderAccess.RunAndCollect);
            this._moduleBuilder = ab.DefineDynamicModule(aName.Name!);
        }

        public virtual object? InvokeSkill(object skillInstance, MethodInfo method, object[] args)
        {
            return method.Invoke(skillInstance, args);
        }

        public IDictionary<string, ISKFunction> ImportSkill(object skillInstance, string skillName = "")
        {
            MethodInfo[] methods = skillInstance.GetType()
                .GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod);

            // Filter out null functions
            MethodInfo[] validMethods = (from method in methods where SKFunction.FromNativeMethod(method, skillInstance, skillName, this._kernel.Log) != null select method).ToArray();

            TypeBuilder tb = this._moduleBuilder.DefineType(skillName + "_" + skillInstance.GetType(), TypeAttributes.Public);

            string skillFieldName = "skill";
            var skillField = tb.DefineField(skillFieldName, skillInstance.GetType(), FieldAttributes.Private | FieldAttributes.InitOnly);

            string kernelFieldName = "kernel";
            var kernelField = tb.DefineField(kernelFieldName, typeof(SkillProxyKernel), FieldAttributes.Private | FieldAttributes.InitOnly);

            string methodInfosFieldName = "methodInfos";
            var methodInfosField = tb.DefineField(methodInfosFieldName, typeof(MethodInfo[]), FieldAttributes.Private | FieldAttributes.InitOnly);

            ConstructorBuilder cb = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { skillInstance.GetType(), typeof(SkillProxyKernel), typeof(MethodInfo[]) });
            ILGenerator ctorIL = cb.GetILGenerator();
            // Call object's default constructor
            ctorIL.Emit(OpCodes.Ldarg_0);
            ctorIL.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);

            ctorIL.Emit(OpCodes.Ldarg_0);
            ctorIL.Emit(OpCodes.Ldarg_1);
            ctorIL.Emit(OpCodes.Stfld, skillField);

            ctorIL.Emit(OpCodes.Ldarg_0);
            ctorIL.Emit(OpCodes.Ldarg_2);
            ctorIL.Emit(OpCodes.Stfld, kernelField);

            ctorIL.Emit(OpCodes.Ldarg_0);
            ctorIL.Emit(OpCodes.Ldarg_3);
            ctorIL.Emit(OpCodes.Stfld, methodInfosField);

            ctorIL.Emit(OpCodes.Ret);

            MethodInfo invokeSkill = typeof(SkillProxyKernel).GetMethod(nameof(InvokeSkill))!;
            for (int i = 0; i < validMethods.Length; i++)
            {
                var method = validMethods[i];
                MethodBuilder mb = tb.DefineMethod(method.Name, method.Attributes, method.CallingConvention, method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray());

                foreach (var customAttribute in method.CustomAttributes)
                {
                    mb.SetCustomAttribute(new CustomAttributeBuilder(
                        customAttribute.Constructor,
                        customAttribute.ConstructorArguments.Select(arg => arg.Value).ToArray()));
                }

                ILGenerator mIL = mb.GetILGenerator();
                mIL.Emit(OpCodes.Ldarg_0);
                mIL.Emit(OpCodes.Ldfld, kernelField);

                mIL.Emit(OpCodes.Ldarg_0);
                mIL.Emit(OpCodes.Ldfld, skillField);

                //mIL.Emit(OpCodes.Ldstr, method.Name);

                mIL.Emit(OpCodes.Ldarg_0);
                mIL.Emit(OpCodes.Ldfld, methodInfosField);
                mIL.Emit(OpCodes.Ldc_I4, i);
                mIL.Emit(OpCodes.Ldelem_Ref);

                int numParams = method.GetParameters().Length;
                mIL.Emit(OpCodes.Ldc_I4, numParams);
                mIL.Emit(OpCodes.Newarr, typeof(object));

                for (int p = 0; p < numParams; p++)
                {
                    mIL.Emit(OpCodes.Dup);
                    mIL.Emit(OpCodes.Ldc_I4, p);
                    // Parameter 0 is this.
                    mIL.Emit(OpCodes.Ldarg, (short)(p + 1));
                    mIL.Emit(OpCodes.Stelem_Ref);
                }

                mIL.Emit(OpCodes.Callvirt, invokeSkill);
                mIL.Emit(OpCodes.Ret);
            }

            Type t = tb.CreateType()!;
            return this._kernel.ImportSkill(Activator.CreateInstance(t, skillInstance, this, validMethods)!, skillName);
        }

        #region Direct calls into _kernel
        public KernelConfig Config => this._kernel.Config;

        public ILogger Log => this._kernel.Log;

        public ISemanticTextMemory Memory => this._kernel.Memory;

        public IPromptTemplateEngine PromptTemplateEngine => this._kernel.PromptTemplateEngine;

        public IReadOnlySkillCollection Skills => this._kernel.Skills;

        public SKContext CreateNewContext()
        {
            return this._kernel.CreateNewContext();
        }

        public ISKFunction Func(string skillName, string functionName)
        {
            return this._kernel.Func(skillName, functionName);
        }

        public T GetService<T>(string name = "")
        {
            return this._kernel.GetService<T>(name);
        }

        public ISKFunction RegisterCustomFunction(string skillName, ISKFunction customFunction)
        {
            return this._kernel.RegisterCustomFunction(skillName, customFunction);
        }

        public void RegisterMemory(ISemanticTextMemory memory)
        {
            this._kernel.RegisterMemory(memory);
        }

        public ISKFunction RegisterSemanticFunction(string functionName, SemanticFunctionConfig functionConfig)
        {
            return this._kernel.RegisterSemanticFunction(functionName, functionConfig);
        }

        public ISKFunction RegisterSemanticFunction(string skillName, string functionName, SemanticFunctionConfig functionConfig)
        {
            return this._kernel.RegisterSemanticFunction(skillName, functionName, functionConfig);
        }

        public Task<SKContext> RunAsync(params ISKFunction[] pipeline)
        {
            return this._kernel.RunAsync(pipeline);
        }

        public Task<SKContext> RunAsync(string input, params ISKFunction[] pipeline)
        {
            return this._kernel.RunAsync(input, pipeline);
        }

        public Task<SKContext> RunAsync(ContextVariables variables, params ISKFunction[] pipeline)
        {
            return this._kernel.RunAsync(variables, pipeline);
        }

        public Task<SKContext> RunAsync(CancellationToken cancellationToken, params ISKFunction[] pipeline)
        {
            return this._kernel.RunAsync(cancellationToken, pipeline);
        }

        public Task<SKContext> RunAsync(string input, CancellationToken cancellationToken, params ISKFunction[] pipeline)
        {
            return this._kernel.RunAsync(input, cancellationToken, pipeline);
        }

        public Task<SKContext> RunAsync(ContextVariables variables, CancellationToken cancellationToken, params ISKFunction[] pipeline)
        {
            return this._kernel.RunAsync(variables, cancellationToken, pipeline);
        }
        #endregion
    }

    internal class MethodArgsEqualityComparer : IEqualityComparer<(MethodInfo method, object[] args)>
    {
        public bool Equals((MethodInfo method, object[] args) x, (MethodInfo method, object[] args) y)
        {
            return StructuralComparisons.StructuralEqualityComparer.Equals(x, y);
        }

        public int GetHashCode([DisallowNull] (MethodInfo method, object[] args) obj)
        {
            return StructuralComparisons.StructuralEqualityComparer.GetHashCode(obj);
        }
    }

    internal class LoggingKernel : SkillProxyKernel
    {
        public Dictionary<(MethodInfo method, object[] args), object?> Log { get; } = new(new MethodArgsEqualityComparer());

        public LoggingKernel(IKernel kernel) : base(kernel)
        {
        }

        public override object? InvokeSkill(object skillInstance, MethodInfo method, object[] args)
        {
            return this.Log[(method, args)] = base.InvokeSkill(skillInstance, method, args);
        }
    }

    internal class ReplayKernel : SkillProxyKernel
    {
        public IReadOnlyDictionary<(MethodInfo method, object[] args), object?> Log { get; }

        public ReplayKernel(IKernel kernel, IReadOnlyDictionary<(MethodInfo method, object[] args), object?> log) : base(kernel)
        {
            this.Log = log;
        }

        public override object? InvokeSkill(object skillInstance, MethodInfo method, object[] args)
        {
            if (this.Log.TryGetValue((method, args), out object? result))
            {
                return result;
            }

            return base.InvokeSkill(skillInstance, method, args);
        }
    }
}
