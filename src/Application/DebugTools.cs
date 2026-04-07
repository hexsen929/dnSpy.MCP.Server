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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Debugger integration tools: get state, manage breakpoints, control execution, and inspect the call stack.
	/// Requires the Debugger extension to be loaded. Operations that need a paused debugger will return
	/// descriptive errors when the debugger is not in the required state.
	/// </summary>
	[Export(typeof(DebugTools))]
	public sealed class DebugTools {
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgCodeBreakpointsService> breakpointsService;
		readonly Lazy<DbgDotNetBreakpointFactory> breakpointFactory;
		readonly IDsDocumentService documentService;
		readonly Lazy<AttachableProcessesService> attachableProcessesService;
		readonly Lazy<DbgExceptionSettingsService> exceptionSettingsService;
		static readonly string[] SupportedBreakKinds = new[] {
			PredefinedBreakKinds.DontBreak,
			PredefinedBreakKinds.CreateProcess,
			PredefinedBreakKinds.ModuleCctorOrEntryPoint,
			PredefinedBreakKinds.EntryPoint,
		};
		string? lastDebugTargetPath;

		[ImportingConstructor]
		public DebugTools(
			Lazy<DbgManager> dbgManager,
			Lazy<DbgCodeBreakpointsService> breakpointsService,
			Lazy<DbgDotNetBreakpointFactory> breakpointFactory,
			IDsDocumentService documentService,
			Lazy<AttachableProcessesService> attachableProcessesService,
			Lazy<DbgExceptionSettingsService> exceptionSettingsService) {
			this.dbgManager = dbgManager;
			this.breakpointsService = breakpointsService;
			this.breakpointFactory = breakpointFactory;
			this.documentService = documentService;
			this.attachableProcessesService = attachableProcessesService;
			this.exceptionSettingsService = exceptionSettingsService;
		}

		/// <summary>
		/// Returns the current debugger state.
		/// Arguments: none
		/// </summary>
		public CallToolResult GetDebuggerState() {
			try {
				var snapshot = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);

				var result = JsonSerializer.Serialize(new {
					IsDebugging = snapshot.IsDebugging,
					IsRunning = snapshot.EffectiveIsRunning,
					ManagerIsRunning = snapshot.ManagerIsRunning,
					ProcessCount = snapshot.ProcessCount,
					Processes = snapshot.Processes
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "GetDebuggerState failed");
				var result = JsonSerializer.Serialize(new {
					IsDebugging = false,
					IsRunning = (bool?)null,
					ProcessCount = 0,
					Processes = Array.Empty<object>(),
					Error = ex.Message
				}, new JsonSerializerOptions { WriteIndented = true });
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
		}

		/// <summary>
		/// Lists all code breakpoints currently registered in dnSpy.
		/// Arguments: none
		/// </summary>
		public CallToolResult ListBreakpoints() {
			try {
				var service = breakpointsService.Value;
				var bps = service.VisibleBreakpoints.Select((bp, idx) => new {
					Index = idx,
					IsEnabled = bp.IsEnabled,
					IsHidden = bp.IsHidden,
					BoundCount = bp.BoundBreakpoints.Length,
					LocationType = bp.Location?.Type ?? "Unknown",
					LocationString = bp.Location?.ToString() ?? "Unknown",
					Labels = bp.Labels?.ToArray() ?? Array.Empty<string>()
				}).ToList();

				var result = JsonSerializer.Serialize(new {
					BreakpointCount = bps.Count,
					Breakpoints = bps
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "ListBreakpoints failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error listing breakpoints: {ex.Message}" } },
					IsError = true
				};
			}
		}

		/// <summary>
		/// Sets a breakpoint at a method entry point (IL offset 0 by default).
		/// Arguments: assembly_name, type_full_name, method_name, il_offset (optional, default 0)
		/// The breakpoint persists across debug sessions via dnSpy's breakpoint storage.
		/// </summary>
		public CallToolResult SetBreakpoint(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("method_name", out var methodNameObj))
				throw new ArgumentException("method_name is required");

			string? filePath = null;
			if (arguments.TryGetValue("file_path", out var fpObj))
				filePath = fpObj?.ToString();

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "", filePath);
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var method = type.Methods.FirstOrDefault(m => m.Name.String == (methodNameObj.ToString() ?? ""));
			if (method == null)
				throw new ArgumentException($"Method not found: {methodNameObj}");

			uint ilOffset = 0;
			if (arguments.TryGetValue("il_offset", out var offsetObj) && offsetObj is JsonElement offsetElem)
				offsetElem.TryGetUInt32(out ilOffset);

			var module = method.Module;
			var moduleId = ModuleId.CreateFromFile(module);
			var token = method.MDToken.Raw;

			// Optional condition expression
			string? conditionExpr = null;
			if (arguments.TryGetValue("condition", out var condObj) && !string.IsNullOrWhiteSpace(condObj?.ToString()))
				conditionExpr = condObj!.ToString()!.Trim();

			try {
				var bp = breakpointFactory.Value.Create(moduleId, token, ilOffset);
				if (bp == null) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = $"A breakpoint already exists at {method.FullName} +IL_{ilOffset:X4}" } }
					};
				}

				string? rewrittenCondition = null;
				List<string>? aliasNotes = null;
				if (conditionExpr != null) {
					var rewrite = DebuggerExpressionAliasHelper.RewriteBreakpointCondition(conditionExpr, method);
					if (rewrite.Error != null)
						throw new ArgumentException(rewrite.Error);
					rewrittenCondition = rewrite.RewrittenExpression;
					aliasNotes = rewrite.Notes.Count == 0 ? null : rewrite.Notes;
					bp.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, rewrittenCondition);
				}

				var result = JsonSerializer.Serialize(new {
					Success    = true,
					Method     = method.FullName,
					ILOffset   = ilOffset,
					Token      = $"0x{token:X8}",
					ModulePath = module.Location,
					IsEnabled  = bp.IsEnabled,
					Condition  = conditionExpr,
					RewrittenCondition = rewrittenCondition != conditionExpr ? rewrittenCondition : null,
					AliasNotes = aliasNotes
				}, new JsonSerializerOptions {
					WriteIndented = true,
					DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
				});

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "SetBreakpoint failed");
				throw new Exception($"Failed to set breakpoint at {method.FullName}: {ex.Message}");
			}
		}

		public CallToolResult SetBreakpointEx(Dictionary<string, object>? arguments) => SetBreakpoint(arguments);

		public CallToolResult BatchBreakpoints(Dictionary<string, object>? arguments) {
			if (arguments == null || !arguments.TryGetValue("items", out var itemsObj))
				throw new ArgumentException("items is required");

			var entries = ParseBatchBreakpointItems(itemsObj);
			if (entries.Count == 0)
				throw new ArgumentException("items must contain at least one breakpoint request.");

			var results = new List<object>();
			foreach (var entry in entries) {
				var entryArgs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) {
					["assembly_name"] = entry.AssemblyName,
					["type_full_name"] = entry.TypeFullName,
					["method_name"] = entry.MethodName
				};
				if (entry.IlOffset.HasValue)
					entryArgs["il_offset"] = entry.IlOffset.Value;
				if (!string.IsNullOrWhiteSpace(entry.Condition))
					entryArgs["condition"] = entry.Condition!;
				if (!string.IsNullOrWhiteSpace(entry.FilePath))
					entryArgs["file_path"] = entry.FilePath!;

				var result = SetBreakpoint(entryArgs);
				results.Add(new {
					entry.AssemblyName,
					entry.TypeFullName,
					entry.MethodName,
					entry.IlOffset,
					Success = !result.IsError,
					Result = result.Content.FirstOrDefault()?.Text
				});
			}

			var json = JsonSerializer.Serialize(new {
				Requested = entries.Count,
				Created = results.Count(r => (bool)r.GetType().GetProperty("Success")!.GetValue(r)!),
				Breakpoints = results
			}, new JsonSerializerOptions { WriteIndented = true });
			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		public CallToolResult GetMethodByToken(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("token", out var tokenObj))
				throw new ArgumentException("token is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var token = ParseMetadataToken(tokenObj);
			var method = assembly.Modules
				.SelectMany(m => GetAllTypesRecursive(m.Types))
				.SelectMany(t => t.Methods)
				.FirstOrDefault(m => m.MDToken.Raw == token);
			if (method == null)
				throw new ArgumentException($"MethodDef token not found: 0x{token:X8}");

			var runtimeInfo = FindLoadedRuntimeMethodInfo(assembly, method);
			var json = JsonSerializer.Serialize(new {
				AssemblyName = assembly.Name.String,
				TypeFullName = method.DeclaringType.FullName,
				MethodName = method.Name.String,
				Signature = method.FullName,
				Token = $"0x{method.MDToken.Raw:X8}",
				RVA = method.RVA == 0 ? null : $"0x{(uint)method.RVA:X8}",
				HasBody = method.HasBody,
				IsStatic = method.IsStatic,
				Parameters = method.Parameters
					.Where(p => p.IsNormalMethodParameter)
					.OrderBy(p => p.MethodSigIndex)
					.Select(p => new {
						Index = p.MethodSigIndex,
						Name = p.Name,
						Type = p.Type?.FullName
					})
					.ToList(),
				JitState = runtimeInfo.HasLoadedModule ? "module_loaded" : "unknown",
				NativeAddress = runtimeInfo.NativeAddress,
				LoadedModule = runtimeInfo.ModuleName,
				ProcessId = runtimeInfo.ProcessId
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		/// <summary>
		/// Removes a breakpoint from a method.
		/// Arguments: assembly_name, type_full_name, method_name, il_offset (optional, default 0)
		/// </summary>
		public CallToolResult RemoveBreakpoint(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("method_name", out var methodNameObj))
				throw new ArgumentException("method_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var method = type.Methods.FirstOrDefault(m => m.Name.String == (methodNameObj.ToString() ?? ""));
			if (method == null)
				throw new ArgumentException($"Method not found: {methodNameObj}");

			uint ilOffset = 0;
			if (arguments.TryGetValue("il_offset", out var offsetObj) && offsetObj is JsonElement offsetElem)
				offsetElem.TryGetUInt32(out ilOffset);

			var module = method.Module;
			var moduleId = ModuleId.CreateFromFile(module);
			var token = method.MDToken.Raw;

			var bp = breakpointFactory.Value.TryGetBreakpoint(moduleId, token, ilOffset);
			if (bp == null) {
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"No breakpoint found at {method.FullName} +IL_{ilOffset:X4}" } }
				};
			}

			breakpointsService.Value.Remove(bp);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = $"Breakpoint removed from {method.FullName} +IL_{ilOffset:X4}" } }
			};
		}

		/// <summary>
		/// Removes all visible breakpoints.
		/// Arguments: none
		/// </summary>
		public CallToolResult ClearAllBreakpoints() {
			try {
				breakpointsService.Value.Clear();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "All breakpoints cleared." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to clear breakpoints: {ex.Message}");
			}
		}

		/// <summary>
		/// Resumes execution of all paused processes.
		/// Arguments: none
		/// </summary>
		public CallToolResult ContinueDebugger() {
			try {
				var snapshot = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);
				if (!snapshot.IsDebugging || snapshot.ProcessCount == 0)
					throw new InvalidOperationException("No active debug session.");
				var pausedBefore = snapshot.Processes
					.Where(p => string.Equals(p.State, DbgProcessState.Paused.ToString(), StringComparison.OrdinalIgnoreCase))
					.ToList();
				if (pausedBefore.Count == 0) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = "No paused process is currently available. The debugger is already running or still initializing." } }
					};
				}
				DebuggerDispatcherHelper.Invoke(() => dbgManager.Value.RunAll());

				var deadline = DateTime.UtcNow.AddSeconds(2);
				while (DateTime.UtcNow < deadline) {
					snapshot = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);
					if (!snapshot.IsDebugging || snapshot.ProcessCount == 0) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = BuildSessionEndedMessage("Continue", snapshot) } },
							IsError = true
						};
					}
					if (!snapshot.Processes.Any(p => string.Equals(p.State, DbgProcessState.Paused.ToString(), StringComparison.OrdinalIgnoreCase))) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = "Debugger resumed (RunAll called)." } }
						};
					}
					Thread.Sleep(100);
				}

				var note = JsonSerializer.Serialize(new {
					Resumed = false,
					ProcessCount = snapshot.ProcessCount,
					Note = "RunAll was issued, but at least one process still reports a paused state. Retry get_debugger_state in a moment."
				}, new JsonSerializerOptions { WriteIndented = true });
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = note } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to continue debugger: {ex.Message}");
			}
		}

		/// <summary>
		/// Pauses all running processes.
		/// Arguments: none
		/// </summary>
		public CallToolResult BreakDebugger(Dictionary<string, object>? arguments) {
			try {
				var snapshot = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);
				if (!snapshot.IsDebugging || snapshot.ProcessCount == 0)
					throw new InvalidOperationException("No active debug session.");

				bool safePause = false;
				if (arguments != null && arguments.TryGetValue("safe_pause", out var spObj)) {
					if (spObj is JsonElement spElem && (spElem.ValueKind == JsonValueKind.True || spElem.ValueKind == JsonValueKind.False))
						safePause = spElem.GetBoolean();
					else if (bool.TryParse(spObj?.ToString(), out var parsed))
						safePause = parsed;
				}

				var alreadyPaused = DebuggerDispatcherHelper.CapturePausedSelection(dbgManager.Value, requireFrame: false);
				if (alreadyPaused.IsDebugging && alreadyPaused.ProcessId.HasValue && alreadyPaused.ProcessIsPaused) {
					var pausedJson = JsonSerializer.Serialize(new {
						Success = true,
						AlreadyPaused = true,
						ProcessId = alreadyPaused.ProcessId,
						ThreadCount = alreadyPaused.ThreadCount,
						ThreadId = alreadyPaused.ThreadId,
						HasStackFrame = alreadyPaused.HasStackFrame,
						Note = "A paused process is already available. Skipping BreakAll()."
					}, new JsonSerializerOptions { WriteIndented = true });
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = pausedJson } }
					};
				}

				DebuggerDispatcherHelper.Invoke(() => dbgManager.Value.BreakAll());
				var deadline = DateTime.UtcNow.AddSeconds(3);
				DebuggerPauseSnapshot? paused = null;
				while (DateTime.UtcNow < deadline) {
					paused = DebuggerDispatcherHelper.CapturePausedSelection(dbgManager.Value, requireFrame: false);
					if (!paused.IsDebugging)
						throw new InvalidOperationException(BuildSessionEndedMessage("Break", DebuggerDispatcherHelper.CaptureState(dbgManager.Value)));
					if (paused.ProcessId.HasValue && paused.ProcessIsPaused)
						break;
					Thread.Sleep(100);
				}

				if (paused == null || !paused.ProcessId.HasValue || !paused.ProcessIsPaused)
					throw new TimeoutException("BreakAll was issued, but no paused process became available within 3 seconds.");

				var json = JsonSerializer.Serialize(new {
					Success = true,
					Strategy = safePause ? "break_all_safe_pause" : "break_all",
					ProcessId = paused.ProcessId,
					ThreadCount = paused.ThreadCount,
					ThreadId = paused.ThreadId,
					HasStackFrame = paused.HasStackFrame,
					Note = safePause
						? "safe_pause requested. dnSpy MCP uses DbgManager.BreakAll() and does not call Debugger.Break()."
						: "Debugger paused using DbgManager.BreakAll()."
				}, new JsonSerializerOptions { WriteIndented = true });
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = json } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to break debugger: {ex.Message}");
			}
		}

		/// <summary>
		/// Stops all active debug sessions.
		/// Arguments: none
		/// </summary>
		public CallToolResult StopDebugging() {
			try {
				DebuggerDispatcherHelper.Invoke(() => dbgManager.Value.StopDebuggingAll());
				var deadline = DateTime.UtcNow.AddSeconds(2);
				while (DateTime.UtcNow < deadline) {
					var snapshot = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);
					if (!snapshot.IsDebugging || snapshot.ProcessCount == 0) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = "All debug sessions stopped." } }
						};
					}
					Thread.Sleep(100);
				}

				var remaining = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);
				var note = JsonSerializer.Serialize(new {
					Stopped = false,
					ProcessCount = remaining.ProcessCount,
					Note = "Stop request was sent, but the debugger is still winding down. Retry get_debugger_state in a moment."
				}, new JsonSerializerOptions { WriteIndented = true });
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = note } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to stop debugging: {ex.Message}");
			}
		}

		/// <summary>
		/// Returns the call stack of the current (or first paused) thread.
		/// Arguments: none — debugger must be paused.
		/// </summary>
		public CallToolResult GetCallStack() {
			try {
				var mgr = dbgManager.Value;
				var waitResult = DebuggerDispatcherHelper.WaitForPausedSelection(
					mgr,
					requireFrame: true,
					timeout: TimeSpan.FromSeconds(1));

				return DebuggerDispatcherHelper.Invoke(() => {
					if (waitResult.SessionEnded) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = BuildSessionEndedMessage("Call stack lookup", waitResult.LastActiveState) } },
							IsError = true
						};
					}
					if (!mgr.IsDebugging) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = "Debugger is not active. Start debugging first." } }
						};
					}
					if (waitResult.TimedOut || waitResult.NoActiveSession) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = DescribePausedThreadUnavailable(waitResult.LastPauseSelection, null) } }
						};
					}

					var selection = ResolveThreadSelectionCore(mgr, null, requirePaused: true, requireFrame: true);
					var currentThread = selection.Thread;
					if (currentThread == null) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = selection.Process == null ? "No paused process found. Debugger may still be running. Use break_debugger first." : (selection.ProcessIsPaused ? "No usable paused thread is available yet." : $"Process {selection.Process.Id} is not paused.") } }
						};
					}

					const int maxFrames = 50;
					var frames = new List<object>();
					var stackFrames = currentThread.GetFrames(maxFrames);
					try {
						foreach (var frame in stackFrames) {
							try {
								frames.Add(new {
									Index = frames.Count,
									FunctionToken = $"0x{frame.FunctionToken:X8}",
									FunctionOffset = frame.FunctionOffset,
									ModuleName = frame.Module?.Name ?? "Unknown",
									IsCurrentStatement = (frame.Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0
								});
							}
							finally {
								frame.Close();
							}
						}
					}
					finally {
						// GetFrames() returns owned frames; they were closed in the loop above.
					}

					var result = JsonSerializer.Serialize(new {
						ThreadId = currentThread.Id,
						ManagedId = currentThread.ManagedId,
						FrameCount = frames.Count,
						MaxFramesReturned = maxFrames,
						Frames = frames
					}, new JsonSerializerOptions { WriteIndented = true });

					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = result } }
					};
				});
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "GetCallStack failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error getting call stack: {ex.Message}" } },
					IsError = true
				};
			}
		}

		// ── start_debugging ──────────────────────────────────────────────────────

		/// <summary>
		/// Launches an EXE under the dnSpy debugger.
		/// Arguments: exe_path* | arguments | working_directory | break_kind (default "EntryPoint")
		/// </summary>
		public CallToolResult StartDebugging(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("exe_path", out var exePathObj))
				throw new ArgumentException("exe_path is required");

			var exePath = exePathObj.ToString() ?? string.Empty;
			if (!System.IO.File.Exists(exePath))
				throw new ArgumentException($"File not found: {exePath}");
			lastDebugTargetPath = exePath;

			string? commandLine = null;
			if (arguments.TryGetValue("arguments", out var argsObj))
				commandLine = argsObj?.ToString();

			string? workingDir = null;
			if (arguments.TryGetValue("working_directory", out var wdObj))
				workingDir = wdObj?.ToString();
			if (string.IsNullOrEmpty(workingDir))
				workingDir = System.IO.Path.GetDirectoryName(exePath);

			string breakKind = PredefinedBreakKinds.EntryPoint;
			if (arguments.TryGetValue("break_kind", out var bkObj) && bkObj?.ToString() is string bkStr && !string.IsNullOrEmpty(bkStr))
				breakKind = bkStr;
			breakKind = NormalizeBreakKind(breakKind)
				?? throw new ArgumentException(
					$"Unsupported break_kind '{breakKind}'. Supported values: {string.Join(", ", SupportedBreakKinds)}.");

			var opts = new DotNetFrameworkStartDebuggingOptions {
				Filename         = exePath,
				CommandLine      = commandLine ?? string.Empty,
				WorkingDirectory = workingDir,
				BreakKind        = breakKind,
			};

			string? error = null;
			DebuggerDispatcherHelper.Invoke(() => error = dbgManager.Value.Start(opts));
			bool sessionCreated = false;
			int processCount = 0;
			if (error == null) {
				var deadline = DateTime.UtcNow.AddSeconds(3);
				do {
					var snapshot = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);
					processCount = snapshot.ProcessCount;
					if (snapshot.IsDebugging && processCount > 0) {
						sessionCreated = true;
						break;
					}

					Thread.Sleep(100);
				} while (DateTime.UtcNow < deadline);
			}
			var pausedSelection = sessionCreated
				? DebuggerDispatcherHelper.CapturePausedSelection(dbgManager.Value, requireFrame: false)
				: null;

			var startNote = error == null
				? (sessionCreated
					? "Debug session created. Use get_debugger_state / wait_for_pause to observe pause state."
					: "Start request was accepted, but no debug session became visible within 3 seconds. The process may have exited immediately, failed before entry, or is still initializing.")
				: null;
			var breakKindNote = GetBreakKindDiagnosticNote(breakKind);
			if (!string.IsNullOrWhiteSpace(breakKindNote))
				startNote = string.IsNullOrWhiteSpace(startNote) ? breakKindNote : startNote + " " + breakKindNote;
			var archNote = GetArchitectureDiagnosticNote(exePath);
			if (!string.IsNullOrWhiteSpace(archNote))
				startNote = string.IsNullOrWhiteSpace(startNote) ? archNote : startNote + " " + archNote;

			var result = JsonSerializer.Serialize(new {
				Started = error == null,
				SessionCreated = sessionCreated,
				ProcessCount = processCount,
				PausedProcessId = pausedSelection?.ProcessId,
				PausedThreadVisible = pausedSelection?.ThreadId != null,
				PausedThreadCount = pausedSelection?.ThreadCount,
				PausedStackFrameVisible = pausedSelection?.HasStackFrame,
				ExePath = exePath,
				BreakKind = breakKind,
				Error = error,
				Note = startNote
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── attach_to_process ────────────────────────────────────────────────────

		/// <summary>
		/// Attaches the dnSpy debugger to a running .NET process by PID.
		/// Arguments: process_id*
		/// </summary>
		public CallToolResult AttachToProcess(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("process_id", out var pidObj))
				throw new ArgumentException("process_id is required");

			int pid = 0;
			if (pidObj is JsonElement pidElem) pidElem.TryGetInt32(out pid);
			else int.TryParse(pidObj?.ToString(), out pid);
			if (pid <= 0)
				throw new ArgumentException("process_id must be a positive integer");

			var processes = attachableProcessesService.Value
				.GetAttachableProcessesAsync(null, new[] { pid }, null)
				.GetAwaiter().GetResult();

			if (processes.Length == 0)
				throw new ArgumentException(
					$"No attachable .NET process found with PID {pid}. " +
					"The process may not exist or does not expose a supported runtime.");

			// Attach to the first matching entry (each entry represents one CLR runtime in the process)
			var target = processes[0];
			lastDebugTargetPath = TryGetProcessImagePath(pid);
			DebuggerDispatcherHelper.Invoke(() => target.Attach());

			bool sessionCreated = false;
			int processCount = 0;
			var deadline = DateTime.UtcNow.AddSeconds(3);
			do {
				var snapshot = DebuggerDispatcherHelper.CaptureState(dbgManager.Value);
				processCount = snapshot.ProcessCount;
				if (snapshot.IsDebugging && processCount > 0) {
					sessionCreated = true;
					break;
				}
				Thread.Sleep(100);
			} while (DateTime.UtcNow < deadline);
			var pausedSelection = sessionCreated
				? DebuggerDispatcherHelper.CapturePausedSelection(dbgManager.Value, requireFrame: false)
				: null;

			var result = JsonSerializer.Serialize(new {
				Attached    = true,
				AttachRequested = true,
				ProcessId   = pid,
				RuntimeName = target.RuntimeName,
				Name        = target.Name,
				SessionCreated = sessionCreated,
				ProcessCount = processCount,
				PausedProcessId = pausedSelection?.ProcessId,
				PausedThreadVisible = pausedSelection?.ThreadId != null,
				PausedThreadCount = pausedSelection?.ThreadCount,
				PausedStackFrameVisible = pausedSelection?.HasStackFrame,
				Note        = sessionCreated
					? "Attach completed and a debug session became visible."
					: "Attach was requested, but no debug session became visible within 3 seconds. Retry get_debugger_state."
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── Helpers ─────────────────────────────────────────────────────────────

	// ── Stepping ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Steps over the current statement.
	/// Arguments: thread_id (optional), process_id (optional), timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult StepOver(Dictionary<string, object>? arguments) =>
		StepImpl(arguments, DbgStepKind.StepOver);

	/// <summary>
	/// Steps into the current statement (enters called methods).
	/// Arguments: thread_id (optional), process_id (optional), timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult StepInto(Dictionary<string, object>? arguments) =>
		StepImpl(arguments, DbgStepKind.StepInto);

	/// <summary>
	/// Steps out of the current method (runs until caller resumes).
	/// Arguments: thread_id (optional), process_id (optional), timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult StepOut(Dictionary<string, object>? arguments) =>
		StepImpl(arguments, DbgStepKind.StepOut);

	/// <summary>
	/// Returns the current execution location (top frame of current/first paused thread).
	/// Arguments: thread_id (optional), process_id (optional)
	/// </summary>
	public CallToolResult GetCurrentLocation(Dictionary<string, object>? arguments) {
		var mgr = dbgManager.Value;
		var processId = ParseOptionalUInt32(arguments, "process_id");
		var waitResult = DebuggerDispatcherHelper.WaitForPausedSelection(
			mgr,
			processId: processId,
			requireFrame: true,
			timeout: TimeSpan.FromSeconds(1));
		if (waitResult.NoActiveSession)
			throw new InvalidOperationException("No active debug session.");
		if (waitResult.SessionEnded)
			throw new InvalidOperationException(BuildSessionEndedMessage("Current location lookup", waitResult.LastActiveState));
		if (waitResult.TimedOut)
			throw new InvalidOperationException(DescribePausedThreadUnavailable(waitResult.LastPauseSelection, processId));

		return DebuggerDispatcherHelper.Invoke(() => {
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("No active debug session.");

			var selection = ResolveThreadSelectionCore(mgr, arguments, requirePaused: true, requireFrame: true);
			var thread = selection.Thread;
			if (thread == null)
				throw new InvalidOperationException(
					selection.Process == null
						? "No paused process found. Use break_debugger or wait for a breakpoint."
						: (selection.ProcessIsPaused
							? "No paused thread found. The process is paused but dnSpy has not published a usable stack yet. Retry wait_for_pause."
							: $"Process {selection.Process.Id} is not paused. Use break_debugger or wait_for_pause first."));

			var frames = thread.GetFrames(1);
			if (frames.Length == 0)
				return new CallToolResult { Content = new List<ToolContent> {
					new ToolContent { Text = "Thread has no stack frames." } } };
			var f = frames[0];
			var json = JsonSerializer.Serialize(new {
				ThreadId        = (int)thread.Id,
				FunctionToken   = $"0x{f.FunctionToken:X8}",
				FunctionOffset  = f.FunctionOffset,
				ModuleName      = f.Module?.Name ?? "?",
				IsNextStatement = (f.Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0
			}, new JsonSerializerOptions { WriteIndented = true });
			f.Close();
			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		});
	}

	/// <summary>
	/// Polls until any process becomes paused, then returns info about it.
	/// Arguments: timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult WaitForPause(Dictionary<string, object>? arguments) {
		var mgr = dbgManager.Value;
		int timeoutSeconds = 30;
		if (arguments != null && arguments.TryGetValue("timeout_seconds", out var tso)) {
			if (tso is JsonElement je && je.TryGetInt32(out var ti)) timeoutSeconds = Math.Max(1, ti);
			else if (int.TryParse(tso?.ToString(), out var ti2)) timeoutSeconds = Math.Max(1, ti2);
		}

		uint? processId = ParseOptionalUInt32(arguments, "process_id");
		bool requireStackFrame = true;
		if (arguments != null) {
			if (arguments.TryGetValue("require_stack_frame", out var rsf))
				requireStackFrame = ParseOptionalBool(rsf) ?? true;
			else if (arguments.TryGetValue("require_frame", out var rf))
				requireStackFrame = ParseOptionalBool(rf) ?? true;
		}

		var waitResult = DebuggerDispatcherHelper.WaitForPausedSelection(
			mgr,
			processId: processId,
			requireFrame: requireStackFrame,
			timeout: TimeSpan.FromSeconds(timeoutSeconds));
		if (waitResult.NoActiveSession)
			throw new InvalidOperationException("No active debug session.");
		if (waitResult.SessionEnded)
			throw new InvalidOperationException(BuildSessionEndedMessage("wait_for_pause", waitResult.LastActiveState));
		if (waitResult.TimedOut)
			throw new TimeoutException($"Debugger did not pause within {timeoutSeconds}s. {DescribePausedThreadUnavailable(waitResult.LastPauseSelection, processId)}");

		var paused = waitResult.LastPauseSelection ?? throw new InvalidOperationException("No paused-thread result was captured.");
		var json = JsonSerializer.Serialize(new {
			Paused      = true,
			ProcessId   = paused.ProcessId!.Value,
			ThreadCount = paused.ThreadCount,
			ThreadId    = paused.ThreadId!.Value,
			HasStackFrame = paused.HasStackFrame
		}, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = json } }
		};
	}

	// ── Private stepping helpers ──────────────────────────────────────────────

	CallToolResult StepImpl(Dictionary<string, object>? arguments, DbgStepKind stepKind) {
		var mgr = dbgManager.Value;
		// Default 15s — leaves a safety margin before typical MCP client timeouts (~30s).
		int timeoutSeconds = 15;
		if (arguments != null && arguments.TryGetValue("timeout_seconds", out var tso)) {
			if (tso is JsonElement je && je.TryGetInt32(out var ti)) timeoutSeconds = Math.Max(1, ti);
			else if (int.TryParse(tso?.ToString(), out var ti2)) timeoutSeconds = Math.Max(1, ti2);
		}

		var mre = new ManualResetEventSlim(false);
		string? stepError = null;
		object? frameInfo = null;
		var processId = ParseOptionalUInt32(arguments, "process_id");
		var waitResult = DebuggerDispatcherHelper.WaitForPausedSelection(
			mgr,
			processId: processId,
			requireFrame: true,
			timeout: TimeSpan.FromSeconds(1));
		if (waitResult.NoActiveSession)
			throw new InvalidOperationException("No active debug session. Use start_debugging or attach_to_process first.");
		if (waitResult.SessionEnded)
			throw new InvalidOperationException(BuildSessionEndedMessage("Step", waitResult.LastActiveState));
		if (waitResult.TimedOut)
			throw new InvalidOperationException(DescribePausedThreadUnavailable(waitResult.LastPauseSelection, processId));

		DebuggerDispatcherHelper.Invoke(() => {
			if (!mgr.IsDebugging)
				throw new InvalidOperationException(
					"No active debug session. Use start_debugging or attach_to_process first.");

			var selection = ResolveThreadSelectionCore(mgr, arguments, requirePaused: true, requireFrame: true);
			var thread = selection.Thread;
			if (thread == null)
				throw new InvalidOperationException(
					selection.Process == null
						? "Cannot step: no paused process is available. Use break_debugger or wait_for_pause first."
						: (selection.ProcessIsPaused
							? "Cannot step: the process is paused but dnSpy has not published a usable stack frame yet. Retry wait_for_pause."
							: $"Cannot step: process {selection.Process.Id} is not paused. Use break_debugger or wait_for_pause first."));

			var stepper = thread.CreateStepper();
			if (!stepper.CanStep) {
				stepper.Close();
				throw new InvalidOperationException(
					"Cannot step: stepper reports CanStep=false. " +
					"The thread may not be at a steppable IL instruction (e.g. native frame or JIT thunk). " +
					"Try step_into or verify the current frame with get_current_location.");
			}
			stepper.StepComplete += (s, e) => {
				stepError = e.Error;
				var frames = e.Thread.GetFrames(1);
				if (frames.Length > 0) {
					frameInfo = new {
						ThreadId        = (int)e.Thread.Id,
						FunctionToken   = $"0x{frames[0].FunctionToken:X8}",
						FunctionOffset  = frames[0].FunctionOffset,
						ModuleName      = frames[0].Module?.Name ?? "?",
						IsNextStatement = (frames[0].Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0
					};
					frames[0].Close();
				}
				mre.Set();
			};
			stepper.Step(stepKind, autoClose: true);
		});

		// Poll every 100 ms instead of a single blocking wait.
		// This allows us to detect process death (crash, unhandled exception, detach) immediately,
		// rather than waiting the full timeout when StepComplete will never fire.
		var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
		while (!mre.Wait(100)) {
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException(
					$"Step did not complete within {timeoutSeconds}s. " +
					"The process may have resumed without triggering StepComplete, " +
					"or a dialog may be blocking the debugger. " +
					"Check get_debugger_state and list_dialogs.");
			if (!DebuggerDispatcherHelper.CaptureState(mgr).IsDebugging)
				throw new InvalidOperationException(BuildSessionEndedMessage("Step", waitResult.LastActiveState));
		}

		if (stepError != null)
			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent {
					Text = $"Step completed with error: {stepError}" } },
				IsError = true
			};

		var json = JsonSerializer.Serialize(new {
			StepKind = stepKind.ToString(),
			Location = frameInfo
		}, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = json } }
		};
	}

	string DescribePausedThreadUnavailable(DebuggerPauseSnapshot? pausedSelection, uint? processId) {
		if (pausedSelection == null || !pausedSelection.ProcessId.HasValue) {
			return processId.HasValue
				? $"No paused thread became available in process {processId.Value}."
				: "No paused thread became available.";
		}

		if (pausedSelection.ProcessIsPaused) {
			return pausedSelection.ThreadCount == 0
				? $"Process {pausedSelection.ProcessId.Value} is paused but dnSpy is currently publishing 0 threads."
				: $"Process {pausedSelection.ProcessId.Value} is paused, but no usable stack frame is available yet.";
		}

		return $"Process {pausedSelection.ProcessId.Value} is not paused.";
	}

	string BuildSessionEndedMessage(string operation, DebuggerStateSnapshot? lastActiveState) {
		var details = new List<string> {
			$"{operation} failed because the debug session ended before a usable paused thread became available."
		};

		if (lastActiveState != null && lastActiveState.ProcessCount > 0) {
			var processes = string.Join(", ", lastActiveState.Processes.Select(p =>
				$"pid={p.Id} state={p.State} threads={p.ThreadCount}"));
			details.Add("Last active state: " + processes + ".");
		}

		var archNote = GetArchitectureDiagnosticNote(lastDebugTargetPath);
		if (!string.IsNullOrWhiteSpace(archNote))
			details.Add(archNote!);

		return string.Join(" ", details);
	}

	static string? TryGetProcessImagePath(int pid) {
		try {
			return System.Diagnostics.Process.GetProcessById(pid).MainModule?.FileName;
		}
		catch {
			return null;
		}
	}

	static string? GetArchitectureDiagnosticNote(string? targetPath) {
		if (RuntimeInformation.OSArchitecture != Architecture.Arm64)
			return null;
		if (string.IsNullOrWhiteSpace(targetPath) || !System.IO.File.Exists(targetPath))
			return "Host OS architecture is ARM64. dnSpy's managed debugger can be unstable there for x86/x64 .NET Framework targets under emulation.";

		var targetArch = TryDescribeManagedImageArchitecture(targetPath!);
		if (string.IsNullOrWhiteSpace(targetArch) || string.Equals(targetArch, "arm64", StringComparison.OrdinalIgnoreCase))
			return null;

		return $"Host OS architecture is ARM64 and target '{System.IO.Path.GetFileName(targetPath)}' is {targetArch}. dnSpy's managed debugger can be unstable there for x86/x64/AnyCPU .NET Framework targets under emulation; prefer an x64 Windows VM for reliable stepping/breaks.";
	}

	static string? NormalizeBreakKind(string? breakKind) {
		foreach (var supported in SupportedBreakKinds) {
			if (string.Equals(breakKind, supported, StringComparison.OrdinalIgnoreCase))
				return supported;
		}
		return null;
	}

	static string? GetBreakKindDiagnosticNote(string breakKind) {
		if (breakKind == PredefinedBreakKinds.CreateProcess) {
			return "break_kind=CreateProcess can pause before dnSpy has published a managed thread or stack frame. " +
				"If stepping/location tools fail there, retry wait_for_pause(require_stack_frame=false) or use EntryPoint / ModuleCctorOrEntryPoint for managed IL debugging.";
		}
		return null;
	}

	static string? TryDescribeManagedImageArchitecture(string path) {
		try {
			using var module = ModuleDefMD.Load(path);
			if (module.Machine == Machine.ARM64)
				return "arm64";
			if (module.Machine == Machine.AMD64)
				return "x64";
			if (module.Machine == Machine.I386) {
				if (module.Is32BitRequired)
					return "x86";
				if (module.Is32BitPreferred)
					return "anycpu-prefer32";
				return "anycpu";
			}
			return module.Machine.ToString();
		}
		catch {
			return null;
		}
	}

	DebuggerSelection ResolveThreadSelectionCore(
		DbgManager mgr,
		Dictionary<string, object>? arguments,
		bool requirePaused,
		bool requireFrame) {
		return DebuggerDispatcherHelper.ResolveSelectionCore(
			mgr,
			processId: ParseOptionalUInt32(arguments, "process_id"),
			threadId: ParseOptionalUInt32(arguments, "thread_id"),
			pausedOnly: requirePaused,
			requireFrame: requireFrame);
	}

	static uint? ParseOptionalUInt32(Dictionary<string, object>? arguments, string key) {
		if (arguments == null || !arguments.TryGetValue(key, out var value) || value == null)
			return null;

		if (value is JsonElement elem) {
			if (elem.ValueKind == JsonValueKind.Number && elem.TryGetUInt32(out var number))
				return number;
			if (elem.ValueKind == JsonValueKind.String && uint.TryParse(elem.GetString(), out number))
				return number;
			return null;
		}

		return uint.TryParse(value.ToString(), out var parsed) ? parsed : (uint?)null;
	}

	static bool? ParseOptionalBool(object? value) {
		if (value == null)
			return null;
		if (value is JsonElement elem) {
			if (elem.ValueKind == JsonValueKind.True)
				return true;
			if (elem.ValueKind == JsonValueKind.False)
				return false;
			if (elem.ValueKind == JsonValueKind.String && bool.TryParse(elem.GetString(), out var parsedString))
				return parsedString;
			return null;
		}
		return bool.TryParse(value.ToString(), out var parsed) ? parsed : (bool?)null;
	}

	// ── Exception breakpoints ─────────────────────────────────────────────────

	/// <summary>
	/// Adds or updates an exception breakpoint so the debugger pauses when the exception is thrown.
	/// Arguments: exception_type* | first_chance (bool, default true) | second_chance (bool, default false) | category (optional, default "DotNet")
	/// </summary>
	public CallToolResult SetExceptionBreakpoint(Dictionary<string, object>? arguments) {
		if (arguments == null || !arguments.TryGetValue("exception_type", out var typeObj))
			throw new ArgumentException("exception_type is required");
		string exceptionType = typeObj?.ToString() ?? "";
		if (string.IsNullOrEmpty(exceptionType))
			throw new ArgumentException("exception_type cannot be empty");

		string category = PredefinedExceptionCategories.DotNet;
		if (arguments.TryGetValue("category", out var catObj) && !string.IsNullOrEmpty(catObj?.ToString()))
			category = catObj!.ToString()!;

		bool firstChance = true;
		if (arguments.TryGetValue("first_chance", out var fcObj)) {
			if (fcObj is JsonElement fce) firstChance = fce.ValueKind != JsonValueKind.False;
			else bool.TryParse(fcObj?.ToString(), out firstChance);
		}
		bool secondChance = false;
		if (arguments.TryGetValue("second_chance", out var scObj)) {
			if (scObj is JsonElement sce) secondChance = sce.ValueKind != JsonValueKind.False;
			else bool.TryParse(scObj?.ToString(), out secondChance);
		}

		var flags = DbgExceptionDefinitionFlags.None;
		if (firstChance)  flags |= DbgExceptionDefinitionFlags.StopFirstChance;
		if (secondChance) flags |= DbgExceptionDefinitionFlags.StopSecondChance;

		var id  = new DbgExceptionId(category, exceptionType);
		var svc = exceptionSettingsService.Value;
		var settings = new DbgExceptionSettings(flags);

		string action;
		if (svc.TryGetDefinition(id, out _)) {
			svc.Modify(id, settings);
			action = "modified";
		} else {
			var def = new DbgExceptionDefinition(id, flags);
			svc.Add(new DbgExceptionSettingsInfo(def, settings));
			action = "added";
		}

		var json = JsonSerializer.Serialize(new {
			Action        = action,
			ExceptionType = exceptionType,
			Category      = category,
			FirstChance   = firstChance,
			SecondChance  = secondChance,
			Note          = "Use continue_debugger to resume; the debugger will now break when this exception is thrown."
		}, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
	}

	/// <summary>
	/// Removes an exception breakpoint previously set via set_exception_breakpoint.
	/// Arguments: exception_type* | category (optional, default "DotNet")
	/// </summary>
	public CallToolResult RemoveExceptionBreakpoint(Dictionary<string, object>? arguments) {
		if (arguments == null || !arguments.TryGetValue("exception_type", out var typeObj))
			throw new ArgumentException("exception_type is required");
		string exceptionType = typeObj?.ToString() ?? "";
		if (string.IsNullOrEmpty(exceptionType))
			throw new ArgumentException("exception_type cannot be empty");

		string category = PredefinedExceptionCategories.DotNet;
		if (arguments.TryGetValue("category", out var catObj) && !string.IsNullOrEmpty(catObj?.ToString()))
			category = catObj!.ToString()!;

		var id  = new DbgExceptionId(category, exceptionType);
		var svc = exceptionSettingsService.Value;

		if (!svc.TryGetDefinition(id, out _))
			return new CallToolResult { Content = new List<ToolContent> {
				new ToolContent { Text = $"Exception '{exceptionType}' not found in the exception settings list. Use list_exception_breakpoints to see active entries." } } };

		svc.Remove(new[] { id });
		return new CallToolResult { Content = new List<ToolContent> {
			new ToolContent { Text = $"Exception breakpoint removed: {category} \u2014 {exceptionType}" } } };
	}

	/// <summary>
	/// Lists all exception breakpoints that have StopFirstChance or StopSecondChance enabled.
	/// Arguments: none
	/// </summary>
	public CallToolResult ListExceptionBreakpoints() {
		var svc = exceptionSettingsService.Value;
		var active = svc.Exceptions
			.Where(e => e.Settings.Flags != DbgExceptionDefinitionFlags.None)
			.Select(e => new {
				Category      = e.Definition.Id.Category,
				ExceptionType = e.Definition.Id.HasName
					? e.Definition.Id.Name
					: (e.Definition.Id.IsDefaultId ? "<<default>>" : $"Code:0x{e.Definition.Id.Code:X8}"),
				FirstChance   = (e.Settings.Flags & DbgExceptionDefinitionFlags.StopFirstChance)  != 0,
				SecondChance  = (e.Settings.Flags & DbgExceptionDefinitionFlags.StopSecondChance) != 0
			})
			.OrderBy(e => e.Category).ThenBy(e => e.ExceptionType)
			.ToList();

		var json = JsonSerializer.Serialize(new {
			Count = active.Count,
			ExceptionBreakpoints = active
		}, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
	}

	AssemblyDef? FindAssemblyByName(string name, string? filePath = null) {
			return LoadedDocumentsHelper.FindAssembly(documentService, name, filePath);
		}

		TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName) =>
			assembly.Modules
				.SelectMany(m => GetAllTypesRecursive(m.Types))
				.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));

	static System.Collections.Generic.IEnumerable<TypeDef> GetAllTypesRecursive(System.Collections.Generic.IEnumerable<TypeDef> types) {
			foreach (var t in types) {
				yield return t;
				foreach (var n in GetAllTypesRecursive(t.NestedTypes))
					yield return n;
			}
		}

		static uint ParseMetadataToken(object tokenObj) {
			var tokenText = tokenObj switch {
				JsonElement elem when elem.ValueKind == JsonValueKind.Number => elem.GetUInt32().ToString(),
				JsonElement elem => elem.ToString(),
				_ => tokenObj.ToString()
			} ?? string.Empty;

			if (tokenText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return Convert.ToUInt32(tokenText.Substring(2), 16);
			return Convert.ToUInt32(tokenText, 10);
		}

		List<BatchBreakpointItem> ParseBatchBreakpointItems(object itemsObj) {
			if (itemsObj is System.Collections.IEnumerable enumerable && itemsObj is not string && itemsObj is not JsonElement) {
				var result = new List<BatchBreakpointItem>();
				foreach (var item in enumerable) {
					switch (item) {
					case null:
						continue;
					case string json when !string.IsNullOrWhiteSpace(json):
						result.Add(ParseBatchBreakpointItemJson(json));
						break;
					case IReadOnlyDictionary<string, object> dict:
						result.Add(ParseBatchBreakpointItemDictionary(dict));
						break;
					case IDictionary<string, object> dict:
						result.Add(ParseBatchBreakpointItemDictionary(dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)));
						break;
					case IReadOnlyDictionary<string, string> stringDict:
						result.Add(ParseBatchBreakpointItemDictionary(stringDict.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)));
						break;
					case IDictionary<string, string> stringDict:
						result.Add(ParseBatchBreakpointItemDictionary(stringDict.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)));
						break;
					default:
						throw new ArgumentException("Each batch breakpoint item must be an object or a JSON object string.");
					}
				}
				return result;
			}

			if (itemsObj is JsonElement elem && elem.ValueKind == JsonValueKind.Array) {
				var result = new List<BatchBreakpointItem>();
				foreach (var item in elem.EnumerateArray()) {
					if (item.ValueKind == JsonValueKind.String) {
						var json = item.GetString();
						if (!string.IsNullOrWhiteSpace(json))
							result.Add(ParseBatchBreakpointItemJson(json!));
						continue;
					}
					if (item.ValueKind == JsonValueKind.Object)
						result.Add(ParseBatchBreakpointItemElement(item));
				}
				return result;
			}

			throw new ArgumentException("items must be a JSON array of breakpoint definitions.");
		}

		static BatchBreakpointItem ParseBatchBreakpointItemJson(string json) {
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
				throw new ArgumentException("Each batch breakpoint JSON string must decode to an object.");
			return ParseBatchBreakpointItemElement(doc.RootElement);
		}

		static BatchBreakpointItem ParseBatchBreakpointItemDictionary(IReadOnlyDictionary<string, object> dict) {
			string? assemblyName = dict.TryGetValue("assembly_name", out var asm) ? asm?.ToString() : null;
			string? typeFullName = dict.TryGetValue("type_full_name", out var type) ? type?.ToString() : null;
			string? methodName = dict.TryGetValue("method_name", out var method) ? method?.ToString() : null;
			string? filePath = dict.TryGetValue("file_path", out var fp) ? fp?.ToString() : null;
			string? condition = dict.TryGetValue("condition", out var cond) ? cond?.ToString() : null;
			uint? ilOffset = null;
			if (dict.TryGetValue("il_offset", out var il) && il != null) {
				if (il is JsonElement ilElem && ilElem.ValueKind == JsonValueKind.Number && ilElem.TryGetUInt32(out var elemOffset))
					ilOffset = elemOffset;
				else if (uint.TryParse(il.ToString(), out var parsedOffset))
					ilOffset = parsedOffset;
			}
			if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(typeFullName) || string.IsNullOrWhiteSpace(methodName))
				throw new ArgumentException("Each batch breakpoint item must include assembly_name, type_full_name, and method_name.");
			return new BatchBreakpointItem(assemblyName!, typeFullName!, methodName!, ilOffset, condition, filePath);
		}

		static BatchBreakpointItem ParseBatchBreakpointItemElement(JsonElement item) {
			string? assemblyName = item.TryGetProperty("assembly_name", out var asm) ? asm.GetString() : null;
			string? typeFullName = item.TryGetProperty("type_full_name", out var type) ? type.GetString() : null;
			string? methodName = item.TryGetProperty("method_name", out var method) ? method.GetString() : null;
			string? filePath = item.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
			string? condition = item.TryGetProperty("condition", out var cond) ? cond.GetString() : null;
			uint? ilOffset = null;
			if (item.TryGetProperty("il_offset", out var il)) {
				if (il.ValueKind == JsonValueKind.Number && il.TryGetUInt32(out var offset))
					ilOffset = offset;
				else if (il.ValueKind == JsonValueKind.String && uint.TryParse(il.GetString(), out var parsedOffset))
					ilOffset = parsedOffset;
			}
			if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(typeFullName) || string.IsNullOrWhiteSpace(methodName))
				throw new ArgumentException("Each batch breakpoint item must include assembly_name, type_full_name, and method_name.");
			return new BatchBreakpointItem(assemblyName!, typeFullName!, methodName!, ilOffset, condition, filePath);
		}

		( bool HasLoadedModule, string? ModuleName, int? ProcessId, string? NativeAddress ) FindLoadedRuntimeMethodInfo(AssemblyDef assembly, MethodDef method) {
			foreach (var process in dbgManager.Value.Processes) {
				foreach (var runtime in process.Runtimes) {
					var module = runtime.Modules.FirstOrDefault(m =>
						!string.IsNullOrWhiteSpace(m.Filename) &&
						!string.IsNullOrWhiteSpace(method.Module.Location) &&
						string.Equals(m.Filename, method.Module.Location, StringComparison.OrdinalIgnoreCase));
					if (module != null) {
						string? nativeAddress = null;
						if (module.HasAddress && method.RVA != 0)
							nativeAddress = $"0x{module.Address + (uint)method.RVA:X16}";
						return (true, module.Name, process.Id, nativeAddress);
					}
				}
			}
			return (false, null, null, null);
		}

		sealed class BatchBreakpointItem {
			public BatchBreakpointItem(string assemblyName, string typeFullName, string methodName, uint? ilOffset, string? condition, string? filePath) {
				AssemblyName = assemblyName;
				TypeFullName = typeFullName;
				MethodName = methodName;
				IlOffset = ilOffset;
				Condition = condition;
				FilePath = filePath;
			}

			public string AssemblyName { get; }
			public string TypeFullName { get; }
			public string MethodName { get; }
			public uint? IlOffset { get; }
			public string? Condition { get; }
			public string? FilePath { get; }
		}
	}
}
