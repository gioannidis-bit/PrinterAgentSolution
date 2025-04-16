using System.Collections.Concurrent;

namespace PrinterAgent.WebUI.Hubs
{
    public static class AgentConnectionMap
    {
        // Now case‑insensitive keys
        private static readonly ConcurrentDictionary<string, string> _map
            = new(StringComparer.OrdinalIgnoreCase);

        public static void Register(string agentId, string connectionId)
            => _map[agentId] = connectionId;
        public static bool TryGetConnection(string agentId, out string connectionId)
            => _map.TryGetValue(agentId, out connectionId);

        public static bool TryGet(string agentId, out string connectionId)
          => _map.TryGetValue(agentId, out connectionId);

        // (Optional) for debugging:
        public static IEnumerable<string> GetAll() => _map.Keys;
    }
}
