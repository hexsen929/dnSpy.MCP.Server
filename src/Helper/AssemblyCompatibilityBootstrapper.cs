using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace dnSpy.MCP.Server.Helper {
	static class AssemblyCompatibilityBootstrapper {
		static readonly object syncRoot = new();
		static bool installed;

		public static void EnsureInstalled() {
			if (installed)
				return;

			lock (syncRoot) {
				if (installed)
					return;

				AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
				installed = true;
				McpLogger.Info("Installed assembly compatibility resolver");
			}
		}

		static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args) {
			AssemblyName requestedAssembly;
			try {
				requestedAssembly = new AssemblyName(args.Name);
			}
			catch {
				return null;
			}

			if (!string.Equals(requestedAssembly.Name, "dnlib", StringComparison.OrdinalIgnoreCase))
				return null;

			var loadedDnlib = AppDomain.CurrentDomain.GetAssemblies()
				.FirstOrDefault(assembly => {
					try {
						return string.Equals(assembly.GetName().Name, "dnlib", StringComparison.OrdinalIgnoreCase);
					}
					catch {
						return false;
					}
				});
			if (loadedDnlib != null) {
				LogResolvedVersion(requestedAssembly, loadedDnlib);
				return loadedDnlib;
			}

			foreach (var candidatePath in GetDnlibCandidatePaths()) {
				try {
					var resolvedAssembly = Assembly.LoadFrom(candidatePath);
					if (string.Equals(resolvedAssembly.GetName().Name, "dnlib", StringComparison.OrdinalIgnoreCase)) {
						LogResolvedVersion(requestedAssembly, resolvedAssembly);
						return resolvedAssembly;
					}
				}
				catch (Exception ex) {
					McpLogger.Debug($"Failed loading dnlib compatibility candidate '{candidatePath}': {ex.Message}");
				}
			}

			return null;
		}

		static IEnumerable<string> GetDnlibCandidatePaths() {
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var baseDirectory in new[] {
				AppDomain.CurrentDomain.BaseDirectory,
				Path.GetDirectoryName(typeof(AssemblyCompatibilityBootstrapper).Assembly.Location)
			}) {
				if (string.IsNullOrWhiteSpace(baseDirectory))
					continue;

				var candidatePath = Path.Combine(baseDirectory!, "dnlib.dll");
				if (File.Exists(candidatePath) && seen.Add(candidatePath))
					yield return candidatePath;
			}
		}

		static void LogResolvedVersion(AssemblyName requestedAssembly, Assembly resolvedAssembly) {
			var resolvedName = resolvedAssembly.GetName();
			if (requestedAssembly.Version == null || resolvedName.Version == null || requestedAssembly.Version == resolvedName.Version)
				return;

			McpLogger.Warning(
				$"Resolved dnlib compatibility request {requestedAssembly.Version} -> {resolvedName.Version} from '{resolvedAssembly.Location}'.");
		}
	}
}
