using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace PrinterAgent.WebUI.Hubs
{
    public class PrintHub : Hub
    {
        // Called by agents on connect, passing their AgentId
        public Task RegisterAgent(string agentId)
        {
            // Map this ConnectionId → agentId
            AgentConnectionMap.Register(agentId, Context.ConnectionId);
            return Task.CompletedTask;
        }
    }
}
