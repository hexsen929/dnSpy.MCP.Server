using System.Threading;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Core {
	public sealed class McpRequestContext {
		public McpTransportKind Transport { get; }
		public string? SessionId { get; }
		public string? RemoteEndPoint { get; }
		public CancellationToken CancellationToken { get; }
		public McpRequest Request { get; }

		public McpRequestContext(McpTransportKind transport, string? sessionId, string? remoteEndPoint, McpRequest request, CancellationToken cancellationToken) {
			Transport = transport;
			SessionId = sessionId;
			RemoteEndPoint = remoteEndPoint;
			Request = request;
			CancellationToken = cancellationToken;
		}

		public object? RequestId => Request.Id;
		public string Method => Request.Method;
		public bool IsNotification => Request.Id is null;
	}
}
