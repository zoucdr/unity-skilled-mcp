using System.Collections.Generic;
using System;
// Migrated from Newtonsoft.Json to SimpleJson
using System.Collections;
using UniMcp.Models;

namespace UniMcp
{
    /// <summary>
    /// Execution context for a single tool call.
    ///
    /// It carries a JSON argument bag plus a side dictionary for non-serializable
    /// object references (Unity objects). A handler receives one context and
    /// completes it exactly once, either synchronously (returning a value) or
    /// asynchronously through AsyncReturn.
    /// </summary>
    public class ToolContext
    {
        /// <summary>JSON-serializable arguments.</summary>
        public JsonClass JsonData { get; }

        /// <summary>
        /// Non-serializable object references (Unity objects and other complex
        /// values that cannot be represented as JSON).
        /// </summary>
        public Dictionary<string, object> ObjectReferences { get; }

        public Action<JsonNode> CompleteAction { get; private set; }

        public JsonNode Result { get; set; }
        public bool IsComplete { get; private set; }

        public ToolContext(JsonClass jsonData = null, Dictionary<string, object> objectReferences = null)
        {
            JsonData = jsonData ?? new JsonClass();
            ObjectReferences = objectReferences ?? new Dictionary<string, object>();
        }

        /// <summary>Register the completion callback.</summary>
        public void RegistComplete(Action<JsonNode> completeAction)
        {
            CompleteAction = completeAction;
            if (Result != null)
            {
                CompleteAction?.Invoke(Result);
            }
        }

        /// <summary>Complete the call with a result (only the first call wins).</summary>
        public void Complete(JsonNode result)
        {
            if (Result == null)
            {
                Result = result;
                IsComplete = true;
                CompleteAction?.Invoke(Result);
            }
        }

        /// <summary>Read a raw JSON field.</summary>
        public bool TryGetJsonValue(string key, out JsonNode token)
        {
            return JsonData.TryGetValue(key, out token);
        }

        /// <summary>Get an object reference.</summary>
        public bool TryGetObjectReference(string key, out object obj)
        {
            return ObjectReferences.TryGetValue(key, out obj);
        }

        /// <summary>Get an object reference with a typed check.</summary>
        public bool TryGetObjectReference<T>(string key, out T obj) where T : class
        {
            if (ObjectReferences.TryGetValue(key, out object objRef) && objRef is T typedObj)
            {
                obj = typedObj;
                return true;
            }
            obj = null;
            return false;
        }

        /// <summary>Set a JSON field.</summary>
        public void SetJsonValue(string key, JsonNode value)
        {
            JsonData[key] = value;
        }

        /// <summary>Set an object reference.</summary>
        public void SetObjectReference(string key, object obj)
        {
            ObjectReferences[key] = obj;
        }

        /// <summary>Clone this context into a fresh instance.</summary>
        public ToolContext Clone()
        {
            var newJsonData = JsonData.Clone();
            var newObjectReferences = new Dictionary<string, object>(ObjectReferences);
            return new ToolContext(newJsonData, newObjectReferences);
        }

        /// <summary>
        /// Indexer over JsonData then ObjectReferences. Assignments route
        /// serializable values to JsonData and Unity objects to ObjectReferences.
        /// </summary>
        public object this[string key]
        {
            get
            {
                if (JsonData.TryGetValue(key, out JsonNode token))
                {
                    switch (token.type)
                    {
                        case JsonNodeType.String: return token.Value;
                        case JsonNodeType.Integer: return token.AsInt;
                        case JsonNodeType.Float: return token.AsFloat;
                        case JsonNodeType.Boolean: return token.AsBool;
                        case JsonNodeType.Null: return null;
                        default: return token;
                    }
                }

                if (ObjectReferences.TryGetValue(key, out object obj))
                {
                    return obj;
                }

                return null;
            }
            set
            {
                if (value == null)
                {
                    JsonData[key] = new JsonData("null");
                    ObjectReferences.Remove(key);
                }
                else if (IsSerializableType(value))
                {
                    JsonData[key] = Json.FromObject(value);
                    ObjectReferences.Remove(key);
                }
                else
                {
                    ObjectReferences[key] = value;
                    JsonData.Remove(key);
                }
            }
        }

