using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;

namespace UniMcp.Models
{
    /// <summary>
    /// Durable record of every tool call, so a conclusion — or the fact that a
    /// task was still running — outlives a domain reload or a full Unity restart.
    ///
    /// Backed by ProjectSettings/UnitySkilledMcpTasks.asset (a local
    /// ScriptableSingleton): project-scoped, survives a Library rebuild, and
    /// never adds imported assets to the user's Assets folder. Everything is
    /// stored as plain strings because JsonNode is not Unity-serializable and
    /// string fields survive the YAML round-trip on every Unity version.
    ///
    /// Writes belong to the main thread (ScriptableSingleton.Save touches the
    /// asset database); background writers are deferred to the editor loop.
    /// </summary>
    [FilePath(AssetPath, FilePathAttribute.Location.ProjectFolder)]
    public sealed class ToolTaskStore : ScriptableSingleton<ToolTaskStore>
    {
        public const string AssetPath = "ProjectSettings/UnitySkilledMcpTasks.asset";
        public const int MaxEntries = 100;
        public const int MaxResultChars = 512 * 1024;
        public const int MaxArgsChars = 8 * 1024;
        public const int MaxTotalResultChars = 4 * 1024 * 1024;

        /// <summary>A single executed tool call. Status is running | completed | failed | interrupted.</summary>
        [Serializable]
        public sealed class Entry
        {
            public string id;
            public string tool;
            public string args;
            public string status;
            public string resultJson;
            public string error;
            public string instanceId;   // editor session that produced this record (survives reload)
            public string domainId;     // AppDomain that produced this record (dies on reload)
            public string startedAt;
            public string completedAt;
        }

        public List<Entry> entries = new List<Entry>();

        public static ToolTaskStore Instance => instance;

        private static int mainThreadId = -1;
        private static bool orphansChecked;

        // Reads are lock-guarded so a background poller still sees the record
        // while the main thread is inside a long tool; only asset writes need
        // the main thread.
        private static readonly object gate = new object();

        private List<Entry> Entries => entries ?? (entries = new List<Entry>());

        private void OnEnable()
        {
            // OnEnable always runs on the main thread; it also runs again after a
            // domain reload, which is what re-arms the orphan sweep below.
            if (mainThreadId == -1) mainThreadId = Thread.CurrentThread.ManagedThreadId;
            orphansChecked = false;
        }

        /// <summary>
        /// A task still marked "running" after a Unity reload can never complete;
        /// surface it as interrupted instead of letting callers poll forever.
        /// Ownership is judged by AppDomain id, not by editor-session id: a hot
        /// reload keeps the session id (so clients stay bound) but does kill the
        /// running call, and that call must be closed as interrupted.
        /// </summary>
        public void EnsureOrphansMarked()
        {
            if (orphansChecked) return;
            orphansChecked = true;
            string currentDomain = McpConnectionInfo.DomainId;
            string currentInstance = McpConnectionInfo.InstanceId;
            bool changed = false;
            lock (gate)
            {
                foreach (var entry in Entries)
                {
                    if (entry == null || entry.status != "running") continue;
                    // Records written before domainId existed fall back to the session id.
                    string owner = string.IsNullOrEmpty(entry.domainId) ? entry.instanceId : entry.domainId;
                    string current = string.IsNullOrEmpty(entry.domainId) ? currentInstance : currentDomain;
                    if (!string.IsNullOrEmpty(owner) && owner == current) continue;
                    entry.status = "interrupted";
                    entry.error = "Unity reloaded or restarted before this task completed.";
                    entry.completedAt = Now();
                    changed = true;
                }
            }
            if (changed) PersistentSave();
        }

        public Entry Get(string id)
        {
            lock (gate) return Entries.FirstOrDefault(e => e != null && e.id == id);
        }

        public IReadOnlyList<Entry> All()
        {
            lock (gate) return Entries.Where(e => e != null).ToList();
        }

        /// <summary>Records that a call was accepted and is now executing.</summary>
        public void MarkRunning(string id, string tool, string args, string instanceId)
        {
            Mutate(() =>
            {
                var entry = Get(id);
                if (entry == null)
                {
                    entry = new Entry { id = id };
                    Entries.Add(entry);
                }
                entry.tool = tool ?? string.Empty;
                entry.args = Truncate(args, MaxArgsChars);
                entry.status = "running";
                entry.resultJson = string.Empty;
                entry.error = string.Empty;
                entry.instanceId = instanceId ?? string.Empty;
                entry.domainId = McpConnectionInfo.DomainId;
                entry.startedAt = Now();
                entry.completedAt = string.Empty;
            });
        }

