using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>
/// 从某条消息分叉与回退通知（docs/design/message-rewind.md §5）：消息下标、带截止点的 session/fork、
/// 截止点校验、ForkedFrom，以及 workspace/revertFile 带会话 id 时下一批输入前追加的系统消息。
/// 与 <see cref="RpcEndToEndTests"/> 一样走真实的 Host 进程（TKCODE_TEST_HOST_EXE 指向 AOT 版本时验证 AOT 序列化）。
/// </summary>
public sealed class RpcForkTests
{
    [Fact]
    public async Task ForkFromMessage_CopiesHistoryBeforeTheCut()
    {
        await using var host = await HostFixture.StartAsync();
        var server = host.Server;
        var models = await server.ListModelsAsync();
        var cwd = Directory.CreateTempSubdirectory("tkcode-fork-").FullName;
        var session = await server.CreateSessionAsync(new CreateSessionRequest(cwd, models[0].Ref));

        var first = await host.RunAsync(session.SessionId, "第一个问题");
        var second = await host.RunAsync(session.SessionId, "第二个问题");
        var firstIndex = Assert.Single(first.OfType<MessageStarted>(), m => m.Role == "user").Index;
        var secondIndex = Assert.Single(second.OfType<MessageStarted>(), m => m.Role == "user").Index;
        Assert.NotNull(firstIndex);
        Assert.NotNull(secondIndex);
        Assert.True(secondIndex > firstIndex);

        // 历史里的下标与实时事件一致
        var opened = await server.OpenSessionAsync(session.FilePath);
        var users = opened.History.Where(m => m.Role == "user").ToArray();
        Assert.Equal([firstIndex!.Value, secondIndex!.Value], users.Select(u => u.Index));

        // 截止到第二个问题之前：新会话只有第一轮，记录分叉来源
        var fork = await server.ForkSessionAsync(session.FilePath, secondIndex);
        Assert.NotEqual(session.SessionId, fork.Session.SessionId);
        Assert.Contains(fork.History, m => m.Role == "user" && Text(m) == "第一个问题");
        Assert.DoesNotContain(fork.History, m => m.Role == "user" && Text(m) == "第二个问题");
        Assert.Equal("assistant", fork.History[^1].Role);
        Assert.Equal(session.SessionId, fork.Session.ForkedFrom?.SessionId);
        Assert.Equal("第一个问题", fork.Session.ForkedFrom?.Title);

        // 分叉出来的会话可以继续对话
        var continued = await host.RunAsync(fork.Session.SessionId, "换个问法");
        Assert.Contains(continued, e => e is TextDelta);

        // 截止在末尾 = 整条复制
        var whole = await server.ForkSessionAsync(session.FilePath, opened.History[^1].Index + 1);
        Assert.Contains(whole.History, m => m.Role == "user" && Text(m) == "第二个问题");

        // 截止点必须在用户输入的消息之前
        var assistantIndex = opened.History.First(m => m.Role == "assistant").Index;
        await Assert.ThrowsAsync<RpcRemoteException>(() => server.ForkSessionAsync(session.FilePath, assistantIndex));
        await Assert.ThrowsAsync<RpcRemoteException>(() => server.ForkSessionAsync(session.FilePath, 10_000));
    }

    [Fact]
    public async Task RevertWithSession_TellsTheAgentBeforeTheNextInput()
    {
        await using var host = await HostFixture.StartAsync();
        var server = host.Server;
        var models = await server.ListModelsAsync();
        var cwd = Directory.CreateTempSubdirectory("tkcode-revert-").FullName;
        var session = await server.CreateSessionAsync(new CreateSessionRequest(cwd, models[0].Ref));
        await host.RunAsync(session.SessionId, "你好");

        var file = Path.Combine(cwd, "a.txt");
        File.WriteAllText(file, "edited by the agent\n");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        var result = await server.RevertFileAsync(new RevertFileRequest(file, sha, "original\n", session.SessionId));
        Assert.Null(result.Error);
        Assert.Equal("original\n", File.ReadAllText(file));

        // 下一批输入前追加系统消息（界面历史不显示系统消息，看会话文件）
        Assert.DoesNotContain("reverted these files", File.ReadAllText(session.FilePath));
        await host.RunAsync(session.SessionId, "继续");
        var log = File.ReadAllText(session.FilePath);
        Assert.Contains("The user reverted these files", log);
        Assert.Contains(JsonEscaped(file), log);

        // 只通知一次
        await host.RunAsync(session.SessionId, "再继续");
        Assert.Equal(1, Count(File.ReadAllText(session.FilePath), "The user reverted these files"));
    }

