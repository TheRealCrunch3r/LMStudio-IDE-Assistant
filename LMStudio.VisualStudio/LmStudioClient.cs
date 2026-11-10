// ---------------------------------------------------------------------------
// LmStudioClient.cs – schlanker HTTP-Client für den lokalen LM Studio Server.
// Nutzt ausschließlich den OpenAI-kompatiblen Endpunkt:  {Base}/v1/chat/completions
// (Dokumentation: https://lmstudio.ai/docs/api — lokale, keylose Anbindung.)
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
    /// <summary>Konfiguration der lokalen Server-Anbindung (persistiert via SettingsStore).</summary>
    public sealed class AssistantSettings
    {
        /// <summary>Base-URL des LM Studio Servers, z. B. "http://localhost:1234".</summary>
        public string BaseUrl { get; set; } = "http://localhost:1234";

        /// <summary>Lediertes Modell-ID. Leer lassen = Server nutzt sein Default-Modell.</summary>
        public string ModelId { get; set; } = "";

        /// <summary>HTTP-Timeout in Sekunden (lokale LLM-Antworten können lang dauern).</summary>
        public int TimeoutSeconds { get; set; } = 600;

        private string NormalizeBase()
        {
            var u = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(u) ? "http://localhost:1234" : u;
        }

        public Uri BaseUri => new Uri(NormalizeBase());
    }

    /// <summary>Eine Chat-Nachricht im OpenAI-kompatiblen Format.</summary>
    public sealed class ChatMessage
    {
        public string Role { get; set; } = "user";   // "system" | "user" | "assistant"
        public string Content { get; set; } = "";

        public ChatMessage() { }
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>Fehler aus dem LM Studio Server (HTTP- oder Parsing-Fehler) mit menschenlesbarer Meldung.</summary>
    public class LmStudioServerException : Exception
    {
        public LmStudioServerException(string message) : base(message) { }
    }

    /// <summary>
    /// Statische, thread-sichere Client-Klasse. HttpClient ist ein Singleton (bekannte .NET-Bestpraxis:
    /// nie pro Request neu anlegen – sonst Socket-Port-Erschöpfung).
    /// </summary>
    public static class LmStudioClient
    {
        private static readonly HttpClient Http = new HttpClient();

        /// <summary>Sendet den Gesprächsverlauf und gibt die Antwort des Modells zurück.</summary>
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
                throw new LmStudioServerException($"LM Studio Server unter '{settings.BaseUrl}' nicht erreichbar. Läuft der Server? ({ShortReason(ex)})");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LmStudioServerException($"Zeitüberschreitung nach {settings.TimeoutSeconds}s – Antwort ausstehend oder Server hängt.");
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            bool success = response.IsSuccessStatusCode;
            int statusCode = (int)response.StatusCode;
            response.Dispose();

            if (!success)
                throw new LmStudioServerException($"LM Studio Server antwortete mit HTTP {statusCode}: {Truncate(body, 300)}");

            return ExtractAssistantText(body);
        }

        /// <summary>Connection-Test: ruft {Base}/v1/models ab und liefert die geladenen Modell-IDs.</summary>
        public static async Task<List<string>> GetModelsAsync(AssistantSettings settings, CancellationToken ct)
        {
            var requestUri = new Uri(settings.BaseUri, "v1/models");

            HttpResponseMessage response;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10)); // Verbindungstest muss schnell sein
                response = await Http.GetAsync(requestUri, cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new LmStudioServerException($"LM Studio Server unter '{settings.BaseUrl}' nicht erreichbar. Läuft der Server? ({ShortReason(ex)})");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LmStudioServerException("Verbindungstest: Zeitüberschreitung (10 s).");
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
                throw new LmStudioServerException("Antwort des Servers ist kein erwartetes JSON-Format.");
            }
        }

        // ----------------------------- interne Helfer -----------------------------

        /// <summary>Parsed choices[0].message.content aus einer Chat-Completion-Antwort.</summary>
        private static string ExtractAssistantText(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                throw new LmStudioServerException("Leere Antwort vom Server.");

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone(); // Clone: Doc wird nach dem using verworfen
            }
            catch (JsonException)
            {
                throw new LmStudioServerException("Antwort des Servers ist kein JSON. (Falscher Endpoint oder Proxy-Antwort?)");
            }

            try
            {
                var choices = root.GetProperty("choices");
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    throw new LmStudioServerException("Antwort enthält keine 'choices'.");
                return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch (LmStudioServerException) { throw; }
            catch (KeyNotFoundException)
            {
                // z. B. "error"-Objekt statt "choices" (API-Fehler des Servers)
                if (root.TryGetProperty("error", out var err))
                {
                    var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString();
                    throw new LmStudioServerException("LM Studio meldete einen Fehler: " + Truncate(msg ?? "?", 300));
                }
                throw new LmStudioServerException("Unerwartetes Antwortformat (Feld 'choices[0].message.content' fehlt).");
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
