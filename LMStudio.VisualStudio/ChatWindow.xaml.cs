// ---------------------------------------------------------------------------
// ChatWindow.xaml.cs – Code-behind for the chat view.
// Threading rule: WPF SynchronizationContext handles marshaling back after await;
// no manual Dispatcher calls needed as long as we never touch UI from another thread.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LMStudio.VisualStudio
{
    public partial class ChatWindow : UserControl
    {
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly List<string> _toolTranscript = new List<string>(); // lightweight record of tool use (kept out of the model context on purpose)
        private AssistantSettings _settings;
        private bool _busy;

        /// <summary>Max number of additional model calls per user message while executing requested file tools.</summary>
        private const int MaxToolRounds = 5;

        private static readonly Brush UserBrush = Brushes.DarkBlue;
        private static readonly Brush AssistantBrush = Brushes.Black;
        private static readonly Brush ErrorBrush = Brushes.Firebrick;
        private static readonly Brush SystemBrushColor = Brushes.DimGray;

        public ChatWindow()
        {
            InitializeComponent();
            _settings = SettingsStore.Load();
            UpdateStatus($"LM Studio: {(_settings.BaseUri == null ? "?" : _settings.BaseUrl)}" +
                         (string.IsNullOrWhiteSpace(_settings.ModelId) ? " · Default model" : " · Model: " + _settings.ModelId));
        }

        // ------------------------------ Chat logic ------------------------------

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            var prompt = PromptInput.Text?.Trim() ?? "";
            if (prompt.Length == 0 || _busy) return;

            PromptInput.Clear();
            AppendMessage("You: ", prompt, UserBrush);
            _history.Add(new ChatMessage("user", prompt));

            SetBusy(true);
            try
            {
                var finalReply = await SendWithToolsAsync(System.Threading.CancellationToken.None);
                if (string.IsNullOrWhiteSpace(finalReply))
                    AppendMessage("(empty response)", "The model didn't produce an answer — context might be too long or temperature setting should be checked.", ErrorBrush);
                else
                    _history.Add(new ChatMessage("assistant", finalReply));
            }
            catch (LmStudioServerException ex)
            {
                // Error handling per spec: MessageBox + prompt becomes editable again.
                AppendMessage("Error: ", ex.Message, ErrorBrush);
                MessageBox.Show(ex.Message, "LM Studio AI Assistant – Server Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppendMessage("Unexpected error: ", ex.Message, ErrorBrush);
                MessageBox.Show(ex.ToString(), "LM Studio AI Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                PromptInput.Focus();
            }
        }

        // ------------------------------ Agent loop (file tools) ------------------------------

        /// <summary>
        /// Sends one request to the local server and, whenever the model embeds file-tool calls
        /// in its reply, executes them against the workspace root and feeds the machine-readable
        /// results back as a user message — repeated for up to MaxToolRounds rounds.
        /// Returns the FINAL assistant text; appending it to _history is done by the caller (exactly once).
        /// </summary>
        private async Task<string> SendWithToolsAsync(System.Threading.CancellationToken ct)
        {
            string reply = await LmStudioClient.SendChatAsync(_settings, BuildApiMessages(), ct);
            AppendMessage("Assistant: ", string.IsNullOrWhiteSpace(reply) ? "(empty response)" : reply, AssistantBrush);

            for (int round = 1; round <= MaxToolRounds; round++)
            {
                var calls = WorkspaceTools.ExtractToolCalls(reply);
                if (calls.Count == 0) return reply ?? ""; // plain text answer — done.

                string workspaceRoot;
                try
                {
                    workspaceRoot = await Task.Run(WorkspaceTools.ResolveWorkspaceRoot); // off-UI-thread: no blocking during the DTE service lookup
                }
                catch (Exception ex)
                {
                    AppendMessage("System: ", "File tools disabled for this message: " + ex.Message, ErrorBrush);
                    return reply ?? ""; // keep the model's text answer; do not loop without a root.
                }

                string toolLog = "";
                foreach (var call in calls)
                {
                    var tRes = await WorkspaceTools.RunAsync(call, workspaceRoot, ct); // file IO runs OFF the UI thread
                    AppendMessage(tRes.Success ? "  Tool OK:   " : "  Tool FAILED:",
                        (call.HasParseError ? "?invalid-json? " : call.ToString() + " — ") + tRes.Summary,
                        tRes.Success ? SystemBrushColor : ErrorBrush);
                    toolLog += (tRes.Output ?? "") + "\n";
                }

                // Feed the results back and let the model continue from there.
                _history.Add(new ChatMessage("assistant", reply));
                _toolTranscript.Add("[" + round + "] " + string.Join(", ", calls.Select(c => c.ToString())));
                _history.Add(new ChatMessage("user",
                    "TOOL_RESULTS:\n" + toolLog.TrimEnd() +
                    "\n\n(These are the machine-readable results of your file-tool calls. Continue: answer the user's question, or emit another ```tool``` block if more files are needed.)"));

                reply = await LmStudioClient.SendChatAsync(_settings, BuildApiMessages(), ct);
                AppendMessage("Assistant: ", string.IsNullOrWhiteSpace(reply) ? "(empty response)" : reply, AssistantBrush);
            }

            // Round cap reached while tool calls were still pending.
            if (reply != null && WorkspaceTools.ExtractToolCalls(reply).Count > 0)
                AppendMessage("System: ", "Tool loop stopped after " + MaxToolRounds + " rounds — remaining tool calls were not executed.", ErrorBrush);

            return reply ?? "";
        }

        /// <summary>Builds the API message list: system prompt (static) + conversation history.
        /// The lightweight _toolTranscript is intentionally NOT part of the model context.</summary>
        private List<ChatMessage> BuildApiMessages()
        {
            var messages = new List<ChatMessage>();
            messages.Add(new ChatMessage("system", WorkspaceTools.SystemPrompt));
            foreach (var m in _history) messages.Add(m);
            return messages;
        }

        private void OnPromptKeyDown(object sender, KeyEventArgs e)
        {
            // Single-line input: Enter sends (Shift+Enter = no line break needed).
            if (e.Key == Key.Enter && _busy == false)
            {
                e.Handled = true;
                OnSendClick(this, new RoutedEventArgs());
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SendButton.IsEnabled = !busy;
            SettingsButton.IsEnabled = !busy;
            StatusText.Text = busy ? "LM Studio is thinking… (local inference may take a while)" : StatusText.Text;
        }

        // ------------------------------ UI helpers ------------------------------

        private void AppendMessage(string prefix, string text, Brush brush)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            var prefixRun = new Run(prefix)
            {
                FontWeight = FontWeights.Bold,
                Foreground = brush,
                FontSize = 13
            };
            var textRun = new Run(text) { FontSize = 13 };
            paragraph.Inlines.Add(prefixRun);
            paragraph.Inlines.Add(textRun);

            ChatOutput.Document.Blocks.Add(paragraph); // History is NOT cleared — it's the chat log.
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            try { ChatOutput.ScrollToEnd(); } catch { /* RichTextBox scroll units may fail on empty doc — harmless. */ }
        }

        private void UpdateStatus(string text) => StatusText.Text = text;

        // ------------------------------ Settings ------------------------------

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var page = new SettingsPage(_settings);
            bool? result = page.ShowDialog();   // modal in the Extension Host
            if (result == true)
            {
                _settings = page.ResultSettings ?? _settings;
                try
                {
                    SettingsStore.Save(_settings);
                    UpdateStatus($"LM Studio: {_settings.BaseUrl} · " +
                                 (string.IsNullOrWhiteSpace(_settings.ModelId) ? "Default model" : "Model: " + _settings.ModelId));
                    AppendMessage("System: ", "Settings saved.", SystemBrushColor);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "LM Studio AI Assistant – Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }
}
