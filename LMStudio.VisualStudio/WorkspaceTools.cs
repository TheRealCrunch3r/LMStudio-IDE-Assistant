// ---------------------------------------------------------------------------
// WorkspaceTools.cs – File-tools layer for the LM Studio AI Assistant.
//
// Provides five workspace-scoped tools (list_dir, read_file, search_files,
// apply_edit, write_file) that the local model can invoke through a simple
// JSON tool-call protocol embedded in its chat replies:
//
//     ```tool
//     {"name": "read_file", "arguments": {"path": "src\Program.cs"}}
//     ```
//
// SAFETY MODEL (hard guard, no exceptions):
//   * Every path is resolved against the currently open solution folder.
//   * Absolute or relative paths that escape that root are REJECTED before
//     any filesystem access happens (containment check on the normalized
//     full path). Nothing outside the workspace is ever read or written.
//
// Note: tool execution must NOT block the VS UI thread – ChatWindow wraps both
// ResolveWorkspaceRoot() (DTE service lookup) and RunAsync() in Task.Run.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace LMStudio.VisualStudio
{
    /// <summary>A single tool invocation extracted from a model reply.</summary>
    public sealed class WorkspaceToolCall
    {
        public string Name { get; set; } = "";
        public Dictionary<string, JsonElement> Arguments { get; set; } = new Dictionary<string, JsonElement>();

        /// <summary>Set when the tool block could not be parsed (JSON error); see ParseError.</summary>
        public bool HasParseError { get; set; }
        /// <summary>Human-readable parse problem (fed back to the model verbatim).</summary>
        public string ParseError { get; set; } = "";

        public override string ToString() => Name + " (" + string.Join(", ", Arguments.Select(kv => kv.Key)) + ")";
    }

    /// <summary>Outcome of executing one tool call.</summary>
    public sealed class ToolExecutionResult
    {
        public string ToolName { get; set; } = "";
        public bool Success { get; set; }
        /// <summary>Short single-line summary for the UI log (e.g. "read_file: src\Program.cs – 120 lines").</summary>
        public string Summary { get; set; } = "";
        /// <summary>FULL text that is fed back to the model in the next request.</summary>
        public string Output { get; set; } = "";
    }

    /// <summary>Raised by internal helpers when a path escapes the workspace root.</summary>
    internal sealed class WorkspaceGuardException : Exception
    {
        public WorkspaceGuardException(string message) : base(message) { }
    }

    /// <summary>Static, thread-safe implementation of the five file tools + tool-call protocol.</summary>
    public static class WorkspaceTools
    {
        // ------------------------- tunables (kept conservative for local models) -------------------------
        private const int MaxResultChars = 12000;   // max chars fed back to the model per tool call
        private const int ReadMaxLines = 400;       // read_file: line cap
        private const int ListMaxEntries = 300;     // list_dir: entry cap (one directory level)
        private const int SearchMaxMatches = 200;   // search_files: match cap
        private const long MaxSearchFileSizeBytes = 1L * 1024 * 1024; // skip files > 1 MB in search

        /// <summary>System prompt fragment describing the available tools and the calling convention.
        /// Appended to the conversation by ChatWindow before every model request.</summary>
        public static string SystemPrompt =>
            "You are an AI assistant running inside Visual Studio, connected to a local LM Studio server (no external cloud).\n" +
            "You have file tools that operate ONLY inside the currently open solution folder. To use one, include a fenced code block with language tag 'tool' containing strict JSON:\n```tool\n{\"name\": \"TOOL_NAME\", \"arguments\": {...}}\n```\n" +
            "Rules:\n" +
            "- One tool call per block; you may output several blocks in one reply (they run in order).\n" +
            "- Paths may be relative to the solution folder or absolute, but must stay inside it.\n" +
            "- After each execution you receive a line 'TOOL_RESULT <name>: ...' containing the full result. Base your final answer on those results.\n" +
            "- If no tool is needed, reply with plain text (no tool block).\n\n" +
            "Available tools:\n" +
            "1) list_dir: {\"path\": \"<dir>\"} – lists one directory level as [DIR]/[FILE] entries.\n" +
            "2) read_file: {\"path\": \"<file>\"} – returns the file content with line numbers (max 400 lines, then truncated).\n" +
            "3) search_files: {\"query\": \"<text>\", \"directory\": \"<dir, optional>\"} – case-insensitive fixed-text search across source files; matches shown as 'file:line: text'.\n" +
            "4) apply_edit: {\"path\": \"<file>\", \"old_text\": \"...\", \"new_text\": \"...\"} – replaces old_text EXACTLY once (verbatim, whitespace included). Fails if it occurs 0 or more than 1 time.\n" +
            "5) write_file: {\"path\": \"<file>\", \"content\": \"...\"} – overwrites the file completely (creates directories as needed).\n\n" +
            "Be precise with paths and quotes; invalid JSON is returned to you unchanged so you can fix it.";

        // ------------------------- tool-call extraction (response parsing) -------------------------

        private static readonly Regex ToolBlockRegex = new Regex(
            @"```tool\s*\r?\n([\s\S]*?)\r?\n?```\s*(?:\r?\n|$)",
            RegexOptions.Compiled);

        /// <summary>Extracts all tool calls from a model reply (empty list when the reply is plain text).</summary>
        public static List<WorkspaceToolCall> ExtractToolCalls(string reply)
        {
            var calls = new List<WorkspaceToolCall>();
            if (string.IsNullOrEmpty(reply)) return calls;

            foreach (Match m in ToolBlockRegex.Matches(reply))
            {
                string block = m.Groups[1].Value.Trim();
                var call = ParseOne(block);
                if (call != null) calls.Add(call);
            }
            return calls;
        }

        /// <summary>Parses one JSON tool block. Never throws – parse problems become a special call.</summary>
        private static WorkspaceToolCall ParseOne(string jsonBlock)
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(jsonBlock);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return new WorkspaceToolCall { Name = "?invalid-json?", HasParseError = true, ParseError = "JSON parse error: " + ex.Message };
            }

            var call = new WorkspaceToolCall();
            if (root.ValueKind != JsonValueKind.Object)
            {
                call.HasParseError = true;
                call.ParseError = "Tool block must be a JSON object like {\"name\": \"...\", \"arguments\": {...}}.";
                return call;
            }

            if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            {
                call.HasParseError = true;
                call.ParseError = "Missing string field 'name' in tool block.";
                return call;
            }
            call.Name = (nameEl.GetString() ?? "").Trim().ToLowerInvariant();

            if (root.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in args.EnumerateObject())
                    call.Arguments[prop.Name] = prop.Value.Clone();
            }
            return call;
        }

        // ------------------------- argument accessors -------------------------

        private static string GetStringArg(WorkspaceToolCall call, string key)
        {
            if (call.Arguments.TryGetValue(key, out var el))
                return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.GetRawText();
            return "";
        }

        // ------------------------- workspace root resolution (UI thread only!) -------------------------

        /// <summary>Resolves the folder of the currently open .sln via the DTE.
        /// MUST be called on the VS UI thread (service lookup). Pattern per Microsoft docs:
        /// "Walkthrough: Access the DTE object from an editor extension".</summary>
        public static string ResolveWorkspaceRoot()
        {
            var dte = ServiceProviderGlobal.GetService(typeof(DTE)) as DTE;
            if (dte != null && dte.Solution != null && !string.IsNullOrWhiteSpace(dte.Solution.FullName))
                return Path.GetDirectoryName(Path.GetFullPath(dte.Solution.FullName));

            throw new InvalidOperationException(
                "No solution is open in Visual Studio, so the file tools are disabled. Open a solution and try again.");
        }

        // ------------------------- execution dispatch (safe on any thread) -------------------------

        /// <summary>Executes one tool call against the given workspace root. Never throws: failures become results.</summary>
        public static Task<ToolExecutionResult> RunAsync(WorkspaceToolCall call, string rootDir, CancellationToken ct)
            => Task.Run(() => Execute(call, rootDir), ct);

        private static ToolExecutionResult Execute(WorkspaceToolCall call, string rootDir)
        {
            var result = new ToolExecutionResult { ToolName = call.Name };
            try
            {
                if (call.HasParseError)
                {
                    result.Success = false;
                    result.Summary = "tool parse error";
                    result.Output = "TOOL_RESULT <parse-error>: Your tool block was invalid. Error: " + call.ParseError +
                                    " Please reply with a corrected ```tool``` block.";
                    return result;
                }

                switch (call.Name)
                {
                    case "list_dir":
                        ExecuteListDir(GetStringArg(call, "path"), rootDir, result);
                        break;
                    case "read_file":
                        ExecuteReadFile(GetStringArg(call, "path"), rootDir, result);
                        break;
                    case "search_files":
                        ExecuteSearchFiles(GetStringArg(call, "query"), GetStringArg(call, "directory"), rootDir, result);
                        break;
                    case "apply_edit":
                        ExecuteApplyEdit(GetStringArg(call, "path"), GetStringArg(call, "old_text"), GetStringArg(call, "new_text"), rootDir, result);
                        break;
                    case "write_file":
                        ExecuteWriteFile(GetStringArg(call, "path"), GetStringArg(call, "content"), rootDir, result);
                        break;
                    default:
                        Fail(result, "Unknown tool '" + call.Name + "'. Available tools: list_dir, read_file, search_files, apply_edit, write_file.");
                        return result;
                }

                if (result.Output.Length > MaxResultChars)
                {
                    int cut = Math.Min(MaxResultChars - 60, result.Output.LastIndexOf('\n', Math.Max(0, MaxResultChars - 1)));
                    result.Output = result.Output.Substring(0, Math.Max(cut, 200)) + "\n[... output truncated ...]";
                }
            }
            catch (WorkspaceGuardException ex)
            {
                Fail(result, "BLOCKED – path outside the workspace root: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Fail(result, "Access denied by OS: " + ex.Message);
            }
            catch (Exception ex) // last-resort guard: model must always receive a machine-readable result
            {
                Fail(result, ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        private static void Fail(ToolExecutionResult r, string message)
        {
            r.Success = false;
            r.Summary = r.ToolName + " failed";
            r.Output = "TOOL_RESULT " + r.ToolName + ": ERROR – " + message;
        }

        // ------------------------- hard path guard -------------------------

        /// <summary>Resolves userPath (relative or absolute) and VERIFIES it stays inside rootDir. Throws otherwise.</summary>
        private static string GuardToRoot(string userPath, string rootDir)
        {
            if (string.IsNullOrWhiteSpace(userPath))
                throw new WorkspaceGuardException("empty path argument.");

            var full = Path.GetFullPath(Path.IsPathRooted(userPath.Trim()) ? userPath.Trim() : Path.Combine(rootDir, userPath.Trim()));
            var rootFull = Path.GetFullPath(rootDir);
            string rootPrefix = rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar) ? rootFull : rootFull + System.IO.Path.DirectorySeparatorChar;

            if (!string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceGuardException("'" + userPath.Trim() + "' is outside the solution folder ('" + rootDir + "').");

            return full;
        }

        // ------------------------- tool implementations -------------------------

        private static void ExecuteListDir(string userPath, string rootDir, ToolExecutionResult result)
        {
            var dir = GuardToRoot(userPath, rootDir);
            if (!Directory.Exists(dir)) { Fail(result, "directory not found: " + Relative(rootDir, dir)); return; }

            var sb = new StringBuilder();
            int count = 0;
            foreach (var sub in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (count++ < ListMaxEntries) sb.Append("[DIR]   ").Append(System.IO.Path.GetFileName(sub)).Append('\n');
            }
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (count++ < ListMaxEntries) sb.Append("[FILE] ").Append(System.IO.Path.GetFileName(f)).Append("  (").Append(FileSizeShort(new FileInfo(f).Length)).Append(")\n");
            }

            result.Success = true;
            result.Summary = "list_dir: " + Relative(rootDir, dir) + " – " + count + " entries";
            result.Output = "TOOL_RESULT list_dir:\n" + sb.ToString() + (count > ListMaxEntries ? "[... listing truncated ...]\n" : "");
        }

        private static void ExecuteReadFile(string userPath, string rootDir, ToolExecutionResult result)
        {
            var file = GuardToRoot(userPath, rootDir);
            if (!File.Exists(file)) { Fail(result, "file not found: " + Relative(rootDir, file)); return; }

            string text = File.ReadAllText(file); // auto-detects BOM/encoding
            var lines = text.Replace("\r\n", "\n").Split('\n');

            var sb = new StringBuilder();
            int shown = Math.Min(lines.Length, ReadMaxLines);
            for (int i = 0; i < shown; i++)
                sb.Append(i + 1).Append("  ").Append(lines[i]).Append('\n');

            result.Success = true;
            result.Summary = "read_file: " + Relative(rootDir, file) + " – " + lines.Length + (lines.Length > ReadMaxLines ? " lines (showing first " + ReadMaxLines + ")" : " lines");
            result.Output = "TOOL_RESULT read_file:\n" + sb.ToString() +
                            (lines.Length > shown ? "[... truncated: showing first " + shown + " of " + lines.Length + " lines ...]\n" : "");
        }

        private static void ExecuteSearchFiles(string query, string userDir, string rootDir, ToolExecutionResult result)
        {
            if (string.IsNullOrEmpty(query)) { Fail(result, "empty 'query' argument."); return; }
            var dir = GuardToRoot(string.IsNullOrWhiteSpace(userDir) ? "." : userDir, rootDir);
            if (!Directory.Exists(dir)) { Fail(result, "directory not found: " + Relative(rootDir, dir)); return; }

            var matches = new List<string>();
            SearchRecursive(dir, dir, query, matches);

            result.Success = true;
            result.Summary = "search_files: '" + TruncateText(query, 40) + "' – " + matches.Count + (matches.Count >= SearchMaxMatches ? "+" : "") + " match(es)";
            var sb2 = new StringBuilder();
            foreach (var line in matches) sb2.Append(line).Append('\n');
            result.Output = "TOOL_RESULT search_files:\n" + sb2.ToString() +
                            (matches.Count >= SearchMaxMatches ? "[... more matches truncated ...]\n" : "");
        }

        private static readonly HashSet<string> SearchSkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "bin", "obj", "node_modules", ".vs", "packages", "dist", "build", ".idea"
        };

        private static void SearchRecursive(string baseDir, string dir, string query, List<string> matches)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (matches.Count >= SearchMaxMatches) return;
                    var name = System.IO.Path.GetFileName(sub);
                    if (!SearchSkipDirs.Contains(name)) SearchRecursive(baseDir, sub, query, matches);
                }
            }
            catch (Exception) { /* unreadable directory – skip silently */ }

            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (matches.Count >= SearchMaxMatches) return;
                    var info = new FileInfo(f);
                    if (info.Length > MaxSearchFileSizeBytes || IsLikelyBinary(info.Name)) continue;

                    string text;
                    try { text = File.ReadAllText(f); } catch { continue; } // binary or locked file

                    // Line-oriented scan (simple + robust for code files):
                    foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    {
                        if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches.Add(Relative(baseDir, f) + ": " + TruncateText(line.Trim(), 200));
                            if (matches.Count >= SearchMaxMatches) return;
                        }
                    }
                }
            }
            catch (Exception) { /* unreadable file – skip silently */ }
        }

        private static void ExecuteApplyEdit(string userPath, string oldText, string newText, string rootDir, ToolExecutionResult result)
        {
            var file = GuardToRoot(userPath, rootDir);
            if (!File.Exists(file)) { Fail(result, "file not found: " + Relative(rootDir, file)); return; }
            if (oldText == null || oldText.Length == 0) { Fail(result, "'old_text' must be non-empty."); return; }

            string content = File.ReadAllText(file);
            int occurrences = CountOccurrences(content, oldText);

            switch (occurrences)
            {
                case 0:
                    Fail(result, "old_text was not found verbatim in the file. Re-read the file and copy the exact text (including whitespace/indentation).");
                    return;
                case > 1:
                    Fail(result, "old_text occurs " + occurrences + " times – it must be unique. Extend old_text with surrounding context lines to make it unique.");
                    return;
                default: // exactly 1 → safe single replacement
                    content = ReplaceOnce(content, oldText, newText ?? "");
                    File.WriteAllText(file, content); // preserves original line endings (only the replaced span changes)
                    result.Success = true;
                    result.Summary = "apply_edit: " + Relative(rootDir, file) + " – replaced 1 block (" + (newText?.Length ?? 0) + " chars written)";
                    result.Output = "TOOL_RESULT apply_edit: OK – applied exactly one replacement in '" + Relative(rootDir, file) + "'.\n" +
                                    "Start of the replaced block:\n" + TruncateText((newText ?? "").Replace("\r\n", "\n"), 300);
                    return;
            }
        }

        private static void ExecuteWriteFile(string userPath, string content, string rootDir, ToolExecutionResult result)
        {
            var file = GuardToRoot(userPath, rootDir);
            bool existed = File.Exists(file);
            var dirName = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);

            File.WriteAllText(file, content ?? "");
            result.Success = true;
            result.Summary = (existed ? "write_file: updated " : "write_file: created ") + Relative(rootDir, file) + " (" + ((content ?? "").Length / 1024) + " KB)";
            result.Output = "TOOL_RESULT write_file: OK – " + (existed ? "overwrote" : "created") + " '" + Relative(rootDir, file) + "' with " + (content?.Length ?? 0) + " characters.";
        }

        // ------------------------- small helpers -------------------------

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string ReplaceOnce(string content, string oldText, string newText)
        {
            int idx = content.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0) return content;
            return content.Substring(0, idx) + newText + content.Substring(idx + oldText.Length);
        }

        private static bool IsLikelyBinary(string fileName)
        {
            var ext = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant() ?? "";
            switch (ext)
            {
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".ico": case ".icns":
                case ".pdf": case ".zip": case ".rar": case ".7z": case ".gz": case ".dll": case ".exe":
                case ".pdb": case ".vsdx": case ".docx": case ".xlsx": case ".pptx": case ".db": case ".md5":
                    return true;
            }
            // Formats without a recognizable extension are treated as text and skipped on read errors.
            return false;
        }

        private static string Relative(string rootDir, string fullPath)
        {
            var root = Path.GetFullPath(rootDir).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (fullPath.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "." + fullPath.Substring(root.Length).Replace('\\', '/');
            return fullPath.Replace('\\', '/');
        }

        private static string FileSizeShort(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return Math.Round(bytes / 1024.0, 1).ToString("0.#") + " KB";
            return Math.Round(bytes / (1024.0 * 1024.0), 1).ToString("0.#") + " MB";
        }

        private static string TruncateText(string s, int max)
            => s == null || s.Length <= max ? (s ?? "") : s.Substring(0, max) + "…";
    }
}
