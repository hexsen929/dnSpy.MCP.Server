# dnSpy MCP Server — Release Notes

---

## v1.8.12 — 2026-04-06

### Fixed: older dnSpyEx output-path packaging

- GitHub Actions packaging now falls back to the newest built `dnSpy.MCP.Server.x.dll` when the output path does not include the target framework name
- this fixes the failed `v1.8.11` release packaging on `dnSpyEx/dnSpy@v6.5.1`
- release binaries remain pinned to the official `v6.5.1` host / `dnlib 4.4.0`

---

## v1.8.11 — 2026-04-06

### Fixed: build against the official dnSpyEx 6.5.1 host

- GitHub Actions now checks out `dnSpyEx/dnSpy@v6.5.1` instead of the moving `master` branch
- this keeps the build aligned with the official `dnSpy-netframework` package and its `dnlib 4.4.0`
- fixes the remaining `dnlib, Version=4.5.0.0` binding mismatch that still affected AgentSmithers-style direct-edit tools in `v1.8.10`

---

## v1.8.10 — 2026-04-06

### Fixed: AgentSmithers alias routing + dnlib compatibility

- `tools/call` now normalizes the original AgentSmithers names to their canonical tool names:
  - `Get_Class_Sourcecode`
  - `Get_Method_SourceCode`
  - `Get_Function_Opcodes`
  - `Set_Function_Opcodes`
  - `Overwrite_Full_Func_Opcodes`
  - `Update_Method_SourceCode`
- net48 startup now installs a `dnlib` compatibility resolver so direct-edit tools can reuse dnSpyEx's shipped `dnlib 4.4.0.0` when some call paths still request `4.5.0.0`
- README now explicitly documents the catalog split: 137 tools in default mode, 143 in full mode

---

## v1.8.9 — 2026-04-06

### New: Selectable listener backend

- added `listenerMode` config and settings UI support: `httpListener`, `tcpListener`, `auto`
- `tcpListener` serves `GET /health` plus `POST|DELETE /mcp`
- `httpListener` remains available when you need legacy `/sse` + `/message`
- intended for environments where Wine/CrossOver `HttpListener` breaks on some MCP client HTTP requests

---

## v1.8.8 — 2026-04-06

### Fixed: POST-only `/mcp` for streamable HTTP

- disabled the long-lived `GET /mcp` SSE stream path
- `/mcp` now behaves like `ida-pro-mcp`: POST requests carry initialize/list/call traffic directly
- legacy SSE remains available on `/sse` + `/message`
- intended to avoid CrossOver / Wine `HttpListener` instability with concurrent `GET /mcp` event streams

---

## v1.8.7 — 2026-04-06

### Changed: Sessionless discovery for MCP clients

- `tools/list`, `resources/list`, `prompts/list`, and `ping` now work without a prior `initialize`
- `tools/call`, `resources/read`, SSE stream attach, and explicit session close still use streamable HTTP session IDs
- intended to match the behavior expected by Claude/Codex management probes without removing multi-client session isolation

---

## v1.8.6 — 2026-04-06

### Changed: Removed localhost dual-prefix listener logic

- listener startup now binds only the configured host/prefix
- removed automatic localhost + 127.0.0.1 dual registration and fallback probing
- intended to rule out listener-side side effects while keeping `127.0.0.1` as the default local bind host

---

## v1.8.5 — 2026-04-06

### Changed: Prefer 127.0.0.1 for local defaults

- default generated `mcp-config.json` now uses `127.0.0.1:3100`
- bundled stdio proxy wrapper now defaults to `http://127.0.0.1:3100/mcp`
- README examples and client snippets now prefer the loopback IP form
- listener startup now binds only the configured host/prefix and no longer auto-registers an extra loopback prefix

---

## v1.8.4 — 2026-04-06

### New: Protection / Malware Triage Toolkit

The main branch now includes an initial static triage toolset for suspicious managed samples:

