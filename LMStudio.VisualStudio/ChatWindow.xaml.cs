// ---------------------------------------------------------------------------
// ChatWindow.xaml.cs – Code-behind for the chat view.
// Threading rule: WPF SynchronizationContext handles marshaling back after await;
// no manual Dispatcher calls needed as long as we never touch UI from another thread.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
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
        private AssistantSettings _settings;
        private bool _busy;

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
                var reply = await LmStudioClient.SendChatAsync(_settings, _history, System.Threading.CancellationToken.None);
                if (string.IsNullOrWhiteSpace(reply))
                    AppendMessage("(empty response)", "The model didn't produce an answer — context might be too long or temperature setting should be checked.", ErrorBrush);
                else
                {
                    AppendMessage("Assistant: ", reply, AssistantBrush);
                    _history.Add(new ChatMessage("assistant", reply));
                }
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
