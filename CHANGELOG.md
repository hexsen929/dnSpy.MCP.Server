# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.8.12] - 2026-04-06

### Fixed
- GitHub Actions packaging now falls back to the newest built `dnSpy.MCP.Server.x.dll` when older dnSpyEx tags do not place the target framework name in the output path, fixing the failed `v1.8.11` packaging/release jobs.
- Keeps the `dnSpyEx/dnSpy@v6.5.1` pin from `v1.8.11`, so released binaries still target the official `dnSpy-netframework` host with `dnlib 4.4.0`.

## [1.8.11] - 2026-04-06

### Fixed
- GitHub Actions now pins the dnSpyEx source checkout to `v6.5.1` instead of tracking `master`, so the release build matches the official `dnSpy-netframework` host (`dnlib 4.4.0`) instead of accidentally compiling against newer `master` snapshots that moved to `dnlib 4.5.0`.
- This removes the remaining `dnlib, Version=4.5.0.0` binding mismatch seen by `get_class_sourcecode` / `update_method_sourcecode` and the AgentSmithers alias variants when running inside the stock `dnSpy-netframework` package.

## [1.8.10] - 2026-04-06

### Fixed
- AgentSmithers legacy aliases are now normalized before `tools/call` validation, so callers can use the original names (`Get_Class_Sourcecode`, `Get_Method_SourceCode`, `Get_Function_Opcodes`, `Set_Function_Opcodes`, `Overwrite_Full_Func_Opcodes`, `Update_Method_SourceCode`) without being rejected as unknown tools.
- Added a startup-time `dnlib` compatibility resolver for the net48 plugin so direct-edit tools can reuse the host dnSpy `dnlib` load instead of failing on `dnlib, Version=4.5.0.0` vs `4.4.0.0` binding mismatches under dnSpyEx.

### Changed
- README now clarifies the tool catalog split: 137 tools by default, 143 when hidden-by-default tools are included.

## [1.8.9] - 2026-04-06

### Added
- Optional `listenerMode` config with `httpListener`, `tcpListener`, and `auto`.
- Settings UI can now switch listener backend and restart the MCP server to apply host/port/listener changes.

### Changed
- Added a `tcpListener` backend for `/health` and streamable HTTP `/mcp`, intended to bypass CrossOver/Wine `HttpListener` header/body parsing issues seen with Codex.
- `httpListener` remains available for full legacy SSE compatibility on `/sse` + `/message`.

## [1.8.8] - 2026-04-06

### Fixed
- Disabled `GET /mcp` SSE streaming for the streamable HTTP transport and reverted `/mcp` to POST-driven request/response behavior, matching the simpler pattern used by `ida-pro-mcp`.
- Keeps legacy SSE available on `/sse` + `/message`, while avoiding the long-lived `/mcp` event stream path that was hanging under CrossOver/Wine and causing Claude Code to stall on capability discovery.

## [1.8.7] - 2026-04-06

### Changed
- Default local bind host changed from `localhost` to `127.0.0.1` in `McpConfig`, generated `mcp-config.json`, and the bundled stdio wrapper.
- README examples and client snippets now prefer `http://127.0.0.1:3100/mcp` for local-only setups.
- Removed the localhost dual-prefix listener/fallback logic and restored single-prefix binding based strictly on the configured `host`.
- `tools/list`, `resources/list`, `prompts/list`, and `ping` no longer require an existing streamable HTTP session, while stateful execution requests continue to use session IDs.

### Notes
- Listener startup no longer attempts dual-prefix loopback registration; the configured `host` is bound as-is.

## [1.8.5] - 2026-04-06

## [1.8.4] - 2026-04-06

### Added
- **Protection / malware triage tools** (10 tools — `MalwareAnalysisTools.cs`):
  - `triage_sample` — one-call suspicious assembly triage
  - `get_strings` — managed string extraction from field constants and `ldstr` sites
  - `search_il_pattern` — IL text / opcode-sequence hunting across methods
  - `analyze_static_constructors` — `.cctor` bootstrap analysis
  - `detect_string_encryption` — heuristic string-decryptor ranking
  - `find_byte_arrays` — byte-array/payload/key staging discovery
  - `find_embedded_pes` — embedded PE blob detection in resources and field data
  - `detect_anti_debug` — anti-debugger API / blacklist / bootstrap heuristics
  - `detect_anti_tamper` — anti-tamper / integrity / RVA bootstrap heuristics
  - `get_protection_report` — one-call protection-oriented aggregate report
