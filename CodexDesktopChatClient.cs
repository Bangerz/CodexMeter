using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace CodexMeter;

public sealed record DesktopChat(string Id, string Title)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Id : Title;
}

// Uses the installed desktop app-tools bridge, not a second app-server or
// `codex exec resume`. This local bridge is version-dependent; fail visibly
// if its catalog/protocol changes, never fall back to a different runtime.
public sealed class CodexDesktopChatClient
{
    private readonly Func<CancellationToken, Task<DesktopBridge>> connect;
    public CodexDesktopChatClient() : this(token => DesktopBridge.ConnectAsync(token)) { }
    internal CodexDesktopChatClient(Func<CancellationToken, Task<DesktopBridge>> connect) => this.connect = connect;
    public async Task<List<DesktopChat>> ListAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // App tools require an existing chat as routing context. A metadata-only
        // local lookup provides it before the user has picked a destination.
        await using var local = new GoalRpc();
        await local.StartAsync(deadline.Token);
        var recent = await local.CallAsync("thread/list", new { limit = 1, sourceKinds = new[] { "appServer", "cli", "vscode" },
            archived = false, sortKey = "updated_at", sortDirection = "desc", useStateDbOnly = true }, deadline.Token);
        var context = recent.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (context.ValueKind != JsonValueKind.Object) throw new DesktopBridgeException("Open a local Codex chat first.");
        var contextId = context.GetProperty("id").GetString()!;
        await using var bridge = await connect(deadline.Token);
        return ParseChats(await bridge.CallToolAsync("list_threads", new { limit = 50 }, contextId, deadline.Token));
    }

    internal static List<DesktopChat> ParseChats(JsonElement listing)
    {
        var result = new List<DesktopChat>();
        foreach (var key in new[] { "pinnedThreads", "threads" })
        {
            if (!listing.TryGetProperty(key, out var threads) || threads.ValueKind != JsonValueKind.Array)
                throw new DesktopBridgeException("The desktop returned an unsupported chat list.");
            foreach (var thread in threads.EnumerateArray())
            {
                if (thread.GetProperty("kind").GetString() != "codex" || thread.GetProperty("hostId").GetString() != "local") continue;
                var id = thread.GetProperty("id").GetString();
                if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty) continue;
                result.Add(new(id!, thread.TryGetProperty("title", out var title) ? title.GetString() ?? id! : id!));
            }
        }
        return result.DistinctBy(t => t.Id).ToList();
    }

    public async Task<PromptRunResult> SendAsync(PromptRule rule, CancellationToken token)
    {
        rule.Validate();
        if (rule.Action != PromptAction.SendToDesktopChat) throw new ArgumentException("Select a desktop chat action.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var submissionStarted = false;
        try
        {
            await using var bridge = await connect(deadline.Token);
            // Validate the destination through the desktop, including chats
            // outside the recent picker. Do not create, unarchive or resume it
            // through a standalone CLI helper.
            var read = await bridge.CallToolAsync("read_thread", new { threadId = rule.DesktopThreadId, hostId = "local",
                turnLimit = 1, includeOutputs = false, maxOutputCharsPerItem = 0 }, rule.DesktopThreadId, deadline.Token);
            if (!read.TryGetProperty("thread", out var target) || target.ValueKind != JsonValueKind.Object
                || !string.Equals(target.GetProperty("id").GetString(), rule.DesktopThreadId, StringComparison.OrdinalIgnoreCase)
                || target.GetProperty("kind").GetString() != "codex" || target.GetProperty("hostId").GetString() != "local")
                throw new DesktopBridgeException("The selected chat is unavailable. Choose a local Codex desktop chat.");
            deadline.Token.ThrowIfCancellationRequested();
            submissionStarted = true;
            await bridge.CallToolAsync("send_message_to_thread", new { threadId = rule.DesktopThreadId, hostId = "local", prompt = rule.Prompt },
                rule.DesktopThreadId, deadline.Token);
            return new("Submitted to desktop", $"The desktop accepted the prompt for chat {rule.DesktopThreadId}.\n"
                + "Continue in that chat to see its response. It uses the chat's model, permissions and available tools. "
                + "This confirms submission, not completion. Meter does not cancel the chat after sending.");
        }
        catch (Exception ex) when (ex is DesktopBridgeException or IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (submissionStarted)
                return new("Delivery unconfirmed", "Meter could not confirm submission. Check the destination chat before sending again; it may already have received the prompt. No automatic retry.");
            return new("Failed to send", ex is DesktopBridgeException ? ex.Message : "Could not reach the desktop chat. Open Codex and check the selected chat. No prompt was sent.");
        }
    }
}

