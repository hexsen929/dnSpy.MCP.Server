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
	[Export(typeof(LegacySseTransport))]
	public sealed class LegacySseTransport {
		readonly McpServerCore core;
		readonly McpSessionManager sessionManager;
		readonly ConcurrentDictionary<string, SseConnection> clients = new ConcurrentDictionary<string, SseConnection>(StringComparer.Ordinal);

		[ImportingConstructor]
		public LegacySseTransport(McpServerCore core, McpSessionManager sessionManager) {
			this.core = core;
			this.sessionManager = sessionManager;
		}

		public async Task HandleSseAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var session = sessionManager.CreateSession(McpTransportKind.LegacySse);
			var host = context.Request.Url?.Host ?? "localhost";
			var port = context.Request.Url?.Port ?? 80;
			var endpointUrl = $"http://{host}:{port}/message?sessionId={session.SessionId}";

			McpProtocolHelpers.PrepareSseResponse(context.Response);
			var connection = new SseConnection(session.SessionId, context.Response);
			clients[session.SessionId] = connection;

			McpLogger.Info($"mcp.sse.connected transport=legacy-sse sessionId={session.SessionId} remote={context.Request.RemoteEndPoint}");

			try {
				await connection.SendEventAsync("endpoint", endpointUrl, cancellationToken).ConfigureAwait(false);
				await connection.SendEventAsync("status", CreateStatusPayload("running"), cancellationToken).ConfigureAwait(false);
				await RunHeartbeatLoopAsync(connection, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, $"mcp.sse.connected transport=legacy-sse sessionId={session.SessionId}");
			}
			finally {
				RemoveConnection(session.SessionId);
			}
		}

		public async Task HandleMessageAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			var sessionId = context.Request.QueryString["sessionId"];
			if (!sessionManager.TryGetSession(sessionId, out var session) || session?.Transport != McpTransportKind.LegacySse || string.IsNullOrWhiteSpace(sessionId)) {
				McpLogger.Warning($"mcp.sse.invalid-session transport=legacy-sse sessionId={sessionId ?? "-"} remote={context.Request.RemoteEndPoint}");
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.NotFound, null, -32001, "Unknown or expired legacy SSE session.", new { sessionId }, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (!clients.TryGetValue(sessionId, out var connection) || connection.IsClosed) {
				McpLogger.Warning($"mcp.sse.closed-session transport=legacy-sse sessionId={sessionId} remote={context.Request.RemoteEndPoint}");
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.NotFound, null, -32001, "Legacy SSE stream is no longer connected for this session.", new { sessionId }, cancellationToken).ConfigureAwait(false);
				return;
			}

			var body = await McpProtocolHelpers.ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
			if (!McpProtocolHelpers.TryDeserializeRequest(body, out var request, out var parseError)) {
				McpLogger.Warning($"mcp.sse.parse-error transport=legacy-sse sessionId={sessionId} error=\"{parseError}\"");
				await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.BadRequest, null, -32700, "Parse error", parseError, cancellationToken).ConfigureAwait(false);
				return;
			}

			context.Response.StatusCode = (int)HttpStatusCode.Accepted;
			context.Response.ContentType = McpProtocolHelpers.JsonContentType;
			context.Response.ContentLength64 = 0;
			context.Response.Close();

			sessionManager.TouchSession(sessionId);
			_ = ProcessMessageAsync(connection, request!, context.Request.RemoteEndPoint?.ToString(), cancellationToken);
		}

		public async Task BroadcastStatusAsync(string status, CancellationToken cancellationToken) {
			foreach (var kvp in clients.ToArray()) {
				var connection = kvp.Value;
				if (connection.IsClosed) {
					RemoveConnection(kvp.Key);
					continue;
				}

				try {
					await connection.SendEventAsync("status", CreateStatusPayload(status), cancellationToken).ConfigureAwait(false);
				}
				catch {
					RemoveConnection(kvp.Key);
				}
			}
		}

		public void Shutdown() {
			foreach (var kvp in clients.ToArray())
				RemoveConnection(kvp.Key);
		}

		async Task ProcessMessageAsync(SseConnection connection, McpRequest request, string? remoteEndPoint, CancellationToken cancellationToken) {
			try {
				var requestContext = new McpRequestContext(McpTransportKind.LegacySse, connection.SessionId, remoteEndPoint, request, cancellationToken);
				var response = await core.ProcessAsync(request, requestContext).ConfigureAwait(false);
				if (response != null) {
					var json = JsonSerializer.Serialize(response, McpProtocolHelpers.JsonOptions);
					await connection.SendEventAsync("message", json, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, $"mcp.sse.processing transport=legacy-sse sessionId={connection.SessionId}");
				var error = JsonSerializer.Serialize(McpProtocolHelpers.CreateError(request.Id, -32603, "Internal error", ex.Message), McpProtocolHelpers.JsonOptions);
				try {
					await connection.SendEventAsync("message", error, cancellationToken).ConfigureAwait(false);
				}
				catch {
					RemoveConnection(connection.SessionId);
				}
			}
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

		void RemoveConnection(string sessionId) {
			if (clients.TryRemove(sessionId, out var connection))
				connection.Dispose();
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