        /// <summary>Stores the conclusion of an accepted call.</summary>
        public void Complete(string id, string resultJson, string error, bool failed)
        {
            Mutate(() =>
            {
                var entry = Get(id);
                if (entry == null) return; // removed while the task was running
                string conclusion = Truncate(resultJson, MaxResultChars);
                if (conclusion.Length < (resultJson ?? string.Empty).Length)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Conclusion truncated: result exceeded the stored size limit."
                        : error + " (conclusion truncated)";
                }
                entry.status = failed ? "failed" : "completed";
                entry.resultJson = conclusion;
                entry.error = error ?? string.Empty;
                entry.completedAt = Now();
            });
        }

        /// <summary>Closes an accepted call that was rejected before or during execution.</summary>
        public void Interrupt(string id, string reason)
        {
            Mutate(() =>
            {
                var entry = Get(id);
                if (entry == null || entry.status != "running") return;
                entry.status = "interrupted";
                entry.error = reason ?? string.Empty;
                entry.completedAt = Now();
            });
        }

        /// <summary>
        /// Read-only view of one record. Returns null when the id was never
        /// recorded, so callers can tell "unknown" apart from "still running".
        /// Safe to call from a background thread while a tool is executing.
        /// </summary>
        public JsonClass Query(string id)
        {
            Entry entry;
            lock (gate) entry = Get(id);
            if (entry == null) return null;

            string currentInstance = McpConnectionInfo.InstanceId;
            string currentDomain = McpConnectionInfo.DomainId;
            string entryOwner = string.IsNullOrEmpty(entry.domainId) ? entry.instanceId : entry.domainId;
            string ownerNow = string.IsNullOrEmpty(entry.domainId) ? currentInstance : currentDomain;
            bool previousSession = !string.IsNullOrEmpty(entryOwner) && entryOwner != ownerNow;

            var payload = new JsonClass
            {
                { "request_id", new JsonData(entry.id ?? string.Empty) },
                { "status", new JsonData(entry.status ?? string.Empty) },
                { "tool", new JsonData(entry.tool ?? string.Empty) },
                { "persisted", new JsonData(true) },
                { "instance_id", new JsonData(currentInstance) },
                { "recorded_by_instance", new JsonData(entry.instanceId ?? string.Empty) },
                { "from_previous_session", new JsonData(previousSession) },
                { "started_at", new JsonData(entry.startedAt ?? string.Empty) },
                { "completed_at", new JsonData(entry.completedAt ?? string.Empty) }
            };

            if (!string.IsNullOrEmpty(entry.error)) payload.Add("error", new JsonData(entry.error));

            if (entry.status == "running")
            {
                payload.Add("message", new JsonData(
                    "Task is still in progress; poll again. If an earlier session owned it, it was interrupted by a reload."));
            }
            else if (entry.status == "interrupted")
            {
                payload.Add("message", new JsonData(
                    "Task did not complete: Unity reloaded or restarted. Re-run it only after checking for side effects."));
            }
            else if (!string.IsNullOrEmpty(entry.resultJson))
            {
                try { payload.Add("result", Json.Parse(entry.resultJson)); }
                catch { payload.Add("result", new JsonData(entry.resultJson)); }
            }
            else
            {
                payload.Add("message", new JsonData("Task completed without a return value."));
            }

            return payload;
        }

        /// <summary>Applies a mutation on the main thread, trims, and rewrites the asset.</summary>
        private void Mutate(Action mutate)
        {
            if (!OnMainThread())
            {
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        Mutate(mutate);
                    }
                    catch (Exception e)
                    {
                        McpLogger.LogError("[ToolTaskStore] deferred write failed: " + e.Message);
                    }
                };
                return;
            }

            lock (gate)
            {
                mutate?.Invoke();
                Trim();
            }
            PersistentSave();
        }

        private static bool OnMainThread()
        {
            return mainThreadId == -1 || Thread.CurrentThread.ManagedThreadId == mainThreadId;
        }

        private void PersistentSave()
        {
            try
            {
                EditorUtility.SetDirty(this);
                Save(true);
            }
            catch (Exception e)
            {
                McpLogger.LogError("[ToolTaskStore] failed to save " + AssetPath + ": " + e.Message);
            }
        }

        /// <summary>Enforces the entry cap, then the total conclusion budget (oldest first).</summary>
        private void Trim()
        {
            if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);

            int total = Entries.Sum(e => e?.resultJson?.Length ?? 0);
            if (total <= MaxTotalResultChars) return;
            foreach (var entry in Entries.ToArray())
            {
                if (total <= MaxTotalResultChars) break;
                if (entry?.resultJson == null || entry.resultJson.Length == 0) continue;
                total -= entry.resultJson.Length;
                entry.resultJson = string.Empty;
                entry.error = string.IsNullOrEmpty(entry.error)
                    ? "Conclusion evicted: the task store hit its size budget; only the status remains."
                    : entry.error;
            }
        }

        private static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= limit ? value : value.Substring(0, limit);
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