| Tool | Description |
|------|-------------|
| `triage_sample` | One-call triage summary: suspicious strings, APIs, P/Invokes, `.cctor` activity, embedded PE payloads, and entropy hints |
| `get_strings` | Managed string extraction from field constants and `ldstr` sites |
| `search_il_pattern` | IL line / opcode-sequence hunting across all methods |
| `analyze_static_constructors` | Bootstrap analysis of type static constructors |
| `detect_string_encryption` | Heuristic ranking of likely string decryptors/decoders |
| `find_byte_arrays` | Discovery of field RVA blobs and method-level byte-array staging sites |
| `find_embedded_pes` | Detection of PE payloads hidden in resources or field data |
| `detect_anti_debug` | Anti-debugger heuristic report over P/Invokes, managed checks, blacklist strings, and `.cctor` activity |
| `detect_anti_tamper` | Anti-tamper heuristic report over `<Module>` bootstrap code, RVA blobs, `InitializeArray`, and integrity/self-inspection APIs |
| `get_protection_report` | One-call aggregate protection report combining anti-debug, anti-tamper, decryptor, payload, and entropy signals |

### Design intent

- keep the analysis **static-first** and safe for hostile samples
- surface the first-pass signals an analyst usually wants immediately after loading an unknown assembly
- complement, not replace, the existing deep decompilation / IL / patching tools

### New: Optional stdio sidecar

An optional `stdio` bridge is now included as `tools/dnSpy.MCP.StdioProxy`.

- it forwards stdio MCP traffic to the local `POST /mcp` endpoint
- it preserves the existing HTTP/SSE server as the primary transport
- it is intended for clients that only support launching stdio MCP servers

### Fixed: localhost / 127.0.0.1 local compatibility

- when the server host is `localhost`, the listener now also registers `127.0.0.1`
- the bundled stdio proxy wrapper can target either loopback form; current examples prefer `http://127.0.0.1:3100/mcp`
- this avoids `Bad Request - Invalid Hostname` when a local client uses the loopback IP form
- if the dual-prefix registration is rejected on a specific machine, the listener now falls back automatically instead of aborting startup

### Current tool count on main

- **137 tools + 6 resources**

---

## v1.8.1 — 2026-04-05

### New: AgentSmithers-Compatible Direct Editing

This release line includes a dedicated compatibility layer for direct source and IL editing workflows:

| Tool | Description |
|------|-------------|
| `get_class_sourcecode` | Decompile a full type to C# in one call |
| `get_method_sourcecode` | Decompile a single method |
| `get_function_opcodes` | Stable IL listing with line indexes and operands |
| `set_function_opcodes` | Insert / append / overwrite IL by line index |
| `overwrite_full_function_opcodes` | Replace the full method body with supplied IL |
| `update_method_sourcecode` | Compile replacement C# statements into a new method body |

Legacy AgentSmithers aliases remain supported:

- `Get_Class_Sourcecode`
- `Get_Method_SourceCode`
- `Get_Function_Opcodes`
- `Set_Function_Opcodes`
- `Overwrite_Full_Func_Opcodes`
- `Update_Method_SourceCode`

### Improved: IL Patch Fidelity

- `set_function_opcodes` now supports branch targets that resolve to:
  - new labels inside the replacement IL block
  - original surviving instructions via `line:<index>` and `IL_<offset>`
- `switch` operands are now handled explicitly through the same target-resolution path
- `update_method_sourcecode` now injects same-type member skeletons into the generated wrapper, allowing replacement bodies to reference more fields, properties, events, and helper methods directly

### Changed: Safer Build / Release Packaging

GitHub Actions build and release artifacts are now shipped as **plugin-only bundles**.

Included files:
- `dnSpy.MCP.Server.x.dll`
- `dnSpy.MCP.Server.x.pdb`
- `mcp-config.json`
- `AssemblyData.dll`
- `de4dot*.dll`
- `Echo*.dll`
- `Echo*.pdb`
- `Echo*.xml`
- `INSTALL.txt`

This avoids encouraging users to overwrite the full dnSpy host tree with a plugin package.

