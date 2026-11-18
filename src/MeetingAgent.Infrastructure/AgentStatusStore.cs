using MeetingAgent.Core;

namespace MeetingAgent.Infrastructure;

public sealed class AgentStatusStore : IAgentStatusStore
{
    private readonly object _sync = new();
    private AgentStatusSnapshot _current = new();

    public event EventHandler<AgentStatusSnapshot>? StatusChanged;

    public AgentStatusSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Update(Func<AgentStatusSnapshot, AgentStatusSnapshot> mutator)
    {
        AgentStatusSnapshot updated;
        lock (_sync)
        {
            updated = mutator(_current);
            _current = updated;
        }

        StatusChanged?.Invoke(this, updated);
    }
}
