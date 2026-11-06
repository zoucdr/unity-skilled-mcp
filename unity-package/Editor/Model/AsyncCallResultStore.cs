using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace UniMcp.Models
{
    /// <summary>
    /// Durable conclusions for async_call. Backed by a local .asset file under
    /// ProjectSettings, so a result written before a domain reload or a full Unity
    /// restart can still be fetched with the same call id afterwards.
    ///
    /// Everything is stored as plain strings: JsonNode is not Unity-serializable,
    /// and string fields survive the YAML round-trip on every Unity version.
    /// ProjectSettings is used instead of Library/ so conclusions also survive a
    /// Library rebuild, without adding imported assets to the user's Assets folder.
    /// </summary>
    [FilePath("ProjectSettings/UnitySkilledMcpAsyncResults.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class AsyncCallResultStore : ScriptableSingleton<AsyncCallResultStore>
    {
        public const int MaxEntries = 100;
        public const int MaxResultChars = 2 * 1024 * 1024;

        [Serializable]
        public sealed class Entry
        {
            public string id;
            public string func;
            public string args;
            public string status;       // running | completed | interrupted
            public string resultJson;   // the raw conclusion of the invoked tool
            public string error;
            public string startedAt;
            public string completedAt;
        }

        public List<Entry> entries = new List<Entry>();

        public static AsyncCallResultStore Instance => instance;

        // Domain reload resets statics, which is exactly when we want to re-check.
        private static bool orphansChecked;

        private List<Entry> Entries => entries ?? (entries = new List<Entry>());

        /// <summary>
        /// A task still marked "running" after Unity reload can never complete;
        /// surface it as interrupted instead of letting callers poll forever.
        /// </summary>
        public void EnsureOrphansMarked()
        {
            if (orphansChecked) return;
            orphansChecked = true;
            bool changed = false;
            foreach (var entry in Entries)
            {
                if (entry == null || entry.status != "running") continue;
                entry.status = "interrupted";
                entry.error = "Unity reloaded or restarted before this task completed.";
                entry.completedAt = Now();
                changed = true;
            }
            if (changed) Save(true);
        }

        public Entry Get(string id)
        {
            return Entries.FirstOrDefault(e => e != null && e.id == id);
        }

        public IReadOnlyList<Entry> All()
        {
            return Entries.Where(e => e != null).ToList();
        }

        public void MarkRunning(string id, string func, string args)
        {
            var entry = Get(id);
            if (entry == null)
            {
                entry = new Entry { id = id };
                Entries.Add(entry);
            }
            entry.func = func;
            entry.args = args ?? string.Empty;
            entry.status = "running";
            entry.resultJson = string.Empty;
            entry.error = string.Empty;
            entry.startedAt = Now();
            entry.completedAt = string.Empty;
            Trim();
            Save(true);
        }

        public void Complete(string id, string resultJson, string error)
        {
            var entry = Get(id);
            if (entry == null) return; // removed while the task was running
            string conclusion = resultJson ?? string.Empty;
            if (conclusion.Length > MaxResultChars)
            {
                conclusion = conclusion.Substring(0, MaxResultChars);
                error = string.IsNullOrEmpty(error)
                    ? "Conclusion truncated: result exceeded the asset size limit."
                    : error + " (conclusion truncated)";
            }
            entry.status = "completed";
            entry.resultJson = conclusion;
            entry.error = error ?? string.Empty;
            entry.completedAt = Now();
            Save(true);
        }

        public bool Remove(string id)
        {
            bool removed = Entries.RemoveAll(e => e != null && e.id == id) > 0;
            if (removed) Save(true);
            return removed;
        }

        public int Clear()
        {
            int count = Entries.Count;
            Entries.Clear();
            if (count > 0) Save(true);
            return count;
        }

        private void Trim()
        {
            if (Entries.Count <= MaxEntries) return;
            Entries.RemoveRange(0, Entries.Count - MaxEntries);
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
