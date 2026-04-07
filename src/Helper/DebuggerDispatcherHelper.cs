using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;

namespace dnSpy.MCP.Server.Helper {
	static class DebuggerDispatcherHelper {
		public static T Invoke<T>(Func<T> callback) {
			if (callback == null)
				throw new ArgumentNullException(nameof(callback));

			var dispatcher = System.Windows.Application.Current?.Dispatcher;
			if (dispatcher == null || dispatcher.CheckAccess())
				return callback();

			return dispatcher.Invoke(callback);
		}

		public static void Invoke(Action callback) {
			if (callback == null)
				throw new ArgumentNullException(nameof(callback));

			var dispatcher = System.Windows.Application.Current?.Dispatcher;
			if (dispatcher == null || dispatcher.CheckAccess()) {
				callback();
				return;
			}

			dispatcher.Invoke(callback);
		}

		public static DebuggerStateSnapshot CaptureState(DbgManager mgr) =>
			Invoke(() => CaptureStateCore(mgr));

		public static DebuggerStateSnapshot CaptureStateCore(DbgManager mgr) {
			if (mgr == null)
				throw new ArgumentNullException(nameof(mgr));

			var processes = mgr.Processes
				.Select(p => new DebuggerProcessSnapshot {
					Id = (int)p.Id,
					State = p.State.ToString(),
					IsRunning = p.IsRunning,
					RuntimeCount = p.Runtimes.Length,
					ThreadCount = GetProcessThreadsCore(p).Count
				})
				.ToList();

			return new DebuggerStateSnapshot {
				IsDebugging = mgr.IsDebugging,
				ManagerIsRunning = mgr.IsRunning,
				EffectiveIsRunning = processes.Any(p => p.IsRunning),
				Processes = processes
			};
		}

		public static DebuggerSelection ResolveSelectionCore(
			DbgManager mgr,
			uint? processId = null,
			uint? threadId = null,
			bool pausedOnly = false,
			bool requireFrame = false) {
			if (mgr == null)
				throw new ArgumentNullException(nameof(mgr));

			var processes = mgr.Processes.AsEnumerable();
			DbgProcess? requestedProcess = null;
			if (processId.HasValue) {
				requestedProcess = processes.FirstOrDefault(p => p.Id == processId.Value);
				if (requestedProcess == null)
					throw new ArgumentException($"Process {processId.Value} not found");
				processes = new[] { requestedProcess };
			}

			if (pausedOnly)
				processes = processes.Where(p => p.State == DbgProcessState.Paused);

			var processList = processes.ToList();
			var selection = new DebuggerSelection {
				Process = GetPreferredProcess(mgr, processList, pausedOnly) ?? processList.FirstOrDefault() ?? requestedProcess
			};
			if (selection.Process != null) {
				selection.ThreadCount = GetProcessThreadsCore(selection.Process).Count;
				selection.ProcessIsPaused = selection.Process.State == DbgProcessState.Paused;
			}

			if (threadId.HasValue) {
				foreach (var thread in GetPreferredThreads(mgr, processList, selection.Process, pausedOnly)) {
					if (thread.Id != threadId.Value)
						continue;
					selection.Process = thread.Process;
					selection.ThreadCount = GetProcessThreadsCore(thread.Process).Count;
					selection.ProcessIsPaused = thread.Process.State == DbgProcessState.Paused;
					selection.Thread = thread;
					selection.HasStackFrame = ThreadHasFrames(thread);
					return selection;
				}
				throw new ArgumentException($"Thread {threadId.Value} not found");
			}

			DbgProcess? fallbackProcess = null;
			DbgThread? fallbackThread = null;
			foreach (var thread in GetPreferredThreads(mgr, processList, selection.Process, pausedOnly)) {
				var process = thread.Process;
				selection.Process ??= process;
				selection.ThreadCount = GetProcessThreadsCore(process).Count;
				var hasFrame = ThreadHasFrames(thread);
				if (!requireFrame || hasFrame) {
					selection.Process = process;
					selection.ThreadCount = GetProcessThreadsCore(process).Count;
					selection.ProcessIsPaused = process.State == DbgProcessState.Paused;
					selection.Thread = thread;
					selection.HasStackFrame = hasFrame;
					return selection;
				}

				if (fallbackThread == null) {
					fallbackProcess = process;
					fallbackThread = thread;
				}
			}

			if (fallbackThread != null && !requireFrame) {
				selection.Process = fallbackProcess;
				selection.ThreadCount = fallbackProcess == null ? 0 : GetProcessThreadsCore(fallbackProcess).Count;
				selection.ProcessIsPaused = fallbackProcess?.State == DbgProcessState.Paused;
				selection.Thread = fallbackThread;
				selection.HasStackFrame = false;
			}

			return selection;
		}