        /// <summary>Check whether a key exists in JsonData or ObjectReferences.</summary>
        public bool ContainsKey(string key)
        {
            return JsonData.ContainsKey(key) || ObjectReferences.ContainsKey(key);
        }

        /// <summary>Try to read a value, JsonData first then ObjectReferences.</summary>
        public bool TryGetValue(string key, out object value)
        {
            if (JsonData.TryGetValue(key, out JsonNode token))
            {
                switch (token.type)
                {
                    case JsonNodeType.String: value = token.Value; return true;
                    case JsonNodeType.Integer: value = token.AsInt; return true;
                    case JsonNodeType.Float: value = token.AsFloat; return true;
                    case JsonNodeType.Boolean: value = token.AsBool; return true;
                    case JsonNodeType.Null: value = null; return true;
                    default: value = token; return true;
                }
            }

            return ObjectReferences.TryGetValue(key, out value);
        }

        /// <summary>Try to read a typed value.</summary>
        public bool TryGetValue<T>(string key, out T value)
        {
            if (TryGetValue(key, out object obj))
            {
                if (obj is T typedValue)
                {
                    value = typedValue;
                    return true;
                }

                try
                {
                    value = (T)Convert.ChangeType(obj, typeof(T));
                    return true;
                }
                catch
                {
                    // fall through
                }
            }

            value = default(T);
            return false;
        }

        /// <summary>Remove a key from both JsonData and ObjectReferences.</summary>
        public bool Remove(string key)
        {
            bool removedFromJson = JsonData.Remove(key) != null;
            bool removedFromObjects = ObjectReferences.Remove(key);
            return removedFromJson || removedFromObjects;
        }

        private static bool IsSerializableType(object value)
        {
            if (value == null) return true;

            var type = value.GetType();

            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return false;

            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return true;

            if (type.IsEnum)
                return true;

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return true;

            if (type == typeof(Guid))
                return true;

            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                return elementType != null && IsSerializableType(Activator.CreateInstance(elementType));
            }

            try
            {
                Json.FromObject(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Run a coroutine and complete this context with its result.
        /// A timeout wrapper aborts the wait if the coroutine takes too long.
        /// </summary>
        public ToolContext AsyncReturn(IEnumerator coroutine, float timeoutSeconds = 60f)
        {
            if (coroutine == null)
                return this;

            IEnumerator timeoutCoroutine = AddTimeoutToCoroutine(coroutine, timeoutSeconds);

            CoroutineRunner.StartCoroutine(timeoutCoroutine, (result) =>
            {
                McpLogger.Log($"AsyncReturn: {result}");
                CompleteAction?.Invoke(Json.FromObject(result));
            });

            return this;
        }

        /// <summary>
        /// Wrap a coroutine so it reports a timeout error instead of hanging forever.
        /// </summary>
        private static IEnumerator AddTimeoutToCoroutine(IEnumerator coroutine, float timeoutSeconds)
        {
            double startTime = UnityEditor.EditorApplication.timeSinceStartup;
            bool completed = false;
            object result = null;

            IEnumerator RunOriginalCoroutine()
            {
                yield return coroutine;
                completed = true;
                result = coroutine.Current;
            }

            yield return RunOriginalCoroutine();

            while (!completed)
            {
                double elapsed = UnityEditor.EditorApplication.timeSinceStartup - startTime;
                if (elapsed >= timeoutSeconds)
                {
                    UnityEngine.Debug.LogWarning($"Coroutine timed out after {elapsed:F1}s");
                    yield return Response.Error($"Operation timed out after {elapsed:F1}s; retry or check the connection.", null);
                    yield break;
                }

                yield return new UnityEngine.WaitForSeconds(0.5f);
            }

            yield return result;
        }
    }
}
