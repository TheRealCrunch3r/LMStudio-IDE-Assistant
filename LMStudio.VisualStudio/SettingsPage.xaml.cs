// ---------------------------------------------------------------------------
// SettingsPage.xaml.cs – modales Einstellungs-Fenster (Base URL + Modell-ID).
// Ergebnis wird über ResultSettings an die ChatView zurückgereicht.
// ---------------------------------------------------------------------------
using System;
using System.Windows;

namespace LMStudio.VisualStudio
{
    public partial class SettingsPage : Window
    {
        private readonly AssistantSettings _current;

        /// <summary>Gültiges Ergebnis, wenn DialogResult == true.</summary>
        public AssistantSettings ResultSettings { get; private set; }

        public SettingsPage(AssistantSettings current)
        {
            InitializeComponent();
            _current = current ?? new AssistantSettings();
            BaseUrlBox.Text = _current.BaseUrl ?? "http://localhost:1234";
            ModelBox.Text = _current.ModelId ?? "";
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            var baseUrl = (BaseUrlBox.Text ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
            {
                MessageBox.Show(this, "Bitte eine Server-URL eingeben (z. B. http://localhost:1234).", "Eingabe prüfen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Uri-Gültigkeit früh prüfen (AssistantSettings.BaseUri würde sonst beim ersten Request werfen).
            try
            {
                var probe = new Uri(baseUrl, UriKind.Absolute);
                if (probe.Scheme != Uri.UriSchemeHttp && probe.Scheme != Uri.UriSchemeHttps)
                    throw new FormatException("nur http:// oder https:// erlaubt");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Ungültige Server-URL: " + ex.Message, "Eingabe prüfen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultSettings = new AssistantSettings
            {
                BaseUrl = baseUrl,
                ModelId = (ModelBox.Text ?? "").Trim(),
                TimeoutSeconds = _current.TimeoutSeconds // v0.1: nicht editierbar, bleibt beim Default
            };
            DialogResult = true;
        }

        private async void OnTestClick(object sender, RoutedEventArgs e)
        {
            var baseUrl = (BaseUrlBox.Text ?? "http://localhost:1234").Trim().TrimEnd('/');

            TestButton.IsEnabled = false;
            TestResultText.Text = "Verbinde mit " + baseUrl + " …";
            try
            {
                var probeSettings = new AssistantSettings
                {
                    BaseUrl = baseUrl,
                    ModelId = (ModelBox.Text ?? "").Trim(),
                    TimeoutSeconds = 10 // Test muss schnell sein
                };

                var models = await LmStudioClient.GetModelsAsync(probeSettings, System.Threading.CancellationToken.None);
                if (models.Count == 0)
                {
                    SetTestResult("⚠️ Server erreichbar, aber es sind keine Modelle geladen.", false);
                }
                else
                {
                    var list = string.Join(", ", models.ToArray());
                    // Bei sehr langen Listen kürzen:
                    if (list.Length > 200) list = list.Substring(0, 200) + " …";
                    SetTestResult("✅ Verbunden. Geladene Modelle (" + models.Count + "): " + list, true);
                }
            }
            catch (LmStudioServerException ex)
            {
                SetTestResult("❌ " + ex.Message, false);
            }
            catch (Exception ex)
            {
                SetTestResult("❌ Unerwarteter Fehler: " + ex.Message, false);
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        private void SetTestResult(string text, bool ok)
        {
            TestResultText.Text = text;
            TestResultText.Foreground = ok ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Firebrick;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
