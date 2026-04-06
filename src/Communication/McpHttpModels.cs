using System;
using System.Collections.Generic;
using System.Text;

namespace dnSpy.MCP.Server.Communication {
	public sealed class McpHttpRequestData {
		public string Method { get; set; } = "GET";
		public string Path { get; set; } = "/";
		public string Body { get; set; } = string.Empty;
		public string? RemoteEndPoint { get; set; }
		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public string? GetHeader(string name) {
			return Headers.TryGetValue(name, out var value) ? value : null;
		}
	}

	public sealed class McpHttpResponseData {
		public int StatusCode { get; set; }
		public string ContentType { get; set; } = "application/octet-stream";
		public Encoding ContentEncoding { get; set; } = Encoding.UTF8;
		public byte[] BodyBytes { get; set; } = Array.Empty<byte>();
		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}
}
