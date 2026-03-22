using System;
using System.Net;

namespace dnSpy.MCP.Server.Core {
	public sealed class McpProtocolException : Exception {
		public int ErrorCode { get; }
		public HttpStatusCode HttpStatusCode { get; }
		public object? ErrorData { get; }

		public McpProtocolException(int errorCode, string message, HttpStatusCode httpStatusCode = HttpStatusCode.BadRequest, object? errorData = null)
			: base(message) {
			ErrorCode = errorCode;
			HttpStatusCode = httpStatusCode;
			ErrorData = errorData;
		}
	}
}
