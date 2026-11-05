using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UniMcp
{
    /// <summary>
    /// 通过反射访问「可选 Unity 包」的类型：ProBuilder、Cinemachine、VFX Graph、Test Runner。
    /// 这些包并非所有项目都会安装，写进 asmdef 的 references 会让未安装的项目编译失败，
    /// 因此这里统一用反射按需查找；找不到类型时由调用方返回清晰的「未安装」提示。
    /// </summary>
    public static class OptionalApi
    {
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        /// <summary>按完整类型名查找类型，找不到返回 null。</summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            lock (TypeCache)
            {
                Type cached;
                if (TypeCache.TryGetValue(fullName, out cached))
                    return cached;
            }

            Type found = Type.GetType(fullName, false);
            if (found == null)
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    try
                    {
                        found = assemblies[i].GetType(fullName, false);
                    }
                    catch (Exception)
                    {
                        found = null;
                    }

                    if (found != null)
                        break;
                }
            }

            lock (TypeCache)
            {
                TypeCache[fullName] = found;
            }

            return found;
        }

        /// <summary>依次尝试多个候选类型名，返回第一个存在的类型。</summary>
        public static Type FindAnyType(params string[] fullNames)
        {
            if (fullNames == null)
                return null;

            for (int i = 0; i < fullNames.Length; i++)
            {
                var type = FindType(fullNames[i]);
                if (type != null)
                    return type;
            }

            return null;
        }

        /// <summary>包是否可用（类型可解析）。</summary>
        public static bool IsAvailable(string fullName)
        {
            return FindType(fullName) != null;
        }

        /// <summary>创建实例，构造参数宽松匹配。</summary>
        public static object CreateInstance(Type type, params object[] args)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            args = args ?? new object[0];
            if (args.Length == 0)
                return Activator.CreateInstance(type);

            var ctors = type.GetConstructors(InstanceFlags);
            for (int i = 0; i < ctors.Length; i++)
            {
                var parameters = ctors[i].GetParameters();
                if (parameters.Length != args.Length)
                    continue;

                bool matched = true;
                for (int p = 0; p < parameters.Length; p++)
                {
                    if (!IsAssignable(parameters[p].ParameterType, args[p]))
                    {
                        matched = false;
                        break;
                    }
                }

                if (!matched)
                    continue;

                return ctors[i].Invoke(CoerceAll(parameters, args));
            }

            throw new MissingMethodException(type.FullName + " 没有匹配的构造函数");
        }

        /// <summary>调用实例方法。</summary>
        public static object Call(object target, string name, params object[] args)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            return Invoke(target.GetType(), target, name, args ?? new object[0], InstanceFlags);
        }

        /// <summary>尝试调用实例方法，失败返回 false。</summary>
        public static bool TryCall(object target, string name, out object result, params object[] args)
        {
            result = null;
            if (target == null)
                return false;

            try
            {
                result = Invoke(target.GetType(), target, name, args ?? new object[0], InstanceFlags);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>调用静态方法。</summary>
        public static object CallStatic(Type type, string name, params object[] args)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            return Invoke(type, null, name, args ?? new object[0], StaticFlags);
        }

        /// <summary>读取实例字段或属性，找不到返回 null。</summary>
        public static object Get(object target, string name)
        {
            object value;
            TryGet(target, name, out value);
            return value;
        }

        /// <summary>读取实例字段或属性。</summary>
        public static bool TryGet(object target, string name, out object value)
        {
            value = null;
            if (target == null || string.IsNullOrEmpty(name))
                return false;

            var type = target.GetType();
            while (type != null)
            {
                PropertyInfo property;
                try
                {
                    property = type.GetProperty(name, InstanceFlags);
                }
                catch (AmbiguousMatchException)
                {
                    property = type.GetProperties(InstanceFlags).FirstOrDefault(p => p.Name == name);
                }

                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        value = property.GetValue(target, null);
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                var field = type.GetField(name, InstanceFlags);
                if (field != null)
                {
                    try
                    {
                        value = field.GetValue(target);
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        /// <summary>读取静态字段或属性。</summary>
        public static object GetStatic(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return null;

            var property = type.GetProperty(name, StaticFlags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(null, null);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            var field = type.GetField(name, StaticFlags);
            if (field == null)
                return null;

            try
            {
                return field.GetValue(null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>写入实例字段或属性（支持 string/int/float/bool/enum 互转）。</summary>
        public static bool Set(object target, string name, object value)
        {
            if (target == null || string.IsNullOrEmpty(name))
                return false;

            var type = target.GetType();
            while (type != null)
            {
                PropertyInfo property;
                try
                {
                    property = type.GetProperty(name, InstanceFlags);
                }
                catch (AmbiguousMatchException)
                {
                    property = type.GetProperties(InstanceFlags).FirstOrDefault(p => p.Name == name && p.CanWrite);
                }

                if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        property.SetValue(target, Coerce(property.PropertyType, value), null);
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                var field = type.GetField(name, InstanceFlags);
                if (field != null)
                {
                    try
                    {
                        field.SetValue(target, Coerce(field.FieldType, value));
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        /// <summary>把字符串解析为枚举值，失败返回 null。</summary>
        public static object ParseEnum(Type enumType, string name)
        {
            if (enumType == null || string.IsNullOrWhiteSpace(name))
                return null;

            try
            {
                var parsed = Enum.Parse(enumType, name.Trim(), true);
                if (Enum.IsDefined(enumType, parsed))
                    return parsed;
                return parsed;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>取出异常最内层可读消息。</summary>
        public static string Unwrap(Exception exception)
        {
            var current = exception;
            while (current is TargetInvocationException && current.InnerException != null)
                current = current.InnerException;
            return current == null ? "" : current.Message;
        }

        private static object Invoke(Type type, object target, string name, object[] args, BindingFlags flags)
        {
            MethodInfo best = null;
            var bestScore = -1;

            var methods = type.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                    continue;

                var score = 0;
                var matched = true;
                for (int p = 0; p < parameters.Length; p++)
                {
                    var parameterType = parameters[p].ParameterType;
                    if (!IsAssignable(parameterType, args[p]))
                    {
                        matched = false;
                        break;
                    }

                    if (args[p] != null && parameterType == args[p].GetType())
                        score += 3;
                    else if (args[p] == null && !parameterType.IsValueType)
                        score += 2;
                    else
                        score += 1;
                }

                if (!matched || score <= bestScore)
                    continue;

                best = method;
                bestScore = score;
            }

            if (best == null)
                throw new MissingMethodException(type.FullName + "." + name);

            return best.Invoke(target, CoerceAll(best.GetParameters(), args));
        }

        private static object[] CoerceAll(ParameterInfo[] parameters, object[] args)
        {
            var result = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
                result[i] = Coerce(parameters[i].ParameterType, args[i]);
            return result;
        }

        private static bool IsAssignable(Type parameterType, object value)
        {
            if (value == null)
                return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;

            var valueType = value.GetType();
            if (parameterType.IsAssignableFrom(valueType))
                return true;

            var target = Nullable.GetUnderlyingType(parameterType) ?? parameterType;

            if (target.IsEnum)
                return value is string || value is int || value is long || target.IsInstanceOfType(value);

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target) && !target.IsEnum)
                return true;

            return false;
        }

        private static object Coerce(Type target, object value)
        {
            if (target == null)
                return value;

            var nullable = Nullable.GetUnderlyingType(target);
            var effective = nullable ?? target;

            if (value == null)
                return nullable != null || !target.IsValueType ? null : Activator.CreateInstance(target);

            if (effective.IsInstanceOfType(value))
                return value;

            if (effective.IsEnum)
            {
                if (value is string)
                    return ParseEnum(effective, (string)value);

                try
                {
                    return Enum.ToObject(effective, Convert.ToInt64(value));
                }
                catch (Exception)
                {
                    return Activator.CreateInstance(effective);
                }
            }

            try
            {
                if (value is string && (effective == typeof(bool)))
                {
                    bool parsed;
                    if (bool.TryParse((string)value, out parsed))
                        return parsed;
                }

                return Convert.ChangeType(value, effective);
            }
            catch (Exception)
            {
                return value;
            }
        }
    }
}
