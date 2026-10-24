using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using CodexMeter;

internal static class DesktopChatChecks
{
    private static int checks;
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
    public static async Task<int> Run()
    {
        var id = Guid.NewGuid().ToString();
        var rule = new PromptRule { Action = PromptAction.SendToDesktopChat, DesktopThreadId = id,
            Prompt = "Quoted \" text, $(),\nUnicode — 中文", WorkingDirectory = "unused folder" };
        rule.Validate();
        Check(PromptRulesForm.ParseDesktopThreadId($"codex://threads/{id}?view=review") == id, "Chat links normalize to exact IDs");
        foreach (var invalid in new[] { "", "a chat title", Guid.Empty.ToString() })
        {
            try { (rule with { DesktopThreadId = invalid }).Validate(); throw new Exception("Invalid destination accepted"); }
            catch (FormatException) { Check(true, "A specific chat UUID is required"); }
        }
        try { (rule with { Prompt = "" }).Validate(); throw new Exception("Empty prompt accepted"); }
        catch (FormatException) { Check(true, "Desktop rules require a prompt"); }
        var serialized = JsonSerializer.Serialize(rule);
        Check(JsonSerializer.Deserialize<PromptRule>(serialized) == rule, "Destination/action round trip unchanged");
        using var listing = JsonDocument.Parse(JsonSerializer.Serialize(new {
            pinnedThreads = new[] { new { id, kind = "codex", hostId = "local", title = "My chat" } },
            threads = new[] { new { id, kind = "codex", hostId = "local", title = "Duplicate" },
                new { id = Guid.NewGuid().ToString(), kind = "codex", hostId = "remote", title = "Remote" },
                new { id = Guid.NewGuid().ToString(), kind = "chatgpt", hostId = "local", title = "ChatGPT" } }
        }));
        var choices = CodexDesktopChatClient.ParseChats(listing.RootElement);
        Check(choices.Count == 1 && choices[0].Title == "My chat", "Picker preserves titles and excludes duplicates, remote and ChatGPT chats");
        var (result, requests) = await Exercise(rule, "ok");
        Check(result.Status == "Submitted to desktop", "Only a confirmed app response is labeled submitted");
        var calls = requests.Where(r => r.GetProperty("method").GetString() == "tools/call").Select(r => r.GetProperty("params")).ToArray();
        Check(calls.Select(c => c.GetProperty("tool").GetString()).SequenceEqual(new[] { "read_thread", "send_message_to_thread" }), "Validate destination before exactly one send");
        var sent = calls[1].GetProperty("arguments");
        Check(sent.GetProperty("threadId").GetString() == id && sent.GetProperty("hostId").GetString() == "local"
            && sent.GetProperty("prompt").GetString() == rule.Prompt, "Exact destination and Unicode prompt survive pipe framing");
        Check(sent.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "hostId", "prompt", "threadId" }), "No model, permission or tool overrides");
        Check(!requests.Any(r => r.GetProperty("method").GetString() is "thread/start" or "thread/resume" or "turn/start"), "No standalone runtime or replacement chat is started");
        (result, requests) = await Exercise(rule, "read-error");
        Check(result.Status == "Failed to send" && requests.Count == 2, "Unavailable destination never receives a send");
        (result, requests) = await Exercise(rule, "wrong-host");
        Check(result.Status == "Failed to send" && requests.Count == 2, "A remote destination is rejected before sending");
        (result, requests) = await Exercise(rule, "disconnect");
        Check(result.Status == "Delivery unconfirmed" && requests.Count == 3, "A lost send acknowledgement never triggers a retry");
        (result, requests) = await Exercise(rule, "send-error");
        Check(result.Status != "Submitted to desktop" && requests.Count == 3, "Desktop error is not reported as success or retried");
        (result, requests) = await Exercise(rule, "bad-frame");
        Check(result.Status == "Failed to send" && requests.Count == 1, "Oversized response is rejected before allocation or submission");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var connects = 0;
        result = await new CodexDesktopChatClient(t => { connects++; t.ThrowIfCancellationRequested(); throw new Exception("Cancelled call continued"); }).SendAsync(rule, cancelled.Token);
        Check(result.Status == "Failed to send" && connects == 1, "Cancellation before submission sends nothing");
        Console.WriteLine($"PASS: {checks} focused desktop chat checks (fake local pipes; no real messages)");
        return 0;
    }

    private static async Task<(PromptRunResult Result, List<JsonElement> Requests)> Exercise(PromptRule rule, string mode)
    {
        var name = "codex-meter-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var requests = new List<JsonElement>();
        var server = Serve(pipe, mode, requests, deadline.Token);
        var client = new CodexDesktopChatClient(t => DesktopBridge.ConnectAsync(t, [@"\\.\pipe\" + name]));
        var result = await client.SendAsync(rule, deadline.Token);
        await server;
        return (result, requests);
    }

    private static async Task Serve(NamedPipeServerStream pipe, string mode, List<JsonElement> requests, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token);
        for (var i = 0; i < 3; i++)
        {
            var header = new byte[4]; await pipe.ReadExactlyAsync(header, token);
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await pipe.ReadExactlyAsync(body, token);
            using var request = JsonDocument.Parse(body); var root = request.RootElement; requests.Add(root.Clone());
            if (mode == "disconnect" && i == 2) { pipe.Disconnect(); return; }
            if (mode == "bad-frame") { BinaryPrimitives.WriteInt32LittleEndian(header, 9 * 1024 * 1024); await pipe.WriteAsync(header, token); return; }
            object response;
            if (i == 0)
                response = new { tools = new[] { "list_threads", "read_thread", "send_message_to_thread" }.Select(name => new { name, @namespace = "codex_app" }) };
            else
                response = new { success = !(mode == "read-error" && i == 1 || mode == "send-error" && i == 2),
                    contentItems = new[] { new { type = "inputText", text = JsonSerializer.Serialize(new {
                        thread = new { id = root.GetProperty("params").GetProperty("arguments").GetProperty("threadId").GetString(),
                            kind = "codex", hostId = mode == "wrong-host" ? "remote" : "local" }, status = "accepted" }) } } };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result = response });
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await pipe.WriteAsync(header, token);
            // Split the body to exercise stream reads rather than relying on message boundaries.
            await pipe.WriteAsync(bytes.AsMemory(0, 3), token); await pipe.WriteAsync(bytes.AsMemory(3), token); await pipe.FlushAsync(token);
            if (mode is "read-error" or "wrong-host" && i == 1) return;
        }
    }
}
