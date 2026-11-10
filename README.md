# LM Studio AI Assistant — Visual Studio VSIX Extension

> **Placeholder / Initial Commit** — Project setup in progress.

## Purpose

Own C#/ .NET VSIX extension for the **full IDE Visual Studio (2025/2022)** that communicates as a chat assistant via the **local LM Studio Server** (`http://localhost:1234`). No external API key, no cloud — everything local on your own machine.

## Status
- [x] Project folder initialized (Git)
- [x] VSIX scaffolding + Chat UI + Settings (.csproj, vsixmanifest, Source.ExtensionPackage.cs, ChatWindow, SettingsPage, LmStudioClient, SettingsStore) — *Build verification pending*
- [ ] Build in Visual Studio 2026 (v18; TargetFramework net10.0-windows) + F5 in Extension Development Host
- [ ] End-to-end test against local LM Studio Server (`http://localhost:1234`)

## Development / Build
```bash
# Prerequisites on the build machine (verified 11/10/2026):
#   - Visual Studio Professional 2026 (v18.8) with workload ".NET-Desktop + VS Extensibility"
#   - .NET SDK 10.x, Microsoft.WindowsDesktop.App Runtime 10
# Package versions (verified on nuget.org): Microsoft.VisualStudio.SDK 17.14.40265, Microsoft.VSSDK.BuildTools 18.9.820

LMStudio-IDE-Assistant> dotnet build LMStudio-IDE-Assistant.sln -c Debug
# … or: open solution in VS → Build → F5 (Extension Development Host)
```
**Opening the chat window (v0.1):** View menu → *Other Windows* → "LM Studio AI Assistant".

## Specification
→ see [`integration_vs_assistant.md`](./integration_vs_assistant.md)
