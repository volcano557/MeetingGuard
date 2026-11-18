namespace MeetingAgent.Core;

public interface IAgentStatusStore
{
    event EventHandler<AgentStatusSnapshot>? StatusChanged;
    AgentStatusSnapshot Current { get; }
    void Update(Func<AgentStatusSnapshot, AgentStatusSnapshot> mutator);
}
