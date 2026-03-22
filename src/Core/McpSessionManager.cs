using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace dnSpy.MCP.Server.Core {
	[Export(typeof(McpSessionManager))]
	public sealed class McpSessionManager {
		readonly ConcurrentDictionary<string, McpSessionState> sessions = new ConcurrentDictionary<string, McpSessionState>(StringComparer.Ordinal);

		public McpSessionState CreateSession(McpTransportKind transport) {
			while (true) {
				var sessionId = Guid.NewGuid().ToString("N");
				var state = new McpSessionState(sessionId, transport);
				if (sessions.TryAdd(sessionId, state))
					return state;
			}
		}

		public bool TryGetSession(string? sessionId, out McpSessionState? session) {
			session = null;
			if (string.IsNullOrWhiteSpace(sessionId))
				return false;
			var normalizedSessionId = sessionId!;
			return sessions.TryGetValue(normalizedSessionId, out session);
		}

		public void TouchSession(string sessionId) {
			if (TryGetSession(sessionId, out var session))
				session!.Touch();
		}

		public void MarkInitialized(string sessionId, string? protocolVersion) {
			if (TryGetSession(sessionId, out var session))
				session!.MarkInitialized(protocolVersion);
		}

		public bool RemoveSession(string? sessionId) {
			if (string.IsNullOrWhiteSpace(sessionId))
				return false;
			var normalizedSessionId = sessionId!;
			return sessions.TryRemove(normalizedSessionId, out _);
		}

		public IReadOnlyCollection<McpSessionState> Snapshot() => sessions.Values.ToList();

		public void Clear() => sessions.Clear();
	}

	public sealed class McpSessionState {
		public string SessionId { get; }
		public McpTransportKind Transport { get; }
		public DateTime CreatedAtUtc { get; }
		public DateTime LastSeenAtUtc { get; private set; }
		public bool IsInitialized { get; private set; }
		public string? ProtocolVersion { get; private set; }

		internal McpSessionState(string sessionId, McpTransportKind transport) {
			SessionId = sessionId;
			Transport = transport;
			CreatedAtUtc = DateTime.UtcNow;
			LastSeenAtUtc = CreatedAtUtc;
		}

		internal void Touch() => LastSeenAtUtc = DateTime.UtcNow;

		internal void MarkInitialized(string? protocolVersion) {
			IsInitialized = true;
			ProtocolVersion = protocolVersion;
			Touch();
		}
	}
}
