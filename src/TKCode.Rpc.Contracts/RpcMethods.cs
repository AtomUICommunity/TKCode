namespace TKCode.Rpc;

/// <summary>JSON-RPC 方法名（"域/动作"），UI 与 Host 两端共用。</summary>
public static class RpcMethods
{
    // UI → Host
    public const string Initialize = "host/initialize";
    public const string ListModels = "models/list";
    public const string ListProviders = "providers/list";
    public const string ListCatalogProviders = "catalog/providers";
    public const string ReloadSettings = "settings/reload";
    public const string TestProvider = "providers/test";
    public const string RefreshProviderModels = "providers/refreshModels";
    public const string CatalogStatus = "catalog/status";
    public const string CheckCatalog = "catalog/check";
    public const string ApplyCatalogUpdate = "catalog/apply";
    public const string DiscardCatalogUpdate = "catalog/discard";
    public const string ListRuntimes = "runtimes/list";
    public const string CreateSession = "session/create";
    public const string ListSessions = "session/list";
    public const string QuerySessions = "session/query";
    public const string RenameSession = "session/rename";
    public const string PinSession = "session/pin";
    public const string DeleteSession = "session/delete";
    public const string ForkSession = "session/fork";
    public const string SessionHistory = "session/history";
    public const string OpenSession = "session/open";
    public const string Prompt = "session/prompt";
    public const string Abort = "session/abort";
    public const string Compact = "session/compact";
    public const string ContextUsage = "session/contextUsage";
    public const string SetModel = "session/setModel";
    public const string SetOptions = "session/setOptions";
    public const string CloseSession = "session/close";
    public const string AddDirectory = "session/addDirectory";
    public const string RemoveDirectory = "session/removeDirectory";
    public const string OpenSubagent = "subagent/open";
    public const string CloseSubagent = "subagent/close";
    public const string McpList = "mcp/list";
    public const string McpTools = "mcp/tools";
    public const string McpLog = "mcp/log";
    public const string McpTest = "mcp/test";
    public const string McpRestart = "mcp/restart";
    public const string McpLogout = "mcp/logout";
    public const string McpTrust = "mcp/trust";
    public const string McpCatalog = "mcp/catalog";
    public const string SearchFiles = "workspace/searchFiles";
    public const string RevertFile = "workspace/revertFile";
    public const string ListAssets = "assets/list";

    // Host → UI
    public const string SessionEvent = "session/event";
    public const string SubagentEvent = "subagent/event";
    public const string ModelsChanged = "models/changed";
    public const string ApproveTool = "tool/approve";
    public const string McpChanged = "mcp/changed";
    public const string McpElicit = "mcp/elicit";
    public const string McpOpenUrl = "mcp/openUrl";
    public const string RequestInput = "input/request";
    public const string ReviewPlan = "plan/review";
    public const string AssetsChanged = "assets/changed";
}

/// <summary>多参数或基础类型参数的方法统一以具名对象作为 params。</summary>
public sealed record SessionIdParams(string SessionId);

public sealed record ListSessionsParams(string? Cwd);

/// <param name="Cwd">只列出该项目的会话；null 表示全部项目。</param>
/// <param name="ProjectId">只列出属于这个项目的会话（docs/design/projects.md §6.5）；与 Cwd 同时给出时两个条件都要满足。</param>
public sealed record QuerySessionsParams(string? Cwd, int Offset, int Limit, string? ProjectId = null);

/// <summary>session/addDirectory、session/removeDirectory：只给本会话追加或移除一个文件夹（§6.4）。</summary>
public sealed record SessionDirectoryParams(string SessionId, string Path);

/// <param name="Title">新标题；空串表示清除自定义标题。</param>
public sealed record RenameSessionParams(string FilePath, string Title);

public sealed record PinSessionParams(string FilePath, bool Pinned);

public sealed record SessionFileParams(string FilePath);

/// <param name="TurnLimit">只返回最近若干轮（每轮以用户消息开头）；null 返回全部。</param>
public sealed record OpenSessionParams(string FilePath, int? TurnLimit = null);

/// <param name="Before">取该位置之前的消息（OpenSessionResult.HistoryStart 或上一页的 Start）。</param>
public sealed record HistoryPageParams(string SessionId, int Before, int TurnLimit);

public sealed record ProviderIdParams(string ProviderId);

public sealed record SetModelParams(string SessionId, string ModelId);

public sealed record SetOptionsParams(string SessionId, SessionOptionsDto Options);

/// <summary>子 Agent 由父会话与派生它的工具调用标识（docs/design/subagent-view.md §4）。</summary>
public sealed record SubagentParams(string SessionId, string ToolCallId);

/// <param name="Instructions">/compact 后面的文字：摘要的重点；null 表示没有。</param>
public sealed record CompactParams(string SessionId, string? Instructions);
