using TKCode.Runtimes.Claude;

namespace TKCode.Runtimes.Tests.Fixtures;

/// <summary>用脚本化的对端代替 claude 进程的运行时（附录 A 夹具与子 Agent 测试共用）。</summary>
internal sealed class ClaudeFixtureRuntime(ScriptedPeer peer, Queue<string> uuids, string sessionId) : IAgentRuntime
{
    public string Id => ClaudeRuntime.RuntimeId;
    public string DisplayName => "Claude Agent";
    public RuntimeDescriptor Descriptor { get; } = new(ClaudeRuntime.RuntimeId, "Claude Agent", RuntimeStatus.Ready)
    {
        Capabilities = new() { SupportsSteering = true },
    };
    public event Action? DescriptorChanged { add { } remove { } }
    public Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Descriptor);

    public async Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
    {
        var session = new ClaudeSession(peer.ClientInput, peer.ClientOutput, request.Host, sessionId, newUuid: () => uuids.Dequeue());
        session.SetInitialOptions(request.Options.Model.Id, null, "default");
        await session.StartAsync(cancellationToken);
        return session;
    }
}
