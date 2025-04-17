using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PrinterAgent.WebUI.Hubs
{
    public static class AgentConnectionMap
    {
        // maps GUID → (connectionId, machineName, location)
        private static readonly ConcurrentDictionary<string, AgentConnectionInfo> _map
          = new ConcurrentDictionary<string, AgentConnectionInfo>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string agentId, string connId, string machineName)
        {
            if (_map.TryGetValue(agentId, out var existingInfo))
            {
                // Διατηρούμε την υπάρχουσα τοποθεσία αν υπάρχει
                _map[agentId] = new AgentConnectionInfo
                {
                    ConnectionId = connId,
                    MachineName = machineName,
                    Location = existingInfo.Location
                };
            }
            else
            {
                // Δημιουργούμε νέα εγγραφή
                _map[agentId] = new AgentConnectionInfo
                {
                    ConnectionId = connId,
                    MachineName = machineName,
                    Location = ""
                };
            }
        }

        // Ενημέρωση τοποθεσίας
        public static void UpdateLocation(string agentId, string location)
        {
            if (_map.TryGetValue(agentId, out var info))
            {
                info.Location = location;
                _map[agentId] = info;
            }
        }

        // Get location
        public static bool TryGetLocation(string agentId, out string location)
        {
            if (_map.TryGetValue(agentId, out var info))
            {
                location = info.Location;
                return true;
            }
            location = null;
            return false;
        }

        // Set location
        public static void SetLocation(string agentId, string location)
        {
            if (_map.TryGetValue(agentId, out var info))
            {
                info.Location = location;
                _map[agentId] = info;
            }
        }

        // Existing TryGet (for printing)
        public static bool TryGetConnection(string agentId, out string connectionId)
        {
            if (_map.TryGetValue(agentId, out var info))
            {
                connectionId = info.ConnectionId;
                return true;
            }
            connectionId = null;
            return false;
        }

        // Existing TryGet (for printing)
        public static bool TryGet(string agentId, out string connectionId)
        {
            if (_map.TryGetValue(agentId, out var info))
            {
                connectionId = info.ConnectionId;
                return true;
            }
            connectionId = null;
            return false;
        }

        // Get the machine name
        public static bool TryGetMachine(string agentId, out string machineName)
        {
            if (_map.TryGetValue(agentId, out var info))
            {
                machineName = info.MachineName;
                return true;
            }
            machineName = null;
            return false;
        }

        // list all agents + machines + locations
        public static IEnumerable<AgentConnectionInfo> ListAgents()
        {
            return _map.Values;
        }

        // For debugging
        public static IEnumerable<string> GetAll() => _map.Keys;
    }

    public class AgentConnectionInfo
    {
        public string ConnectionId { get; set; }
        public string MachineName { get; set; }
        public string Location { get; set; }
    }
}