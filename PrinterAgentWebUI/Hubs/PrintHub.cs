using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using PrinterAgent.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PrinterAgent.WebUI.Hubs
{
    public class PrintHub : Hub
    {
        // Dictionary για αποθήκευση πλήρων στοιχείων των agents
        private static readonly ConcurrentDictionary<string, AgentState> _agents =
            new ConcurrentDictionary<string, AgentState>();

        // Timeout για να θεωρηθεί ένας agent offline (σε δευτερόλεπτα)
        private const int AGENT_TIMEOUT_SECONDS = 60;

        // Called by agents on connect, passing their AgentId
        public Task RegisterAgent(string agentId, string machineName, string location)
        {
            // Κρατάμε πληροφορίες για την κατάσταση του agent
            var agentState = new AgentState
            {
                AgentId = agentId,
                MachineName = machineName,
                Location = location,
                ConnectionId = Context.ConnectionId,
                LastSeen = DateTime.UtcNow,
                IsOnline = true
            };

            _agents.AddOrUpdate(agentId, agentState, (key, oldValue) =>
            {
                // Διατηρούμε την υπάρχουσα τοποθεσία αν δεν παρέχεται νέα
                if (string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(oldValue.Location))
                {
                    agentState.Location = oldValue.Location;
                }
                return agentState;
            });

            // Καταγράφουμε τη σύνδεση στο mapping
            AgentConnectionMap.Register(agentId, Context.ConnectionId, machineName);

            // Ενημερώνουμε όλους τους clients για την αλλαγή
            Clients.All.SendAsync("AgentStatusChanged", GetAgentStatuses());

            return Task.CompletedTask;
        }

        // Νέα μέθοδος για αποσύνδεση agent
        public Task UnregisterAgent(string agentId)
        {
            if (_agents.TryGetValue(agentId, out var state))
            {
                state.IsOnline = false;
                state.LastSeen = DateTime.UtcNow;
                _agents[agentId] = state;
            }

            // Ενημερώνουμε όλους τους clients για την αλλαγή
            Clients.All.SendAsync("AgentStatusChanged", GetAgentStatuses());

            return Task.CompletedTask;
        }

        // Νέα μέθοδος για ενημέρωση της λίστας εκτυπωτών
        public Task UpdatePrinters(string agentId, List<PrinterInfo> printers)
        {
            if (_agents.TryGetValue(agentId, out var state))
            {
                state.Printers = printers;
                state.LastSeen = DateTime.UtcNow;
                state.IsOnline = true;
                _agents[agentId] = state;

                // Ενημερώνουμε όλους τους clients για τους νέους εκτυπωτές
                Clients.All.SendAsync("PrintersUpdated", GetAllPrinters());
            }

            return Task.CompletedTask;
        }

        // Νέα μέθοδος για ενημέρωση της τοποθεσίας
        public Task UpdateLocation(string agentId, string location)
        {
            if (_agents.TryGetValue(agentId, out var state))
            {
                state.Location = location;
                _agents[agentId] = state;

                // Ενημερώνουμε τον agent για τη νέα τοποθεσία
                if (!string.IsNullOrEmpty(state.ConnectionId))
                {
                    Clients.Client(state.ConnectionId).SendAsync("UpdateLocation", location);
                }

                // Ενημερώνουμε όλους τους clients για την αλλαγή
                Clients.All.SendAsync("AgentStatusChanged", GetAgentStatuses());
            }

            return Task.CompletedTask;
        }

        // Επιβεβαίωση αλλαγής τοποθεσίας από τον agent
        public Task LocationUpdated(string agentId, string location)
        {
            if (_agents.TryGetValue(agentId, out var state))
            {
                state.Location = location;
                _agents[agentId] = state;

                // Ενημερώνουμε όλους τους clients για την αλλαγή
                Clients.All.SendAsync("AgentStatusChanged", GetAgentStatuses());
            }

            return Task.CompletedTask;
        }

        // Heartbeat από agents για να μείνουν online
        public Task Heartbeat(string agentId)
        {
            if (_agents.TryGetValue(agentId, out var state))
            {
                state.LastSeen = DateTime.UtcNow;
                state.IsOnline = true;
                _agents[agentId] = state;

                // Ελέγχουμε για τυχόν agents που έχουν κάνει timeout
                CheckAgentTimeouts();

                // Ενημερώνουμε όλους τους clients για την κατάσταση των agents
                Clients.All.SendAsync("AgentStatusChanged", GetAgentStatuses());
            }

            return Task.CompletedTask;
        }

        // Χειρισμός αποσύνδεσης client
        public override Task OnDisconnectedAsync(Exception exception)
        {
            string connectionId = Context.ConnectionId;

            // Βρίσκουμε ποιος agent χρησιμοποιούσε αυτό το connection id
            foreach (var agentId in _agents.Keys)
            {
                if (_agents.TryGetValue(agentId, out var state) &&
                    state.ConnectionId == connectionId)
                {
                    state.IsOnline = false;
                    state.LastSeen = DateTime.UtcNow;
                    _agents[agentId] = state;

                    // Ενημερώνουμε όλους τους clients για την αλλαγή
                    Clients.All.SendAsync("AgentStatusChanged", GetAgentStatuses());
                    Clients.All.SendAsync("PrintersUpdated", GetAllPrinters());
                    break;
                }
            }

            return base.OnDisconnectedAsync(exception);
        }

        // Βοηθητική μέθοδος για έλεγχο timeout agents
        private void CheckAgentTimeouts()
        {
            var now = DateTime.UtcNow;
            var timeoutThreshold = now.AddSeconds(-AGENT_TIMEOUT_SECONDS);
            bool changed = false;

            foreach (var agentId in _agents.Keys)
            {
                if (_agents.TryGetValue(agentId, out var state) &&
                    state.IsOnline && state.LastSeen < timeoutThreshold)
                {
                    state.IsOnline = false;
                    _agents[agentId] = state;
                    changed = true;
                }
            }

            if (changed)
            {
                // Ενημερώνουμε όλους τους clients για την αλλαγή
                Clients.All.SendAsync("AgentStatusChanged", GetAgentStatuses());
                Clients.All.SendAsync("PrintersUpdated", GetAllPrinters());
            }
        }

        // Βοηθητική μέθοδος για τη δημιουργία λίστας με τις καταστάσεις των agents
        private List<AgentStatusDto> GetAgentStatuses()
        {
            CheckAgentTimeouts();

            return _agents.Values.Select(a => new AgentStatusDto
            {
                AgentId = a.AgentId,
                MachineName = a.MachineName,
                Location = a.Location,
                IsOnline = a.IsOnline,
                LastSeen = a.LastSeen
            }).ToList();
        }

        // Βοηθητική μέθοδος για τη δημιουργία πλήρους λίστας με τους εκτυπωτές
        private List<PrinterStatusDto> GetAllPrinters()
        {
            CheckAgentTimeouts();

            var result = new List<PrinterStatusDto>();

            foreach (var agent in _agents.Values)
            {
                if (agent.Printers == null) continue;

                foreach (var printer in agent.Printers)
                {
                    result.Add(new PrinterStatusDto
                    {
                        AgentId = agent.AgentId,
                        AgentName = agent.MachineName,
                        AgentLocation = agent.Location ?? "",
                        PrinterName = printer.Name,
                        PrinterStatus = printer.Status,
                        AgentIsOnline = agent.IsOnline,
                        DriverName = printer.DriverName,
                        IPAddress = printer.IPAddress,
                        ResponseTime = printer.ResponseTime
                    });
                }
            }

            return result;
        }
    }

    // Μοντέλα για την αποθήκευση και αποστολή δεδομένων

    public class AgentState
    {
        public string AgentId { get; set; }
        public string MachineName { get; set; }
        public string Location { get; set; }
        public string ConnectionId { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsOnline { get; set; }
        public List<PrinterInfo> Printers { get; set; }
    }

    public class AgentStatusDto
    {
        public string AgentId { get; set; }
        public string MachineName { get; set; }
        public string Location { get; set; }
        public bool IsOnline { get; set; }
        public DateTime LastSeen { get; set; }
    }

  public class PrinterStatusDto
{
    public string AgentId { get; set; }
    public string AgentName { get; set; }
    public string AgentLocation { get; set; }
    public string PrinterName { get; set; }
    public string PrinterStatus { get; set; }
    public bool AgentIsOnline { get; set; }
    public string DriverName { get; set; } = "Unknown";
    public string IPAddress { get; set; } = "Not Available";
    public int ResponseTime { get; set; } = -1;
}
}