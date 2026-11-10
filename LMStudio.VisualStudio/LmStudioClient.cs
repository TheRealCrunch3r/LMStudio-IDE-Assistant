// ---------------------------------------------------------------------------
// LmStudioClient.cs – lightweight HTTP client for the local LM Studio server.
// Uses exclusively the OpenAI-compatible endpoint:  {Base}/v1/chat/completions
// (documentation: https://lmstudio.ai/docs/api — local, API-keyless connection).
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LMStudio.VisualStudio
{
    /// <summary>Local server connection configuration (persisted via SettingsStore).</summary>
    public sealed class AssistantSettings
    {
        /// <summary>Base URL of the LM Studio server, e.g. "http://localhost:1234".</summary>
        public string BaseUrl { get; set; } = "http://localhost:1234";

        /// <summary>Desired model ID. Leave empty = server uses its default model.</summary>
        public string ModelId { get; set; } = "";

        /// <summary>HTTP timeout in seconds (local LLM answers may take a while).</summary>
        public int TimeoutSeconds { get; set; } = 600;

        private string NormalizeBase()
        {
            var u = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(u) ? "http://localhost:1234" : u;
        }

        public Uri BaseUri => new Uri(NormalizeBase());
    }

    /// <summary>A chat message in OpenAI-compatible format.</summary>
    public sealed class ChatMessage
    {
        public string Role { get; set; } = "user";   // "system" | "user" | "assistant"
        public string Content { get; set; } = "";

        public ChatMessage() { }
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>Error from the LM Studio server (HTTP or parsing error) with a human-readable message.</summary>
    public class LmStudioServerException : Exception
    {
        public LmStudioServerException(string message) : base(message) { }
    }

    /// <summary>
    /// Static, thread-safe client class. HttpClient is a singleton (well-known .NET best practice:
    /// never create new per request — otherwise socket port exhaustion).
    /// </summary>
    public static class LmStudioClient
    {
        private static readonly HttpClient Http = new HttpClient();

        /// <summary>Sends the conversation history and returns the model's response.</summary>
        public static async Task<string> SendChatAsync(AssistantSettings settings, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            var payload = new
            {
                model = string.IsNullOrWhiteSpace(settings.ModelId) ? (string)null : settings.ModelId.Trim(),
                messages = messages
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var requestUri = new Uri(settings.BaseUri, "v1/chat/completions");

            HttpResponseMessage response;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));
                response = await Http.PostAsync(requestUri, content, cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new LmStudioServerException($"LM Studio server at '{settings.BaseUrl}' not reachable. Is the server running? ({ShortReason(ex)})");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LmStudioServerException($"Timed out after {settings.TimeoutSeconds}s — response pending or server hung up.");
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            bool success = response.IsSuccessStatusCode;
            int statusCode = (int)response.StatusCode;
            response.Dispose();

            if (!success)
                throw new LmStudioServerException($"LM Studio server responded with HTTP {statusCode}: {Truncate(body, 300)}");

            return ExtractAssistantText(body);
        }

        /// <summary>Connection test: fetches {Base}/v1/models and returns loaded model IDs.</summary>
        public static async Task<List<string>> GetModelsAsync(AssistantSettings settings, CancellationToken ct)
        {
            var requestUri = new Uri(settings.BaseUri, "v1/models");

            HttpResponseMessage response;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10)); // connection test must be fast
                response = await Http.GetAsync(requestUri, cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new LmStudioServerException($"LM Studio server at '{settings.BaseUrl}' not reachable. Is the server running? ({ShortReason(ex)})");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LmStudioServerException("Connection test: timed out (10 s).");
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            bool success = response.IsSuccessStatusCode;
            int statusCode = (int)response.StatusCode;
            response.Dispose();

            if (!success)
                throw new LmStudioServerException($"HTTP {statusCode}: {Truncate(body, 200)}");

            try
            {
                using var doc = JsonDocument.Parse(body);
                var models = new List<string>();
                foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id)) models.Add(id.GetString() ?? "?");
                }
                return models;
            }
            catch (JsonException)
            {
                throw new LmStudioServerException("Server response is not in the expected JSON format.");
            }
        }

        // ----------------------------- internal helpers -----------------------------

        /// <summary>Parses choices[0].message.content from a chat completion response.</summary>
        private static string ExtractAssistantText(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                throw new LmStudioServerException("Empty server response.");

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone(); // Clone: the doc is disposed after this block
            }
            catch (JsonException)
            {
                throw new LmStudioServerException("Server response is not JSON. (Wrong endpoint or proxy reply?)");
            }

            try
            {
                var choices = root.GetProperty("choices");
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    throw new LmStudioServerException("Response contains no 'choices'.");
                return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch (LmStudioServerException) { throw; }
            catch (KeyNotFoundException)
            {
                // e.g. "error" object instead of "choices" (server's API error)
                if (root.TryGetProperty("error", out var err))
                {
                    var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString();
                    throw new LmStudioServerException("LM Studio reported an error: " + Truncate(msg ?? "?", 300));
                }
                throw new LmStudioServerException("Unexpected response format (field 'choices[0].message.content' missing).");
            }
        }

        private static string ShortReason(Exception ex)
        {
            var m = ex.InnerException?.Message ?? ex.Message;
            return m != null && m.Length > 120 ? m[..120] + "…" : (m ?? "");
        }

        private static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s[..max] + "…";
    }
}
