using System;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

		public async Task HandleGetAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var acceptsSse = (context.Request.Headers["Accept"] ?? string.Empty).IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!acceptsSse) {
				await McpProtocolHelpers.WriteJsonAsync(context.Response, (int)HttpStatusCode.MethodNotAllowed, new {
					error = "Use POST /mcp for JSON-RPC requests or GET /mcp with Accept: text/event-stream for an SSE stream.",
				}, cancellationToken).ConfigureAwait(false);
				return;
			}

			var sessionId = context.Request.Headers[McpProtocolHelpers.SessionHeaderName];
			if (!sessionManager.TryGetSession(sessionId, out var session) || session?.Transport != McpTransportKind.StreamableHttp || string.IsNullOrWhiteSpace(sessionId)) {
				McpLogger.Warning($"mcp.stream.invalid-session transport=streamable-http sessionId={sessionId ?? "-"} remote={context.Request.RemoteEndPoint}");
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.NotFound, null, -32001, "Unknown or expired streamable HTTP session.", new { sessionId }, cancellationToken).ConfigureAwait(false);
				return;
			}

			McpProtocolHelpers.PrepareSseResponse(context.Response);
			var connection = new SseConnection(sessionId, context.Response);
			streams.AddOrUpdate(sessionId, connection, (_, existing) => {
				existing.Dispose();
				return connection;
			});
			sessionManager.TouchSession(sessionId);

			McpLogger.Info($"mcp.stream.connected transport=streamable-http sessionId={sessionId} remote={context.Request.RemoteEndPoint}");

			try {
				await connection.SendEventAsync("ready", JsonSerializer.Serialize(new {
					sessionId,
					transport = "streamable-http",
				}, McpProtocolHelpers.JsonOptions), cancellationToken).ConfigureAwait(false);
				await connection.SendEventAsync("status", CreateStatusPayload("running"), cancellationToken).ConfigureAwait(false);
				await RunHeartbeatLoopAsync(connection, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, $"mcp.stream.connected transport=streamable-http sessionId={sessionId}");
			}
			finally {
				RemoveConnection(sessionId, removeSession: false);
			}
		}

		public async Task HandlePostAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var body = await McpProtocolHelpers.ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
			if (!McpProtocolHelpers.TryDeserializeRequest(body, out var request, out var parseError)) {
				McpLogger.Warning($"mcp.stream.parse-error transport=streamable-http remote={context.Request.RemoteEndPoint} error=\"{parseError}\"");
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.BadRequest, null, -32700, "Parse error", parseError, cancellationToken).ConfigureAwait(false);
				return;
			}

			var sessionIdHeader = context.Request.Headers[McpProtocolHelpers.SessionHeaderName];
			var isInitialize = McpProtocolHelpers.IsInitializeRequest(request!);
			McpSessionState? session;

			if (isInitialize) {
				if (string.IsNullOrWhiteSpace(sessionIdHeader)) {
					session = sessionManager.CreateSession(McpTransportKind.StreamableHttp);
				}
				else {
					session = GetExistingStreamableSession(sessionIdHeader);
					if (session == null) {
						McpLogger.Warning($"mcp.stream.initialize-invalid-session transport=streamable-http sessionId={sessionIdHeader} remote={context.Request.RemoteEndPoint}");
						await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.NotFound, request!.Id, -32001, "Unknown streamable HTTP session for initialize.", new { sessionId = sessionIdHeader }, cancellationToken).ConfigureAwait(false);
						return;
					}
				}
				context.Response.Headers[McpProtocolHelpers.SessionHeaderName] = session.SessionId;
			}
			else {
				session = GetExistingStreamableSession(sessionIdHeader);
			}

			if (session == null) {
				McpLogger.Warning($"mcp.stream.invalid-session transport=streamable-http sessionId={sessionIdHeader ?? "-"} remote={context.Request.RemoteEndPoint} method={request!.Method}");
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.NotFound, request!.Id, -32001, "Unknown or expired streamable HTTP session.", new { sessionId = sessionIdHeader }, cancellationToken).ConfigureAwait(false);
				return;
			}

			sessionManager.TouchSession(session.SessionId);
			var requestContext = new McpRequestContext(McpTransportKind.StreamableHttp, session.SessionId, context.Request.RemoteEndPoint?.ToString(), request!, cancellationToken);
			McpResponse? response;

			try {
				response = await core.ProcessAsync(request!, requestContext).ConfigureAwait(false);
			}
			catch (McpProtocolException ex) {
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, ex.HttpStatusCode, request!.Id, ex.ErrorCode, ex.Message, ex.ErrorData, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (response == null) {
				context.Response.StatusCode = (int)HttpStatusCode.Accepted;
				context.Response.ContentType = McpProtocolHelpers.JsonContentType;
				context.Response.ContentLength64 = 0;
				context.Response.Close();
				return;
			}

			await McpProtocolHelpers.WriteJsonAsync(context.Response, (int)HttpStatusCode.OK, response, cancellationToken).ConfigureAwait(false);
		}

		public async Task HandleDeleteAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var sessionId = context.Request.Headers[McpProtocolHelpers.SessionHeaderName];
			if (!sessionManager.TryGetSession(sessionId, out var session) || session?.Transport != McpTransportKind.StreamableHttp || string.IsNullOrWhiteSpace(sessionId)) {
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.NotFound, null, -32001, "Unknown or expired streamable HTTP session.", new { sessionId }, cancellationToken).ConfigureAwait(false);
				return;
			}

			RemoveConnection(sessionId, removeSession: true);
			await McpProtocolHelpers.WriteJsonAsync(context.Response, (int)HttpStatusCode.OK, new {
				sessionId,
				closed = true,
			}, cancellationToken).ConfigureAwait(false);
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

		async Task RunHeartbeatLoopAsync(SseConnection connection, CancellationToken cancellationToken) {
			try {
				while (!cancellationToken.IsCancellationRequested && !connection.IsClosed) {
					await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
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
