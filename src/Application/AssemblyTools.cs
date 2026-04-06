/*
    Copyright (C) 2026 @chichicaste

    This file is part of dnSpy MCP Server module. 

    dnSpy MCP Server is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy MCP Server is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy MCP Server.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application
{
    /// <summary>
    /// Assembly-focused utilities extracted from McpTools.
    /// Provides: list_assemblies, get_assembly_info, list_types, list_native_modules,
    /// scan_pe_strings, and load_assembly.
    /// </summary>
    [Export(typeof(AssemblyTools))]
    public sealed class AssemblyTools
    {
        readonly IDocumentTreeView documentTreeView;
        readonly IDsDocumentService documentService;
        readonly Lazy<DbgManager> dbgManager;
        readonly Lazy<IDocumentTabService> documentTabService;

        [ImportingConstructor]
        public AssemblyTools(
            IDocumentTreeView documentTreeView,
            IDsDocumentService documentService,
            Lazy<DbgManager> dbgManager,
            Lazy<IDocumentTabService> documentTabService)
        {
            this.documentTreeView = documentTreeView;
            this.documentService = documentService;
            this.dbgManager = dbgManager;
            this.documentTabService = documentTabService;
        }

        public CallToolResult ListAssemblies()
        {
            var assemblies = LoadedDocumentsHelper.GetDocumentsSnapshot(documentService)
                .Where(d => d.AssemblyDef != null)
                .Select(doc =>
                {
                    var a = doc.AssemblyDef!;
                    return new
                    {
                        Name = a.Name.String,
                        Version = a.Version?.ToString() ?? "N/A",
                        FullName = a.FullName,
                        Culture = string.IsNullOrEmpty(a.Culture) ? "neutral" : a.Culture.String,
                        PublicKeyToken = a.PublicKeyToken?.ToString() ?? "null",
                        FilePath = doc.Filename ?? ""
                    };
                })
                .ToList();

            var result = JsonSerializer.Serialize(assemblies, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        public CallToolResult GetAssemblyInfo(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var modules = assembly.Modules.Select(m => new
            {
                Name = m.Name.String,
                Kind = m.Kind.ToString(),
                Architecture = m.Machine.ToString(),
                RuntimeVersion = m.RuntimeVersion
            }).ToList();

            var allNamespaces = assembly.Modules
                .SelectMany(m => m.Types)
                .Select(t => t.Namespace.String)
                .Distinct()
                .OrderBy(ns => ns)
                .ToList();

            var namespacesToReturn = allNamespaces.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allNamespaces.Count;

            var info = new Dictionary<string, object>
            {
                ["Name"] = assembly.Name.String,
                ["Version"] = assembly.Version?.ToString() ?? "N/A",
                ["FullName"] = assembly.FullName,
                ["Culture"] = string.IsNullOrEmpty(assembly.Culture) ? "neutral" : assembly.Culture.String,
                ["PublicKeyToken"] = assembly.PublicKeyToken?.ToString() ?? "null",
                ["Modules"] = modules,
                ["Namespaces"] = namespacesToReturn,
                ["NamespacesTotalCount"] = allNamespaces.Count,
                ["NamespacesReturnedCount"] = namespacesToReturn.Count,
                ["TypeCount"] = assembly.Modules.Sum(m => m.Types.Count)
            };

            if (hasMore)
            {
                info["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        public CallToolResult ListTypes(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? namespaceFilter = null;
            if (arguments.TryGetValue("namespace", out var nsObj))
                namespaceFilter = nsObj.ToString();

            string? namePattern = null;
            if (arguments.TryGetValue("name_pattern", out var npObj))
                namePattern = npObj?.ToString();

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            System.Text.RegularExpressions.Regex? nameRegex = null;
            if (!string.IsNullOrEmpty(namePattern))
                nameRegex = BuildPatternRegex(namePattern!);

            var types = assembly.Modules
                .SelectMany(m => m.Types)
                .Where(t => {
                    if (!string.IsNullOrEmpty(namespaceFilter) &&
                        !t.Namespace.String.Equals(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (nameRegex != null)
                        return nameRegex.IsMatch(t.Name.String) || nameRegex.IsMatch(t.FullName);
                    return true;
                })
                .Select(t => new
                {
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic,
                    IsClass = t.IsClass,
                    IsInterface = t.IsInterface,
                    IsEnum = t.IsEnum,
                    IsValueType = t.IsValueType,
                    IsAbstract = t.IsAbstract,
                    IsSealed = t.IsSealed,
                    BaseType = t.BaseType?.FullName ?? "None"
                })
                .ToList();

            return CreatePaginatedResponse(types, offset, pageSize);
        }

        public CallToolResult ListNativeModules(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var filePath = arguments.TryGetValue("file_path", out var fpObj) ? fpObj?.ToString() : null;
            var assembly = !string.IsNullOrWhiteSpace(filePath)
                ? FindAssemblyByFilePath(filePath!)
                : FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var modules = new Dictionary<string, HashSet<object>>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in assembly.Modules.SelectMany(m => m.Types))
            {
                foreach (var method in type.Methods)
                {
                    try
                    {
                        if (method.IsPinvokeImpl && method.ImplMap != null)
                        {
                            var dllName = method.ImplMap.Module?.Name ?? string.Empty;
                            if (!string.IsNullOrEmpty(dllName))
                            {
                                if (!modules.TryGetValue(dllName, out var set))
                                {
                                    set = new HashSet<object>();
                                    modules[dllName] = set;
                                }

                                set.Add(new { Type = type.FullName, Method = method.Name.String });
                            }
                        }

                        foreach (var ca in method.CustomAttributes)
                        {
                            var at = ca.AttributeType.FullName;
                            if (string.IsNullOrEmpty(at) || !at.EndsWith("DllImportAttribute", StringComparison.Ordinal))
                                continue;

                            var dllName = ca.ConstructorArguments.Count > 0 ? ca.ConstructorArguments[0].Value?.ToString() ?? string.Empty : string.Empty;
                            if (string.IsNullOrEmpty(dllName))
                                continue;

                            if (!modules.TryGetValue(dllName, out var set))
                            {
                                set = new HashSet<object>();
                                modules[dllName] = set;
                            }

                            set.Add(new { Type = type.FullName, Method = method.Name.String });
                        }
                    }
                    catch { /* tolerate metadata issues */ }
                }
            }

            var list = modules.Select(kvp => new
            {
                ModuleName = kvp.Key,
                PathHint = string.Empty,
                ImportedBy = kvp.Value.ToList()
            }).ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
        }

        /// <summary>
        /// Scans the raw PE file bytes for printable ASCII and UTF-16 strings.
        /// Useful for finding URLs, keys, and other plaintext data in obfuscated assemblies.
        /// </summary>
        public CallToolResult ScanPeStrings(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("assembly_name or file_path is required");

            string? filePath = null;

            // Prefer direct file_path if provided — avoids the ambiguity where two loaded
            // assemblies share the same internal name (e.g. original packed + unpacked copy).
            if (arguments.TryGetValue("file_path", out var fpObj) && fpObj?.ToString() is string fpStr && !string.IsNullOrEmpty(fpStr))
            {
                if (!System.IO.File.Exists(fpStr))
                    throw new ArgumentException($"File not found: {fpStr}");
                filePath = fpStr;
            }
            else
            {
                // Fall back to assembly name lookup
                if (!arguments.TryGetValue("assembly_name", out var asmObj) || asmObj == null)
                    throw new ArgumentException("assembly_name is required (or provide file_path for direct path access)");

                var assemblyName = asmObj.ToString() ?? string.Empty;

                // When file_path is omitted, prefer the module whose Filename matches the
                // assembly_name hint — this avoids picking the wrong entry when two assemblies
                // share the same internal name (e.g. packed original + unpacked copy).
                var matchingDoc = LoadedDocumentsHelper.GetDocumentsSnapshot(documentService)
                    .Where(d => d.AssemblyDef != null &&
                        d.AssemblyDef.Name.String.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => {
                        // Prefer the document whose file name contains the assembly_name hint
                        var fn = System.IO.Path.GetFileNameWithoutExtension(d.Filename ?? "");
                        return fn.IndexOf(assemblyName, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                    })
                    .FirstOrDefault();

                if (matchingDoc == null)
                    throw new ArgumentException($"Assembly not found: {assemblyName}. If you have multiple files with the same internal name, use file_path instead.");

                filePath = matchingDoc.Filename;
                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                    throw new ArgumentException(
                        $"File not found on disk: {filePath ?? "(null)"}. " +
                        "This can happen with assemblies loaded from memory (PID). " +
                        "Use file_path to specify the file directly.");
            }

            int minLength = 5;
            if (arguments.TryGetValue("min_length", out var minLenObj) &&
                int.TryParse(minLenObj.ToString(), out var ml) && ml > 0)
                minLength = ml;

            bool includeUtf16 = true;
            if (arguments.TryGetValue("include_utf16", out var utf16Obj))
                bool.TryParse(utf16Obj.ToString(), out includeUtf16);

            string? filterPattern = null;
            if (arguments.TryGetValue("filter_pattern", out var fObj))
                filterPattern = fObj.ToString();

            System.Text.RegularExpressions.Regex? filterRx = null;
            if (!string.IsNullOrEmpty(filterPattern))
                filterRx = new System.Text.RegularExpressions.Regex(filterPattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            var bytes = System.IO.File.ReadAllBytes(filePath);
            var found = new List<(string Encoding, string Offset, string Value)>();
            var seen = new HashSet<string>();

            // Scan ASCII strings
            int start = -1;
            for (int i = 0; i <= bytes.Length; i++)
            {
                bool printable = i < bytes.Length && bytes[i] >= 0x20 && bytes[i] < 0x7F;
                if (printable)
                {
                    if (start < 0) start = i;
                }
                else
                {
                    if (start >= 0)
                    {
                        int len = i - start;
                        if (len >= minLength)
                        {
                            var s = Encoding.ASCII.GetString(bytes, start, len);
                            if ((filterRx == null || filterRx.IsMatch(s)) && seen.Add(s))
                                found.Add(("ASCII", $"0x{start:X}", s));
                        }
                        start = -1;
                    }
                }
            }

            // Scan UTF-16 LE strings
            if (includeUtf16)
            {
                start = -1;
                for (int i = 0; i <= bytes.Length - 1; i += 2)
                {
                    bool printable = i + 1 < bytes.Length && bytes[i] >= 0x20 && bytes[i] < 0x7F && bytes[i + 1] == 0x00;
                    if (printable)
                    {
                        if (start < 0) start = i;
                    }
                    else
                    {
                        if (start >= 0)
                        {
                            int len = i - start;
                            if (len / 2 >= minLength)
                            {
                                var s = Encoding.Unicode.GetString(bytes, start, len);
                                if ((filterRx == null || filterRx.IsMatch(s)) && seen.Add(s))
                                    found.Add(("UTF-16", $"0x{start:X}", s));
                            }
                            start = -1;
                        }
                    }
                }
            }

            // Highlight suspicious strings (URLs, IPs, emails, paths)
            var suspicious = new System.Text.RegularExpressions.Regex(
                @"https?://|ftp://|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|@[a-z0-9-]+\.[a-z]{2,}|[A-Z]:\\|/[a-z]+/|[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var allStrings = found.Select(t => new { Encoding = t.Encoding, Offset = t.Offset, Value = t.Value }).ToList();
            var suspiciousStrings = found
                .Where(t => suspicious.IsMatch(t.Value))
                .Select(t => new { Encoding = t.Encoding, Offset = t.Offset, Value = t.Value })
                .ToList();

            var result = JsonSerializer.Serialize(new
            {
                FilePath = filePath,
                FileSize = bytes.Length,
                TotalStrings = found.Count,
                SuspiciousStrings = suspiciousStrings,
                AllStrings = allStrings
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        // ── load_assembly ────────────────────────────────────────────────────────

        /// <summary>
        /// Load an assembly into dnSpy from a file on disk or from a running process by PID.
        /// Arguments:
        ///   file_path  – absolute path to a .NET assembly or memory dump on disk.
        ///   memory_layout – (bool, default false) when true the file is treated as a
        ///                   raw memory-layout dump rather than a normal PE file.
        ///   pid        – PID of the running process (alternative to file_path).
        ///   module_name – name/filename filter for the module to dump (optional with pid).
        ///   process_id – alias for pid.
        /// </summary>
        public CallToolResult LoadAssembly(Dictionary<string, object>? arguments) {
            if (arguments == null)
                throw new ArgumentException("Arguments required: provide 'file_path' or 'pid'");

            // ── Mode 1: load from file ────────────────────────────────────────────
            if (arguments.TryGetValue("file_path", out var fpObj) && fpObj?.ToString() is string filePath && !string.IsNullOrWhiteSpace(filePath)) {
                if (!File.Exists(filePath))
                    throw new ArgumentException($"File not found: {filePath}");

                bool memoryLayout = false;
                if (arguments.TryGetValue("memory_layout", out var mlObj))
                    bool.TryParse(mlObj?.ToString(), out memoryLayout);

                IDsDocument doc;
                string displayName;

                if (memoryLayout) {
                    // Load raw bytes with memory-layout flag so dnlib can parse
                    // sections from VAs instead of file offsets.
                    var bytes = File.ReadAllBytes(filePath);
                    var filename = Path.GetFileName(filePath);
                    var docInfo = DsDocumentInfo.CreateInMemory(() => (bytes, isFileLayout: false), filename);
                    doc = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        documentService.TryGetOrCreate(docInfo, isAutoLoaded: false))
                        ?? throw new InvalidOperationException("Failed to create in-memory document");
                    displayName = doc.AssemblyDef?.Name.String
                        ?? doc.ModuleDef?.Name.String
                        ?? filename;
                }
                else {
                    var docInfo = DsDocumentInfo.CreateDocument(filePath);
                    doc = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        documentService.TryGetOrCreate(docInfo, isAutoLoaded: false))
                        ?? throw new InvalidOperationException("Failed to load document");
                    displayName = doc.AssemblyDef?.Name.String
                        ?? doc.ModuleDef?.Name.String
                        ?? Path.GetFileNameWithoutExtension(filePath);
                }

                var result = JsonSerializer.Serialize(new {
                    Status = "loaded",
                    AssemblyName = displayName,
                    FilePath = filePath,
                    MemoryLayout = memoryLayout,
                    IsAssembly = doc.AssemblyDef != null,
                    Version = doc.AssemblyDef?.Version?.ToString() ?? "N/A"
                }, new JsonSerializerOptions { WriteIndented = true });

                return new CallToolResult {
                    Content = new List<ToolContent> { new ToolContent { Text = result } }
                };
            }

            // ── Mode 2: dump from running process ────────────────────────────────
            int pid = 0;
            if (arguments.TryGetValue("pid", out var pidObj) && pidObj?.ToString() is string pidStr)
                int.TryParse(pidStr, out pid);
            if (pid == 0 && arguments.TryGetValue("process_id", out var pidObj2) && pidObj2?.ToString() is string pidStr2)
                int.TryParse(pidStr2, out pid);

            if (pid == 0)
                throw new ArgumentException("Provide either 'file_path' (load from disk) or 'pid' (dump from running process)");

            string? moduleFilter = null;
            if (arguments.TryGetValue("module_name", out var mnObj))
                moduleFilter = mnObj?.ToString();

            var mgr = dbgManager.Value;
            if (!mgr.IsDebugging)
                throw new InvalidOperationException("No active debug session. Start or attach to a process first, then use this tool.");

            // Find process
            var process = pid > 0
                ? mgr.Processes.FirstOrDefault(p => p.Id == pid)
                : mgr.Processes.FirstOrDefault();
            if (process == null)
                throw new ArgumentException($"Process {pid} not found in active debug session");

            // Find the target .NET module
            DbgModule? target = null;
            foreach (var rt in process.Runtimes) {
                foreach (var mod in rt.Modules) {
                    var name = mod.Name ?? mod.Filename ?? "";
                    if (string.IsNullOrEmpty(moduleFilter) && mod.IsExe) {
                        target = mod; break;
                    }
                    if (!string.IsNullOrEmpty(moduleFilter) &&
                        (Path.GetFileName(name).Equals(moduleFilter, StringComparison.OrdinalIgnoreCase) ||
                         name.IndexOf(moduleFilter, StringComparison.OrdinalIgnoreCase) >= 0)) {
                        target = mod; break;
                    }
                }
                if (target != null) break;
            }
            if (target == null)
                throw new ArgumentException(
                    string.IsNullOrEmpty(moduleFilter)
                        ? "No EXE module found. Pass 'module_name' to specify one."
                        : $"Module '{moduleFilter}' not found in process {process.Id}");

            // Dump module bytes using IDbgDotNetRuntime.GetRawModuleBytes
            byte[]? moduleBytes = null;
            bool isFileLayout = false;

            // Find runtime that owns this module
            DbgRuntime? owningRuntime = null;
            foreach (var rt in process.Runtimes) {
                if (rt.Modules.Any(module => ReferenceEquals(module, target))) { owningRuntime = rt; break; }
            }

            if (owningRuntime?.InternalRuntime is IDbgDotNetRuntime dnRuntime) {
                var rawResult = dnRuntime.GetRawModuleBytes(target);
                if (rawResult.RawBytes != null && rawResult.RawBytes.Length > 0) {
                    moduleBytes = rawResult.RawBytes;
                    isFileLayout = rawResult.IsFileLayout;
                }
            }

            // Fallback: read via process.ReadMemory
            if (moduleBytes == null) {
                if (!target.HasAddress)
                    throw new InvalidOperationException($"Module '{target.Name}' has no address/size; cannot dump");
                moduleBytes = process.ReadMemory(target.Address, (int)target.Size);
                isFileLayout = false;
            }

            if (moduleBytes == null || moduleBytes.Length == 0)
                throw new InvalidOperationException($"Failed to read module bytes for '{target.Name}'");

            var modFilename = Path.GetFileName(target.Filename ?? target.Name ?? "module.dll");
            var inMemDocInfo = DsDocumentInfo.CreateInMemory(() => (moduleBytes, isFileLayout), modFilename);
            var loadedDoc = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                documentService.TryGetOrCreate(inMemDocInfo, isAutoLoaded: false))
                ?? throw new InvalidOperationException("Failed to create in-memory document from process dump");

            var asmName = loadedDoc.AssemblyDef?.Name.String
                ?? loadedDoc.ModuleDef?.Name.String
                ?? modFilename;

            var resultJson = JsonSerializer.Serialize(new {
                Status = "loaded",
                AssemblyName = asmName,
                SourcePid = process.Id,
                ModuleAddress = $"0x{target.Address:X}",
                ModuleSize = moduleBytes.Length,
                IsFileLayout = isFileLayout,
                IsAssembly = loadedDoc.AssemblyDef != null,
                Version = loadedDoc.AssemblyDef?.Version?.ToString() ?? "N/A"
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult {
                Content = new List<ToolContent> { new ToolContent { Text = resultJson } }
            };
        }

        AssemblyDef? FindAssemblyByName(string name)
        {
            return LoadedDocumentsHelper.FindAssembly(documentService, name);
        }

        /// <summary>
        /// Finds an assembly by its loaded file path. Returns null if not found.
        /// Normalizes separators so forward- and back-slashes are treated the same.
        /// Must NOT be called on the UI thread — it will marshal internally.
        /// </summary>
        AssemblyDef? FindAssemblyByFilePath(string filePath)
        {
            return LoadedDocumentsHelper.FindAssembly(documentService, string.Empty, filePath);
        }

        string EncodeCursor(int offset, int pageSize)
        {
            var cursorData = new { offset, pageSize };
            var json = JsonSerializer.Serialize(cursorData);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes);
        }

        static System.Text.RegularExpressions.Regex BuildPatternRegex(string pattern)
        {
            bool isRegex = pattern.IndexOfAny(new[] { '^', '$', '[', '(', '|', '+', '{' }) >= 0;
            if (isRegex)
                return new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var escaped = System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*").Replace(@"\?", ".");
            return new System.Text.RegularExpressions.Regex("^" + escaped + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        (int offset, int pageSize) DecodeCursor(string? cursor)
        {
            const int defaultPageSize = 50;
            if (string.IsNullOrEmpty(cursor))
                return (0, defaultPageSize);

            try
            {
                var bytes = Convert.FromBase64String(cursor);
                var json = Encoding.UTF8.GetString(bytes);
                var cursorData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (cursorData == null)
                    throw new ArgumentException("Invalid cursor: cursor data is null");

                if (!cursorData.TryGetValue("offset", out var offsetObj) || !(offsetObj is JsonElement offsetElem) || !offsetElem.TryGetInt32(out var offset))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'offset' field");

                if (!cursorData.TryGetValue("pageSize", out var pageSizeObj) || !(pageSizeObj is JsonElement pageSizeElem) || !pageSizeElem.TryGetInt32(out var pageSize))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'pageSize' field");

                if (offset < 0)
                    throw new ArgumentException("Invalid cursor: offset cannot be negative");

                if (pageSize <= 0)
                    throw new ArgumentException("Invalid cursor: pageSize must be positive");

                return (offset, pageSize);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid cursor: {ex.Message}");
            }
        }

        CallToolResult CreatePaginatedResponse<T>(List<T> allItems, int offset, int pageSize)
        {
            var itemsToReturn = allItems.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allItems.Count;

            var response = new Dictionary<string, object>
            {
                ["items"] = itemsToReturn,
                ["total_count"] = allItems.Count,
                ["returned_count"] = itemsToReturn.Count
            };

            if (hasMore)
            {
                response["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        /// <summary>
        /// Selects an assembly in the document tree view and opens it in a tab,
        /// making it the "current" assembly for all subsequent MCP operations.
        /// </summary>
        public CallToolResult SelectAssembly(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var nameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = nameObj.ToString() ?? string.Empty;
            var filePath = arguments.TryGetValue("file_path", out var fpObj) ? fpObj?.ToString() : null;

            var document = LoadedDocumentsHelper.FindDocument(documentService, assemblyName, filePath);
            if (document == null)
            {
                if (!string.IsNullOrWhiteSpace(filePath))
                    throw new ArgumentException($"No assembly loaded from path: {filePath}. Use list_assemblies to see FilePath values.");
                throw new ArgumentException($"Assembly not found: {assemblyName}. Use list_assemblies to see loaded assemblies.");
            }

            var assembly = document.AssemblyDef
                ?? throw new ArgumentException($"Loaded document '{document.Filename ?? assemblyName}' is not a managed assembly.");

            bool openedTab = false;
            string? openError = null;
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    documentTabService.Value.FollowReference(document, newTab: false, setFocus: true));
                openedTab = true;
            }
            catch (Exception ex)
            {
                openError = ex.Message;
                try
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        documentTabService.Value.FollowReference(assembly, newTab: false, setFocus: true));
                    openedTab = true;
                    openError = null;
                }
                catch (Exception fallbackEx)
                {
                    openError = fallbackEx.Message;
                }
            }

            bool selected = false;
            string? selectionError = null;
            for (int attempt = 0; attempt < 10 && !selected; attempt++)
            {
                var selectionState = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var node = documentTreeView.FindNode(document) ?? documentTreeView.FindNode(assembly);
                    if (node == null)
                        return false;

                    documentTreeView.TreeView.SelectItems(new[] { node });
                    documentTreeView.TreeView.ScrollIntoView();
                    return true;
                });

                if (selectionState)
                {
                    selected = true;
                    break;
                }

                System.Threading.Thread.Sleep(50);
            }

            if (!selected)
                selectionError = "Tree node not materialized yet; active tab was updated, but tree selection could not be synchronized.";

            var response = new
            {
                Assembly = assembly.FullName,
                FilePath = document.Filename,
                Selected = selected,
                OpenedTab = openedTab,
                Error = selectionError ?? openError
            };

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }) }
                }
            };
        }

        /// <summary>
        /// Closes (removes) a specific assembly from dnSpy by name or file path.
        /// </summary>
        public CallToolResult CloseAssembly(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var nameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = nameObj.ToString() ?? string.Empty;
            string? filePath = arguments.TryGetValue("file_path", out var fpObj) ? fpObj?.ToString() : null;

            var documents = LoadedDocumentsHelper.FindDocuments(documentService, assemblyName, filePath)
                .Where(d => d.AssemblyDef != null)
                .ToList();
            var removed = documents
                .Select(d => d.Filename ?? d.AssemblyDef?.FullName ?? "?")
                .ToList();

            if (removed.Count == 0)
                throw new ArgumentException($"Assembly not found: {assemblyName}. Use list_assemblies to see loaded assemblies.");

            documentService.Remove(documents);

            var result = JsonSerializer.Serialize(new { Removed = removed, Count = removed.Count }, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        /// <summary>
        /// Closes all assemblies currently loaded in dnSpy.
        /// </summary>
        public CallToolResult CloseAllAssemblies()
        {
            var removed = LoadedDocumentsHelper.GetDocumentsSnapshot(documentService)
                .Select(d => d.Filename ?? d.AssemblyDef?.FullName ?? "?")
                .ToList();

            if (removed.Count > 0)
                documentService.Clear();

            var result = JsonSerializer.Serialize(new { Removed = removed, Count = removed.Count }, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }
    }
}