### Fixed: net48 Runtime Dependency Guidance

The net48 deployment path is now explicitly documented for the common dependency mismatch case:

- `System.Text.Json`
- `System.Collections.Immutable`
- `System.Text.Encodings.Web`
- `System.Memory`

The recommended repair flow is:
1. start from a clean dnSpyEx netframework install
2. copy in the plugin-only bundle
3. update the three host config files (`dnSpy.exe.config`, `dnSpy-x86.exe.config`, `dnSpy.Console.exe.config`) with the required binding redirects when needed

### Total tools: **133**

---

## v1.8.0 — 2026-03-23

### New: Streamable HTTP Transport

The server now uses streamable HTTP at `POST /mcp` as the primary MCP transport. Session-oriented requests use the `Mcp-Session-Id` header after `initialize`.

### New: Echo-Based Managed Control-Flow Analysis

The first stable Echo integration was added using only the managed serializable subset:

| Tool | Description |
|------|-------------|
| `get_control_flow_graph` | Full CIL CFG view with blocks, edges, entry block, and summary metrics |
| `get_basic_blocks` | Reduced basic-block summary view for quick LLM consumption |

Integrated Echo components:
- `Echo.Core`
- `Echo.ControlFlow`
- `Echo.Platforms.Dnlib`

Not included:
- HoLLy UI
- MSAGL
- AsmResolver backends
- symbolic execution
- native CFG

### New: SourceMap Support Without HoLLy UI

The useful non-UI SourceMap functionality was ported into dedicated MCP tools:

| Tool | Description |
|------|-------------|
| `get_source_map_name` | Resolve the current SourceMap name for a type or member |
| `set_source_map_name` | Set or update a SourceMap entry |
| `list_source_map_entries` | List cached SourceMap entries for an assembly |
| `save_source_map` | Persist SourceMap cache to disk |
| `load_source_map` | Load SourceMap XML into the MCP cache |

### New: Runtime Reversing And Interception

Runtime and native reversing support was expanded substantially:

| Tool | Description |
|------|-------------|
| `get_proc_address` | Resolve loaded native export addresses |
| `patch_native_function` | Patch native exports in memory with optional `VirtualProtect` handling |
| `revert_patch` | Revert tracked native patches |
| `list_active_patches` | List tracked runtime patches |
| `disassemble_native_function` | Disassemble exports directly from process memory using Iced |
| `read_native_memory` | Read memory in `hex`, `ascii`, or `disasm` form |
| `suspend_threads` | Freeze all or selected threads |
| `resume_threads` | Resume only threads previously frozen through MCP |
| `get_peb` | Read best-effort PEB anti-debug fields |
| `inject_native_dll` | Native DLL injection via `LoadLibraryW` |
| `inject_managed_dll` | Managed DLL injection via CLR or Mono entrypoint invocation |
| `trace_method` | Persistent managed tracing without pausing execution |
| `hook_function` | Persistent managed interception with `break`, `log`, or `count` actions |
| `list_active_interceptors` | Summary list of interceptor sessions |
| `get_interceptor_log` | Retrieve per-session hit logs |
| `remove_interceptor` | Remove a session and its underlying breakpoint |

`hook_function` intentionally rejects `modify_return` for now with a clear error instead of exposing an unstable implementation.

### New: Alias-Aware Debugger Expressions

Debugger expressions and breakpoint conditions now support safer aliases for obfuscated or hostile symbol names:

- `$arg0`, `$arg1`, ...
- `$local0`, `$local1`, ...
- `arg(0)`, `local(0)`
- `field("Name")`
- `memberByToken("0x06001234")`

This support is wired into:
- `eval_expression`
- `eval_expression_ex`
- `set_breakpoint`
- `set_breakpoint_ex`
- persistent interceptor conditions

### New: Tool Catalog Rationalization

The tool catalog now carries per-tool metadata:

- `category`
- `hidden_by_default`
- `is_legacy`
- `preferred_replacement`
- `notes`