- **Optional stdio bridge**:
  - added `tools/dnSpy.MCP.StdioProxy` as a standalone console sidecar
  - forwards stdio-framed MCP traffic to the existing local `POST /mcp` endpoint
  - keeps the original HTTP/SSE server transport unchanged

### Fixed
- `localhost` listeners now also register `127.0.0.1`, avoiding `Bad Request - Invalid Hostname` when local clients use loopback IP form instead of hostname.
- The bundled stdio proxy wrapper now defaults to `http://localhost:3100/mcp` instead of `127.0.0.1`.
- If dual-prefix localhost startup fails on a given machine, the server now falls back automatically to a reduced prefix set instead of failing the whole listener startup.

### Documentation
- README now documents the expanded protection / malware analysis tool group and updates the tool count to 143+ on the main branch.
- Architecture docs now include `MalwareAnalysisTools`, the expanded command surface, and the optional stdio sidecar.

---

## [1.8.1] - 2026-04-05

### Added
- **AgentSmithers-style direct editing tools** (6 tools — `AgentCompatibilityTools.cs`):
  - `get_class_sourcecode` — decompile a whole type in one call
  - `get_method_sourcecode` — decompile a single method
  - `get_function_opcodes` — stable IL listing with line indexes and operands for patch workflows
  - `set_function_opcodes` — line-based IL splicing with labels, branch targets, and `switch` targets
  - `overwrite_full_function_opcodes` — full method-body IL replacement
  - `update_method_sourcecode` — compile a replacement method body from C# statements and swap the IL in-memory
- **AgentSmithers legacy aliases** remain wired in `McpTools.cs`:
  - `Get_Class_Sourcecode`
  - `Get_Method_SourceCode`
  - `Get_Function_Opcodes`
  - `Set_Function_Opcodes`
  - `Overwrite_Full_Func_Opcodes`
  - `Update_Method_SourceCode`

### Changed
- `set_function_opcodes` now supports branch and `switch` operands that resolve to:
  - labels introduced in the replacement IL block
  - surviving original instructions via `line:<index>` or `IL_<offset>`
- `update_method_sourcecode` now generates a richer wrapper that includes same-type field/property/event/helper member skeletons so replacement bodies can reference more instance members directly.
- GitHub Actions build/release artifacts are now packaged as **plugin-only bundles** (`dnSpy.MCP.Server.x.dll`, de4dot/Echo dependencies, config, symbols) instead of mirroring the full dnSpy output folder.

### Fixed
- Trimmed unused `ModelContextProtocol` / `Microsoft.Extensions.Hosting` package dependencies from the net48 build path to reduce avoidable runtime dependency churn inside dnSpyEx's AppDomain.
- Documented the required net48 binding-redirect repair path for `System.Text.Json`, `System.Collections.Immutable`, `System.Text.Encodings.Web`, and `System.Memory` when deploying the plugin into a clean dnSpy tree.

### Documentation
- README now documents:
  - the AgentSmithers-compatible editing workflow
  - safe Windows deployment of the plugin-only bundle
  - manual net48 dependency repair steps
  - updated project structure for `AgentCompatibilityTools`, `SourceMapTools`, `NativeRuntimeTools`, `InterceptionTools`, and `McpTools.Schemas`

### Total tools: **133**

---

## [1.7.0] - 2026-02-28

### Added
- **Exception breakpoints** (3 new tools — `DebugTools.cs`):
  - `set_exception_breakpoint` — fire when a specific exception type is thrown (configurable first-chance / second-chance). Uses `DbgExceptionSettingsService.Add/Modify`. Default category: `DotNet`.
  - `remove_exception_breakpoint` — remove an exception breakpoint for a specific type
  - `list_exception_breakpoints` — list all active exception breakpoints with category, type, and chance flags
- **Remote debugging support** — host and port now configurable in `mcp-config.json`:
  - New `host` field (default `"localhost"`). Set to `"0.0.0.0"` to listen on all interfaces — required for remote debugging from a sandbox or VM. Automatically translated to the HttpListener `"+"` wildcard.
  - New `port` field (default `3100`). Previously required rebuilding the extension to change.
  - `McpSettingsImpl` seeds both values from `mcp-config.json` at startup — the JSON file is the single source of truth for network configuration.
- New `de4dotMaxSearchDepth` field in `mcp-config.json` (default `6`). Controls how many directory levels are walked upward when auto-discovering `de4dot.exe`.

