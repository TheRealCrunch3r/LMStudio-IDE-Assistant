// ---------------------------------------------------------------------------
// SettingsPage.xaml.cs – modal settings window (base URL + model ID).
// The result is passed back to the chat view via ResultSettings.
// ---------------------------------------------------------------------------
using System;
using System.Windows;

namespace LMStudio.VisualStudio
{
    public partial class SettingsPage : Window
    {
        private readonly AssistantSettings _current;

        /// <summary>Valid result when DialogResult == true.</summary>
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
                MessageBox.Show(this, "Please enter a server URL (e.g. http://localhost:1234).", "Check input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check URI validity early (AssistantSettings.BaseUri would throw on first request otherwise).
            try
            {
                var probe = new Uri(baseUrl, UriKind.Absolute);
                if (probe.Scheme != Uri.UriSchemeHttp && probe.Scheme != Uri.UriSchemeHttps)
                    throw new FormatException("only http:// or https:// allowed");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Invalid server URL: " + ex.Message, "Check input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultSettings = new AssistantSettings
            {
                BaseUrl = baseUrl,
                ModelId = (ModelBox.Text ?? "").Trim(),
                TimeoutSeconds = _current.TimeoutSeconds // v0.1: not editable, stays at default
            };
            DialogResult = true;
        }

        private async void OnTestClick(object sender, RoutedEventArgs e)
        {
            var baseUrl = (BaseUrlBox.Text ?? "http://localhost:1234").Trim().TrimEnd('/');

            TestButton.IsEnabled = false;
            TestResultText.Text = "Connecting to " + baseUrl + " …";
            try
            {
                var probeSettings = new AssistantSettings
                {
                    BaseUrl = baseUrl,
                    ModelId = (ModelBox.Text ?? "").Trim(),
                    TimeoutSeconds = 10 // test must be fast
                };

                var models = await LmStudioClient.GetModelsAsync(probeSettings, System.Threading.CancellationToken.None);
                if (models.Count == 0)
                {
                    SetTestResult("⚠️ Server reachable, but no models loaded.", false);
                }
                else
                {
                    var list = string.Join(", ", models.ToArray());
                    // Truncate very long lists:
                    if (list.Length > 200) list = list.Substring(0, 200) + " …";
                    SetTestResult("✅ Connected. Loaded models (" + models.Count + "): " + list, true);
                }
            }
            catch (LmStudioServerException ex)
            {
                SetTestResult("❌ " + ex.Message, false);
            }
            catch (Exception ex)
            {
                SetTestResult("❌ Unexpected error: " + ex.Message, false);
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
