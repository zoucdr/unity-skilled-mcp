using System;
using System.IO;
using UniMcp.Models;
using UnityEditor;

namespace UniMcp
{
    /// <summary>Dependency-free protocol safety self-tests; do not execute tools or mutate project assets.</summary>
    public static class McpProtocolSafetyTests
    {
        /// <summary>Runs deterministic ledger, binding and fingerprint tests.</summary>
        [MenuItem("Tools/Unity MCP/Run Protocol Safety Self Tests")]
        public static void RunAll()
        {
            var now = DateTime.UtcNow;
            var ledger = new McpRequestLedger("one", 2, 16, () => now);
            Check(ledger.Accept("a", "payload") == "accepted", "accept before queue");
            Check(ledger.Accept("a", "payload") == "duplicate", "same payload is not replayed");
            Check(ledger.Accept("a", "other") == "conflict", "changed payload conflicts");
            ledger.MarkRunning("a");
            Check(ledger.Query("a", "one")["status"].Value == "running", "running state");
            Check(ledger.Accept("b", "payload") == "accepted", "second acceptance");
            now += TimeSpan.FromHours(1);
            Check(ledger.Accept("c", "payload") == "full", "TTL never removes active work");
            Check(ledger.Query("a", "two")["status"].Value == "unknown", "foreign instance");
            ledger.Complete("a", "{\"ok\":true}", false);
            Check(ledger.Query("a", "one")["status"].Value == "completed", "late completion");
            Check(ledger.Query("a", "one")["result"]["ok"].Value == "true", "saved result");
            Check(ledger.Accept("c", "payload") == "accepted", "evict only terminal work");
            Check(ledger.Query("a", "one")["status"].Value == "unknown", "evicted is unknown");
            ledger.Complete("b", "{\"tooLargeForBudget\":true}", true);
            Check(ledger.Query("b", "one")["status"].Value == "failed", "oversized failure retains status");
            Check(!ledger.Query("b", "one").ContainsKey("result"), "oversized result omitted");
            now += TimeSpan.FromMinutes(30);
            Check(ledger.Query("b", "one")["status"].Value == "unknown", "terminal TTL");
            Check(new McpRequestLedger("two").Query("c", "one")["status"].Value == "unknown", "restart unknown");

            var first = Json.Parse("{\"name\":\"test\",\"arguments\":{\"b\":2,\"a\":1}}");
            var second = Json.Parse("{\"arguments\":{\"a\":1,\"b\":2},\"name\":\"test\"}");
            Check(McpService.RequestFingerprint(first) == McpService.RequestFingerprint(second), "key order ignored");
            Check(McpService.RequestFingerprint(Json.Parse("[1,2]"))
                != McpService.RequestFingerprint(Json.Parse("[2,1]")), "array order preserved");
            string project = Path.GetFullPath(".");
            Check(McpService.NormalizeProjectPath(Path.Combine(project, "folder", ".."))
                == McpService.NormalizeProjectPath(project), "dot segments normalized");
            TestBindings(project);
            UnityEngine.Debug.Log("[UniMcp] 协议安全自检通过");
        }

        /// <summary>Exercises binding decisions without starting a listener or constructing Unity services.</summary>
        private static void TestBindings(string project)
        {
            string bound = McpService.NormalizeProjectPath(project);
            Func<string, string, bool, string> check = (path, instance, required) =>
                McpService.ValidateBindingFor(bound, path, instance, required);
            Check(check(null, null, false) == null, "unbound metadata allowed");
            Check(check(null, null, true) != null, "unbound execution rejected");
            Check(check(Uri.EscapeDataString(project), McpConnectionInfo.InstanceId, true) == null, "valid binding");
            Check(check(Uri.EscapeDataString(project), "stale", true) != null, "stale instance rejected");
            Check(check(Uri.EscapeDataString(Path.Combine(project, "other")),
                McpConnectionInfo.InstanceId, true) != null, "wrong project rejected");
            bool ignoresCase = Path.DirectorySeparatorChar == '\\';
            Check((check(Uri.EscapeDataString(project.ToUpperInvariant()), McpConnectionInfo.InstanceId, true) == null)
                == (ignoresCase || project == project.ToUpperInvariant()), "host path case rules");
        }

        /// <summary>Fails immediately with the violated contract.</summary>
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Protocol safety self-test failed: " + message);
        }
    }
}