### Fixed
- **Reported port** — `GetStatusMessage()` now reports the port the server actually bound to (may differ from `settings.Port` when the preferred port was already in use).
- **`enableRunScript` default** — was incorrectly `true` in `McpConfig`; corrected to `false` (matches the "disable in untrusted environments" intent).
- **Double serialization** — `HandleCallTool` was serializing the already-deserialized `parameters` dict back to JSON and then deserializing again. Now extracts `name` and `arguments` directly from the `JsonElement` values without a round-trip.
- **Emergency disk I/O** — leftover debug `File.AppendAllText(emergencyLog, …)` calls in `TheExtension` (static constructor + `OnEvent`) were writing to disk on every extension lifecycle event. Removed.

### Total tools: **98** (was 95)

---

## [1.6.0] - 2026-02-27

### Added
- **Debug stepping** (5 new tools — `DebugTools.cs`):
  - `step_over` — step over the current statement; blocks until `StepComplete` fires (configurable `timeout_seconds`)
  - `step_into` — step into the called method
  - `step_out` — run until the current method returns to its caller
  - `get_current_location` — read the top-frame execution location (token, IL offset, module name) without stepping
  - `wait_for_pause` — poll at 100 ms intervals until any debugged process enters `Paused` state; useful after `continue_debugger` + breakpoint
- **Runtime expression evaluation** (1 new tool — `MemoryInspectTools.cs`):
  - `eval_expression` — evaluate a C# expression in the context of the current paused stack frame; equivalent to the Watch window. Returns typed value: primitive (`Int32`, `Boolean`, …), string, or object address. Supports func-eval with configurable `func_eval_timeout_seconds`.
- **Live memory patching** (1 new tool — `DumpTools.cs`):
  - `write_process_memory` — write bytes to a process address via `DbgProcess.WriteMemory`. Accepts `bytes_base64` (base64 string) or `hex_bytes` (e.g. `"90 90 C3"`, `"9090C3"`, `"0x90 0x90"`). Useful for hot-patching checks without modifying the binary on disk.
