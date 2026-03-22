using System;

namespace dnSpy.MCP.Server.Core {
	public enum McpTransportKind {
		LegacySse,
		StreamableHttp,
	}

	static class McpTransportKindExtensions {
		public static string ToLogValue(this McpTransportKind transport) {
			switch (transport) {
			case McpTransportKind.LegacySse:
				return "legacy-sse";
			case McpTransportKind.StreamableHttp:
				return "streamable-http";
			default:
				return transport.ToString().ToLowerInvariant();
			}
		}
	}
}
