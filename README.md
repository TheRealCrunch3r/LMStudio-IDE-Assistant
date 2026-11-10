# LM Studio AI Assistant – Visual Studio VSIX-Erweiterung

> **Platzhalter / Initial-Commit** — Projektsetup in Arbeit.

## Zweck
Eigene C#/.NET-VSIX-Erweiterung für die **Voll-IDE Visual Studio (2025/2022)**, die als Chat-Assistent über den **lokalen LM Studio Server** (`http://localhost:1234`) kommuniziert. Kein externer API-Key, keine Cloud — alles lokal auf dem eigenen Rechner.

## Status
- [x] Projektordner initialisiert (Git)
- [x] VSIX-Scaffolding + Chat-UI + Settings (.csproj, vsixmanifest, Source.ExtensionPackage.cs, ChatWindow, SettingsPage, LmStudioClient, SettingsStore) — *Build-Verifikation ausstehend*
- [ ] Build in Visual Studio 2026 (v18; TargetFramework net10.0-windows) + F5 im Extension Development Host
- [ ] End-to-End-Test gegen lokalen LM Studio Server (`http://localhost:1234`)

## Entwicklung / Build
```bash
# Voraussetzungen auf der Build-Maschine (verifiziert am 10.11.2026):
#   - Visual Studio Professional 2026 (v18.8) mit Workload ".NET-Desktop + VS Extensibility"
#   - .NET SDK 10.x, Microsoft.WindowsDesktop.App Runtime 10
# Paketversionen (nuget.org-verifiziert): Microsoft.VisualStudio.SDK 17.14.40265, Microsoft.VSSDK.BuildTools 18.9.820

LMStudio-IDE-Assistant> dotnet build LMStudio-IDE-Assistant.sln -c Debug
# … oder: Lösung in VS öffnen → Build → F5 (Extension Development Host)
```
**Öffnen des Chat-Fensters (v0.1):** View-Menü → *Other Windows* → „LM Studio AI Assistant".

## Spezifikation
→ siehe [`integration_vs_assistant.md`](./integration_vs_assistant.md)