This reduces default discoverability noise without breaking `tools/call` compatibility.

### Compatibility Notes

- `list_assembly_attributes` is now treated as a legacy compatibility view; prefer `get_assembly_metadata`
- `deobfuscate_assembly` remains for compatibility, but `run_de4dot` is the preferred deobfuscation entry point
- `get_basic_blocks` remains intentionally as the reduced summary form of `get_control_flow_graph`
- the failed `feature/mcp-phase-batch-1` line was retired instead of repaired

### Total tools: **100+**

---

## v1.6.0 — 2026-02-27

### New: Active Debug Stepping, Expression Evaluation & Memory Patching

Seven new tools and one extended tool that complete the runtime inspection workflow — the AI can now drive single-step execution, inspect arbitrary expressions, and hot-patch memory without stopping the session.

#### Debug Stepping (5 new tools)

| Tool | Description |
|------|-------------|
| `step_over` | Step over the current statement. Blocks until `StepComplete` fires (default 30 s timeout). Returns the new execution location. |
| `step_into` | Step into the called method. Same blocking behaviour. |
| `step_out` | Run until the current method returns to its caller. |
| `get_current_location` | Read the top-frame location (token, IL offset, module name) without stepping. Read-only, no side-effects. |
| `wait_for_pause` | Poll every 100 ms until any process becomes paused — useful after `continue_debugger` + breakpoint, or `start_debugging` before the first pause. |

All step tools accept optional `thread_id`, `process_id`, and `timeout_seconds`. Thread resolution: explicit `thread_id` → current thread → first paused thread.

#### Expression Evaluation (1 new tool)

| Tool | Description |
|------|-------------|
| `eval_expression` | Evaluate a C# expression in the current paused stack frame context. Equivalent to the Watch window in dnSpy. Returns typed value: primitive, string, or object address + size. Configurable `func_eval_timeout_seconds`. |

#### Live Memory Patching (1 new tool)

| Tool | Description |
|------|-------------|
| `write_process_memory` | Write bytes to a process address (`DbgProcess.WriteMemory`). Accepts `bytes_base64` (base64) or `hex_bytes` (`"90 90 C3"`, `"9090C3"`, `"0x90 0x90"`). Hot-patches without touching the binary on disk. |

#### Conditional Breakpoints (extended existing tool)

`set_breakpoint` now accepts an optional `condition` parameter. The breakpoint only fires when the C# expression evaluates to `true`, eliminating manual `continue` loops.

### Implementation
- Stepping: `DbgStepper.Step(kind)` on WPF Dispatcher + `ManualResetEventSlim` for cross-thread synchronisation.
- Eval: `DbgLanguage.ExpressionEvaluator.Evaluate()` + `FormatDbgValue()` helper for `DbgValue` base API.
- Write memory: `DbgProcess.WriteMemory(ulong, byte[])`.
- `ParseHexBytes` handles `0x`/`0X` prefixes and separators; net48-compatible.

### Total tools: **95**

---

## v1.5.0 — 2026-02-27

### New: Window / Dialog Management Tools

Two new tools that let MCP clients detect and dismiss dialog boxes (error popups, confirmation dialogs) that appear inside the dnSpy process — previously impossible without direct UI interaction. Especially useful when a debug operation triggers an error MessageBox that freezes the session.

| Tool | Description |
|------|-------------|
| `list_dialogs` | Enumerate all active dialog/message-box windows (Win32 `#32770` + WPF). Returns title, HWND, message text, and button labels |
| `close_dialog` | Click a button on a dialog by name (EN/ES). Defaults to the first dialog and the OK button |

**Button matching** (case-insensitive): `ok`/`aceptar`, `yes`/`sí`, `no`, `cancel`/`cancelar`, `retry`/`reintentar`, `ignore`/`omitir`. Falls back to `WM_CLOSE` if no button matches.

