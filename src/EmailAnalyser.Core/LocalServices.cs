using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmailAnalyser.Core;

/// <summary>Only literal loopback addresses are accepted for local services.</summary>
public static class LoopbackGuard
{
    public static Uri Ensure(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException($"Osoitteen pitää olla http-osoite: {url}");
        }

        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException($"Vain loopback-osoitteet (127.0.0.1, ::1) ovat sallittuja: {url}");
        }

        return uri;
    }

    /// <summary>No proxy, no redirects: requests can only reach the given loopback address.</summary>
    public static HttpClient CreateClient(Uri baseAddress, TimeSpan timeout) =>
        new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
        })
        {
            BaseAddress = baseAddress,
            Timeout = timeout,
        };
}

public enum ModelFailureKind
{
    /// <summary>Server not reachable: not the message's fault.</summary>
    Unavailable,
    /// <summary>No answer in time while processing a request.</summary>
    Timeout,
    /// <summary>HTTP 5xx or a malformed answer.</summary>
    ServerError,
    /// <summary>HTTP 400/413/422: the server refused this request (e.g. too many tokens).</summary>
    Rejected,
}

public sealed class ModelServerException(string message, ModelFailureKind kind = ModelFailureKind.Unavailable, Exception? inner = null)
    : Exception(message, inner)
{
    public ModelFailureKind Kind { get; } = kind;
}

public sealed record LoadedInstance(string ModelKey, string InstanceId);

public sealed record LoadResult(string InstanceId, double LoadSeconds);

public sealed record ChatRequest(string Instance, string System, string User, string SchemaJson, int MaxTokens);

public sealed record ChatResponse(string Content, string? FinishReason, int? PromptTokens, int? CompletionTokens, TimeSpan Elapsed);

public interface ILanguageModelServer
{
    Task<IReadOnlyList<LoadedInstance>> GetLoadedInstancesAsync(CancellationToken cancellationToken);
    Task<LoadResult> LoadAsync(string modelKey, int contextLength, CancellationToken cancellationToken);
    Task UnloadAsync(string instanceId, CancellationToken cancellationToken);
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>LM Studio over its REST API (model lifecycle) and OpenAI-compatible chat.</summary>
public sealed class LmStudioClient : ILanguageModelServer, IDisposable
{
    private readonly HttpClient _http;

    public LmStudioClient(string baseUrl, TimeSpan requestTimeout)
    {
        _http = LoopbackGuard.CreateClient(LoopbackGuard.Ensure(baseUrl), requestTimeout);
    }

    public async Task<IReadOnlyList<LoadedInstance>> GetLoadedInstancesAsync(CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Get, "/api/v1/models", null, cancellationToken);
        var result = new List<LoadedInstance>();
        foreach (var model in root["models"]?.AsArray() ?? [])
        {
            var key = model?["key"]?.GetValue<string>() ?? "?";
            foreach (var instance in model?["loaded_instances"]?.AsArray() ?? [])
            {
                var id = instance switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonObject o => (o["id"] ?? o["instance_id"] ?? o["identifier"])?.GetValue<string>(),
                    _ => null,
                };
                result.Add(new LoadedInstance(key, id ?? key));
            }
        }

        return result;
    }

    public async Task<LoadResult> LoadAsync(string modelKey, int contextLength, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["model"] = modelKey, ["context_length"] = contextLength, ["flash_attention"] = true };
        var started = Stopwatch.GetTimestamp();
        var root = await SendAsync(HttpMethod.Post, "/api/v1/models/load", body, cancellationToken);
        var id = root["instance_id"]?.GetValue<string>()
                 ?? throw new ModelServerException("LM Studio ei palauttanut ladatun instanssin tunnistetta.", ModelFailureKind.ServerError);
        var seconds = root["load_time_seconds"]?.GetValue<double>() ?? Stopwatch.GetElapsedTime(started).TotalSeconds;
        return new LoadResult(id, seconds);
    }

    public async Task UnloadAsync(string instanceId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, "/api/v1/models/unload", new JsonObject { ["instance_id"] = instanceId }, cancellationToken);

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = request.Instance,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User },
            },
            ["temperature"] = 0,
            ["max_tokens"] = request.MaxTokens,
            ["reasoning_effort"] = "none",
            ["stream"] = false,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "arvio",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.SchemaJson),
                },
            },
        };

        var started = Stopwatch.GetTimestamp();
        var root = await SendAsync(HttpMethod.Post, "/v1/chat/completions", body, cancellationToken);
        var choice = root["choices"]?[0];
        return new ChatResponse(
            choice?["message"]?["content"]?.GetValue<string>() ?? "",
            choice?["finish_reason"]?.GetValue<string>(),
            root["usage"]?["prompt_tokens"]?.GetValue<int>(),
            root["usage"]?["completion_tokens"]?.GetValue<int>(),
            Stopwatch.GetElapsedTime(started));
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The error body may echo the request, so only the status is reported.
                var status = (int)response.StatusCode;
                var kind = status is 400 or 413 or 422 ? ModelFailureKind.Rejected
                    : status >= 500 ? ModelFailureKind.ServerError
                    : ModelFailureKind.Unavailable;
                throw new ModelServerException($"LM Studio {path}: HTTP {status}", kind);
            }

            return JsonNode.Parse(text) ?? throw new ModelServerException($"LM Studio {path}: tyhjä vastaus", ModelFailureKind.ServerError);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelServerException($"LM Studio ei vastaa ({path}).", ModelFailureKind.Unavailable, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelServerException($"LM Studio ei vastannut ajoissa ({path}).", ModelFailureKind.Timeout, ex);
        }
        catch (JsonException ex)
        {
            throw new ModelServerException($"LM Studio {path}: vastaus ei ole JSONia.", ModelFailureKind.ServerError, ex);
        }
    }
}

public interface IGpuActivityProbe
{
    /// <summary>True when Automatic1111 is generating; false when idle or not running.</summary>
    Task<bool> IsGeneratingAsync(CancellationToken cancellationToken);
}

public sealed class Automatic1111Probe : IGpuActivityProbe, IDisposable
{
    private readonly HttpClient _http;

    public Automatic1111Probe(string baseUrl)
    {
        _http = LoopbackGuard.CreateClient(LoopbackGuard.Ensure(baseUrl), TimeSpan.FromSeconds(5));
    }

    public async Task<bool> IsGeneratingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                await _http.GetStringAsync("/sdapi/v1/progress?skip_current_image=true", cancellationToken));
            var root = doc.RootElement;
            var jobs = root.TryGetProperty("state", out var state) && state.TryGetProperty("job_count", out var count)
                ? count.GetInt32()
                : 0;
            var progress = root.TryGetProperty("progress", out var p) ? p.GetDouble() : 0;
            return jobs > 0 || progress > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Not running or not answering: it is not using the GPU for generation.
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

public interface IGpuMemoryProbe
{
    int? UsedMiB();
}

public sealed class NvidiaSmiProbe : IGpuMemoryProbe
{
    public int? UsedMiB()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return int.TryParse(output.Trim().Split('\n')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) ? mib : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
