// ---------------------------------------------------------------------------
// Source.ExtensionPackage.cs – VS package for the extension.
//
// GUIDs (must remain consistent!):
//   PackageGuidString  == source.extension.vsixmanifest InstallationIdentity@Id
//   ToolWindow-GUID    == [ProvideToolWindow] + [Guid] on ChatToolWindowPane
//
// Opening the window in v0.1: View menu -> "Other Windows" (VS lists all
// panes registered via [ProvideToolWindow] there automatically; a custom
// .vsct command follows in v0.2 — intentionally no fabricated group GUID here).
// ---------------------------------------------------------------------------
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace LMStudio.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("LM Studio AI Assistant", "Local-first AI chat assistant for Visual Studio (local LM Studio server).", "1.0")]
    // Dock the chat window to the right by default, tabbed — like Solution Explorer & Co.
    [ProvideToolWindow(typeof(ChatToolWindowPane), Style = VsDockStyle.Tabbed, DockedWidth = 360, Orientation = ToolWindowOrientation.Right)]
    [Guid(PackageIdentifiers.PackageGuidString)]
    public sealed class LmStudioAssistantPackage : AsyncPackage
    {
        /// <summary>Identity GUID — identical with source.extension.vsixmanifest (InstallationIdentity@Id).</summary>
        internal const string PackageGuidString = "4C7A9B3E-8D2F-4E61-B5A2-7D9C1E0F3A64";

        /// <summary>For AsyncPackage: title of the ToolWindow (shown in the window caption bar).</summary>
        protected override string GetToolWindowTitle(Type toolWindowType, int id)
            => toolWindowType == typeof(ChatToolWindowPane) ? ChatToolWindowPane.ToolWindowTitle : base.GetToolWindowTitle(toolWindowType, id);

        /// <summary>No heavy construction at startup — the chat view is built lazily on first open.</summary>
        protected override async Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress progress)
        {
            // Background loading: switching to UI thread not needed here (nothing to render),
            // but we mark initialization cleanly as completed.
            await JoinableTaskFactory.RunAsync(() => { });
        }
    }

    /// <summary>
    /// ToolWindowPane wrapper: wraps the WPF UserControl ChatWindow as a VS tool window.
    /// The VS window host renders the content via the built-in WpfControlAdapter —
    /// the view remains a normal, independently testable WPF UserControl.
    /// </summary>
    [Guid(PackageIdentifiers.ChatToolWindowGuidString)]
    public sealed class ChatToolWindowPane : ToolWindowPane
    {
        internal const string ToolWindowTitle = "LM Studio AI Assistant";

        public ChatToolWindowPane() : base(null)
        {
            Caption = ToolWindowTitle;
        }

        /// <summary>Called by the VS host once window content is needed.</summary>
        protected override void EnsureContentCreated()
        {
            try
            {
                Content = new ChatWindow();
            }
            catch (Exception ex)
            {
                // Never leave UI errors unhandled in the VS host: instead show an error screen.
                System.Diagnostics.Debug.WriteLine("[LMStudioAssistant] ChatView init failed: " + ex);
                Content = BuildErrorPanel(ex.Message);
            }
        }

        private static System.Windows.FrameworkElement BuildErrorPanel(string message)
        {
            var text = new System.Windows.Controls.TextBlock
            {
                Text = "Error initializing the chat panel:\n\n" + message,
                Margin = new System.Windows.Thickness(12),
                Foreground = System.Windows.Media.Brushes.Red,
                TextWrapping = System.Windows.TextWrapping.Wrap
            };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(text);
            return panel;
        }
    }

    /// <summary>Central GUID collection (single source of truth).</summary>
    internal static class PackageIdentifiers
    {
        public const string PackageGuidString = "4C7A9B3E-8D2F-4E61-B5A2-7D9C1E0F3A64";
        public const string ChatToolWindowGuidString = "6E8B1F02-4D5C-4A73-A9B8-D2C3E4F5A607";
    }
}
