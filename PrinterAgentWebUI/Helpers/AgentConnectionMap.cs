using System.Collections.Concurrent;

namespace PrinterAgent.WebUI.Hubs
{
    public static class AgentConnectionMap
    {
        // maps GUID → (connectionId, machineName)
        private static readonly ConcurrentDictionary<string, (string ConnId, string Machine)> _map
          = new(StringComparer.OrdinalIgnoreCase);

        public static void Register(string agentId, string connId, string machineName)
           => _map[agentId] = (connId, machineName);
       

        // Existing TryGet (for printing)
        public static bool TryGetConnection(string agentId, out string connectionId)
        {
            if (_map.TryGetValue(agentId, out var v))
            {
                connectionId = v.ConnId;
                return true;
            }
            connectionId = null;
            return false;
        }
   

        // Existing TryGet (for printing)
        public static bool TryGet(string agentId, out string connectionId)
        {
            if (_map.TryGetValue(agentId, out var v))
            {
                connectionId = v.ConnId;
                return true;
            }
            connectionId = null;
            return false;
        }

        // New: get the machine name
        public static bool TryGetMachine(string agentId, out string machineName)
        {
            if (_map.TryGetValue(agentId, out var v))
            {
                machineName = v.Machine;
                return true;
            }
            machineName = null;
            return false;
        }


        // New: list all agents + machines
        public static IEnumerable<(string AgentId, string MachineName)> ListAgents()
            => _map.Select(kvp => (kvp.Key, kvp.Value.Machine));

        // (Optional) for debugging:
        public static IEnumerable<string> GetAll() => _map.Keys;
    }
}