internal sealed class DesktopBridgeException(string message) : Exception(message);

internal sealed class DesktopBridge(NamedPipeClientStream pipe) : IAsyncDisposable
{
    private const int MaxFrameBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, string> namespaces = [];
    private int nextId;

    internal static async Task<DesktopBridge> ConnectAsync(CancellationToken token, IEnumerable<string>? candidates = null)
    {
        // Explorer-launched Meter has no inherited app environment. Discover
        // the app's current local pipe on every dispatch so app restarts work.
        var inherited = Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");
        var names = candidates?.ToList() ?? Directory.GetFiles(@"\\.\pipe\", "codex-app-tools-*").Order().ToList();
        if (candidates is null && !string.IsNullOrWhiteSpace(inherited) && inherited.StartsWith(@"\\.\pipe\codex-app-tools-", StringComparison.OrdinalIgnoreCase))
            names.Insert(0, inherited);
        foreach (var path in names.Distinct(StringComparer.OrdinalIgnoreCase).Take(16))
        {
            token.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", path[9..], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            var bridge = new DesktopBridge(pipe);
            try
            {
                await pipe.ConnectAsync(750, token);
                var catalog = await bridge.RequestAsync("tools/list", new { threadStartKind = "all" }, token);
                foreach (var tool in catalog.GetProperty("tools").EnumerateArray())
                    if (tool.GetProperty("name").GetString() is { } name && name is "list_threads" or "read_thread" or "send_message_to_thread")
                        bridge.namespaces[name] = tool.GetProperty("namespace").GetString()!;
                if (bridge.namespaces.Count != 3) throw new DesktopBridgeException("This Codex version does not expose desktop chat messaging.");
                return bridge;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or KeyNotFoundException or DesktopBridgeException)
            { await bridge.DisposeAsync(); }
            catch { await bridge.DisposeAsync(); throw; }
        }
        throw new DesktopBridgeException("The Codex desktop messaging bridge is unavailable. Open Codex and try again. No prompt was sent.");
    }

    internal async Task<JsonElement> CallToolAsync(string tool, object arguments, string contextThreadId, CancellationToken token)
    {
        if (!namespaces.TryGetValue(tool, out var ns)) throw new DesktopBridgeException("The desktop tool is unavailable.");
        var result = await RequestAsync("tools/call", new { arguments, callerSource = "codex", callId = "codex-meter-" + Guid.NewGuid(),
            hostId = "local", @namespace = ns, threadId = contextThreadId, tool, turnId = "codex-meter-" + Guid.NewGuid() }, token);
        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new DesktopBridgeException("The desktop rejected the request. Check the selected chat and its availability.");
        var text = result.GetProperty("contentItems").EnumerateArray().FirstOrDefault(i => i.GetProperty("type").GetString() == "inputText");
        if (text.ValueKind != JsonValueKind.Object) throw new DesktopBridgeException("The desktop returned no confirmation.");
        using var document = JsonDocument.Parse(text.GetProperty("text").GetString()!);
        var data = document.RootElement;
        if (data.TryGetProperty("error", out _) || data.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new DesktopBridgeException("The desktop could not complete the request. Check the destination chat.");
        return data.Clone();
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        var id = ++nextId;
        var body = JsonSerializer.SerializeToUtf8Bytes(new { id, jsonrpc = "2.0", method, @params = parameters });
        if (body.Length > MaxFrameBytes) throw new DesktopBridgeException("Desktop request is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(body, token);
        await pipe.FlushAsync(token);
        await pipe.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaxFrameBytes) throw new DesktopBridgeException("Invalid desktop response size.");
        var payload = new byte[size];
        await pipe.ReadExactlyAsync(payload, token);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("id").GetInt32() != id) throw new DesktopBridgeException("Desktop response did not match the request.");
        if (root.TryGetProperty("error", out _)) throw new DesktopBridgeException("The desktop rejected the request.");
        return root.GetProperty("result").Clone();
    }

    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
