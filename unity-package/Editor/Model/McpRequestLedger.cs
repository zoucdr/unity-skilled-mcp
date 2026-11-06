using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UniMcp.Models
{
    /// <summary>Instance-local, bounded execution records; unknown never proves non-execution.</summary>
    public sealed class McpRequestLedger
    {
        public const int MaxEntries = 100;
        public const int MaxResultBytes = 2 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Func<DateTime> clock;
        private readonly int capacity;
        private readonly int resultBudget;
        private readonly TimeSpan ttl;
        private int resultBytes;
        public string InstanceId { get; }

        private sealed class Entry
        {
            public string Fingerprint;
            public string Status = "accepted";
            public string Result;
            public int ResultBytes;
            public DateTime Finished;
            public bool Terminal => Status == "completed" || Status == "failed";
        }

        /// <summary>Creates a ledger with injectable limits and clock for deterministic tests.</summary>
        public McpRequestLedger(string instanceId, int capacity = MaxEntries,
            int resultBudget = MaxResultBytes, Func<DateTime> clock = null, TimeSpan? ttl = null)
        {
            InstanceId = instanceId;
            this.capacity = capacity;
            this.resultBudget = resultBudget;
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.ttl = ttl ?? TimeSpan.FromMinutes(30);
        }

        /// <summary>Atomically accepts an ID before enqueueing; active records are never evicted.</summary>
        public string Accept(string id, string fingerprint)
        {
            lock (gate)
            {
                Prune();
                if (entries.TryGetValue(id, out var existing))
                    return existing.Fingerprint == fingerprint ? "duplicate" : "conflict";
                while (entries.Count >= capacity)
                {
                    if (!EvictOldest()) return "full";
                }
                entries.Add(id, new Entry { Fingerprint = fingerprint });
                return "accepted";
            }
        }

        /// <summary>Marks execution only when the queued action actually starts.</summary>
        public void MarkRunning(string id)
        {
            lock (gate)
            {
                if (entries.TryGetValue(id, out var entry) && entry.Status == "accepted") entry.Status = "running";
            }
        }

        /// <summary>Closes records that were accepted but rejected before execution started.</summary>
        public void Abandon(string id, string result)
        {
            lock (gate)
            {
                if (!entries.TryGetValue(id, out var entry) || entry.Status != "accepted") return;
                Finish(entry, result, true);
            }
        }

        /// <summary>Records late completion; failed does not imply absence of side effects.</summary>
        public void Complete(string id, string result, bool failed)
        {
            lock (gate)
            {
                if (!entries.TryGetValue(id, out var entry) || entry.Terminal) return;
                Finish(entry, result, failed);
            }
        }

        /// <summary>Stores the outcome, giving way to newer results when the byte budget is tight.</summary>
        private void Finish(Entry entry, string result, bool failed)
        {
            int bytes = Encoding.UTF8.GetByteCount(result ?? "");
            if (bytes > resultBudget || !MakeRoom(bytes)) bytes = 0;
            if (bytes > 0)
            {
                entry.Result = result;
                entry.ResultBytes = bytes;
                resultBytes += bytes;
            }
            entry.Status = failed ? "failed" : "completed";
            entry.Finished = clock();
        }

        /// <summary>Queries without Unity main-thread access, including foreign-instance unknown.</summary>
        public JsonClass Query(string id, string instanceId)
        {
            lock (gate)
            {
                Prune();
                var result = new JsonClass
                {
                    { "request_id", new JsonData(id) },
                    { "instance_id", new JsonData(instanceId ?? "") },
                    { "status", new JsonData("unknown") }
                };
                if (instanceId == InstanceId && entries.TryGetValue(id, out var entry))
                {
                    result["status"] = new JsonData(entry.Status);
                    if (entry.Result != null) result.Add("result", Json.Parse(entry.Result));
                }
                return result;
            }
        }

        /// <summary>Drops the oldest stored results until the requested size fits.</summary>
        private bool MakeRoom(int bytes)
        {
            var stored = entries.Where(pair => pair.Value.Result != null)
                .OrderBy(pair => pair.Value.Finished).ToArray();
            foreach (var pair in stored)
            {
                if (resultBytes + bytes <= resultBudget) return true;
                resultBytes -= pair.Value.ResultBytes;
                pair.Value.Result = null;
                pair.Value.ResultBytes = 0;
            }
            return resultBytes + bytes <= resultBudget;
        }

        /// <summary>Expires only terminal records.</summary>
        private void Prune()
        {
            var expired = entries.Where(pair => pair.Value.Terminal && clock() - pair.Value.Finished >= ttl)
                .Select(pair => pair.Key).ToArray();
            foreach (string id in expired) Remove(id);
        }

        /// <summary>Evicts the oldest terminal record, never accepted or running work.</summary>
        private bool EvictOldest()
        {
            var oldest = entries.Where(pair => pair.Value.Terminal)
                .OrderBy(pair => pair.Value.Finished).FirstOrDefault();
            if (oldest.Key == null) return false;
            Remove(oldest.Key);
            return true;
        }

        /// <summary>Removes a record and accounts for its stored result.</summary>
        private void Remove(string id)
        {
            resultBytes -= entries[id].ResultBytes;
            entries.Remove(id);
        }
    }
}
