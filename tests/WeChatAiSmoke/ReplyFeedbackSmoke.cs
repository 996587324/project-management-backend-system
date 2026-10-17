using System.Net;
using Interface;
using Microsoft.Extensions.Logging.Abstractions;
using Model.Agent;
using Service;

internal static class ReplyFeedbackSmoke
{
    public static async Task RunAsync()
    {
        var runtime = new RuntimeAgentOptions
        {
            AgentCode = "production-dashboard",
            Provider = "offline",
            BaseUrl = "https://offline.invalid/",
            ApiKey = "offline-test-not-sent",
            ApiKeyHeader = "Authorization",
            Model = "fixture",
            MaxTokens = 1200,
            TimeoutSeconds = 30
        };
        var resolver = Proxy.Make<IAgentModelConfigurationResolver>((method, _) =>
            method.Name == "ResolveCandidatesAsync"
                ? Task.FromResult<IReadOnlyList<RuntimeAgentOptions>>([runtime])
                : throw new NotSupportedException(method.Name));
        var logs = new List<AgentModelInvocationLogWriteDto>();
        var logService = Proxy.Make<IAgentModelInvocationLogService>((method, values) =>
        {
            logs.Add((AgentModelInvocationLogWriteDto)values[0]!);
            return Task.CompletedTask;
        });
        var fixtures = new[]
        {
            ("""{"choices":[{"finish_reason":"length","message":{"content":null}}]}""", AgentReplyFailureKind.OutputLimit),
            ("""{"choices":[{"finish_reason":"length","message":{"content":"partial response"}}]}""", AgentReplyFailureKind.OutputLimit),
            ("""{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""", AgentReplyFailureKind.EmptyResponse),
            ("""{"choices":[{"finish_reason":"stop","message":{"content":null}}]}""", AgentReplyFailureKind.EmptyResponse),
            ("not-json", AgentReplyFailureKind.InvalidResponse),
            ("""{"choices":[{"finish_reason":"stop","message":{"content":"complete answer"}}]}""", AgentReplyFailureKind.None)
        };
        foreach (var (json, expected) in fixtures)
        {
            using var http = new HttpClient(new OfflineReplyHandler(json));
            var model = new AgentModelClient(http, resolver, logService, NullLogger<AgentModelClient>.Instance);
            var reply = await model.GenerateReplyAsync(new AgentModelRequest { AgentCode = runtime.AgentCode, UserMessage = "test" });
            Check(reply.FailureKind == expected && reply.Success == (expected == AgentReplyFailureKind.None),
                "model classifies final response: " + expected);
            Check(logs[^1].Success == reply.Success && logs[^1].StatusCode == 200 &&
                  (reply.Success || !string.IsNullOrWhiteSpace(logs[^1].ErrorMessage)),
                "HTTP 200 does not hide empty/truncated/invalid replies in model invocation audit");
        }

        var tools = new List<AgentToolDefinitionDto>
        {
            new() { Name = "dashboard.overview", IsWriteAction = false },
            new() { Name = "dashboard.save", IsWriteAction = true }
        };
        Check(!AgentReplyFailureFeedback.MayHaveWriteEffects(
            [new() { ToolName = "dashboard.overview", Status = "Succeeded" }], tools), "read-only success is not a write side effect");
        Check(AgentReplyFailureFeedback.MayHaveWriteEffects(
            [new() { ToolName = "dashboard.save", Status = "Succeeded" }], tools), "completed writes require review");
        Check(!AgentReplyFailureFeedback.MayHaveWriteEffects(
            [new() { ToolName = "dashboard.save", Status = "PendingConfirm" }], tools), "unconfirmed plan has not executed a write");
        Check(AgentReplyFailureFeedback.MayHaveWriteEffects(
            [new() { ToolName = "unknown", Status = "Succeeded" }], tools), "unrecognized tool is conservatively treated as potentially mutating");

        var dispatchCount = 0;
        var replyCount = 0;
        var fakeModel = Proxy.Make<IAgentModelClient>((method, _) =>
        {
            if (method.Name == "SelectToolsAsync")
                return Task.FromResult(new AgentToolSelectionReply
                {
                    Success = true,
                    ToolCalls = [new() { ToolName = "dashboard.overview", ArgumentsJson = "{}" }]
                });
            if (method.Name != "GenerateReplyAsync") throw new NotSupportedException(method.Name);
            replyCount++;
            return Task.FromResult(new AgentModelReply { FailureKind = AgentReplyFailureKind.OutputLimit });
        });
        var dispatcher = Proxy.Make<IAgentToolDispatcher>((method, _) =>
        {
            if (method.Name != "DispatchAsync") throw new NotSupportedException(method.Name);
            dispatchCount++;
            return Task.FromResult(new AgentToolCallDto { ToolName = "dashboard.overview", Status = "Succeeded", ResponseJson = "{}" });
        });
        var framework = new MicrosoftAgentFrameworkRuntime(fakeModel, dispatcher,
            NullLoggerFactory.Instance, NullLogger<MicrosoftAgentFrameworkRuntime>.Instance);
        using var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default);
        var result = await framework.RunAsync(new AgentFrameworkRunRequest
        {
            AgentCode = runtime.AgentCode,
            UserMessage = "test",
            Tools = [tools[0]]
        });
        Check(!result.Success && result.FailureKind == AgentReplyFailureKind.OutputLimit && result.ToolCalls.Count == 1 &&
              dispatchCount == 1 && replyCount == 1, "real framework preserves failure category and never retries business tool after reply failure");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class OfflineReplyHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }
}
