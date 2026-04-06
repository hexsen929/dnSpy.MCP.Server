/*
    Copyright (C) 2026 @chichicaste

    This file is part of dnSpy MCP Server module.

    dnSpy MCP Server is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy MCP Server is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy MCP Server.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.ComponentModel.Composition;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Server.Configuration;
using dnSpy.MCP.Server.Core;
using dnSpy.MCP.Server.Helper;
using dnSpy.MCP.Server.Presentation;
using dnSpy.MCP.Server.Transports;

namespace dnSpy.MCP.Server.Communication {
	[Export(typeof(McpServer))]
	public sealed class McpServer : IDisposable {
		readonly McpSettings settings;
		readonly LegacySseTransport legacySseTransport;
		readonly StreamableHttpTransport streamableHttpTransport;
		readonly McpSessionManager sessionManager;

		HttpListener? httpListener;
		CancellationTokenSource? cts;
		int actualPort;

		[ImportingConstructor]
		public McpServer(McpSettings settings, LegacySseTransport legacySseTransport, StreamableHttpTransport streamableHttpTransport, McpSessionManager sessionManager) {
			this.settings = settings;
			this.legacySseTransport = legacySseTransport;
			this.streamableHttpTransport = streamableHttpTransport;
			this.sessionManager = sessionManager;
			actualPort = settings.Port;
		}

		public void Start() {
			McpLogger.Debug($"Start() called - EnableServer={settings.EnableServer}");

			if (!settings.EnableServer) {
				McpLogger.Info("Server not enabled in settings - skipping start");
				return;
			}

			if (httpListener != null) {
				McpLogger.Warning($"Server is already running on {settings.Host}:{actualPort}");
				return;
			}

			try {
				cts = new CancellationTokenSource();
				StartHttpListenerServer();
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed to start server");
			}
		}

		public void Restart() {
			McpLogger.Info("Restarting MCP server...");
			Stop();
			Task.Delay(500).Wait();
			Start();
			McpLogger.Info("MCP server restart completed");
		}

		public bool IsRunning => httpListener != null && httpListener.IsListening;

		public string GetStatusMessage() {
			return IsRunning ? $"Server is running on {settings.Host}:{actualPort}" : "Server is stopped";
		}

		public void Stop() {
			McpLogger.Info("Stopping MCP server...");
			try {
				cts?.Cancel();

				if (httpListener != null) {
					try {
						httpListener.Stop();
					}
					catch {
					}
					try {
						httpListener.Abort();
					}
					catch {
					}
					httpListener.Close();
					httpListener = null;
				}

				legacySseTransport.Shutdown();
				streamableHttpTransport.Shutdown();
				sessionManager.Clear();

				Thread.Sleep(100);

				McpLogger.Info("MCP server stopped successfully");
				cts?.Dispose();
				cts = null;
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Error stopping server");
			}
		}

		static string BuildPrefix(string host, int port) {
			var normalizedHost = host == "0.0.0.0" || host == "*" ? "+" : host;
			return $"http://{normalizedHost}:{port}/";
		}

		void StartHttpListenerServer() {
			Task.Run(async () => {
				var cancellationToken = cts!.Token;
				var port = settings.Port;
				const int maxAttempts = 10;

				for (var attempt = 0; attempt < maxAttempts; attempt++) {
					var currentPort = port + attempt;
					HttpListener? listener = null;
					var prefix = BuildPrefix(settings.Host, currentPort);

					try {
						listener = new HttpListener();
						listener.Prefixes.Add(prefix);
						listener.Start();

						httpListener = listener;
						actualPort = currentPort;

						McpLogger.Info("═══════════════════════════════════════════════════════");
						McpLogger.Info("Starting MCP Server");
						McpLogger.Info($"Host: {settings.Host}");
						McpLogger.Info($"Port: {actualPort}");
						McpLogger.Info($"Prefix: {prefix}");
						McpLogger.Info("Routes: GET /sse, POST /message, GET|POST|DELETE /mcp, GET /health");
						McpLogger.Info("═══════════════════════════════════════════════════════");

						await BroadcastStatusAsync("running", cancellationToken).ConfigureAwait(false);

						while (!cancellationToken.IsCancellationRequested) {
							HttpListenerContext context;
							try {
								context = await listener.GetContextAsync().ConfigureAwait(false);
							}
							catch (HttpListenerException) {
								break;
							}
							catch (ObjectDisposedException) {
								break;
							}

							_ = HandleHttpRequestAsync(context, cancellationToken);
						}

						break;
					}
					catch (HttpListenerException ex) when (ex.ErrorCode == 5) {
						McpLogger.Exception(ex, $"Access denied to port {currentPort}. Run: netsh http add urlacl url={prefix} user=Everyone");
						listener?.Close();
						break;
					}
					catch (HttpListenerException ex) {
						McpLogger.Warning($"Port {currentPort} unavailable: {ex.Message}");
						listener?.Close();
					}
					catch (Exception ex) {
						McpLogger.Exception(ex, $"Error starting HttpListener on port {currentPort}");
						listener?.Close();
						break;
					}
				}
			}, cts!.Token);
		}

		async Task HandleHttpRequestAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			try {
				ApplyCorsHeaders(context.Response);

				if (context.Request.HttpMethod == "OPTIONS") {
					context.Response.StatusCode = (int)HttpStatusCode.OK;
					context.Response.Close();
					return;
				}

				var path = context.Request.Url?.AbsolutePath ?? "/";
				if (path != "/health" && !IsRequestAuthorized(context.Request)) {
					await McpProtocolHelpers.WriteJsonAsync(context.Response, (int)HttpStatusCode.Unauthorized, new {
						error = "Unauthorized",
					}, cancellationToken).ConfigureAwait(false);
					return;
				}

				switch (context.Request.HttpMethod) {
				case "GET" when path == "/health":
					await McpProtocolHelpers.WriteJsonAsync(context.Response, (int)HttpStatusCode.OK, new {
						status = "ok",
						service = "dnSpy MCP Server",
						port = actualPort,
					}, cancellationToken).ConfigureAwait(false);
					return;

				case "GET" when path == "/" || path == "/sse" || path == "/events":
					await legacySseTransport.HandleSseAsync(context, cancellationToken).ConfigureAwait(false);
					return;

				case "POST" when path == "/message" || path == "/messages":
					await legacySseTransport.HandleMessageAsync(context, cancellationToken).ConfigureAwait(false);
					return;

				case "GET" when path == "/mcp":
					await streamableHttpTransport.HandleGetAsync(context, cancellationToken).ConfigureAwait(false);
					return;

				case "POST" when path == "/mcp":
					await streamableHttpTransport.HandlePostAsync(context, cancellationToken).ConfigureAwait(false);
					return;

				case "DELETE" when path == "/mcp":
					await streamableHttpTransport.HandleDeleteAsync(context, cancellationToken).ConfigureAwait(false);
					return;
				}

				await McpProtocolHelpers.WriteJsonAsync(context.Response, (int)HttpStatusCode.NotFound, new {
					error = "Route not found",
					method = context.Request.HttpMethod,
					path,
				}, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, $"Unhandled HTTP request failure method={context.Request.HttpMethod} path={context.Request.Url?.AbsolutePath}");
				try {
					if (context.Response.OutputStream.CanWrite) {
						await McpProtocolHelpers.WriteJsonRpcErrorAsync(context.Response, HttpStatusCode.InternalServerError, null, -32603, "Internal error", ex.Message, cancellationToken).ConfigureAwait(false);
					}
				}
				catch {
				}
			}
		}

		bool IsRequestAuthorized(HttpListenerRequest request) {
			var config = McpConfig.Instance;
			if (!config.RequireApiKey || string.IsNullOrEmpty(config.ApiKey))
				return true;

			var apiKey = request.Headers["X-API-Key"];
			if (apiKey == config.ApiKey)
				return true;

			var authorization = request.Headers["Authorization"];
			if (authorization != null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization.Substring(7) == config.ApiKey)
				return true;

			return false;
		}

		void ApplyCorsHeaders(HttpListenerResponse response) {
			response.Headers["Access-Control-Allow-Origin"] = "*";
			response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
			response.Headers["Access-Control-Allow-Headers"] = $"Content-Type, X-API-Key, Authorization, {McpProtocolHelpers.SessionHeaderName}";
			response.Headers["Access-Control-Expose-Headers"] = McpProtocolHelpers.SessionHeaderName;
		}

		Task BroadcastStatusAsync(string status, CancellationToken cancellationToken) {
			return Task.WhenAll(
				legacySseTransport.BroadcastStatusAsync(status, cancellationToken),
				streamableHttpTransport.BroadcastStatusAsync(status, cancellationToken)
			);
		}

		public void Dispose() {
			Stop();
		}
	}
}
