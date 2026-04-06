using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Core;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Transports {
	[Export(typeof(StreamableHttpTransport))]
	public sealed class StreamableHttpTransport {
		readonly McpServerCore core;
		readonly McpSessionManager sessionManager;
		readonly ConcurrentDictionary<string, SseConnection> streams = new ConcurrentDictionary<string, SseConnection>(StringComparer.Ordinal);

		[ImportingConstructor]
		public StreamableHttpTransport(McpServerCore core, McpSessionManager sessionManager) {
			this.core = core;
			this.sessionManager = sessionManager;
		}

		public Task<McpHttpResponseData> HandleGetAsync(McpHttpRequestData request, CancellationToken cancellationToken) {
			McpLogger.Info($"mcp.stream.get-disabled transport=streamable-http remote={request.RemoteEndPoint ?? "-"}");
			return Task.FromResult(McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.MethodNotAllowed, new {
				error = "GET /mcp is disabled. Use POST /mcp for streamable HTTP requests. Use /sse only for the legacy SSE transport.",
			}));
		}

		public async Task HandleGetAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var request = new McpHttpRequestData {
				Method = context.Request.HttpMethod,
				Path = context.Request.Url?.AbsolutePath ?? "/",
				RemoteEndPoint = context.Request.RemoteEndPoint?.ToString(),
			};
			var response = await HandleGetAsync(request, cancellationToken).ConfigureAwait(false);
			await McpProtocolHelpers.WriteResponseAsync(context.Response, response, cancellationToken).ConfigureAwait(false);
		}

		public async Task<McpHttpResponseData> HandlePostAsync(McpHttpRequestData request, CancellationToken cancellationToken) {
			if (!McpProtocolHelpers.TryDeserializeRequest(request.Body, out var rpcRequest, out var parseError)) {
				McpLogger.Warning($"mcp.stream.parse-error transport=streamable-http remote={request.RemoteEndPoint ?? "-"} error=\"{parseError}\"");
				return McpProtocolHelpers.CreateJsonRpcErrorResponse(HttpStatusCode.BadRequest, null, -32700, "Parse error", parseError);
			}

			var sessionIdHeader = request.GetHeader(McpProtocolHelpers.SessionHeaderName);
			var isInitialize = McpProtocolHelpers.IsInitializeRequest(rpcRequest!);
			var allowAnonymous = AllowsAnonymousRequest(rpcRequest!);
			McpSessionState? session;
			var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			if (isInitialize) {
				if (string.IsNullOrWhiteSpace(sessionIdHeader)) {
					session = sessionManager.CreateSession(McpTransportKind.StreamableHttp);
				}
				else {
					session = GetExistingStreamableSession(sessionIdHeader);
					if (session == null) {
						McpLogger.Warning($"mcp.stream.initialize-invalid-session transport=streamable-http sessionId={sessionIdHeader} remote={request.RemoteEndPoint ?? "-"}");
						return McpProtocolHelpers.CreateJsonRpcErrorResponse(HttpStatusCode.NotFound, rpcRequest!.Id, -32001, "Unknown streamable HTTP session for initialize.", new { sessionId = sessionIdHeader });
					}
				}
				responseHeaders[McpProtocolHelpers.SessionHeaderName] = session.SessionId;
			}
			else {
				session = GetExistingStreamableSession(sessionIdHeader);
				if (session == null && allowAnonymous)
					session = sessionManager.CreateAnonymousSession(McpTransportKind.StreamableHttp);
			}

			if (session == null) {
				McpLogger.Warning($"mcp.stream.invalid-session transport=streamable-http sessionId={sessionIdHeader ?? "-"} remote={request.RemoteEndPoint ?? "-"} method={rpcRequest!.Method}");
				return McpProtocolHelpers.CreateJsonRpcErrorResponse(HttpStatusCode.NotFound, rpcRequest!.Id, -32001, "Unknown or expired streamable HTTP session.", new { sessionId = sessionIdHeader });
			}

			sessionManager.TouchSession(session.SessionId);
			var requestContext = new McpRequestContext(McpTransportKind.StreamableHttp, session.SessionId, request.RemoteEndPoint, rpcRequest!, cancellationToken);
			McpResponse? response;

			try {
				response = await core.ProcessAsync(rpcRequest!, requestContext).ConfigureAwait(false);
			}
			catch (McpProtocolException ex) {
				var errorResponse = McpProtocolHelpers.CreateJsonRpcErrorResponse(ex.HttpStatusCode, rpcRequest!.Id, ex.ErrorCode, ex.Message, ex.ErrorData);
				foreach (var kvp in responseHeaders)
					errorResponse.Headers[kvp.Key] = kvp.Value;
				return errorResponse;
			}

			McpHttpResponseData finalResponse;
			if (response == null) {
				finalResponse = new McpHttpResponseData {
					StatusCode = (int)HttpStatusCode.Accepted,
					ContentType = McpProtocolHelpers.JsonContentType,
					ContentEncoding = McpProtocolHelpers.Utf8NoBom,
					BodyBytes = Array.Empty<byte>(),
				};
			}
			else {
				finalResponse = McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.OK, response);
			}

			foreach (var kvp in responseHeaders)
				finalResponse.Headers[kvp.Key] = kvp.Value;
			return finalResponse;
		}

		public async Task HandlePostAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var request = new McpHttpRequestData {
				Method = context.Request.HttpMethod,
				Path = context.Request.Url?.AbsolutePath ?? "/",
				Body = await McpProtocolHelpers.ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false),
				RemoteEndPoint = context.Request.RemoteEndPoint?.ToString(),
			};
			foreach (var key in context.Request.Headers.AllKeys) {
				if (key == null)
					continue;
				request.Headers[key] = context.Request.Headers[key] ?? string.Empty;
			}
			var response = await HandlePostAsync(request, cancellationToken).ConfigureAwait(false);
			await McpProtocolHelpers.WriteResponseAsync(context.Response, response, cancellationToken).ConfigureAwait(false);
		}

		public Task<McpHttpResponseData> HandleDeleteAsync(McpHttpRequestData request, CancellationToken cancellationToken) {
			var sessionId = request.GetHeader(McpProtocolHelpers.SessionHeaderName);
			if (!sessionManager.TryGetSession(sessionId, out var session) || session?.Transport != McpTransportKind.StreamableHttp || string.IsNullOrWhiteSpace(sessionId))
				return Task.FromResult(McpProtocolHelpers.CreateJsonRpcErrorResponse(HttpStatusCode.NotFound, null, -32001, "Unknown or expired streamable HTTP session.", new { sessionId }));

			RemoveConnection(sessionId, removeSession: true);
			return Task.FromResult(McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.OK, new {
				sessionId,
				closed = true,
			}));
		}

		public async Task HandleDeleteAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var request = new McpHttpRequestData {
				Method = context.Request.HttpMethod,
				Path = context.Request.Url?.AbsolutePath ?? "/",
				RemoteEndPoint = context.Request.RemoteEndPoint?.ToString(),
			};
			foreach (var key in context.Request.Headers.AllKeys) {
				if (key == null)
					continue;
				request.Headers[key] = context.Request.Headers[key] ?? string.Empty;
			}
			var response = await HandleDeleteAsync(request, cancellationToken).ConfigureAwait(false);
			await McpProtocolHelpers.WriteResponseAsync(context.Response, response, cancellationToken).ConfigureAwait(false);
		}

		public async Task BroadcastStatusAsync(string status, CancellationToken cancellationToken) {
			foreach (var kvp in streams.ToArray()) {
				var connection = kvp.Value;
				if (connection.IsClosed) {
					RemoveConnection(kvp.Key, removeSession: false);
					continue;
				}

				try {
					await connection.SendEventAsync("status", CreateStatusPayload(status), cancellationToken).ConfigureAwait(false);
				}
				catch {
					RemoveConnection(kvp.Key, removeSession: false);
				}
			}
		}

		public void Shutdown() {
			foreach (var kvp in streams.ToArray())
				RemoveConnection(kvp.Key, removeSession: true);
		}

		McpSessionState? GetExistingStreamableSession(string? sessionId) {
			if (!sessionManager.TryGetSession(sessionId, out var session))
				return null;
			if (session!.Transport != McpTransportKind.StreamableHttp)
				return null;
			return session;
		}

		static bool AllowsAnonymousRequest(McpRequest request) {
			return string.Equals(request.Method, "tools/list", StringComparison.Ordinal) ||
			       string.Equals(request.Method, "resources/list", StringComparison.Ordinal) ||
			       string.Equals(request.Method, "prompts/list", StringComparison.Ordinal) ||
			       string.Equals(request.Method, "ping", StringComparison.Ordinal);
		}

		async Task RunHeartbeatLoopAsync(SseConnection connection, CancellationToken cancellationToken) {
			try {
				while (!cancellationToken.IsCancellationRequested && !connection.IsClosed) {
					await Task.Delay(TimeSpan.FromSeconds(15d), cancellationToken).ConfigureAwait(false);
					if (connection.IsClosed)
						break;
					await connection.SendCommentAsync("heartbeat", cancellationToken).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) {
			}
		}

		static string CreateStatusPayload(string status) {
			return JsonSerializer.Serialize(new {
				status,
				timestamp = DateTime.UtcNow,
			}, McpProtocolHelpers.JsonOptions);
		}

		void RemoveConnection(string sessionId, bool removeSession) {
			if (streams.TryRemove(sessionId, out var connection))
				connection.Dispose();
			if (removeSession)
				sessionManager.RemoveSession(sessionId);
		}

		sealed class SseConnection : IDisposable {
			readonly HttpListenerResponse response;
			readonly StreamWriter writer;
			readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
			volatile bool isClosed;

			public string SessionId { get; }
			public bool IsClosed => isClosed;

			public SseConnection(string sessionId, HttpListenerResponse response) {
				SessionId = sessionId;
				this.response = response;
				writer = new StreamWriter(response.OutputStream, McpProtocolHelpers.Utf8NoBom) {
					AutoFlush = true,
				};
			}

			public async Task SendEventAsync(string eventName, string data, CancellationToken cancellationToken) {
				if (isClosed)
					return;
				await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
				try {
					if (isClosed)
						return;
					await writer.WriteLineAsync($"event: {eventName}").ConfigureAwait(false);
					await writer.WriteLineAsync($"data: {data}").ConfigureAwait(false);
					await writer.WriteLineAsync().ConfigureAwait(false);
					await writer.FlushAsync().ConfigureAwait(false);
				}
				finally {
					writeLock.Release();
				}
			}

			public async Task SendCommentAsync(string comment, CancellationToken cancellationToken) {
				if (isClosed)
					return;
				await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
				try {
					if (isClosed)
						return;
					await writer.WriteLineAsync($": {comment}").ConfigureAwait(false);
					await writer.WriteLineAsync().ConfigureAwait(false);
					await writer.FlushAsync().ConfigureAwait(false);
				}
				finally {
					writeLock.Release();
				}
			}

			public void Dispose() {
				if (isClosed)
					return;
				isClosed = true;
				try {
					writer.Dispose();
				}
				catch {
				}
				try {
					response.OutputStream.Close();
				}
				catch {
				}
				try {
					response.Close();
				}
				catch {
				}
				writeLock.Dispose();
			}
		}
	}
}
