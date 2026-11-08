using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    /// <summary>
    /// 通过反射调用 Unity Test Runner（com.unity.test-framework），执行 EditMode / PlayMode 测试并轮询结果。
    /// 测试框架不是本包的硬依赖，因此不使用 asmdef 引用，缺失时返回明确提示。
    /// 对应方法名: run_tests
    /// </summary>
    [ToolModule("tests", GroupEn = "Runtime", GroupZh = "运行与调试",
        Tags = new[] { "testing", "test-runner", "diagnostics" })]
    public class TestsTool : ToolModuleBase
    {
        private const string ApiTypeName = "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi";
        private const string CallbacksTypeName = "UnityEditor.TestTools.TestRunner.Api.ICallbacks";
        private const string ExecutionSettingsTypeName = "UnityEditor.TestTools.TestRunner.Api.ExecutionSettings";
        private const string FilterTypeName = "UnityEditor.TestTools.TestRunner.Api.Filter";

        private static readonly object JobLock = new object();
        private static string jobState = "idle";
        private static string jobId = "";
        private static string jobMode = "";
        private static string jobMessage = "";
        private static string startedAt = "";
        private static string finishedAt = "";
        private static int passCount;
        private static int failCount;
        private static int skipCount;
        private static int inconclusiveCount;
        private static double durationSeconds;
        private static readonly List<string> FailedTests = new List<string>();
        private static int jobCounter;
        private static TestRunnerCallbacksProxy proxy;

        public override string Description => L.T(
            "Run Unity Test Runner suites in Edit or Play mode and poll pass/fail results",
            "运行 Unity Test Runner 的 Edit / Play 模式测试并轮询通过/失败结果");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Operation type", "操作类型"), false)
                    .SetEnumValues("run", "get_status", "clear"),

                new MethodStr("mode", L.T("Test mode: EditMode or PlayMode", "测试模式：EditMode 或 PlayMode"))
                    .SetEnumValues("EditMode", "PlayMode")
                    .SetDefault("EditMode"),

                new MethodArr("test_names", L.T("Full test names to run", "要运行的完整测试名"), true, "string")
                    .AddExample("MyNamespace.MyTests.Passes"),

                new MethodArr("category_names", L.T("Test categories to run", "要运行的测试类别"), true, "string")
                    .AddExample("Fast", "Integration"),

                new MethodArr("assembly_names", L.T("Test assembly names to run", "要运行的测试程序集名"), true, "string")
                    .AddExample("MyProject.Tests"),

                new MethodArr("group_names", L.T("Test group names to run", "要运行的测试组名"), true, "string")
            };
        }

        [ToolAction("run", Parameters = new string[] { "mode", "test_names", "category_names", "assembly_names", "group_names" }, Inject = new string[] { "action=run" })]
        private object Run(JsonClass args)
        {
            object error = ResolveTypes(out var apiType, out var callbacksType, out var executionSettingsType, out var filterType);
            if (error != null)
            {
                return error;
            }
            lock (JobLock)
            {
                if (jobState == "running")
                {
                    return Response.Error("tests_running: a test run is already in progress. Poll get_status before starting another run.");
                }
            }
            string mode = (args["mode"]?.Value ?? "EditMode").Trim();
            Type testModeType = OptionalApi.FindType("UnityEditor.TestTools.TestRunner.Api.Filter+TestMode");
            if (testModeType == null)
            {
                return Response.Error("Test Runner API is present but Filter.TestMode was not found; the test framework version may be incompatible.");
            }
            object testMode = OptionalApi.ParseEnum(testModeType, mode);
            if (testMode == null)
            {
                testMode = OptionalApi.ParseEnum(testModeType, "EditMode");
            }
            try
            {
                object api = OptionalApi.CallStatic(apiType, "GetInstance");
                if (api == null)
                {
                    return Response.Error("TestRunnerApi.GetInstance() returned null.");
                }
                object filter = Activator.CreateInstance(filterType);
                if (!OptionalApi.Set(filter, "testMode", testMode))
                {
                    OptionalApi.Set(filter, "TestMode", testMode);
                }
                ApplyStringArray(filter, "testNames", ReadStringArray(args, "test_names"));
                ApplyStringArray(filter, "categoryNames", ReadStringArray(args, "category_names"));
                ApplyStringArray(filter, "assemblyNames", ReadStringArray(args, "assembly_names"));
                ApplyStringArray(filter, "groupNames", ReadStringArray(args, "group_names"));
                object executionSettings = CreateExecutionSettings(executionSettingsType, filterType, filter);
                if (executionSettings == null)
                {
                    return Response.Error("Failed to build ExecutionSettings for the Test Runner.");
                }
                object callbacks = CreateProxy(callbacksType);
                OptionalApi.Call(api, "RegisterCallbacks", callbacks, 0);
                lock (JobLock)
                {
                    jobCounter++;
                    jobId = jobCounter.ToString();
                    jobMode = mode;
                    jobMessage = "Test run started.";
                    jobState = "running";
                    startedAt = Now();
                    finishedAt = "";
                    passCount = 0;
                    failCount = 0;
                    skipCount = 0;
                    inconclusiveCount = 0;
                    durationSeconds = 0.0;
                    FailedTests.Clear();
                }
                OptionalApi.Call(api, "Execute", executionSettings);
                return Response.Success("Test run started.", Snapshot());
            }
            catch (Exception exception)
            {
                lock (JobLock)
                {
                    jobState = "error";
                    jobMessage = OptionalApi.Unwrap(exception);
                }
                return Response.Error("Failed to start the test run: " + OptionalApi.Unwrap(exception));
            }
        }

        [ToolAction("get_status", Inject = new string[] { "action=get_status" })]
        private object GetStatus(JsonClass args)
        {
            lock (JobLock)
            {
                if (jobState == "idle")
                    return Response.Success("No test run has been started in this Editor session.", Snapshot());

                return Response.Success("Test job status retrieved.", Snapshot());
            }
        }

        [ToolAction("clear", Inject = new string[] { "action=clear" })]
        private object Clear(JsonClass args)
        {
            lock (JobLock)
            {
                var previous = jobState;
                jobState = "idle";
                jobId = "";
                jobMode = "";
                jobMessage = "State cleared.";
                startedAt = "";
                finishedAt = "";
                durationSeconds = 0;
                passCount = 0;
                failCount = 0;
                skipCount = 0;
                inconclusiveCount = 0;
                FailedTests.Clear();
                proxy = null;

                return Response.Success("Test job state cleared.", new { previous_state = previous });
            }
        }

        // ---------------- 回调 ----------------

        private static object CreateProxy(Type callbacksType)
        {
            // Unity 的 .NET 配置里 DispatchProxy 往往只有泛型 Create<T,TProxy>()，
            // 编译期不存在 Create(Type, Type)。统一走反射，兼容新旧两种运行时。
            var methods = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static);
            object created = null;

            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.Name != "Create")
                    continue;

                try
                {
                    if (method.IsGenericMethodDefinition
                        && method.GetGenericArguments().Length == 2
                        && method.GetParameters().Length == 0)
                    {
                        created = method
                            .MakeGenericMethod(callbacksType, typeof(TestRunnerCallbacksProxy))
                            .Invoke(null, null);
                    }
                    else if (!method.IsGenericMethodDefinition)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 2
                            && parameters[0].ParameterType == typeof(Type)
                            && parameters[1].ParameterType == typeof(Type))
                        {
                            created = method.Invoke(null, new object[] { callbacksType, typeof(TestRunnerCallbacksProxy) });
                        }
                    }
                }
                catch (Exception)
                {
                    created = null;
                }

                if (created != null)
                    break;
            }

            if (created == null)
                throw new MissingMethodException("DispatchProxy.Create() is not available in this runtime.");

            var callbacks = (TestRunnerCallbacksProxy)created;
            callbacks.Handler = OnTestCallback;
            proxy = callbacks;
            return callbacks;
        }

        private static void OnTestCallback(string method, object argument)
        {
            try
            {
                lock (JobLock)
                {
                    if (method == "RunStarted")
                    {
                        jobState = "running";
                        startedAt = Now();
                        finishedAt = "";
                        passCount = 0;
                        failCount = 0;
                        skipCount = 0;
                        inconclusiveCount = 0;
                        FailedTests.Clear();
                        return;
                    }

                    if (method == "TestFinished")
                    {
                        RecordTest(argument);
                        return;
                    }

                    if (method == "RunFinished")
                    {
                        RecordSummary(argument);
                    }
                }
            }
            catch (Exception)
            {
                // 回调中的异常不能反向影响 Test Runner
            }
        }

        private static void RecordTest(object result)
        {
            if (result == null)
                return;

            var hasChildren = OptionalApi.Get(result, "HasChildren");
            if (hasChildren is bool && (bool)hasChildren)
                return;

            var status = OptionalApi.Get(result, "TestStatus") as string;
            if (string.IsNullOrEmpty(status))
                return;

            if (status == "Passed")
            {
                passCount++;
            }
            else if (status == "Failed")
            {
                failCount++;
                AddFailure(result);
            }
            else if (status == "Skipped")
            {
                skipCount++;
            }
            else
            {
                inconclusiveCount++;
            }
        }

        private static void RecordSummary(object result)
        {
            jobState = "finished";
            finishedAt = Now();
            jobMessage = "Test run finished.";

            if (result != null)
            {
                var totalPass = ReadInt(result, "PassCount", -1);
                var totalFail = ReadInt(result, "FailCount", -1);
                var totalSkip = ReadInt(result, "SkipCount", -1);
                var totalInconclusive = ReadInt(result, "InconclusiveCount", -1);
                var totalDuration = OptionalApi.Get(result, "Duration");

                if (totalPass >= 0)
                    passCount = totalPass;
                if (totalFail >= 0)
                    failCount = totalFail;
                if (totalSkip >= 0)
                    skipCount = totalSkip;
                if (totalInconclusive >= 0)
                    inconclusiveCount = totalInconclusive;

                if (totalDuration != null)
                {
                    try
                    {
                        durationSeconds = Convert.ToDouble(totalDuration);
                    }
                    catch (Exception)
                    {
                        durationSeconds = 0;
                    }
                }

                if (FailedTests.Count == 0)
                    CollectFailures(result, 0);
            }
        }

        private static void CollectFailures(object node, int depth)
        {
            if (node == null || depth > 8 || FailedTests.Count >= 50)
                return;

            var status = OptionalApi.Get(node, "TestStatus") as string;
            var hasChildren = OptionalApi.Get(node, "HasChildren");
            var isLeaf = !(hasChildren is bool) || !(bool)hasChildren;

            if (isLeaf && status == "Failed")
                AddFailure(node);

            var children = OptionalApi.Get(node, "Children") as IEnumerable;
            if (children == null)
                return;

            foreach (var child in children)
                CollectFailures(child, depth + 1);
        }

        private static void AddFailure(object result)
        {
            if (FailedTests.Count >= 50)
                return;

            var name = OptionalApi.Get(result, "FullName") as string;
            if (string.IsNullOrEmpty(name))
                name = OptionalApi.Get(result, "Name") as string;
            if (string.IsNullOrEmpty(name))
                name = "<unnamed test>";

            var message = OptionalApi.Get(result, "Message") as string;
            if (!string.IsNullOrEmpty(message))
            {
                var trimmed = message.Trim();
                if (trimmed.Length > 200)
                    trimmed = trimmed.Substring(0, 200) + "...";
                name += " :: " + trimmed;
            }

            FailedTests.Add(name);
        }

        // ---------------- 辅助 ----------------

        private object ResolveTypes(out Type apiType, out Type callbacksType, out Type executionSettingsType, out Type filterType)
        {
            apiType = OptionalApi.FindType(ApiTypeName);
            callbacksType = OptionalApi.FindType(CallbacksTypeName);
            executionSettingsType = OptionalApi.FindType(ExecutionSettingsTypeName);
            filterType = OptionalApi.FindType(FilterTypeName);

            if (apiType == null || callbacksType == null || executionSettingsType == null || filterType == null)
                return Response.Error("Unity Test Runner is not available. Add 'com.unity.test-framework' to the project first.");

            return null;
        }

        private static object CreateExecutionSettings(Type executionSettingsType, Type filterType, object filter)
        {
            var filters = Array.CreateInstance(filterType, 1);
            filters.SetValue(filter, 0);

            try
            {
                return Activator.CreateInstance(executionSettingsType, new object[] { filters });
            }
            catch (Exception)
            {
                // 老版本只有 ExecutionSettings(Filter) 构造
            }

            try
            {
                return Activator.CreateInstance(executionSettingsType, new object[] { filter });
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ApplyStringArray(object target, string fieldName, List<string> values)
        {
            if (values == null || values.Count == 0)
                return;

            OptionalApi.Set(target, fieldName, values.ToArray());
        }

        private static List<string> ReadStringArray(JsonClass args, string key)
        {
            var result = new List<string>();
            var array = args[key] as JsonArray;
            if (array == null)
                return result;

            for (int i = 0; i < array.Count; i++)
            {
                var value = array[i]?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }

            return result;
        }

        private static int ReadInt(object target, string name, int fallback)
        {
            var value = OptionalApi.Get(target, name);
            if (value == null)
                return fallback;

            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static object Snapshot()
        {
            return new
            {
                job_id = jobId,
                state = jobState,
                mode = jobMode,
                message = jobMessage,
                started_at = startedAt,
                finished_at = finishedAt,
                duration_seconds = durationSeconds,
                passed = passCount,
                failed = failCount,
                skipped = skipCount,
                inconclusive = inconclusiveCount,
                total = passCount + failCount + skipCount + inconclusiveCount,
                callback_registered = proxy != null,
                failed_tests = new List<string>(FailedTests)
            };
        }
    }

    /// <summary>
    /// 用 DispatchProxy 动态实现 Test Runner 的 ICallbacks 接口，避免对测试框架程序集的编译期依赖。
    /// </summary>
    public class TestRunnerCallbacksProxy : DispatchProxy
    {
        public Action<string, object> Handler;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            var handler = Handler;
            if (handler != null && targetMethod != null)
                handler(targetMethod.Name, args != null && args.Length > 0 ? args[0] : null);

            return null;
        }
    }
}
