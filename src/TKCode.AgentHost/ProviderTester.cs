using System.Diagnostics;
using TKCode.AgentHost.Catalog;
using TKCode.Ai;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;
using TKCode.Config;
using TKCode.Rpc;

namespace TKCode.AgentHost;

/// <summary>
/// 设置页的“测试连接”：可以使用尚未保存的表单值。
/// 先 GET /models（不消耗 tokens）；端点不支持（404/405 等非鉴权错误）时，再用第一个启用的模型发一次最小请求。
/// </summary>
public sealed class ProviderTester(ModelCatalog catalog, ModelDiscovery discovery, WireApiRegistry apis)
{
    public async Task<ProviderTestResult> TestAsync(ProviderTestRequest request, CancellationToken ct)
    {
        var state = catalog.Providers.FirstOrDefault(p => p.Id == request.ProviderId);
        var builtIn = catalog.BuiltIn.Find(request.ProviderId);
        var api = request.Api ?? state?.Api ?? builtIn?.Api;
        var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? state?.BaseUrl ?? builtIn?.BaseUrl : request.BaseUrl.Trim();
        var key = string.IsNullOrEmpty(request.ApiKey) ? state?.Key.Value : Secrets.Resolve(request.ApiKey.Trim()).Value;
        if (KnownApisShort.ToWireApi(api) is not { } wireApi || string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ProviderTestResult(false, null, 0, "Protocol and base URL are required.");
        }

        var listed = await discovery.FetchAsync(api!, baseUrl, key, state?.Headers, state?.BearerForMessages ?? false, ct);
        if (listed.Success)
        {
            return new ProviderTestResult(true, listed.StatusCode, listed.LatencyMs, $"{listed.Ids!.Count} models", listed.Ids.Count);
        }
        if (listed.StatusCode is 401 or 403)
        {
            return new ProviderTestResult(false, listed.StatusCode, listed.LatencyMs, listed.Error ?? "unauthorized");
        }

        // 不支持 /models：用一个最小请求验证（约消耗几个 tokens）
        var model = state?.Models.FirstOrDefault(m => m.Enabled)?.Model ?? state?.Models.FirstOrDefault()?.Model;
        if (model is null)
        {
            return new ProviderTestResult(false, listed.StatusCode, listed.LatencyMs, listed.Error ?? "no model to test with");
        }
        var probe = model with { Api = wireApi, BaseUrl = baseUrl, MaxTokens = 16 };
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stream = apis.Stream(probe, new LlmContext(null, [UserMessage.FromText("ping")], []),
            new StreamOptions { ApiKey = key, ThinkingLevel = ThinkingLevel.Off }, timeout.Token);
        await foreach (var e in stream.WithCancellation(CancellationToken.None))
        {
            if (e is ErrorEvent error)
            {
                return new ProviderTestResult(false, null, watch.Elapsed.TotalMilliseconds, error.Message.ErrorMessage ?? "request failed");
            }
            // 自动重试的通知不算连通：等它真正开始返回内容或报错
            if (e is not StartEvent and not RetryEvent)
            {
                // 收到首个内容即视为连通，不必等待完整回复
                timeout.Cancel();
                return new ProviderTestResult(true, 200, watch.Elapsed.TotalMilliseconds, model.Id);
            }
        }
        return new ProviderTestResult(true, 200, watch.Elapsed.TotalMilliseconds, model.Id);
    }
}
