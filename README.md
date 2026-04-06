# dnSpy MCP Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server embedded in dnSpy that exposes full .NET assembly analysis, editing, debugging, memory-dump, and deobfuscation capabilities to any MCP-compatible AI assistant.

**Version**: 1.8.23 | **Tools**: 143 base catalog (config can filter per client) | **Resources**: 6 | **Status**: beta | **Targets**: .NET 4.8 + .NET 10.0-windows

---

## Table of Contents

1. [Features](#features)
2. [Build & Install](#build--install)
3. [Client Configuration](#client-configuration)
4. [Tool Reference](#tool-reference)
   - [Assembly Tools](#assembly-tools)
   - [Type & Member Tools](#type--member-tools)
   - [Method & Decompilation Tools](#method--decompilation-tools)
   - [IL Tools](#il-tools)
   - [Control Flow Tools](#control-flow-tools)
   - [Analysis & Cross-Reference Tools](#analysis--cross-reference-tools)
   - [Edit Tools](#edit-tools)
   - [Agent Compatibility Editing Tools](#agent-compatibility-editing-tools)
   - [Embedded Resource Tools](#embedded-resource-tools)
   - [Debug Tools](#debug-tools)
   - [SourceMap Tools](#sourcemap-tools)
   - [Runtime Reversing Tools](#runtime-reversing-tools)
   - [Memory Dump & PE Tools](#memory-dump--pe-tools)
   - [Static PE Analysis](#static-pe-analysis)
   - [Protection / Malware Analysis Tools](#protection--malware-analysis-tools)
   - [Deobfuscation Tools](#deobfuscation-tools)
   - [Window / Dialog Tools](#window--dialog-tools)
   - [Utility](#utility)
5. [Pattern Syntax](#pattern-syntax)
6. [Pagination](#pagination)
7. [Usage Examples](#usage-examples)
8. [Architecture](#architecture)
9. [Project Structure](#project-structure)
10. [Configuration](#configuration)
11. [Troubleshooting](#troubleshooting)

---

## Features

### What Changed In 1.8.x

- streamable HTTP at `POST /mcp` is now the primary MCP surface
- managed CIL control-flow analysis was added using Echo
- HoLLy-inspired non-UI SourceMap support was added
- runtime reversing support was expanded with native export resolution, Iced disassembly, patch tracking, PEB inspection, thread suspension, and DLL injection
- persistent managed interception was added through `trace_method` and `hook_function`
- AgentSmithers-style direct editing was added via `get_class_sourcecode`, `get_method_sourcecode`, `get_function_opcodes`, `set_function_opcodes`, `overwrite_full_function_opcodes`, and `update_method_sourcecode`
- `set_function_opcodes` now supports branch and `switch` targets that point at newly inserted labels or surviving original instructions (`line:<index>` / `IL_<offset>`)
- `update_method_sourcecode` now compiles replacement method bodies inside a generated wrapper that includes same-type member stubs, so patches can reference more fields, properties, events, and helper methods directly
- first-pass protection / malware triage tools were added via `triage_sample`, `get_strings`, `search_il_pattern`, `analyze_static_constructors`, `detect_string_encryption`, `find_byte_arrays`, `find_embedded_pes`, `detect_anti_debug`, `detect_anti_tamper`, and `get_protection_report`
- GitHub Actions build/release artifacts are now shipped as **plugin-only bundles** for safer Windows deployment
- tool discoverability now includes catalog metadata such as `category`, `hidden_by_default`, `is_legacy`, `preferred_replacement`, and `notes`

| Category | Capabilities |
|----------|-------------|
| **Assembly** | List loaded assemblies, namespaces, type counts, P/Invoke imports |
| **Types** | Inspect types, fields, properties, events, nested types, attributes, inheritance |
| **Decompilation** | Decompile entire types or individual methods to C# |
| **IL** | View IL instructions, raw bytes, local variables, exception handlers |
| **Control Flow** | Build managed CFGs and reduced basic-block views for CIL methods using Echo |
| **Analysis** | Find callers/users, trace field reads/writes, call graphs, dead code, cross-assembly dependencies |
| **Edit** | Rename members, change access modifiers, edit metadata, patch methods, inject types, save to disk |
| **Agent Compatibility** | AgentSmithers-style class/method decompilation, stable IL listing, line-based IL splicing, full method-body replacement, and source-body hot patching |
| **Resources** | List, read, add, remove embedded resources (ManifestResource table); extract Costura.Fody-embedded assemblies |
| **Debug** | Manage breakpoints (with alias-aware conditions), launch/attach processes, pause/resume/stop sessions, single-step (over/into/out), inspect call stacks, read locals, evaluate expressions |
| **Interception** | Persistent managed tracing and breakpoint-backed interception with `trace_method` and `hook_function` |
| **SourceMap** | Query, update, list, save, and load SourceMap entries without HoLLy UI |
| **Runtime Reversing** | Resolve exports, disassemble native functions with Iced, patch/revert exports, inspect PEB, suspend/resume threads, inject native/managed DLLs |
| **Memory Dump** | List runtime modules, dump .NET or native modules from memory, read/write process memory, extract PE sections |
| **Static PE Analysis** | Scan raw PE bytes for strings; all-in-one ConfuserEx unpacker |
| **Protection / Malware Analysis** | Triage suspicious assemblies, extract managed strings, search IL patterns, inspect static constructors, rank likely string decryptors, find byte-array payloads, detect embedded PE blobs, detect anti-debug / anti-tamper heuristics, and build one-call protection reports |
| **Deobfuscation** | de4dot integration: detect obfuscator, rename mangled symbols, decrypt strings. Both in-process (`deobfuscate_assembly`) and external process (`run_de4dot`) modes available in all builds |
| **Window / Dialog** | List active dialog/message-box windows (Win32 `#32770` + WPF) in the dnSpy process; dismiss them by clicking any button by name (supports EN and ES) |
| **Search** | Glob and regex search across all loaded assemblies |

### Echo Integration

`dnSpy.MCP.Server` integrates only the stable managed subset of Echo:

- `Echo.Core`
- `Echo.ControlFlow`
- `Echo.Platforms.Dnlib`

The MCP server does **not** port HoLLy UI, MSAGL, AsmResolver backends, or symbolic execution. The Echo integration is limited to serializable CIL control-flow analysis that can be consumed reliably by MCP clients and LLMs.

---

## Build & Install

### Prerequisites

| Requirement | Version |
|-------------|---------|
| .NET SDK | 10.0+ (for net10.0-windows target) |
| .NET Framework SDK | 4.8 (for net48 target) |
| OS | Windows (WPF dependency) |
| dnSpy | dnSpyEx (this repo) |

> **de4dot integration** — de4dot libraries are bundled in `libs/de4dot/` (net48) and `libs/de4dot-net8/` (net8/net10). No external dependencies required; all deobfuscation tools are available in both build targets.

### Supported build lines

| Build baseline | Plugin target | Status | Notes |
|---|---:|---|---|
| `dnSpyEx/dnSpy@master` source tree | `net10.0-windows` | **Primary / recommended** | Mainline path. Build the host and extension from the same source baseline. |
| `dnSpyEx/dnSpy@master` source tree | `net48` | Compatibility | Still built from the same source baseline as the primary line; kept to preserve the upstream dual-target model. |
| Official prebuilt release zips | not authoritative for current source line | Best-effort only | Public release zip hosts may lag the current source baseline. Use them only if you explicitly choose a release-aligned deployment path. |

### Clone & Restore

```bash
git clone https://github.com/dnSpyEx/dnSpy --recursive
cd dnSpy
```

### Build commands

```bash
# Build only the MCP Server extension (Debug)
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Debug

# Build only the MCP Server extension (Release)
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release

# Build the full dnSpy solution (both targets)
dotnet build dnSpy.sln -c Debug

# Build for a specific target framework only
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release -f net10.0-windows
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release -f net48

# Restore NuGet packages without building
dotnet restore Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj

# Clean build artifacts
dotnet clean Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj
```

### Output locations

| Target | Output path |
|--------|-------------|
| .NET 10.0-windows | `dnSpy/dnSpy/bin/Release/net10.0-windows/Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.x.dll` |
| .NET Framework 4.8 | `dnSpy/dnSpy/bin/Release/net48/Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.x.dll` |

> The MCP Server now builds straight into dnSpy's extension layout: `bin/Extensions/dnSpy.MCP.Server`.
> GitHub Actions release/build artifacts are packaged in the same structure, so you extract the zip into dnSpy's `bin` directory and get `bin/Extensions/dnSpy.MCP.Server/*`.
> Do **not** replace the whole dnSpy folder or overwrite `dnSpy.exe.config`.

### Verify the build

```bash
# Check for errors (expects "Compilación correcta" or "Build succeeded")
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release --nologo 2>&1 | tail -5
```

### Recommended Windows deployment

The source-first deployment flow is:

1. clone **`dnSpyEx/dnSpy`** and checkout the source baseline you want to use
2. overlay this extension into `Extensions/dnSpy.MCP.Server`
3. build the host and extension from the **same source tree**
4. launch the resulting dnSpy build from a **local Windows drive**

For the primary line, that means:

```bash
git clone https://github.com/dnSpyEx/dnSpy --recursive
cd dnSpy
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release -f net10.0-windows
```

For the compatibility line:

```bash
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release -f net48
```

For a prebuilt dnSpy package, the release-aligned deployment flow is:

1. download the matching `dnSpy.MCP.Server-<target>.zip`
2. open your dnSpy install's `bin` directory
3. extract the zip there
4. verify the result is `bin/Extensions/dnSpy.MCP.Server/`

That keeps updates isolated to one extension directory instead of mixing MCP files into the dnSpy root.

If you instead deploy into an **official prebuilt** dnSpy zip, treat that as a separate release-aligned path and verify the host runtime/dependency baseline before assuming it matches the current source branch.

Do **not**:

- replace the whole dnSpy folder with the MCP artifact zip
- mix files into an already-patched or unknown dnSpy install
- launch dnSpy directly from a UNC path such as `\\Mac\Home\...`

### Manual net48 dependency repair

If the extension fails to load with a message like:

- `Could not load file or assembly 'System.Text.Json, Version=8.0.0.5'`
- `Could not load file or assembly 'System.Collections.Immutable, Version=10.0.0.5'`
- `Could not load file or assembly 'System.Memory, Version=4.0.5.0'`

then the plugin was copied into a dnSpy install whose `*.config` binding redirects do not cover the newer dependency versions expected by the plugin.

Patch **all three** host config files:

- `dnSpy.exe.config`
- `dnSpy-x86.exe.config`
- `dnSpy.Console.exe.config`

Inside `<assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">`, ensure these redirects exist:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Collections.Immutable" culture="neutral" publicKeyToken="b03f5f7f11d50a3a" />
  <bindingRedirect oldVersion="0.0.0.0-10.0.0.5" newVersion="8.0.0.0" />
</dependentAssembly>
<dependentAssembly>
  <assemblyIdentity name="System.Text.Json" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-8.0.0.5" newVersion="8.0.0.0" />
</dependentAssembly>
<dependentAssembly>
  <assemblyIdentity name="System.Text.Encodings.Web" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-8.0.0.0" newVersion="8.0.0.0" />
</dependentAssembly>
<dependentAssembly>
  <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-4.0.5.0" newVersion="4.0.1.2" />
</dependentAssembly>
```

If you are unsure whether a dnSpy tree is still contaminated by older plugin files, delete the whole install directory, unpack a fresh dnSpy release, and redeploy only the plugin bundle.

### Runtime

1. Start dnSpy — the MCP server starts automatically on `http://127.0.0.1:3100`
2. Verify it is running:
   ```bash
   curl http://127.0.0.1:3100/health
   curl -i -X POST http://127.0.0.1:3100/mcp -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"probe\",\"version\":\"1.0\"}}}"
   ```
3. Configure your MCP client (see next section)

---

## Client Configuration

The server now uses **MCP streamable HTTP** as the primary transport on `http://127.0.0.1:3100/mcp`.

### Transport Summary

- `GET /health` returns a simple health payload
- `POST /mcp` accepts JSON-RPC MCP requests
- `initialize` returns the negotiated protocol version and a `Mcp-Session-Id` response header
- `tools/call`, `resources/read`, and session close continue to use `Mcp-Session-Id`
- discovery methods such as `tools/list`, `resources/list`, `prompts/list`, and `ping` also work without a prior session
- `listenerMode` can select `httpListener`, `tcpListener`, or `auto`
- `tcpListener` is recommended for CrossOver / Wine / Codex compatibility
- legacy `/sse` + `/message` remain available only in `httpListener` mode
- `httpListener` keeps the original dnSpy legacy SSE transport; `tcpListener` is a lean `/health` + `/mcp` backend
- an optional `stdio` sidecar/proxy is shipped for MCP clients that only support stdio

### Generic MCP client configuration

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "http",
      "url": "http://127.0.0.1:3100/mcp"
    }
  }
}
```

### Minimal HTTP example

```bash
curl -X POST http://127.0.0.1:3100/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2025-03-26",
      "capabilities": {},
      "clientInfo": { "name": "example-client", "version": "1.0" }
    }
  }'
```

> Some MCP clients still label streamable HTTP as `http`, `streamable-http`, or `remote` depending on their config format. The endpoint is the same: `http://127.0.0.1:3100/mcp`.

### Optional stdio proxy

If your MCP client only supports `stdio`, use the bundled proxy under `stdio-proxy/`.

It does **not** replace the built-in HTTP server.
It simply forwards stdio-framed MCP requests to the local dnSpy HTTP endpoint and preserves the server session header automatically.

Example:

```bat
stdio-proxy\Run-dnSpy-MCP-Stdio-Proxy.cmd --url http://127.0.0.1:3100/mcp
```

Environment variables also work:

```bat
set DNSPY_MCP_URL=http://127.0.0.1:3100/mcp
set DNSPY_MCP_API_KEY=your_api_key_if_enabled
stdio-proxy\Run-dnSpy-MCP-Stdio-Proxy.cmd
```

For local-only use, prefer `127.0.0.1` over `localhost` in client configs. The server binds exactly the configured host/prefix, and `127.0.0.1` avoids hostname-specific quirks seen in some MCP clients, Wine/CrossOver setups, and Windows `HttpListener` environments.

Notes:

- keep dnSpy's built-in MCP server enabled and running
- the stdio proxy is **optional**; HTTP/SSE remains the primary transport
- the proxy currently forwards request/response traffic to `POST /mcp` and is sufficient for `initialize`, `tools/list`, `tools/call`, and normal resource calls
- `shutdown` / `exit` are handled locally by the proxy for better stdio-client compatibility

---

## Tool Reference

All tools are called over MCP as `tools/call` with a JSON arguments object.
Parameters marked **required** must always be provided; all others are optional.

---

### Assembly Tools

Tools for listing and inspecting .NET assemblies loaded in dnSpy.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_assemblies` | List every assembly currently open in dnSpy, with version, culture, and public key token | — | — |
| `get_assembly_info` | Detailed info for one assembly: modules, namespaces, type count | `assembly_name` | `cursor` |
| `list_types` | List types in an assembly, with class/interface/enum flags. Supports glob and regex via `name_pattern` | `assembly_name` | `namespace`, `name_pattern`, `cursor` |
| `list_native_modules` | List native DLLs imported via `[DllImport]`, grouped by DLL name with the managed methods that use them | `assembly_name` | — |
| `load_assembly` | Load a .NET assembly into dnSpy from a file on disk **or** from a running process by PID. Supports both normal PE layout and raw memory-layout dumps | — | `file_path`, `memory_layout`, `pid`, `module_name` |
| `select_assembly` | Select an assembly in the dnSpy document tree and open it in the active tab; changes the current assembly context for subsequent operations | `assembly_name` | `file_path` |
| `close_assembly` | Close (remove) a specific assembly from dnSpy | `assembly_name` | `file_path` |
| `close_all_assemblies` | Close all assemblies currently loaded in dnSpy, clearing the document tree | — | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `assembly_name` | string | Short assembly name as shown in dnSpy (e.g. `UnityEngine.CoreModule`) |
| `namespace` | string | Exact namespace filter (e.g. `System.Collections.Generic`) |
| `name_pattern` | string | Glob or regex filter on type name — see [Pattern Syntax](#pattern-syntax) |
| `cursor` | string | Opaque base-64 pagination cursor from `nextCursor` in a previous response |
| `file_path` | string | (`load_assembly`) Absolute path to a .NET assembly or memory dump |
| `memory_layout` | boolean | (`load_assembly`) When `true`, treat the file as raw memory-layout (VAs, not file offsets). Default `false` |
| `pid` | integer | (`load_assembly`) PID of a running .NET process to dump from. Requires active debug session. |
| `module_name` | string | (`load_assembly`) Module name/filename to pick when using `pid`. Defaults to first EXE module. |
| `file_path` | string | (`select_assembly`, `close_assembly`) Absolute path (FilePath from `list_assemblies`) to disambiguate when multiple assemblies share the same short name |

---

### Type & Member Tools

Tools for inspecting the internals of a specific type.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_type_info` | Full type overview: visibility, base type, interfaces, fields, properties, methods (paginated) | `assembly_name`, `type_full_name` | `cursor` |
| `search_types` | Search for types by name across **all** loaded assemblies | `query` | `cursor` |
| `get_type_fields` | List fields matching a name pattern, with type, visibility, and `readonly`/`const` flags | `assembly_name`, `type_full_name`, `pattern` | `cursor` |
| `get_type_property` | Full detail for a single property: getter/setter signatures, attributes | `assembly_name`, `type_full_name`, `property_name` | — |
| `list_properties_in_type` | Summary list of all properties with read/write flags | `assembly_name`, `type_full_name` | `cursor` |
| `list_events_in_type` | All events with `add`/`remove` method info | `assembly_name`, `type_full_name` | — |
| `list_nested_types` | All nested types recursively (full name, visibility, kind) | `assembly_name`, `type_full_name` | — |
| `get_custom_attributes` | Custom attributes on the type or on one of its members | `assembly_name`, `type_full_name` | `member_name`, `member_kind` |
| `analyze_type_inheritance` | Full inheritance chain (base classes + interfaces) | `assembly_name`, `type_full_name` | — |
| `find_path_to_type` | BFS traversal of property/field references to find how one type reaches another | `assembly_name`, `from_type`, `to_type` | `max_depth` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `type_full_name` | string | Fully-qualified type name (e.g. `MyNamespace.MyClass`) |
| `query` | string | Substring, glob, or regex matched against `FullName` |
| `pattern` | string | Glob or regex for field name matching; use `*` to list all |
| `property_name` | string | Exact property name (case-insensitive) |
| `member_name` | string | Member name for attribute lookup (omit to get type-level attributes) |
| `member_kind` | string | Disambiguates overloaded names: `method`, `field`, `property`, or `event` |
| `from_type` | string | Full name of starting type for BFS path search |
| `to_type` | string | Name or substring of the target type |
| `max_depth` | integer | BFS depth limit (default `5`) |

---

### Method & Decompilation Tools

Tools for decompiling code and exploring method metadata.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `decompile_type` | Decompile an entire type (class/struct/interface/enum) to C# | `assembly_name`, `type_full_name` | — |
| `decompile_method` | Decompile a single method to C# | `assembly_name`, `type_full_name`, `method_name` | — |
| `list_methods_in_type` | List methods with return type, visibility, static, virtual, parameter count. Filter by visibility or name pattern | `assembly_name`, `type_full_name` | `visibility`, `name_pattern`, `cursor` |
| `get_method_signature` | Full signature for one method: parameters, return type, generic constraints | `assembly_name`, `type_full_name`, `method_name` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `method_name` | string | Method name (first match when overloads exist; use `get_method_signature` to disambiguate) |
| `visibility` | string | Filter: `public`, `private`, `protected`, or `internal` |
| `name_pattern` | string | Glob or regex on method name (e.g. `Get*`, `^On[A-Z]`, `Async$`) |

---

### IL Tools

Low-level IL inspection for a method body.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_method_il` | IL instruction listing with offsets, opcodes, operands, and local variable table | `assembly_name`, `type_full_name`, `method_name` | — |
| `get_method_il_bytes` | Raw IL bytes as a hex string and Base64 | `assembly_name`, `type_full_name`, `method_name` | — |
| `get_method_exception_handlers` | try/catch/finally/fault region table (offsets and handler type) | `assembly_name`, `type_full_name`, `method_name` | — |
| `dump_cordbg_il` | For each MethodDef in the paused module, reads `ICorDebugFunction.ILCode.Address` and `ILCode.Size` via the CorDebug COM API (through reflection). Reports whether IL addresses fall inside the PE image (encrypted stubs) or outside (JIT hook buffers). Useful for ConfuserEx JIT-hook analysis. Requires an active paused debug session | — | `module_name`, `output_path`, `max_methods`, `include_bytes` |

#### Parameter details (`dump_cordbg_il`)

| Parameter | Type | Description |
|-----------|------|-------------|
| `module_name` | string | Module name or filename filter (default: first EXE module) |
| `output_path` | string | Optional path to save full JSON results to disk |
| `max_methods` | integer | Max number of MethodDef tokens to scan (default `10000`) |
| `include_bytes` | boolean | When `true`, include Base64-encoded IL bytes for each method (default `false`) |

---

### Control Flow Tools

Serializable managed control-flow analysis for CIL methods using Echo over dnlib-backed method bodies.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_control_flow_graph` | Full CFG view with blocks, edges, entry block, and summary metrics | `assembly_name`, `type_full_name`, `method_name` | — |
| `get_basic_blocks` | Reduced basic-block summary view intended for quick LLM consumption | `assembly_name`, `type_full_name`, `method_name` | — |

> `get_basic_blocks` is intentionally the compact summary form of `get_control_flow_graph`.

---

### Analysis & Cross-Reference Tools

Call-graph, usage, and dependency analysis across all loaded assemblies.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `find_who_calls_method` | Find every method whose IL contains a `call`/`callvirt` to the target | `assembly_name`, `type_full_name`, `method_name` | — |
| `find_who_uses_type` | Find all types, methods, and fields that reference a type (base class, interface, field type, parameter, return type) | `assembly_name`, `type_full_name` | — |
| `find_who_reads_field` | Find all methods that read a field via `ldfld`/`ldsfld` IL instructions | `assembly_name`, `type_full_name`, `field_name` | — |
| `find_who_writes_field` | Find all methods that write to a field via `stfld`/`stsfld` IL instructions | `assembly_name`, `type_full_name`, `field_name` | — |
| `analyze_call_graph` | Build a recursive call graph for a method, showing all methods it calls down to a configurable depth | `assembly_name`, `type_full_name`, `method_name` | `max_depth` |
| `find_dependency_chain` | Find all dependency paths between two types via BFS over base types, interfaces, fields, parameters, and return types | `assembly_name`, `from_type`, `to_type` | `max_depth` |
| `analyze_cross_assembly_dependencies` | Compute a dependency matrix for all loaded assemblies, showing which assemblies each depends on | — | — |
| `find_dead_code` | Identify methods and types never called or referenced (static approximation; virtual dispatch and reflection are not tracked) | `assembly_name` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `field_name` | string | Exact field name to search for read/write access |
| `max_depth` | integer | Recursion depth limit for call-graph or BFS traversal (default `5`) |

---

### Edit Tools

In-memory metadata editing. Changes are applied immediately to dnlib's in-memory model and persist until `save_assembly` is called or dnSpy is closed without saving.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `change_member_visibility` | Change the access modifier of a type or one of its members | `assembly_name`, `type_full_name`, `member_kind`, `new_visibility` | `member_name` |
| `rename_member` | Rename a type or one of its members | `assembly_name`, `type_full_name`, `member_kind`, `old_name`, `new_name` | — |
| `save_assembly` | Write the (possibly modified) assembly to disk using dnlib's `ModuleWriter` | `assembly_name` | `output_path` |
| `get_assembly_metadata` | Read assembly-level metadata: name, version, culture, public key, flags, hash algorithm, module count, custom attributes | `assembly_name` | — |
| `edit_assembly_metadata` | Edit assembly-level metadata fields: name, version, culture, or hash algorithm | `assembly_name` | `name`, `version`, `culture`, `hash_algorithm` |
| `set_assembly_flags` | Set or clear an individual assembly attribute flag (e.g. `PublicKey`, `Retargetable`, processor architecture) | `assembly_name`, `flag_name`, `value` | — |
| `list_assembly_references` | List all assembly references (AssemblyRef table entries) in the manifest module | `assembly_name` | — |
| `add_assembly_reference` | Add an assembly reference by loading a DLL from disk. Creates a TypeForwarder to anchor the reference | `assembly_name`, `dll_path` | — |
| `remove_assembly_reference` | Remove an AssemblyRef entry and all TypeForwarder entries that target it. Returns a warning if TypeRefs in code still use the reference | `assembly_name`, `reference_name` | — |
| `inject_type_from_dll` | Deep-clone a type (fields, methods with IL, properties, events) from an external DLL into the target assembly | `assembly_name`, `dll_path`, `type_full_name` | — |
| `list_pinvoke_methods` | List all P/Invoke (`DllImport`) declarations in a type: managed name, token, DLL name, native function name | `assembly_name`, `type_full_name` | — |
| `patch_method_to_ret` | Replace a method's IL body with a minimal return stub (`nop` + `ret`) to neutralize it. Works on P/Invoke methods too (converts to managed stub) | `assembly_name`, `type_full_name`, `method_name` | — |

#### Parameter details

| Parameter | Type | Values / Description |
|-----------|------|---------------------|
| `member_kind` | string | `type`, `method`, `field`, `property`, or `event` |
| `new_visibility` | string | `public`, `private`, `protected`, `internal`, `protected_internal`, `private_protected` |
| `old_name` | string | Current member name |
| `new_name` | string | Desired new name |
| `output_path` | string | Absolute path for output file. Defaults to the original file location. |
| `flag_name` | string | Assembly flag to toggle (e.g. `PublicKey`, `Retargetable`, `PA_MSIL`, `PA_x86`, `PA_AMD64`) |
| `value` | boolean | `true` to set the flag, `false` to clear it |
| `dll_path` | string | Absolute path to the source DLL |

> **Note**: `rename_member` changes only the metadata name. It does **not** update call sites, string literals, or XML docs.

> **Note**: `patch_method_to_ret` is ideal for disabling anti-debug, anti-tamper, or license-check routines before saving and re-analyzing.

---

### Agent Compatibility Editing Tools

Direct source and IL editing tools intended to preserve familiar AgentSmithers-style workflows while staying inside dnSpyEx's in-memory editing model.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_class_sourcecode` | Decompile an entire type to C# in one call | `assembly_name`, `type_full_name` or (`namespace`, `class_name`) | `file_path` |
| `get_method_sourcecode` | Decompile a single method to C# | `assembly_name`, `method_name` | `type_full_name`, `namespace`, `class_name`, `method_token`, `parameter_count`, `file_path` |
| `get_function_opcodes` | Return method IL with stable line indexes, IL offsets, opcode names, and operands in an AgentSmithers-friendly shape | `assembly_name`, `method_name` | `type_full_name`, `namespace`, `class_name`, `method_token`, `parameter_count`, `file_path` |
| `set_function_opcodes` | Insert, append, or overwrite IL at a specific instruction index. Supports labels, branch targets, and `switch` targets that resolve to new labels or surviving original instructions | `assembly_name`, `method_name`, `il_opcodes`, `il_line_number` | `type_full_name`, `namespace`, `class_name`, `method_token`, `parameter_count`, `mode` |
| `overwrite_full_function_opcodes` | Replace the full method body with the supplied IL instruction list | `assembly_name`, `method_name`, `il_opcodes` | `type_full_name`, `namespace`, `class_name`, `method_token`, `parameter_count` |
| `update_method_sourcecode` | Compile C# statements into a generated replacement method body and swap the target method's IL in-memory | `assembly_name`, `method_name`, `source` | `type_full_name`, `namespace`, `class_name`, `method_token`, `parameter_count`, `file_path` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `method_token` | string | Optional metadata token (hex or decimal) to disambiguate overloads |
| `parameter_count` | integer | Optional overload disambiguator: number of normal method parameters |
| `il_opcodes` | array of strings | IL lines such as `Ldstr Hello`, `loop: br.s loop`, `brtrue.s line:17`, or `switch case0, case1, IL_0010` |
| `il_line_number` | integer | 0-based instruction index used by `set_function_opcodes` |
| `mode` | string | `append`, `insert`, or `overwrite` (default `append`) |
| `source` | string | C# statements that become the body of the replacement method |

#### Compatibility aliases

The following legacy names are still accepted for compatibility with existing AgentSmithers prompts and clients:

- `Get_Class_Sourcecode`
- `Get_Method_SourceCode`
- `Get_Function_Opcodes`
- `Set_Function_Opcodes`
- `Overwrite_Full_Func_Opcodes`
- `Update_Method_SourceCode`

#### Notes

- `set_function_opcodes` supports branch and `switch` operands that point either to labels introduced in the new block or to original surviving instructions via `line:<index>` / `IL_<offset>`
- `update_method_sourcecode` now injects same-type member skeletons into the generated wrapper so patch bodies can reference more fields, properties, events, and helper methods directly
- `update_method_sourcecode` currently rejects nested types, generic types, generic methods, lambdas/local functions that synthesize helper types, and helper generic instantiations
- all edits are **in-memory** until you call `save_assembly`

---

### Embedded Resource Tools

Read, write, and extract entries from the ManifestResource table. All write operations are in-memory until `save_assembly` is called.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_resources` | List all ManifestResource entries: name, kind (Embedded/Linked/AssemblyLinked), size, visibility, and whether it looks like a Costura.Fody-embedded assembly | `assembly_name` | — |
| `get_resource` | Extract an embedded resource as Base64 (up to 4 MB inline) and/or save to disk | `assembly_name`, `resource_name` | `output_path`, `skip_base64` |
| `add_resource` | Embed a file from disk as a new EmbeddedResource in the assembly | `assembly_name`, `resource_name`, `file_path` | `is_public` |
| `remove_resource` | Delete a ManifestResource entry by name | `assembly_name`, `resource_name` | — |
| `extract_costura` | Detect and extract Costura.Fody-embedded assemblies (`costura.*.dll.compressed` resources). Decompresses gzip automatically. Useful for analysing assemblies packed with Costura | `assembly_name`, `output_directory` | `decompress` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `resource_name` | string | Exact resource name (use `list_resources` to find it) |
| `output_path` | string | (`get_resource`) Absolute path to write raw resource bytes |
| `skip_base64` | boolean | (`get_resource`) Omit Base64 from response; useful when saving large resources to disk (default `false`) |
| `is_public` | boolean | (`add_resource`) Resource visibility — `true` = Public (default), `false` = Private |
| `output_directory` | string | (`extract_costura`) Directory where extracted DLLs/PDBs will be written |
| `decompress` | boolean | (`extract_costura`) Decompress gzip-compressed resources (default `true`) |

> **Costura.Fody workflow**: `list_resources` (confirm `costura.*` entries exist) → `extract_costura output_directory=C:\extracted` → `load_assembly` each extracted DLL → analyse normally with MCP tools.

---

### Skills Knowledge Base

A persistent reverse-engineering knowledge base stored in `%APPDATA%\dnSpy\dnSpy.MCPServer\skills\`. Each skill is a pair of files: a Markdown narrative (`{id}.md`) and a JSON technical record (`{id}.json`). Skills capture step-by-step procedures, magic values, crypto keys, algorithms, offsets, and generic prompts — so once you've reversed a packer or obfuscator, the knowledge is reusable.

**Workflow**: Before analysing a new binary, call `search_skills` to check for existing knowledge. After completing an analysis, call `save_skill` to record findings.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_skills` | List all skills in the knowledge base with ID, name, description, tags, and targets | — | `tag` |
| `get_skill` | Retrieve the full Markdown narrative and JSON technical record of a skill | `skill_id` | — |
| `save_skill` | Create or update a skill. Writes Markdown and/or JSON. Use `merge=true` to append findings without overwriting existing data | `skill_id` | `name`, `description`, `tags`, `targets`, `markdown`, `json_data`, `merge` |
| `search_skills` | Full-text search across all skill Markdown and JSON files. Returns matches with context snippets | `query` or `tag` | `query`, `tag` |
| `delete_skill` | Permanently delete a skill (both `.md` and `.json` files) | `skill_id` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `skill_id` | string | Skill identifier — will be slugified (e.g. `confuserex-unpacking`). Use `list_skills` to see existing IDs |
| `name` | string | Human-readable skill name |
| `description` | string | Short summary of what the skill covers |
| `tags` | string | Comma-separated or JSON array of tags (e.g. `packer,confuserex,unpacking`) |
| `targets` | string | Comma-separated or JSON array of target binary names or hashes this skill applies to |
| `markdown` | string | Markdown narrative: what to do, why, key observations, procedure steps in prose |
| `json_data` | string | JSON object with technical details: `procedure` (steps with `tool`/`prompt`/`expected`), `magic_values`, `crypto_keys`, `algorithms`, `offsets`, `findings`, `prompts` (identify/apply/verify/troubleshoot) |
| `merge` | boolean | If `true`, deep-merge `json_data` into the existing record instead of replacing it (default `false`) |
| `query` | string | (`search_skills`) Keyword or phrase to search for in all skill files |
| `tag` | string | (`list_skills`, `search_skills`) Filter results to skills containing this tag substring |

#### Example JSON skill record structure

```json
{
  "id": "confuserex-unpacking",
  "name": "ConfuserEx Unpacking",
  "version": "1",
  "tags": ["packer", "confuserex", "unpacking"],
  "targets": ["MyProtectedApp.exe"],
  "description": "Step-by-step procedure to unpack ConfuserEx 1.x protected assemblies",
  "procedure": [
    { "step": 1, "tool": "detect_obfuscator", "prompt": "Detect which obfuscator was applied", "expected": "ConfuserEx v1.x" },
    { "step": 2, "tool": "start_debugging",   "prompt": "Launch under dnSpy with break_kind=EntryPoint", "expected": "Paused at entry point after .cctor" },
    { "step": 3, "tool": "dump_module_from_memory", "prompt": "Dump decrypted module from RAM", "expected": "Raw PE bytes" }
  ],
  "magic_values": { "key_offset": "0x1234", "xor_key": "0xDEADBEEF" },
  "crypto_keys": [],
  "algorithms": ["XOR", "AES-128-ECB"],
  "prompts": {
    "identify": "Use detect_obfuscator on the assembly. If ConfuserEx, proceed with this skill.",
    "apply": "Follow procedure steps 1-3. After dump, reload and deobfuscate.",
    "verify": "Decompile Main() — if readable C# appears, unpacking succeeded.",
    "troubleshoot": "If dump_module_from_memory fails, try dump_module_unpacked with fix_pe_header=true."
  }
}
```

---

### Debug Tools

Interact with dnSpy's integrated debugger. Most tools require an active debug session.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_debugger_state` | Current state: `IsDebugging`, `IsRunning`, process list with thread/runtime counts | — | — |
| `list_breakpoints` | All registered code breakpoints with enabled state, bound count, and location | — | — |
| `set_breakpoint` | Set a breakpoint at a method entry point or specific IL offset. Supports alias-aware conditions such as `$arg0`, `$local0`, `arg(0)`, `local(0)`, `field("Name")`, and `memberByToken("0x06001234")` | `assembly_name`, `type_full_name`, `method_name` | `il_offset`, `condition`, `file_path` |
| `set_breakpoint_ex` | Extended compatibility alias of `set_breakpoint` with the same alias-aware condition support | `assembly_name`, `type_full_name`, `method_name` | `il_offset`, `condition`, `file_path` |
| `remove_breakpoint` | Remove a specific breakpoint | `assembly_name`, `type_full_name`, `method_name` | `il_offset` |
| `clear_all_breakpoints` | Remove every visible breakpoint | — | — |
| `continue_debugger` | Resume all paused processes (`RunAll`) | — | — |
| `break_debugger` | Pause all running processes (`BreakAll`). `safe_pause=true` uses dnSpy-managed pause only and never calls `Debugger.Break()` | — | `safe_pause` |
| `stop_debugging` | Terminate all active debug sessions | — | — |
| `get_call_stack` | Call stack of the currently selected (or first paused) thread — up to 50 frames | — | — |
| `step_over` | Step over the current statement. Blocks until the step completes (or timeout). Returns the new execution location (token, IL offset, module). | — | `thread_id`, `process_id`, `timeout_seconds` |
| `step_into` | Step into the next called method. Same blocking behaviour as `step_over`. | — | `thread_id`, `process_id`, `timeout_seconds` |
| `step_out` | Run until the current method returns to its caller. | — | `thread_id`, `process_id`, `timeout_seconds` |
| `get_current_location` | Read the top-frame execution location without stepping. Requires paused debugger. | — | `thread_id`, `process_id` |
| `wait_for_pause` | Poll until any process becomes paused (after a `continue_debugger` or `start_debugging`). Returns process info on pause, throws `TimeoutException` otherwise. | — | `timeout_seconds` |
| `start_debugging` | Launch an EXE under the dnSpy debugger. By default breaks at `EntryPoint` (after the module `.cctor` has run, so ConfuserEx-decrypted bodies are in RAM) | `exe_path` | `arguments`, `working_directory`, `break_kind` |
| `attach_to_process` | Attach the dnSpy debugger to a running .NET process by PID | `process_id` | — |
| `set_exception_breakpoint` | Break when a specific exception type is thrown. Configurable first-chance (before catch) and second-chance (unhandled). Default: first-chance enabled. | `exception_type` | `first_chance`, `second_chance`, `category` |
| `remove_exception_breakpoint` | Remove an exception breakpoint for a specific exception type | `exception_type` | `category` |
| `list_exception_breakpoints` | List all active exception breakpoints (those with at least one chance flag set) | — | — |
| `batch_breakpoints` | Create multiple breakpoints in one call to reduce round-trips | `items` | — |
| `get_method_by_token` | Resolve a method by metadata token and return signature, RVA, JIT/load-state hints, and native-address metadata when available | `assembly_name`, `token` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `il_offset` | integer | IL byte offset within the method body (default `0` = method entry) |
| `condition` | string | C# expression evaluated at each breakpoint hit. Alias-aware forms include `$arg0`, `$local0`, `arg(0)`, `local(0)`, `field("Name")`, and `memberByToken("0x06001234")` |
| `exe_path` | string | Absolute path to the EXE to launch |
| `arguments` | string | Command-line arguments to pass to the process |
| `working_directory` | string | Working directory for the launched process |
| `break_kind` | string | `EntryPoint` (default, pauses after `.cctor`) or `ModuleCctorOrEntryPoint` (pauses before `.cctor`) |
| `thread_id` | integer | Explicit thread ID to step/inspect. Defaults to current thread, then first paused thread. |
| `timeout_seconds` | integer | Max seconds to wait for a step or pause to complete (default `30`) |
| `exception_type` | string | Exception class name (e.g. `System.NullReferenceException`, `System.IO.IOException`) |
| `first_chance` | boolean | Break before a `catch` block handles the exception (default `true`) |
| `second_chance` | boolean | Break on unhandled exceptions (default `false`) |
| `category` | string | Exception category string (default `DotNet`). Use `DotNet` for managed exceptions. |

> **Tip**: Use `start_debugging` + `break_kind: EntryPoint` for ConfuserEx-packed assemblies — method bodies are decrypted by the time the breakpoint hits. Then use `dump_module_from_memory` or `unpack_from_memory`.

> **Step workflow**: `break_debugger` (or wait for a BP) → `get_current_location` → `step_over` / `step_into` → inspect with `get_local_variables` or `eval_expression` → repeat.

---

### SourceMap Tools

SourceMap support ported from the useful non-UI parts of HoLLy. These tools keep naming data in the MCP cache without depending on HoLLy tabs or menus.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_source_map_name` | Resolve the current SourceMap name for a type or member | `assembly_name`, `type_full_name` | `member_kind`, `member_name` |
| `set_source_map_name` | Set or update a SourceMap entry in the MCP cache | `assembly_name`, `type_full_name`, `mapped_name` | `member_kind`, `member_name` |
| `list_source_map_entries` | List all cached SourceMap entries for an assembly | `assembly_name` | — |
| `save_source_map` | Persist the current SourceMap cache for an assembly to disk | `assembly_name` | `output_path` |
| `load_source_map` | Load a SourceMap XML file into the MCP cache | `assembly_name`, `input_path` | — |

---

### Runtime Reversing Tools

Process-level reversing and persistent interception tools.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_proc_address` | Resolve an exported function address from a loaded module | `module`, `function` | `process_id` |
| `patch_native_function` | Patch a native export in memory and optionally auto-wrap protection changes | `module`, `function` | `hex_bytes`, `bytes`, `bytes_base64`, `process_id`, `auto_virtual_protect` |
| `revert_patch` | Revert a tracked patch by `patch_id` | `patch_id` | `auto_virtual_protect` |
| `list_active_patches` | List tracked native patches created by the MCP server | — | — |
| `disassemble_native_function` | Symbol-oriented native disassembly using Iced | `module`, `function` | `size`, `process_id` |
| `read_native_memory` | Read native memory in `hex`, `ascii`, or `disasm` form | `address`, `size` | `format`, `process_id` |
| `suspend_threads` | Freeze all or selected threads in the target process | — | `process_id`, `thread_ids` |
| `resume_threads` | Resume only threads previously frozen through the MCP server | — | `process_id`, `thread_ids` |
| `get_peb` | Best-effort read of PEB anti-debug fields | — | `process_id` |
| `inject_native_dll` | Native DLL injection via `LoadLibraryW` and `CreateRemoteThread` | `dll_path` | `process_id` |
| `inject_managed_dll` | Managed injection via CLR or Mono entrypoint invocation | `dll_path`, `type_name`, `method_name` | `argument`, `copy_to_temp`, `process_id` |
| `trace_method` | Persistent managed tracing without pausing execution | `assembly_name`, `type_full_name`, `method_name` | `token`, `file_path`, `il_offset`, `condition`, `max_calls`, `max_log_entries` |
| `hook_function` | Persistent managed interception with `break`, `log`, or `count` actions | `assembly_name`, `type_full_name`, `method_name` | `token`, `file_path`, `il_offset`, `condition`, `action`, `max_calls`, `max_log_entries` |
| `list_active_interceptors` | Summary view of interceptor sessions | — | `include_inactive` |
| `get_interceptor_log` | Detailed hit log for one interceptor session | `session_id` | — |
| `remove_interceptor` | Remove an interceptor session and its underlying breakpoint | `session_id` | — |

> `hook_function` intentionally rejects `modify_return` for now instead of exposing an unstable implementation.

---

### Memory Dump & PE Tools

Extract raw bytes from a debugged process. Requires an active debug session unless otherwise noted.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_runtime_modules` | Enumerate all .NET modules loaded in the debugged processes with address, size, `IsDynamic`, `IsInMemory`, and AppDomain | — | `process_id`, `name_filter` |
| `dump_module_from_memory` | Extract a .NET module from process memory to a file (preserves file layout when possible) | `module_name`, `output_path` | `process_id` |
| `read_process_memory` | Read up to 64 KB from any process address; returns a formatted hex dump and Base64 | `address`, `size` | `process_id` |
| `write_process_memory` | Write bytes to a process address (hot-patching). Accepts `bytes_base64` (base64) or `hex_bytes` (e.g. `"90 90 C3"`). Useful for disabling checks without touching the binary on disk. | `address`, (`bytes_base64` or `hex_bytes`) | `process_id` |
| `get_local_variables` | Read local variables and parameters from a paused stack frame; returns primitives, strings, and addresses for complex objects | — | `frame_index`, `process_id` |
| `eval_expression` | Evaluate a C# expression in the current paused frame context (Watch window equivalent). Returns typed value: primitive, string, or object address | `expression` | `frame_index`, `process_id`, `func_eval_timeout_seconds` |
| `get_pe_sections` | List PE section headers of a module in process memory (names, virtual addresses, sizes, characteristics) | `module_name` | `process_id` |
| `dump_pe_section` | Extract a specific PE section (e.g. `.text`, `.data`, `.rsrc`) from a module in process memory; writes to file and/or returns Base64 | `module_name`, `section_name` | `output_path`, `process_id` |
| `dump_module_unpacked` | Dump a full module with memory-to-file layout conversion (produces a valid loadable PE). Handles .NET, native, and mixed-mode modules | `module_name`, `output_path` | `process_id` |
| `dump_memory_to_file` | Save a contiguous range of process memory to a file. Supports up to 256 MB | `address`, `size`, `output_path` | `process_id` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `process_id` | integer | Target process ID (use `get_debugger_state` to find it). Defaults to the first paused process. |
| `name_filter` | string | Glob or regex filter on module name or filename |
| `module_name` | string | Module name, full filename, or basename (e.g. `MyApp.dll`). Use `list_runtime_modules` to find exact names. |
| `output_path` | string | Absolute path where the dumped bytes will be written. Parent directories are created automatically. |
| `address` | string | Memory address in hex (`0x7FF000`) or decimal |
| `size` | integer | Number of bytes to read/dump |
| `bytes_base64` | string | Bytes to write as base64 (use with `write_process_memory`) |
| `hex_bytes` | string | Bytes to write as hex string — `"90 90 C3"`, `"9090C3"`, `"0x90 0x90"` all accepted |
| `frame_index` | integer | Stack frame index (0 = top/innermost, default `0`) |
| `expression` | string | C# expression to evaluate in the current frame context (e.g. `"myObj.Field"`, `"arr.Length"`) |
| `func_eval_timeout_seconds` | integer | Timeout for function evaluation calls in the debuggee (default `5`) |
| `section_name` | string | PE section name (e.g. `.text`, `.data`, `.rsrc`) |

> **Layout note**: `dump_module_from_memory` reports `IsFileLayout` in its response. If `false`, use `dump_module_unpacked` instead for a corrected PE layout.

---

### Static PE Analysis

Tools that operate on raw PE file bytes — no debug session required.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `scan_pe_strings` | Scan raw PE file bytes for printable ASCII and UTF-16 strings. Useful for finding URLs, API keys, IP addresses, and embedded plaintext in packed/obfuscated assemblies | `assembly_name` | `min_length`, `encoding` |
| `unpack_from_memory` | All-in-one ConfuserEx unpacker: launches the EXE under the debugger (pausing at `EntryPoint` after decryption), dumps the main module with PE-layout fix, and optionally stops the session. Output can be loaded in dnSpy or passed to `deobfuscate_assembly` | `exe_path` | `output_path`, `stop_after_dump` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `min_length` | integer | Minimum string length to include (default `4`) |
| `encoding` | string | `ascii`, `unicode`, or `both` (default `both`) |
| `exe_path` | string | Absolute path to the packed EXE to unpack |
| `output_path` | string | Destination for the unpacked PE (default: `<original_name>_unpacked.exe` next to the input) |
| `stop_after_dump` | boolean | Whether to stop the debug session after dumping (default `true`) |

> **Workflow**: `scan_pe_strings` → understand what the packed binary contains → `unpack_from_memory` → `deobfuscate_assembly` → load the clean file in dnSpy.

---

### Protection / Malware Analysis Tools

Static-first triage helpers focused on suspicious managed loaders, protected samples, and payload staging patterns. These tools do **not** execute the target assembly.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `triage_sample` | High-level triage report combining suspicious strings, P/Invokes, suspicious API usage, static constructors, decryptor candidates, embedded PE payloads, and high-entropy resources | `assembly_name` or `file_path` | `max_strings` |
| `get_strings` | Extract useful managed strings from literal fields and `ldstr` sites, grouped by value with occurrence locations | `assembly_name` or `file_path` | `min_length`, `max_results`, `filter_pattern` |
| `search_il_pattern` | Search IL text or exact opcode sequences across all methods in a loaded assembly | `assembly_name` or `file_path` plus `pattern` or `opcode_sequence` | `use_regex`, `type_pattern`, `method_pattern`, `max_results` |
| `analyze_static_constructors` | Summarize type static constructors (`.cctor`) with strings, calls, field writes, and suspicious indicators common in bootstrap code | `assembly_name` or `file_path` | `max_results` |
| `detect_string_encryption` | Heuristically rank methods that look like string decryptors/decoders | `assembly_name` or `file_path` | `max_results` |
| `find_byte_arrays` | Find field RVA blobs and method-level byte-array construction sites that may hide payloads, keys, or encrypted configuration | `assembly_name` or `file_path` | `max_results` |
| `find_embedded_pes` | Detect PE payloads embedded in ManifestResource blobs or field data by looking for `MZ` headers and DOS stub markers | `assembly_name` or `file_path` | `max_results` |
| `detect_anti_debug` | Heuristically detect native debugger APIs, managed `Debugger` probes, blacklist strings, and suspicious `.cctor` checks | `assembly_name` or `file_path` | `max_results` |
| `detect_anti_tamper` | Heuristically detect `<Module>` bootstrap logic, `RuntimeHelpers.InitializeArray`, field RVA blobs, integrity/self-inspection APIs, and protection-family strings | `assembly_name` or `file_path` | `max_results` |
| `get_protection_report` | Aggregate anti-debug, anti-tamper, decryptor, payload-staging, and entropy heuristics into a single protection-oriented report | `assembly_name` or `file_path` | `max_results` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `file_path` | string | Optional path of a **loaded** assembly used to disambiguate duplicate assembly names |
| `max_strings` | integer | (`triage_sample`) Max suspicious strings to include in the summary (default `25`) |
| `min_length` | integer | (`get_strings`) Minimum extracted string length (default `4`) |
| `max_results` | integer | Max returned matches/candidates/entries depending on tool |
| `filter_pattern` | string | (`get_strings`) Regex applied to extracted values |
| `pattern` | string | (`search_il_pattern`) Substring or regex matched against textual IL lines |
| `opcode_sequence` | array of strings | (`search_il_pattern`) Exact opcode chain such as `["ldstr", "call", "stsfld"]` |
| `use_regex` | boolean | (`search_il_pattern`) Treat `pattern` as regex |
| `type_pattern` | string | (`search_il_pattern`) Regex filter for declaring type full names |
| `method_pattern` | string | (`search_il_pattern`) Regex filter for method names |

#### Notes

- `triage_sample` is the best first call when you open an unknown suspicious assembly in dnSpy
- `get_strings` focuses on **managed literals and `ldstr` sites**, not raw file carving — use `scan_pe_strings` when you need on-disk plaintext extraction
- `analyze_static_constructors` is especially useful for loaders that stage config, resources, byte arrays, or bootstrap hooks in `.cctor`
- `detect_string_encryption` is heuristic ranking only; use it to shortlist methods before manual decompilation or patching
- `find_embedded_pes` complements `list_resources` / `get_resource` by pointing directly at likely second-stage payload carriers
- `detect_anti_debug` is useful before live debugging so you can patch or route around obvious debugger checks first
- `detect_anti_tamper` helps prioritize `<Module>` and RVA/InitializeArray-heavy bootstrap logic common in protected assemblies
- `get_protection_report` is the best one-call summary when you want the overall protection picture before deciding whether to decompile, patch, or unpack

---

### Deobfuscation Tools

Two de4dot integration modes: **in-process** (`deobfuscate_assembly` — uses bundled de4dot libraries, available in all builds) and **external process** (`run_de4dot` — spawns `de4dot.exe`, supports dynamic string decryption, available in all builds).

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_deobfuscators` | List all obfuscator types supported by the in-process de4dot engine | — | — |
| `detect_obfuscator` | Detect which obfuscator was applied to a .NET assembly file on disk using de4dot's heuristic detection | `file_path` | — |
| `deobfuscate_assembly` | Deobfuscate a .NET assembly in-process: renames mangled symbols, deobfuscates control flow, decrypts strings | `file_path`, `output_path` | `obfuscator_type`, `rename_symbols` |
| `save_deobfuscated` | Return a previously deobfuscated file as a Base64-encoded blob. Useful when the output file cannot be accessed directly | `file_path` | — |
| `run_de4dot` | Run `de4dot.exe` as an external process. Supports dynamic string decryption and ConfuserEx method decryption that require a separate process | `file_path` | `output_path`, `obfuscator_type`, `dont_rename`, `no_cflow_deob`, `string_decrypter`, `extra_args`, `de4dot_path`, `timeout_ms` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `file_path` | string | Absolute path to the .NET assembly on disk |
| `output_path` | string | Absolute path for the cleaned output assembly |
| `obfuscator_type` | string | Force a specific obfuscator type code (`cr` for ConfuserEx, `un` for unknown/auto, etc.). Omit to let de4dot auto-detect. |
| `rename_symbols` | boolean | (`deobfuscate_assembly`) Whether to rename obfuscated symbols (default `true`) |
| `dont_rename` | boolean | (`run_de4dot`) Skip symbol renaming if `true` (default `false`) |
| `no_cflow_deob` | boolean | (`run_de4dot`) Skip control-flow deobfuscation if `true` (default `false`) |
| `string_decrypter` | string | (`run_de4dot`) String decrypter mode: `none`, `default`, `static`, `delegate`, `emulate` |
| `extra_args` | string | (`run_de4dot`) Additional de4dot command-line arguments passed verbatim |
| `de4dot_path` | string | (`run_de4dot`) Override path to `de4dot.exe`. Defaults to well-known search paths. |
| `timeout_ms` | integer | (`run_de4dot`) Max milliseconds to wait for de4dot to finish (default `120000`) |

---

### Window / Dialog Tools

Enumerate and dismiss dialog boxes (Win32 `MessageBox`, `#32770` dialogs, and WPF windows) that appear in the dnSpy process — for example, error popups that block a debug session. No debug session required.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_dialogs` | List all active dialog/message-box windows. Returns title, HWND (hex), message text, and available button labels for each | — | — |
| `close_dialog` | Close a dialog by clicking a named button. Resolves the target by HWND or picks the first active dialog | — | `hwnd`, `button` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `hwnd` | string | Hex HWND of the target dialog, as returned by `list_dialogs` (e.g. `"1A2B3C"`). If omitted, the first active dialog is used |
| `button` | string | Button to click (case-insensitive, EN and ES): `ok`/`aceptar`, `yes`/`sí`, `no`, `cancel`/`cancelar`, `retry`/`reintentar`, `ignore`/`omitir`. Default: `ok` |

> **Button matching** — the tool first checks common exact tokens (EN + ES), then falls back to substring matching. If no button matches, `WM_CLOSE` is sent to the dialog.

#### Example

```json
// List all open dialogs
{ "tool": "list_dialogs" }

// Dismiss the first dialog by clicking OK
{ "tool": "close_dialog" }

// Dismiss a specific dialog by HWND, clicking Cancel
{ "tool": "close_dialog", "arguments": { "hwnd": "1A2B3C", "button": "cancel" } }
```

---

### Utility

| Tool | Description | Required params |
|------|-------------|-----------------|
| `list_tools` | Return the schema for the current client-visible tool catalog as JSON (full by default; config can filter/disable per client) | — |
| `get_mcp_config` | Return the current MCP server configuration and the path to `mcp-config.json` | — |
| `reload_mcp_config` | Reload `mcp-config.json` from disk without restarting dnSpy | — |

---

## Pattern Syntax

Several tools accept a `name_pattern` or `query` parameter that supports both **glob** and **regex** syntax. The engine auto-detects the mode.

| Mode | Detected when | Examples |
|------|---------------|---------|
| **Glob** | Pattern contains only `*` or `?` wildcards | `Get*`, `*Controller`, `On?Click` |
| **Regex** | Pattern contains any of `^ $ [ ( \| + {` | `^Get[A-Z]`, `Controller$`, `^I[A-Z].*Service$` |
| **Substring** | No special characters (for `search_types` only) | `Player`, `Manager` |

All pattern matching is **case-insensitive**.

```
# Find all types whose name starts with "Player"
name_pattern: "Player*"

# Find all interfaces (start with I, followed by uppercase)
name_pattern: "^I[A-Z]"

# Find methods ending in "Async"
name_pattern: "Async$"

# Find all Get* or Set* methods
name_pattern: "^(Get|Set)[A-Z]"
```

---

## Pagination

List operations return paginated results. The default page size is **50 items**.

```json
{
  "items": [ ... ],
  "total_count": 312,
  "returned_count": 50,
  "nextCursor": "eyJvZmZzZXQiOjUwLCJwYWdlU2l6ZSI6NTB9"
}
```

To fetch the next page, pass the `nextCursor` value as the `cursor` argument in the next call. When `nextCursor` is absent, you have reached the last page.

---

## Usage Examples

### Workflow: explore an unknown assembly

```
1. list_assemblies                           → find "UnityEngine.CoreModule"
2. get_assembly_info  assembly=UnityEngine…  → see namespaces
3. list_types  assembly=…  namespace=UnityEngine  name_pattern="*Manager"
4. get_type_info  assembly=…  type=UnityEngine.NetworkManager
5. decompile_method  …  method=Awake
```

### Search with regex across all assemblies

```json
{ "tool": "search_types", "arguments": { "query": "^I[A-Z].*Repository$" } }
```

### Dump a Unity game module from memory

```json
{ "tool": "list_runtime_modules", "arguments": { "name_filter": "Assembly-CSharp*" } }

{ "tool": "dump_module_from_memory", "arguments": {
    "module_name": "Assembly-CSharp.dll",
    "output_path": "C:\\dump\\Assembly-CSharp_dump.dll"
}}
```

### Set a breakpoint and inspect the call stack

```json
{ "tool": "set_breakpoint", "arguments": {
    "assembly_name": "Assembly-CSharp",
    "type_full_name": "PlayerController",
    "method_name": "TakeDamage"
}}

{ "tool": "get_call_stack" }
```

### Change a private method to public and save

```json
{ "tool": "change_member_visibility", "arguments": {
    "assembly_name": "MyAssembly",
    "type_full_name": "MyNamespace.MyClass",
    "member_kind": "method",
    "member_name": "InternalHelper",
    "new_visibility": "public"
}}

{ "tool": "save_assembly", "arguments": {
    "assembly_name": "MyAssembly",
    "output_path": "C:\\patched\\MyAssembly.dll"
}}
```

### Read process memory at a known address

```json
{ "tool": "read_process_memory", "arguments": {
    "address": "0x7FFE00001000",
    "size": 256
}}
```

### Unpack a ConfuserEx-protected EXE and deobfuscate it

```
1. scan_pe_strings  assembly_name=MyApp  → confirm it's packed (few readable strings)
2. unpack_from_memory  exe_path=C:\MyApp.exe  output_path=C:\MyApp_unpacked.exe
3. detect_obfuscator  file_path=C:\MyApp_unpacked.exe  → identify remaining obfuscation
4. deobfuscate_assembly  file_path=C:\MyApp_unpacked.exe  output_path=C:\MyApp_clean.dll
```

### Patch anti-debug stubs and save a clean binary

```
1. list_pinvoke_methods  assembly=MyApp  type=AntiDebugClass
   → finds "CheckRemoteDebuggerPresent" → kernel32.dll
2. patch_method_to_ret  assembly=MyApp  type=AntiDebugClass  method=CheckRemoteDebuggerPresent
3. save_assembly  assembly=MyApp  output_path=C:\MyApp_patched.exe
```

### Load an assembly from disk or from a running process

```json
// Load a .NET DLL from disk
{ "tool": "load_assembly", "arguments": {
    "file_path": "C:\\dump\\MyApp_unpacked.dll"
}}

// Load a raw memory-layout dump (VAs instead of file offsets)
{ "tool": "load_assembly", "arguments": {
    "file_path": "C:\\dump\\MyApp_memdump.bin",
    "memory_layout": true
}}

// Dump from a running process and load directly into dnSpy
{ "tool": "load_assembly", "arguments": {
    "pid": 1234,
    "module_name": "MyPlugin.dll"
}}
```

### Dismiss a dialog that is blocking a debug session

```json
{ "tool": "list_dialogs" }
// → [1] Title: "Error de depuración"
//       Hwnd: 1A2B3C  |  Type: Win32 (#32770)
//       Message: "No se puede continuar la operación."
//       Buttons: Aceptar, Cancelar

{ "tool": "close_dialog", "arguments": { "hwnd": "1A2B3C", "button": "aceptar" } }
// → Clicked 'Aceptar' in dialog 'Error de depuración'.
```

### Find all callers and usages of a suspicious type

```json
{ "tool": "find_who_uses_type", "arguments": {
    "assembly_name": "MyAssembly",
    "type_full_name": "MyNamespace.ObfuscatedLicenseChecker"
}}

{ "tool": "find_who_writes_field", "arguments": {
    "assembly_name": "MyAssembly",
    "type_full_name": "MyNamespace.ObfuscatedLicenseChecker",
    "field_name": "isValid"
}}
```

---

## Architecture

### Design Principles

- `dnSpy.MCP.Server` is an extension hosted inside dnSpy; it does not fork or embed a custom dnSpy tree
- MCP features are implemented inside `dnSpy.MCP.Server` without patching dnSpy source
- HoLLy-inspired functionality is ported only when it can be exposed as stable, serializable, non-UI MCP tools
- Echo integration is limited to the stable managed CFG subset that can be serialized cleanly for LLMs

### Catalog Metadata

The tool catalog now exposes metadata intended to improve client-side discoverability:

- `category`
- `hidden_by_default`
- `is_legacy`
- `preferred_replacement`
- `notes`

`tools/list` now returns the full 143-tool base catalog by default. If you want the old filtered view back, set `toolCatalogMode` to `"default"` in `mcp-config.json`, or pass `mode="default"` in the request. `list_tools` mirrors the same behavior and returns the same catalog metadata in-band. Global `disabledTools` and per-client `clientToolPolicies` remove tools from both `tools/list` and `tools/call`.

| File | Responsibility |
|------|---------------|
| `src/Communication/McpServer.cs` | Streamable HTTP listener and JSON-RPC dispatch |
| `src/Application/McpTools.cs` | Central tool registry, schema definitions, routing |
| `src/Application/AssemblyTools.cs` | Assembly/type listing, P/Invoke analysis |
| `src/Application/TypeTools.cs` | Type detail, methods, IL, BFS path analysis |
| `src/Application/EditTools.cs` | Metadata editing, decompilation, assembly saving, method patching |
| `src/Application/DebugTools.cs` | Debugger state, breakpoints, stepping, process launch/attach |
| `src/Application/DumpTools.cs` | Runtime module enumeration, memory dump, PE section tools |
| `src/Application/MemoryInspectTools.cs` | Local variable inspection from paused debug frame |
| `src/Application/UsageFindingCommandTools.cs` | Cross-assembly IL usage analysis (callers, field reads/writes) |
| `src/Application/CodeAnalysisHelpers.cs` | Static call-graph, dependency chain, dead code analysis |
| `src/Application/De4dotTools.cs` | de4dot in-process integration; available in all builds |
| `src/Application/SkillsTools.cs` | Persistent skills knowledge base (Markdown + JSON) in `%APPDATA%\dnSpy\dnSpy.MCPServer\skills\` |
| `src/Application/ScriptTools.cs` | Roslyn C# scripting (`run_script`) |
| `src/Application/WindowTools.cs` | Win32 + WPF dialog enumeration and dismissal |
| `src/Presentation/TheExtension.cs` | MEF entry point, server lifecycle |
| `src/Contracts/McpProtocol.cs` | MCP DTOs (ToolInfo, CallToolResult, …) |

---

## Project Structure

```
dnSpy.MCP.Server/
├── dnSpy.MCP.Server.csproj   # Multi-target: net48 + net10.0-windows
├── CHANGELOG.md
├── README.md
├── RELEASE_NOTES.md
├── tools/
│   └── dnSpy.MCP.StdioProxy/      # Optional stdio -> HTTP MCP bridge
└── src/
    ├── Application/
    │   ├── AssemblyTools.cs         # Assembly & type listing
    │   ├── TypeTools.cs             # Type internals + IL
    │   ├── EditTools.cs             # Metadata editing, method patching
    │   ├── AgentCompatibilityTools.cs # AgentSmithers-style source / IL editing
    │   ├── DebugTools.cs            # Debugger integration
    │   ├── DumpTools.cs             # Memory dump & PE tools
    │   ├── MemoryInspectTools.cs    # Local variable inspection
    │   ├── UsageFindingCommandTools.cs  # IL usage analysis
    │   ├── CodeAnalysisHelpers.cs   # Call-graph & dependency analysis
    │   ├── MalwareAnalysisTools.cs  # Triage, strings, IL pattern, payload heuristics
    │   ├── De4dotTools.cs           # de4dot deobfuscation (all builds)
    │   ├── SourceMapTools.cs        # HoLLy-style non-UI SourceMap support
    │   ├── NativeRuntimeTools.cs    # Native reversing, export patching, DLL injection
    │   ├── InterceptionTools.cs     # Trace / hook persistent managed methods
    │   ├── SkillsTools.cs           # Skills knowledge base (MD + JSON)
    │   ├── ScriptTools.cs           # Roslyn C# scripting
    │   ├── WindowTools.cs           # Win32/WPF dialog management
    │   ├── McpTools.cs              # Tool registry & routing
    │   └── McpTools.Schemas.cs      # Tool schemas + catalog metadata
    ├── Communication/
    │   ├── McpServer.cs             # HTTP entrypoint / route dispatch
    │   └── McpInteropTools.cs       # MCP ecosystem compatibility helpers
    ├── Configuration/
    │   └── McpConfig.cs             # JSON config next to the plugin DLL
    ├── Contracts/
    │   └── McpProtocol.cs           # DTO types
    ├── Core/
    │   ├── McpProtocolHelpers.cs
    │   ├── McpRequestDispatcher.cs
    │   └── McpSessionManager.cs
    ├── Helper/
    │   └── McpLogger.cs
    └── Presentation/
        ├── TheExtension.cs          # MEF export / entry point
        ├── ToolbarCommands.cs
        ├── McpSettings.cs
        └── McpSettingsPage.cs
```

---

## Configuration

### `mcp-config.json`

A `mcp-config.json` file is created automatically next to the MCP Server DLL on first run. Edit it to change network or de4dot settings — **no rebuild required**.

```json
{
  "host": "127.0.0.1",
  "port": 3100,
  "listenerMode": "httpListener",
  "toolCatalogMode": "full",
  "disabledTools": [],
  "clientToolPolicies": [
    {
      "clientNamePattern": "Claude",
      "toolCatalogMode": "full",
      "disabledTools": []
    },
    {
      "clientNamePattern": "Codex",
      "toolCatalogMode": "full",
      "disabledTools": []
    }
  ],
  "requireApiKey": false,
  "apiKey": "",
  "enableRunScript": false,
  "de4dotExePath": "",
  "de4dotSearchPaths": [],
  "de4dotMaxSearchDepth": 6
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `host` | `"127.0.0.1"` | Bind address for local-only access. `localhost` is also supported, but the server binds only the configured host value. Use `"0.0.0.0"` to listen on all interfaces (for remote debugging from a sandbox or VM). See note below. |
| `port` | `3100` | TCP port the server listens on. |
| `listenerMode` | `"httpListener"` | Listener backend. Use `tcpListener` to bypass CrossOver/Wine `HttpListener` quirks; use `httpListener` when you need legacy `/sse` + `/message`; use `auto` to prefer `tcpListener` in Wine-like environments and `httpListener` elsewhere. |
| `toolCatalogMode` | `"full"` | Default tool listing mode for clients that do not explicitly request a mode. Use `"full"` to show the entire catalog by default, or `"default"` to hide tools marked `hidden_by_default`. |
| `disabledTools` | `[]` | Global denylist. Removed from `tools/list` and rejected by `tools/call` for every client. |
| `clientToolPolicies` | `[]` | Per-client overrides matched by `initialize.clientInfo.name` substring. Matching rules can override `toolCatalogMode` and append more `disabledTools`. |
| `requireApiKey` | `false` | Require `X-API-Key` / `Authorization: Bearer` on every request. |
| `apiKey` | `""` | API key value. Generate with `openssl rand -hex 32`. |
| `enableRunScript` | `false` | Enable the `run_script` tool (Roslyn C# scripting). Set to `true` only in trusted environments. |
| `de4dotExePath` | `""` | Absolute path to `de4dot.exe`. Leave empty for auto-discovery. |
| `de4dotSearchPaths` | `[]` | Extra directories to search for `de4dot.exe` (absolute or relative to this file). |
| `de4dotMaxSearchDepth` | `6` | Directory levels to walk upward when auto-discovering a sibling `de4dot` repository. |

Example: keep the full catalog globally, but hide selected runtime mutation tools only from Claude:

```json
{
  "toolCatalogMode": "full",
  "disabledTools": [],
  "clientToolPolicies": [
    {
      "clientNamePattern": "Claude",
      "disabledTools": [
        "write_process_memory",
        "patch_native_function",
        "run_script"
      ]
    }
  ]
}
```

> **Remote access** — when `listenerMode` is `httpListener` and `host` is `"0.0.0.0"` or `"*"`, the server binds with the HttpListener wildcard `+`. This requires a one-time URL ACL reservation (run as Administrator):
> ```
> netsh http add urlacl url=http://+:3100/ user=Everyone
> ```
> Then point your MCP client at `http://<dnspy-machine-ip>:3100/mcp`. In `tcpListener` mode, normal socket binding is used instead and no HttpListener URL ACL is required.

After editing `mcp-config.json`, call `reload_mcp_config`. Changes to `host`, `port`, or `listenerMode` take effect the next time the MCP server restarts. `toolCatalogMode`, `disabledTools`, and `clientToolPolicies` take effect immediately for subsequent `tools/list` / `tools/call` requests after reload.

### Verify the server is running

```bash
# Windows (PowerShell)
Invoke-RestMethod http://127.0.0.1:3100

# Windows (cmd)
curl http://127.0.0.1:3100

# Check port is listening
netstat -ano | findstr :3100
```

---

## Troubleshooting

| Symptom | Likely cause | Solution |
|---------|-------------|----------|
| Extension not loading | MCP files not under `bin/Extensions/dnSpy.MCP.Server` | Rebuild with `-c Release`; check output path in `.csproj` and the extracted zip layout |
| `Connection refused` on port 3100 | Server failed to start | Check dnSpy's log window; port 3100 may be in use — `netstat -ano \| findstr :3100` |
| Tool returns `Unknown tool: …` | Name typo or outdated client cache | Call `list_tools` to see the current tool list |
| `Assembly not found` | Name mismatch | Call `list_assemblies` and use the exact `Name` value shown |
| `Type not found` | Wrong `type_full_name` | Use `list_types` or `search_types` to find the exact full name |
| `Debugger is not active` | No debug session running | Start debugging via `start_debugging` or dnSpy's **Debug** menu |
| `No paused process found` | Process is still running | Call `break_debugger` first |
| `dump_module_from_memory` returns no bytes | Module has no address (pure dynamic) | Some in-memory modules emitted by reflection emit cannot be dumped |
| Dump `IsFileLayout: false` | Memory layout dump | Use `dump_module_unpacked` instead — it performs the layout fix automatically |
| `unpack_from_memory` fails with anti-debug error | Process kills itself before EntryPoint | Use `patch_method_to_ret` to neutralize anti-debug methods first, save the patched binary, then retry |
| `Failed to connect` when adding MCP server | Wrong transport type or endpoint | Use the streamable HTTP endpoint `http://127.0.0.1:3100/mcp` and ensure the client is configured for HTTP/streamable HTTP, not SSE |
| `Could not load file or assembly 'System.Text.Json'` / `System.Collections.Immutable` / `System.Memory` | net48 plugin copied into a dnSpy install whose `*.config` redirects are too old | Redeploy from a clean dnSpy tree and add the binding redirects shown in **Manual net48 dependency repair** above |
| dnSpy still loads an old MCP build after redeploy | Multiple dnSpy folders or stale copied plugin files | Search for every `dnSpy.MCP.Server.x.dll`, delete old copies, and redeploy only the current `bin/Extensions/dnSpy.MCP.Server` bundle into a fresh local install |
| `dump_cordbg_il` returns E_NOINTERFACE errors | COM STA apartment threading | `ICorDebugModule` COM objects belong to the CorDebug engine thread; calling from another STA fails. This is a known limitation — use `dump_module_unpacked` instead for memory dumps. |
| `Connection refused` from VM / sandbox | `host` is still `"localhost"` | Set `"host": "0.0.0.0"` in `mcp-config.json` and run `netsh http add urlacl url=http://+:3100/ user=Everyone` as Administrator. |
| `Access denied` when binding to `0.0.0.0` | Missing URL ACL | Run `netsh http add urlacl url=http://+:3100/ user=Everyone` as Administrator (replace `3100` with your configured port). |
| Debug session appears frozen / no response | A dialog box is blocking the UI thread | Call `list_dialogs` to detect open dialogs, then `close_dialog` to dismiss them and unblock the session. |

---

## Contributing

1. Fork or branch from `master`
2. Implement your changes under `src/Application/` or `src/Communication/`
3. Register new tools in `McpTools.cs` (`GetAvailableTools` + `ExecuteTool` switch)
4. Build with `dotnet build … --nologo` — must produce **0 errors, 0 warnings**
5. Manually test via any MCP client (`list_tools` to verify registration)
6. Submit a PR with a description of the new tool(s) and their parameters

---

## License

Copyright (C) 2026 @chichicaste.

Licensed under the GNU General Public License, version 3 or, at your option, any later version. See [LICENSE](LICENSE) for details.
