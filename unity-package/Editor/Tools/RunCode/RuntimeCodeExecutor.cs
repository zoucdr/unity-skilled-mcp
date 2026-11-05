using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using ReflectionAssembly = System.Reflection.Assembly;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace UniMcp.Tools
{
    /// <summary>
    /// 运行时内存编译执行引擎。
    ///
    /// 与 CodeRunner（UnityEditor.Compilation.AssemblyBuilder → 落盘 .dll → Assembly.Load）
    /// 不同，这里用 CodeDOM 的 CSharpCodeProvider 且 GenerateInMemory=true：
    /// 不写 .cs 源文件、不产出工程内 DLL、不触发 Unity 编译管线与域重载，
    /// 编译产物只作为内存程序集反射调用一次。
    ///
    /// CodeDOM 类型通过反射解析：不同 Unity/.NET profile 下 Microsoft.CSharp.CSharpCodeProvider
    /// 可能位于 System.dll、System.CodeDom.dll 或缺失，硬引用会让 asmdef 在未提供的项目里编译失败。
    /// 解析不到时由调用方返回明确错误，而不是让整个包无法编译。
    /// </summary>
    internal static class RuntimeCodeExecutor
    {
        public const string EngineName = "CodeDOM/GenerateInMemory";
        public const string WrapperNamespace = "UniMcpRuntime";
        public const string WrapperClass = "Executor";

        public const int MaxCodeChars = 60000;
        public const int MaxLogCharsDefault = 8000;
        public const int MaxLogEntries = 200;

        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "System.Reflection",
            "UnityEngine",
            "UnityEngine.SceneManagement",
            "UnityEditor",
            "UnityEditor.SceneManagement"
        };

        /// <summary>
        /// 已加载程序集名白名单。CSharpCodeProvider 不会沿 asmdef/引用链解析依赖，
        /// 必须显式传入程序集路径；业务程序集请用 assembly_patterns 追加（如 ^MyGame）。
        /// </summary>
        private static readonly string[] DefaultAssemblyPatterns =
        {
            "^System\\.",
            "^Microsoft\\.",
            "^netstandard$",
            "^UnityEngine",
            "^UnityEditor",
            "^Unity\\.",
            "^UniMcp",
            "^Assembly-CSharp"
        };

        private static readonly Regex NamespacePattern =
            new Regex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        private const BindingFlags DeclaredFlags = BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static;

        /// <summary>一次内存编译/执行的完整结果。字段均为可安全 JSON 序列化的简单类型。</summary>
        public sealed class Result
        {
            public string Engine = EngineName;
            public bool Compiled;
            public bool Success;
            public bool Executed;
            public string ReturnValue;
            public string Output = "";
            public int LogCount;
            public string[] Errors = new string[0];
            public string[] Warnings = new string[0];
            public string Error = "";
            public string StackTrace = "";
            public string Source = "";
            public int ReferenceCount;
            public double CompileSeconds;
            public double ExecuteSeconds;
        }

        private sealed class LogCapture
        {
            private readonly List<string> lines = new List<string>();
            private readonly int maxChars;
            private int chars;

            public LogCapture(int maxChars)
            {
                this.maxChars = maxChars;
            }

            public void OnLog(string condition, string stackTrace, LogType type)
            {
                if (lines.Count >= MaxLogEntries)
                    return;

                var text = "[" + type + "] " + (condition ?? "");
                var remaining = maxChars - chars;
                if (remaining <= 0)
                    return;
                if (text.Length > remaining)
                    text = text.Substring(0, remaining);
                lines.Add(text);
                chars += text.Length;
            }

            public int Count { get { return lines.Count; } }

            public string Text { get { return string.Join("\n", lines.ToArray()); } }
        }

        public static string[] GetDefaultUsings()
        {
            return (string[])DefaultUsings.Clone();
        }

        public static string[] GetDefaultAssemblyPatterns()
        {
            return (string[])DefaultAssemblyPatterns.Clone();
        }

        /// <summary>probe 用：统计按给定规则可引用的程序集数量；规则非法时返回 -1。</summary>
        public static int CountReferences(string[] extraPatterns)
        {
            string[] usings;
            string[] patterns;
            string error;
            if (!ValidateOptions(null, extraPatterns, out usings, out patterns, out error))
                return -1;
            return CollectReferences(patterns).Length;
        }

        /// <summary>探测当前 Editor 进程是否具备内存编译能力，不执行任何代码。</summary>
        public static bool TryResolve(out Type providerType, out Type parametersType, out string reason)
        {
            providerType = ResolveType("Microsoft.CSharp.CSharpCodeProvider");
            parametersType = ResolveType("System.CodeDom.Compiler.CompilerParameters");

            if (providerType == null || parametersType == null)
            {
                reason = "当前 Unity/.NET profile 未提供 Microsoft.CSharp.CSharpCodeProvider 或 "
                    + "System.CodeDom.Compiler.CompilerParameters；可改用 code_runner 的 mode=dll（AssemblyBuilder 流程）。";
                return false;
            }

            var compile = FindCompileMethod(providerType, parametersType);
            if (compile == null)
            {
                reason = "已找到 CodeDOM 类型，但缺少 CompileAssemblyFromSource(CompilerParameters, string[])。";
                return false;
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// 编译（可选执行）一段 C# 语句片段。同步执行，编译与用户代码都在 Editor 主线程运行。
        /// </summary>
        public static Result Run(string code, string[] extraUsings, string[] extraPatterns,
            bool captureLogs, int maxLogChars, bool execute, bool includeSource)
        {
            var result = new Result();

            if (string.IsNullOrWhiteSpace(code))
                return Failed(result, "code 参数不能为空。");

            if (code.Length > MaxCodeChars)
                return Failed(result, "code 超过 " + MaxCodeChars + " 字符上限；请拆分或改用 code_runner 的 mode=dll。");

            string usageError;
            string[] usings;
            string[] patterns;
            if (!ValidateOptions(extraUsings, extraPatterns, out usings, out patterns, out usageError))
                return Failed(result, usageError);

            Type providerType;
            Type parametersType;
            string reason;
            if (!TryResolve(out providerType, out parametersType, out reason))
                return Failed(result, reason);

            int lineOffset;
            var source = BuildSource(code, usings, out lineOffset);
            if (includeSource)
                result.Source = source;

            var references = CollectReferences(patterns);
            result.ReferenceCount = references.Length;

            var watch = Stopwatch.StartNew();
            ReflectionAssembly assembly;
            string[] errors;
            string[] warnings;
            string compileError;
            if (!Compile(providerType, parametersType, source, references, lineOffset,
                    out assembly, out errors, out warnings, out compileError))
            {
                result.CompileSeconds = watch.Elapsed.TotalSeconds;
                result.Errors = errors;
                result.Warnings = warnings;
                result.Error = compileError;
                return result;
            }

            result.Compiled = true;
            result.Warnings = warnings;
            result.Errors = errors;
            result.CompileSeconds = watch.Elapsed.TotalSeconds;

            if (!execute)
            {
                result.Success = true;
                return result;
            }

            if (assembly == null)
                return Failed(result, "编译成功但未取得内存程序集（CompiledAssembly 为空）。");

            var executorType = assembly.GetType(WrapperNamespace + "." + WrapperClass, false);
            if (executorType == null)
                return Failed(result, "包装类型 " + WrapperNamespace + "." + WrapperClass + " 未生成。");

            var executeMethod = executorType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (executeMethod == null)
                return Failed(result, "包装类型缺少静态 Execute 方法。");

            var execWatch = Stopwatch.StartNew();
            LogCapture capture = captureLogs ? new LogCapture(maxLogChars) : null;
            Application.LogCallback handler = null;

            if (capture != null)
            {
                handler = capture.OnLog;
                Application.logMessageReceived += handler;
            }

            try
            {
                object returnValue = executeMethod.Invoke(null, null);
                result.Success = true;
                result.Executed = true;
                result.ReturnValue = SafeToString(returnValue);
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                result.Error = inner.GetType().FullName + ": " + inner.Message;
                result.StackTrace = inner.StackTrace ?? "";
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().FullName + ": " + ex.Message;
                result.StackTrace = ex.StackTrace ?? "";
            }
            finally
            {
                if (handler != null)
                    Application.logMessageReceived -= handler;
                execWatch.Stop();
            }

            result.ExecuteSeconds = execWatch.Elapsed.TotalSeconds;
            if (capture != null)
            {
                result.Output = capture.Text;
                result.LogCount = capture.Count;
            }

            return result;
        }

        private static Result Failed(Result result, string message)
        {
            result.Error = message;
            result.Errors = new string[] { message };
            return result;
        }

        /// <summary>附加命名空间与程序集正则的合法性校验：命名空间会被拼进源码，必须严格限制。</summary>
        private static bool ValidateOptions(string[] extraUsings, string[] extraPatterns,
            out string[] usings, out string[] patterns, out string error)
        {
            usings = new string[0];
            patterns = new string[0];
            error = "";

            var usingList = new List<string>(DefaultUsings);
            var seen = new HashSet<string>(DefaultUsings);

            if (extraUsings != null)
            {
                if (extraUsings.Length > 64)
                {
                    error = "usings 最多 64 项。";
                    return false;
                }

                foreach (var raw in extraUsings)
                {
                    var item = raw == null ? "" : raw.Trim();
                    if (item.Length == 0)
                        continue;
                    if (item.Length > 128 || !NamespacePattern.IsMatch(item))
                    {
                        error = "非法命名空间：" + item + "；只允许点分隔的标识符。";
                        return false;
                    }
                    if (seen.Add(item))
                        usingList.Add(item);
                }
            }

            var patternList = new List<string>(DefaultAssemblyPatterns);
            if (extraPatterns != null)
            {
                if (extraPatterns.Length > 64)
                {
                    error = "assembly_patterns 最多 64 项。";
                    return false;
                }

                foreach (var raw in extraPatterns)
                {
                    var item = raw == null ? "" : raw.Trim();
                    if (item.Length == 0)
                        continue;
                    if (item.Length > 256)
                    {
                        error = "程序集正则过长（上限 256 字符）。";
                        return false;
                    }
                    try
                    {
                        new Regex(item);
                    }
                    catch (ArgumentException)
                    {
                        error = "非法程序集名称正则：" + item;
                        return false;
                    }
                    patternList.Add(item);
                }
            }

            usings = usingList.ToArray();
            patterns = patternList.ToArray();
            return true;
        }

        /// <summary>构建包装源码，返回用户代码起始行偏移，用于把编译错误行号还原成片段行号。</summary>
        private static string BuildSource(string userCode, string[] usings, out int lineOffset)
        {
            var sb = new StringBuilder();

            foreach (var ns in usings)
                sb.AppendLine("using " + ns + ";");

            // System.Object / System.Random 与 UnityEngine 同名类型同时可见时，优先解析为 Unity 对象。
            if (Regex.IsMatch(userCode, @"\bObject\b"))
                sb.AppendLine("using Object = UnityEngine.Object;");
            if (Regex.IsMatch(userCode, @"\bRandom\b") && !userCode.Contains("System.Random"))
                sb.AppendLine("using Random = UnityEngine.Random;");

            sb.AppendLine();
            sb.AppendLine("namespace " + WrapperNamespace);
            sb.AppendLine("{");
            sb.AppendLine("    public static class " + WrapperClass);
            sb.AppendLine("    {");
            sb.AppendLine("        public static object Execute()");
            sb.AppendLine("        {");

            lineOffset = CountLines(sb);

            var lines = userCode.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append("            ");
                sb.AppendLine(lines[i]);
            }

            sb.AppendLine("            return null;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static int CountLines(StringBuilder sb)
        {
            int count = 0;
            for (int i = 0; i < sb.Length; i++)
            {
                if (sb[i] == '\n')
                    count++;
            }
            return count;
        }

        /// <summary>收集当前域中匹配白名单的程序集文件路径。</summary>
        private static string[] CollectReferences(string[] patterns)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loaded = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < loaded.Length; i++)
            {
                var assembly = loaded[i];
                if (assembly == null || assembly.IsDynamic)
                    continue;

                string name;
                try
                {
                    name = assembly.GetName().Name;
                }
                catch (Exception)
                {
                    continue;
                }

                // mscorlib / System 由 CodeDOM 编译器默认引用，重复传入会引发冲突。
                if (string.IsNullOrEmpty(name) || name == "mscorlib" || name == "System")
                    continue;

                bool matched = false;
                for (int p = 0; p < patterns.Length; p++)
                {
                    try
                    {
                        if (Regex.IsMatch(name, patterns[p]))
                        {
                            matched = true;
                            break;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 无效规则已在 ValidateOptions 里拦截，这里仅防御。
                    }
                }

                if (!matched)
                    continue;

                try
                {
                    var location = assembly.Location;
                    if (!string.IsNullOrEmpty(location) && File.Exists(location))
                        paths.Add(location);
                }
                catch (Exception)
                {
                    // 某些动态程序集没有文件路径。
                }
            }

            return new List<string>(paths).ToArray();
        }

        private static bool Compile(Type providerType, Type parametersType, string source,
            string[] references, int lineOffset, out ReflectionAssembly assembly,
            out string[] errors, out string[] warnings, out string error)
        {
            assembly = null;
            errors = new string[0];
            warnings = new string[0];
            error = "";

            object compilerParameters;
            object provider;
            try
            {
                provider = Activator.CreateInstance(providerType);
                compilerParameters = Activator.CreateInstance(parametersType);

                SetProperty(parametersType, compilerParameters, "GenerateInMemory", true);
                SetProperty(parametersType, compilerParameters, "GenerateExecutable", false);
                SetProperty(parametersType, compilerParameters, "IncludeDebugInformation", false);
                SetProperty(parametersType, compilerParameters, "TreatWarningsAsErrors", false);
                SetProperty(parametersType, compilerParameters, "WarningLevel", 0);

                var refsProp = parametersType.GetProperty("ReferencedAssemblies", DeclaredFlags);
                if (refsProp == null)
                {
                    error = "CompilerParameters 缺少 ReferencedAssemblies 属性。";
                    return false;
                }

                var collection = refsProp.GetValue(compilerParameters, null);
                var addMethod = collection == null
                    ? null
                    : collection.GetType().GetMethod("Add", new Type[] { typeof(string) });
                if (addMethod == null)
                {
                    error = "无法写入 ReferencedAssemblies。";
                    return false;
                }

                for (int i = 0; i < references.Length; i++)
                    addMethod.Invoke(collection, new object[] { references[i] });
            }
            catch (Exception ex)
            {
                error = "编译器参数配置失败：" + Unwrap(ex).Message;
                return false;
            }

            object results;
            try
            {
                var compile = FindCompileMethod(providerType, parametersType);
                results = compile.Invoke(provider, new object[] { compilerParameters, new string[] { source } });
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                error = "内存编译调用失败：" + inner.Message
                    + "（若当前 Unity 未附带 CodeDOM 使用的 C# 编译器，请改用 code_runner 的 mode=dll）";
                return false;
            }

            if (results == null)
            {
                error = "编译器未返回 CompilerResults。";
                return false;
            }

            var resultsType = results.GetType();
            var errorsProp = resultsType.GetProperty("Errors", DeclaredFlags);
            var errorsObject = errorsProp == null ? null : errorsProp.GetValue(results, null);

            var errorList = new List<string>();
            var warningList = new List<string>();
            bool hasErrors = false;

            if (errorsObject != null)
            {
                var hasErrorsProp = errorsObject.GetType().GetProperty("HasErrors", DeclaredFlags);
                if (hasErrorsProp != null)
                {
                    try
                    {
                        hasErrors = Convert.ToBoolean(hasErrorsProp.GetValue(errorsObject, null));
                    }
                    catch (Exception)
                    {
                        hasErrors = false;
                    }
                }

                var enumerable = errorsObject as IEnumerable;
                if (enumerable != null)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null)
                            continue;
                        var itemType = item.GetType();
                        bool isWarning = ReadBool(itemType, item, "IsWarning");
                        int line = ReadInt(itemType, item, "Line");
                        int column = ReadInt(itemType, item, "Column");
                        var number = ReadString(itemType, item, "ErrorNumber");
                        var text = ReadString(itemType, item, "ErrorText");
                        int userLine = line - lineOffset;
                        var location = userLine > 0
                            ? "片段行 " + userLine + ":" + column
                            : "包装行 " + line + ":" + column;
                        var formatted = location + " " + number + " " + text;
                        if (isWarning)
                            warningList.Add(formatted);
                        else
                            errorList.Add(formatted);
                    }
                }
            }

            warnings = warningList.ToArray();
            errors = errorList.ToArray();

            if (hasErrors || errorList.Count > 0)
            {
                error = "代码编译失败（" + errorList.Count + " 个错误）。只接受 C# 语句片段；"
                    + "完整类/命名空间代码请用 code_runner 的 mode=dll。";
                return false;
            }

            try
            {
                var compiledProp = resultsType.GetProperty("CompiledAssembly", DeclaredFlags);
                assembly = compiledProp == null ? null : compiledProp.GetValue(results, null) as ReflectionAssembly;

                // 某些 Mono 实现会忽略 GenerateInMemory，只给出临时 DLL 路径；
                // 读入字节后立即删除临时文件，仍然不往项目里写任何程序集。
                if (assembly == null)
                {
                    var pathProp = resultsType.GetProperty("PathToAssembly", DeclaredFlags);
                    var path = pathProp == null ? null : pathProp.GetValue(results, null) as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 0)
                            assembly = ReflectionAssembly.Load(bytes);
                        try { File.Delete(path); } catch (Exception) { }
                    }
                }
            }
            catch (Exception ex)
            {
                error = "读取编译产物失败：" + Unwrap(ex).Message;
                return false;
            }

            if (assembly == null)
            {
                error = "编译未报错但没有生成内存程序集；请用 probe 检查 CodeDOM 支持情况。";
                return false;
            }

            return true;
        }

        private static MethodInfo FindCompileMethod(Type providerType, Type parametersType)
        {
            try
            {
                return providerType.GetMethod("CompileAssemblyFromSource",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { parametersType, typeof(string[]) }, null);
            }
            catch (AmbiguousMatchException)
            {
                var methods = providerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "CompileAssemblyFromSource")
                        continue;
                    var args = methods[i].GetParameters();
                    if (args.Length == 2 && args[1].ParameterType == typeof(string[]))
                        return methods[i];
                }
                return null;
            }
        }

        private static Type ResolveType(string fullName)
        {
            var candidates = new string[]
            {
                fullName,
                fullName + ", System",
                fullName + ", System.CodeDom",
                fullName + ", Microsoft.CSharp"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                Type found = null;
                try { found = Type.GetType(candidates[i], false); }
                catch (Exception) { found = null; }
                if (found != null)
                    return found;
            }

            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                Type found = null;
                try { found = loaded[i].GetType(fullName, false); }
                catch (Exception) { found = null; }
                if (found != null)
                    return found;
            }

            return null;
        }

        private static void SetProperty(Type type, object instance, string name, object value)
        {
            var prop = type.GetProperty(name, DeclaredFlags);
            if (prop != null && prop.CanWrite)
                prop.SetValue(instance, value, null);
        }

        private static bool ReadBool(Type type, object instance, string name)
        {
            var prop = type.GetProperty(name, DeclaredFlags);
            if (prop == null)
                return false;
            try { return Convert.ToBoolean(prop.GetValue(instance, null)); }
            catch (Exception) { return false; }
        }

        private static int ReadInt(Type type, object instance, string name)
        {
            var prop = type.GetProperty(name, DeclaredFlags);
            if (prop == null)
                return 0;
            try { return Convert.ToInt32(prop.GetValue(instance, null)); }
            catch (Exception) { return 0; }
        }

        private static string ReadString(Type type, object instance, string name)
        {
            var prop = type.GetProperty(name, DeclaredFlags);
            if (prop == null)
                return "";
            try { return Convert.ToString(prop.GetValue(instance, null)) ?? ""; }
            catch (Exception) { return ""; }
        }

        private static Exception Unwrap(Exception ex)
        {
            var invocation = ex as TargetInvocationException;
            return invocation != null && invocation.InnerException != null
                ? invocation.InnerException
                : ex;
        }

        private static string SafeToString(object value)
        {
            if (value == null)
                return null;
            try
            {
                var text = value.ToString();
                if (text != null && text.Length > MaxLogCharsDefault)
                    return text.Substring(0, MaxLogCharsDefault) + "…(truncated)";
                return text;
            }
            catch (Exception)
            {
                return "<ToString 失败：" + value.GetType().FullName + ">";
            }
        }
    }
}