- **Conditional breakpoints** (extends `set_breakpoint`):
  - `set_breakpoint` now accepts an optional `condition` (C# expression string). Condition is evaluated by dnSpy's expression evaluator at each hit; the process only pauses when the result is `true`. Implemented via `DbgCodeBreakpointCondition(IsTrue, expr)`.

### Technical
- `StepImpl` uses `Dispatcher.Invoke` to call `DbgStepper.Step()` on the WPF UI thread, then waits on a `ManualResetEventSlim` for `StepComplete` — avoids deadlock between MCP background thread and the debugger dispatcher.
- `EvalExpression` uses `DbgLanguage.ExpressionEvaluator.Evaluate(evalInfo, expr, DbgEvaluationOptions.Expression, null)` returning `DbgEvaluationResult`. Value is formatted via `FormatDbgValue()` which handles primitives, strings, and object addresses from the base `DbgValue` API.
- `ParseHexBytes` normalises input by stripping `0x`/`0X` prefixes, spaces, commas, and dashes before parsing pairs — net48 compatible (no `String.Replace(string, string, StringComparison)` overload used).

### Total tools: **95** (was 88)

---

## [1.5.0] - 2026-02-27

### Added
- **Window / Dialog management tools** (2 new tools — `WindowTools.cs`):
  - `list_dialogs` — enumerate active dialog/message-box windows in the dnSpy process (Win32 `#32770` class + WPF windows). Returns title, HWND, message text, and button labels for each dialog.
  - `close_dialog` — dismiss a dialog by clicking a named button. Supports EN and ES button names (`ok`/`aceptar`, `yes`/`sí`, `no`, `cancel`/`cancelar`, `retry`/`reintentar`, `ignore`/`omitir`). Falls back to `WM_CLOSE` if no button matches. Target resolved by HWND or defaults to the first active dialog.

### Implementation notes
- `WindowTools` has no dnSpy service dependencies (only P/Invoke + `System.Windows.Application`).
- Uses `EnumWindows` for Win32 dialogs and `Application.Current.Windows` for WPF dialogs.
- `WpfApp = System.Windows.Application` alias used to avoid namespace ambiguity inside `dnSpy.MCP.Server.Application`.

### Total tools: **88** (was 86)

---

## [1.4.0] - 2026-02-26

### Added
- **Process launch and all-in-one unpacking** (3 new tools):
  - `start_debugging` — launch a .NET EXE under the dnSpy debugger with configurable break-kind (`EntryPoint`, `ModuleCctorOrEntryPoint`, `CreateProcess`, `DontBreak`)
  - `attach_to_process` — attach to a running .NET process by PID
  - `unpack_from_memory` — all-in-one ConfuserEx unpacker: launch → pause at EntryPoint → dump + PE-layout fix → stop session
- **Extended memory / PE tools** (5 new tools):
  - `get_pe_sections` — list PE sections (name, VA, size, characteristics) of a debugged module
  - `dump_pe_section` — extract a specific PE section (.text, .data, .rsrc, etc.) from process memory
  - `dump_module_unpacked` — dump a module with memory-to-file layout conversion (proper PE for use in IDA / dnSpy)
  - `dump_memory_to_file` — save a raw memory range to disk (supports up to 256 MB)
  - `get_local_variables` — read local variables and parameters from a paused debug-session stack frame
- **CorDebug IL introspection** (1 new tool):
  - `dump_cordbg_il` — detect ConfuserEx JIT hooks: reads `ICorDebugFunction.ILCode.Address` and reports whether IL lies inside or outside the PE image
- **PE string scanning** (1 new tool):
  - `scan_pe_strings` — scan raw PE bytes for ASCII/UTF-16 strings; useful for extracting URLs, keys, and paths from packed assemblies without a debug session
- **Deobfuscation tools** via integrated de4dot engine (4 new tools):
  - `list_deobfuscators` — list all supported obfuscator types
  - `detect_obfuscator` — heuristically detect which obfuscator was applied to an assembly
  - `deobfuscate_assembly` — deobfuscate with configurable rename/cflow/string options
  - `save_deobfuscated` — return a deobfuscated file as Base64 for direct download
- **de4dot external runner** (1 new tool):
  - `run_de4dot` — invoke `de4dot.exe` as an external process for dynamic string decryption and full de4dot feature set
- **Skills knowledge base** (5 new tools):
  - `list_skills`, `get_skill`, `save_skill`, `search_skills`, `delete_skill` — store and retrieve RE procedures, magic values, crypto keys, and step-by-step workflows in `%APPDATA%\dnSpy\dnSpy.MCPServer\skills\`
- **Roslyn scripting** (1 new tool):
  - `run_script` — execute arbitrary C# inside the dnSpy process via `Microsoft.CodeAnalysis.CSharp.Scripting`; globals: `module`, `allModules`, `docService`, `dbgManager`, `print()`
- **Config management** (2 new tools):
  - `get_mcp_config` — return the current server configuration and `mcp-config.json` path
  - `reload_mcp_config` — reload `mcp-config.json` without restarting dnSpy

### Technical
- de4dot DLLs bundled locally in `libs/de4dot/` (net48) and `libs/de4dot-net8/` (net10); selected via `De4DotBin` MSBuild property
- Skills persistence uses paired `{id}.md` (Markdown narrative) + `{id}.json` (technical record) files
- `run_script` uses `Microsoft.CodeAnalysis.CSharp.Scripting` v5.0.0 with `<ExcludeAssets>runtime</ExcludeAssets>` to avoid runtime dependency conflicts

### Total tools: **86** (was 63)

---

## [1.3.0] - 2026-02-25

### Added
- **Extended assembly editing** — metadata, flags, and references (6 new tools):
  - `get_assembly_metadata` — read assembly name, version, culture, public key, and flags
  - `edit_assembly_metadata` — change name, version, culture, or hash algorithm in-memory
  - `set_assembly_flags` — set/clear individual `AssemblyAttributes` flags (PublicKey, Retargetable, ProcessorArchitecture, etc.)
  - `list_assembly_references` — list `AssemblyRef` table entries
  - `add_assembly_reference` — add an `AssemblyRef` anchored by a `TypeForwarder` so it persists on save
  - `remove_assembly_reference` — remove an `AssemblyRef` and its associated `ExportedType` forwarders
- **IL injection and patching** (3 new tools):
  - `inject_type_from_dll` — deep-clone a type (fields, methods with IL, properties, events) from an external DLL into the target assembly
  - `list_pinvoke_methods` — list all P/Invoke (`DllImport`) declarations in a type; useful for locating anti-debug/tamper stubs
  - `patch_method_to_ret` — replace a method body with a `nop + ret` stub to neutralize it
- **Resource management** (5 new tools):
  - `list_resources` — enumerate all `ManifestResource` entries; flags Costura.Fody resources
  - `get_resource` — extract a resource as Base64 and/or save to disk
  - `add_resource` — embed a file as a new `EmbeddedResource`
  - `remove_resource` — remove a resource entry from the manifest
  - `extract_costura` — detect and extract Costura.Fody-embedded assemblies (handles gzip-compressed and uncompressed)
- **Assembly lifecycle management** (4 new tools):
  - `load_assembly` — load a DLL/EXE from disk or dump from a running process by PID
  - `select_assembly` — select an assembly in the document tree to set the active context
  - `close_assembly` — remove a specific assembly from the document tree
  - `close_all_assemblies` — clear all loaded assemblies
- **Cross-assembly usage analysis** (3 new tools):
  - `find_who_uses_type` — find all usages of a type (base class, interface, field, parameter, return type) across all loaded assemblies
  - `find_who_reads_field` — find IL `ldfld`/`ldsfld` usages of a field
  - `find_who_writes_field` — find IL `stfld`/`stsfld` usages of a field
- **Call graph and dead code analysis** (4 new tools):
  - `analyze_call_graph` — recursive call graph from a root method up to configurable depth
  - `find_dependency_chain` — BFS dependency path between two types via field/parameter/inheritance edges
  - `analyze_cross_assembly_dependencies` — dependency matrix across all loaded assemblies
  - `find_dead_code` — identify methods/types with no static references (approximation; does not track reflection or virtual dispatch)

### Total tools: **63** (was 38)

---

## [1.2.0] - 2026-02-24

### Added
- **Memory Dump Tools** (3 new tools):
  - `list_runtime_modules` — enumerate all .NET modules loaded in debugged processes with address, size, dynamic/in-memory flags
  - `dump_module_from_memory` — extract a .NET module from process memory; uses `IDbgDotNetRuntime.GetRawModuleBytes` first (best quality), falls back to `DbgProcess.ReadMemory`
  - `read_process_memory` — read raw bytes from any process address, returned as formatted hex dump + Base64
- **Previously-hidden tools now exposed** (4 tools):
  - `get_type_fields` — list/search fields with glob/regex pattern
  - `get_type_property` — detailed property info with getter/setter/attributes
  - `find_path_to_type` — BFS reference path from one type to another
  - `list_native_modules` — P/Invoke DLL imports grouped by native DLL name

### Improved
- **Regex/glob support** added to `list_types` (`name_pattern`), `list_methods_in_type` (`name_pattern`), `list_runtime_modules` (`name_filter`), and `search_types` (existing query upgraded to detect regex)
- **Default page size** raised from 10 → 50 across all paginated tools
- Tool descriptions updated to document pattern syntax (glob `*`/`?` vs regex `^/$`)

### Total tools: 38 (was 31)

---

## [1.1.0] - 2026-02-24

### Added
- **Edit Tools** (7): `decompile_type`, `change_member_visibility`, `rename_member`, `save_assembly`, `list_events_in_type`, `get_custom_attributes`, `list_nested_types`
- **Debug Tools** (9): `get_debugger_state`, `list_breakpoints`, `set_breakpoint`, `remove_breakpoint`, `clear_all_breakpoints`, `continue_debugger`, `break_debugger`, `stop_debugging`, `get_call_stack`
- `dnSpy.Contracts.Debugger.DotNet` project reference for `DbgDotNetBreakpointFactory`

---

## [1.0.0] - 2026-02-24

### Added
- Initial release of dnSpy MCP Server
- Model Context Protocol (MCP) server integration with dnSpy
- HTTP/SSE server on localhost:3100

### Core Capabilities
- **Assembly Discovery**: List and navigate loaded .NET assemblies
- **Type Inspection**: Analyze types, methods, properties, and fields
- **Code Decompilation**: Decompile methods to C# code
- **IL Inspection**: View IL instructions, bytes, and exception handlers
- **Usage Finding**: Track method callers via IL analysis
- **Inheritance Analysis**: Analyze type inheritance chains
- **Search**: Find types by name across all loaded assemblies

### Available Tools (15)
| Category | Tools |
|----------|-------|
| Assembly | `list_assemblies`, `get_assembly_info` |
| Type | `list_types`, `get_type_info`, `search_types` |
| Method | `decompile_method`, `list_methods_in_type`, `get_method_signature` |
| Property | `list_properties_in_type` |
| Analysis | `find_who_calls_method`, `analyze_type_inheritance` |
| IL | `get_method_il`, `get_method_il_bytes`, `get_method_exception_handlers` |
| Utility | `list_tools` |

### Technical Details
- **Protocol**: MCP 2024-11-05
- **Framework**: .NET Framework 4.8 & .NET 10.0
- **Transport**: HTTP/SSE

### Client Configuration Examples
```json
{
  "mcpServers": {
    "dnspy-mcp": {
      "type": "streamable-http",
      "url": "http://localhost:3100",
      "alwaysAllow": ["list_assemblies", "list_tools"],
      "disabled": false
    }
  }
}
```
