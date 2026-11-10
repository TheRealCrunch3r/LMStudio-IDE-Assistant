// ---------------------------------------------------------------------------
// Source.ExtensionPackage.cs – VS-Package der Extension.
//
// GUIDs (müssen konsistent bleiben!):
//   PackageGuidString  == source.extension.vsixmanifest InstallationIdentity@Id
//   ToolWindow-GUID    == [ProvideToolWindow] + [Guid] auf ChatToolWindowPane
//
// Öffnen des Fensters in v0.1: View-Menü -> "Other Windows" (VS listet alle per
// [ProvideToolWindow] registrierten Panes automatisch dort; ein eigenes Menükommando
// per .vsct folgt in v0.2 – bewusst kein erfundener Gruppen-GUID im Code).
// ---------------------------------------------------------------------------
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace LMStudio.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("LM Studio AI Assistant", "Local-first KI-Chat-Assistent für Visual Studio (lokaler LM Studio Server).", "1.0")]
    // Dockt das Chat-Fenster initial nach rechts, tabbierbar – wie Solution Explorer & Co.
    [ProvideToolWindow(typeof(ChatToolWindowPane), Style = VsDockStyle.Tabbed, DockedWidth = 360, Orientation = ToolWindowOrientation.Right)]
    [Guid(PackageIdentifiers.PackageGuidString)]
    public sealed class LmStudioAssistantPackage : AsyncPackage
    {
        /// <summary>Ident-GUID – identisch mit source.extension.vsixmanifest (InstallationIdentity@Id).</summary>
        internal const string PackageGuidString = "4C7A9B3E-8D2F-4E61-B5A2-7D9C1E0F3A64";

        /// <summary>Für AsyncPackage: Titel des ToolWindows (wird in der Fensterkopfzeile angezeigt).</summary>
        protected override string GetToolWindowTitle(Type toolWindowType, int id)
            => toolWindowType == typeof(ChatToolWindowPane) ? ChatToolWindowPane.ToolWindowTitle : base.GetToolWindowTitle(toolWindowType, id);

        /// <summary>Kein schweres Anlegen bei Startup – ChatView baut sich lazy beim ersten Öffnen.</summary>
        protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress progress)
        {
            // Background-Loading: auf UI-Thread wechseln ist hier NICHT nötig (nichts zu rendern),
            // aber wir markieren die Initialisierung sauber als abgeschlossen.
            await JoinableTaskFactory.RunAsync(() => { });
        }
    }

    /// <summary>
    /// ToolWindowPane-Wrapper: kapselt die WPF UserControl <see cref="ChatWindow"/> als VS-ToolWindow.
    /// Der VS-Fensterhost rendert den Content über den integrierten WpfControlAdapter –
    /// die View bleibt eine normale, isoliert testbare WPF UserControl.
    /// </summary>
    [Guid(PackageIdentifiers.ChatToolWindowGuidString)]
    public sealed class ChatToolWindowPane : ToolWindowPane
    {
        internal const string ToolWindowTitle = "LM Studio AI Assistant";

        public ChatToolWindowPane() : base(null)
        {
            Caption = ToolWindowTitle;
        }

        /// <summary>Vom VS-Host aufgerufen, sobald der Window-Inhalt benötigt wird.</summary>
        protected override void EnsureContentCreated()
        {
            try
            {
                Content = new ChatWindow();
            }
            catch (Exception ex)
            {
                // UI-Fehler nie unhandled im VS-Host lassen: stattdessen Fehlerscreen zeigen.
                System.Diagnostics.Debug.WriteLine("[LMStudioAssistant] ChatView-Init fehlgeschlagen: " + ex);
                Content = BuildErrorPanel(ex.Message);
            }
        }

        private static System.Windows.FrameworkElement BuildErrorPanel(string message)
        {
            var text = new System.Windows.Controls.TextBlock
            {
                Text = "Fehler beim Initialisieren des Chat-Paneels:\n\n" + message,
                Margin = new System.Windows.Thickness(12),
                Foreground = System.Windows.Media.Brushes.Red,
                TextWrapping = System.Windows.TextWrapping.Wrap
            };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(text);
            return panel;
        }
    }

    /// <summary>Zentrale GUID-Sammlung (single source of truth).</summary>
    internal static class PackageIdentifiers
    {
        public const string PackageGuidString = "4C7A9B3E-8D2F-4E61-B5A2-7D9C1E0F3A64";
        public const string ChatToolWindowGuidString = "6E8B1F02-4D5C-4A73-A9B8-D2C3E4F5A607";
    }
}