		public static IReadOnlyList<DbgThread> GetProcessThreads(DbgProcess process) =>
			Invoke(() => (IReadOnlyList<DbgThread>)GetProcessThreadsCore(process));

		static List<DbgThread> GetProcessThreadsCore(DbgProcess process) {
			if (process == null)
				throw new ArgumentNullException(nameof(process));

			var result = new List<DbgThread>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			AddThreadRange(result, seen, process.Threads);
			foreach (var runtime in process.Runtimes)
				AddThreadRange(result, seen, runtime.Threads);
			return result;
		}

		static DbgProcess? GetPreferredProcess(DbgManager mgr, IReadOnlyCollection<DbgProcess> processList, bool pausedOnly) {
			if (processList.Count == 0)
				return null;

			bool Matches(DbgProcess? process) =>
				process != null &&
				processList.Contains(process) &&
				(!pausedOnly || process.State == DbgProcessState.Paused);

			var breakProcess = mgr.CurrentProcess?.Break;
			if (Matches(breakProcess))
				return breakProcess;

			var currentProcess = mgr.CurrentProcess?.Current;
			if (Matches(currentProcess))
				return currentProcess;

			return processList.FirstOrDefault();
		}

		static IEnumerable<DbgThread> GetPreferredThreads(
			DbgManager mgr,
			IReadOnlyCollection<DbgProcess> processList,
			DbgProcess? preferredProcess,
			bool pausedOnly) {
			var ordered = new List<DbgThread>();
			var seen = new HashSet<string>(StringComparer.Ordinal);

			void TryAdd(DbgThread? thread) {
				if (thread == null)
					return;
				if (!processList.Contains(thread.Process))
					return;
				if (pausedOnly && thread.Process.State != DbgProcessState.Paused)
					return;
				if (seen.Add(GetThreadKey(thread)))
					ordered.Add(thread);
			}

			TryAdd(mgr.CurrentThread?.Break);
			TryAdd(mgr.CurrentThread?.Current);

			if (preferredProcess != null) {
				foreach (var thread in GetProcessThreadsCore(preferredProcess))
					TryAdd(thread);
			}

			foreach (var process in processList) {
				if (preferredProcess != null && ReferenceEquals(process, preferredProcess))
					continue;
				foreach (var thread in GetProcessThreadsCore(process))
					TryAdd(thread);
			}

			return ordered;
		}

		static void AddThreadRange(List<DbgThread> sink, HashSet<string> seen, IEnumerable<DbgThread> threads) {
			foreach (var thread in threads) {
				if (thread == null)
					continue;
				if (seen.Add(GetThreadKey(thread)))
					sink.Add(thread);
			}
		}

		static string GetThreadKey(DbgThread thread) => $"{thread.Process.Id}:{thread.Id}";

		static bool ThreadHasFrames(DbgThread thread) {
			DbgStackFrame[] frames;
			try {
				frames = thread.GetFrames(1);
			}
			catch {
				return false;
			}
			try {
				return frames.Length > 0;
			}
			finally {
				foreach (var frame in frames)
					frame.Close();
			}
		}
	}

	sealed class DebuggerStateSnapshot {
		public bool IsDebugging { get; set; }
		public bool? ManagerIsRunning { get; set; }
		public bool EffectiveIsRunning { get; set; }
		public List<DebuggerProcessSnapshot> Processes { get; set; } = new List<DebuggerProcessSnapshot>();
		public int ProcessCount => Processes.Count;
	}

	sealed class DebuggerProcessSnapshot {
		public int Id { get; set; }
		public string State { get; set; } = string.Empty;
		public bool IsRunning { get; set; }
		public int RuntimeCount { get; set; }
		public int ThreadCount { get; set; }
	}

	sealed class DebuggerSelection {
		public DbgProcess? Process { get; set; }
		public DbgThread? Thread { get; set; }
		public int ThreadCount { get; set; }
		public bool HasStackFrame { get; set; }
		public bool ProcessIsPaused { get; set; }
	}
}