### Implementation
- New file: `src/Application/WindowTools.cs` — P/Invoke only (`EnumWindows`, `EnumChildWindows`, `SendMessage`, `PostMessage`). No dnSpy service dependencies.
- `McpTools.cs` updated: field, constructor parameter, schema, and dispatch for both tools.

### Total tools: **87**

---

## v1.2.0 — 2026-02-24

### New: Memory Dump Tools

Three new tools to extract data from running processes.

| Tool | Description |
|------|-------------|
| `list_runtime_modules` | Enumerate all .NET modules in every debugged process, with base address, size, `IsDynamic`, `IsInMemory`, and AppDomain |
| `dump_module_from_memory` | Extract a .NET module from process memory to disk. Uses `IDbgDotNetRuntime.GetRawModuleBytes` (file layout, preferred) with automatic fallback to `DbgProcess.ReadMemory` |
| `read_process_memory` | Read raw bytes from any process address (1–65 536 bytes); returns a formatted hex dump and Base64 payload |

### Previously-hidden tools now exposed

Four tools that existed in the codebase but lacked MCP schemas and dispatch entries:

| Tool | Description |
|------|-------------|
| `get_type_fields` | List fields in a type with glob/regex pattern filtering |
| `get_type_property` | Detailed property info: getter/setter signatures and custom attributes |
| `find_path_to_type` | BFS traversal of property/field references to trace how one type reaches another |
| `list_native_modules` | All native DLLs imported via `[DllImport]`, grouped by DLL name |

### Improvements

- **Glob and regex support** added to `list_types` (`name_pattern`), `list_methods_in_type` (`name_pattern`), and `list_runtime_modules` (`name_filter`). Auto-detects regex (`^ $ [ ( | + {`) vs glob (`* ?`).
- **Default page size** raised from 10 → **50** items across all paginated tools.
- Tool descriptions updated to include pattern syntax examples.

### Total tools: **38**

---

## v1.1.0 — 2026-02-24

### New: Edit Tools (7)

| Tool | Description |
|------|-------------|
| `decompile_type` | Decompile an entire type to C# source |
| `change_member_visibility` | Change access modifier of a type or member |
| `rename_member` | Rename a type, method, field, property, or event |
| `save_assembly` | Save modified assembly to disk |
| `list_events_in_type` | List all events in a type |
| `get_custom_attributes` | Get custom attributes on a type or member |
| `list_nested_types` | Recursively list all nested types |

### New: Debug Tools (9)

| Tool | Description |
|------|-------------|
| `get_debugger_state` | Current debugger state and process list |
| `list_breakpoints` | All registered breakpoints |
| `set_breakpoint` | Add a breakpoint at a method/IL offset |
| `remove_breakpoint` | Remove a specific breakpoint |
| `clear_all_breakpoints` | Remove all breakpoints |
| `continue_debugger` | Resume all paused processes |
| `break_debugger` | Pause all running processes |
| `stop_debugging` | Stop all debug sessions |
| `get_call_stack` | Call stack of the current thread |

### Other
- Added `dnSpy.Contracts.Debugger.DotNet` project reference for `DbgDotNetBreakpointFactory`.
- Fixed `NativeModuleWriterOptions` to correctly cast `ModuleDef → ModuleDefMD`.

### Total tools: **31**

---

## v1.0.0 — 2026-02-24

### Initial release

15 tools covering assembly discovery, type/method inspection, decompilation, IL analysis, usage finding, and inheritance analysis.

| Category | Tools |
|----------|-------|
| Assembly | `list_assemblies`, `get_assembly_info` |
| Type | `list_types`, `get_type_info`, `search_types` |
| Method | `decompile_method`, `list_methods_in_type`, `get_method_signature` |
| Property | `list_properties_in_type` |
| Analysis | `find_who_calls_method`, `analyze_type_inheritance` |
| IL | `get_method_il`, `get_method_il_bytes`, `get_method_exception_handlers` |
| Utility | `list_tools` |

**Protocol**: MCP 2024-11-05 | **Transport**: HTTP/SSE | **Targets**: net48 + net10.0-windows
