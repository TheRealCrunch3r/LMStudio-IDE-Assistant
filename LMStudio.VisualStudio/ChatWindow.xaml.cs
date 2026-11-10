// ---------------------------------------------------------------------------
// ChatWindow.xaml.cs – Code-Behind der ChatView.
// Thread-Regel: WPF-SynchronizationContext übernimmt die Marshaling-Rückkehr nach await;
// keine manuellen Dispatcher-Calls nötig, solange wir nie auf einem anderen Thread UI anfassen.
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
                         (string.IsNullOrWhiteSpace(_settings.ModelId) ? " · Default-Modell" : " · Modell: " + _settings.ModelId));
        }

        // ------------------------------ Chat-Logik ------------------------------

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            var prompt = PromptInput.Text?.Trim() ?? "";
            if (prompt.Length == 0 || _busy) return;

            PromptInput.Clear();
            AppendMessage("Du: ", prompt, UserBrush);
            _history.Add(new ChatMessage("user", prompt));

            SetBusy(true);
            try
            {
                var reply = await LmStudioClient.SendChatAsync(_settings, _history, System.Threading.CancellationToken.None);
                if (string.IsNullOrWhiteSpace(reply))
                    AppendMessage("(leere Antwort)", "Das Modell hat keine Antwort geliefert – ggf. Kontext zu lang oder Temperatur-Einstellung prüfen.", ErrorBrush);
                else
                {
                    AppendMessage("Assistant: ", reply, AssistantBrush);
                    _history.Add(new ChatMessage("assistant", reply));
                }
            }
            catch (LmStudioServerException ex)
            {
                // Fehlerbehandlung gemäß Spezifikation: MessageBox + Prompt bleibt wieder editierbar.
                AppendMessage("Fehler: ", ex.Message, ErrorBrush);
                MessageBox.Show(ex.Message, "LM Studio AI Assistant – Serverfehler", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppendMessage("Unerwarteter Fehler: ", ex.Message, ErrorBrush);
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
            // Single-Line-Eingabe: Enter sendet (Shift+Enter = kein Zeilenumbruch nötig).
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
            StatusText.Text = busy ? "LM Studio denkt nach … (lokale Inferenz kann dauern)" : StatusText.Text;
        }

        // ------------------------------ UI-Helfer ------------------------------

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

            ChatOutput.Document.Blocks.Add(paragraph); // Historie wird NICHT geleert – das ist der Chatverlauf.
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            try { ChatOutput.ScrollToEnd(); } catch { /* RichTextBox-Scrolleinheiten können bei leerem Doc fehlschlagen – harmlos. */ }
        }

        private void UpdateStatus(string text) => StatusText.Text = text;

        // ------------------------------ Einstellungen ------------------------------

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var page = new SettingsPage(_settings);
            bool? result = page.ShowDialog();   // modal im Extension-Host
            if (result == true)
            {
                _settings = page.ResultSettings ?? _settings;
                try
                {
                    SettingsStore.Save(_settings);
                    UpdateStatus($"LM Studio: {_settings.BaseUrl} · " +
                                 (string.IsNullOrWhiteSpace(_settings.ModelId) ? "Default-Modell" : "Modell: " + _settings.ModelId));
                    AppendMessage("System: ", "Einstellungen gespeichert.", SystemBrushColor);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "LM Studio AI Assistant – Einstellungen", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }
}