    [Fact]
    public async Task Retry_AfterError_ContinuesWithoutNewInput()
    {
        await using var host = await HostFixture.StartAsync();
        var server = host.Server;
        var models = await server.ListModelsAsync();
        var cwd = Directory.CreateTempSubdirectory("tkcode-retry-").FullName;
        var session = await server.CreateSessionAsync(new CreateSessionRequest(cwd, models[0].Ref));

        // 第一次请求：先通知自动重试，再以 503 失败
        var failed = await host.RunAsync(session.SessionId, "fake:fail-once");
        var retrying = Assert.Single(failed.OfType<RequestRetrying>());
        Assert.Equal(1, retrying.Attempt);
        Assert.Contains("503", retrying.Reason);
        Assert.Contains(failed, e => e is MessageEnded { Role: "assistant", StopReason: "Error" });

        // 重试：不追加新的用户消息，按现有历史再请求一次
        var started = await server.RetryAsync(session.SessionId);
        Assert.True(started.Started);
        var retried = await host.CollectAsync(session.SessionId);
        Assert.DoesNotContain(retried, e => e is MessageStarted { Role: "user" });
        Assert.Contains(retried, e => e is MessageEnded { Role: "assistant", StopReason: "Stop" });

        var history = (await server.OpenSessionAsync(session.FilePath)).History;
        Assert.Single(history, m => m.Role == "user");
        Assert.Equal(["Error", "Stop"], history.Where(m => m.Role == "assistant").Select(m => m.StopReason));

        // 最后一条不是出错的回复：不能重试
        Assert.False((await server.RetryAsync(session.SessionId)).Started);
    }

    private static string Text(HistoryMessageDto message) => string.Concat(message.Blocks.OfType<HistoryText>().Select(t => t.Text));

    private static string JsonEscaped(string value) => System.Text.Json.JsonSerializer.Serialize(value)[1..^1];

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>真实的 Host 进程（Fake 厂商、隔离的数据目录）。</summary>
    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly JsonRpcConnection _rpc;
        private readonly RpcEndToEndTests.RecordingClient _client;

        private HostFixture(Process process, JsonRpcConnection rpc, RpcEndToEndTests.RecordingClient client)
        {
            _process = process;
            _rpc = rpc;
            _client = client;
            Server = new AgentHostServerProxy(rpc);
        }

        public AgentHostServerProxy Server { get; }

        public static async Task<HostFixture> StartAsync()
        {
            var aotHost = Environment.GetEnvironmentVariable("TKCODE_TEST_HOST_EXE");
            var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0/TKCode.AgentHost.dll"));
            var start = aotHost is { Length: > 0 } ? new ProcessStartInfo(aotHost) : new ProcessStartInfo("dotnet", $"\"{hostDll}\"");
            var process = Process.Start(new ProcessStartInfo(start.FileName, start.Arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment =
                {
                    ["TKCODE_HOME"] = Directory.CreateTempSubdirectory("tkcode-rpc-home-").FullName,
                    ["TKCODE_FAKE_PROVIDER"] = "1",
                    ["TKCODE_EXTERNAL_RUNTIMES"] = "0",
                },
            })!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var client = new RpcEndToEndTests.RecordingClient();
            var rpc = RpcSerialization.CreateConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
            AgentHostClientDispatcher.Register(rpc, client);
            var fixture = new HostFixture(process, rpc, client);
            rpc.Start();
            await fixture.Server.InitializeAsync(new InitializeRequest(RpcProtocol.Version, "test"));
            return fixture;
        }

        /// <summary>发一条消息并收集这一轮的事件（到 AgentEnded 为止）。</summary>
        public async Task<List<SessionEvent>> RunAsync(string sessionId, string text)
        {
            var result = await Server.PromptAsync(new PromptRequest(sessionId, text));
            Assert.True(result.Started);
            return await CollectAsync(sessionId);
        }

        /// <summary>收集会话接下来一轮的事件（到 AgentEnded 为止）。</summary>
        public async Task<List<SessionEvent>> CollectAsync(string sessionId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var seen = new List<SessionEvent>();
            await foreach (var n in _client.Events.Reader.ReadAllAsync(timeout.Token))
            {
                if (n.SessionId != sessionId)
                {
                    continue;
                }
                seen.Add(n.Event);
                if (n.Event is AgentEnded)
                {
                    break;
                }
            }
            return seen;
        }

        public async ValueTask DisposeAsync()
        {
            await _rpc.DisposeAsync();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }
    }
}
