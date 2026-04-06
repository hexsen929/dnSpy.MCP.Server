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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
		TcpListener? tcpListener;
		CancellationTokenSource? cts;
		int actualPort;
		McpListenerMode activeListenerMode;

		[ImportingConstructor]
		public McpServer(McpSettings settings, LegacySseTransport legacySseTransport, StreamableHttpTransport streamableHttpTransport, McpSessionManager sessionManager) {
			this.settings = settings;
			this.legacySseTransport = legacySseTransport;
			this.streamableHttpTransport = streamableHttpTransport;
			this.sessionManager = sessionManager;
			actualPort = settings.Port;
			activeListenerMode = McpListenerMode.HttpListener;
		}

		public void Start() {
			McpLogger.Debug($"Start() called - EnableServer={settings.EnableServer}");

			if (!settings.EnableServer) {
				McpLogger.Info("Server not enabled in settings - skipping start");
				return;
			}

			if (IsRunning) {
				McpLogger.Warning($"Server is already running on {settings.Host}:{actualPort}");
				return;
			}

			try {
				var cfg = McpConfig.Reload();
				settings.Host = cfg.Host;
				settings.Port = cfg.Port;
				settings.ListenerMode = cfg.ListenerMode;
				actualPort = settings.Port;

				cts = new CancellationTokenSource();
				activeListenerMode = ResolveListenerMode(cfg);
				McpLogger.Info($"Configured listenerMode={settings.ListenerMode}, active backend={McpConfig.ToConfigValue(activeListenerMode)}");
				if (activeListenerMode == McpListenerMode.TcpListener)
					StartTcpListenerServer();
				else
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

		public bool IsRunning => httpListener?.IsListening == true || tcpListener != null;

		public string GetStatusMessage() {
			return IsRunning ? $"Server is running on {settings.Host}:{actualPort} ({activeListenerMode})" : "Server is stopped";
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

				if (tcpListener != null) {
					try {
						tcpListener.Stop();
					}
					catch {
					}
					tcpListener = null;
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

						LogListenerStartup("HttpListener", settings.Host, actualPort, prefix, legacySseAvailable: true);

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

							_ = HandleHttpListenerRequestAsync(context, cancellationToken);
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

		void StartTcpListenerServer() {
			Task.Run(async () => {
				var cancellationToken = cts!.Token;
				var port = settings.Port;
				const int maxAttempts = 10;

				for (var attempt = 0; attempt < maxAttempts; attempt++) {
					var currentPort = port + attempt;
					TcpListener? listener = null;
					try {
						listener = new TcpListener(ResolveBindAddress(settings.Host), currentPort);
						listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
						listener.Start();

						tcpListener = listener;
						actualPort = currentPort;

						LogListenerStartup("TcpListener", settings.Host, actualPort, $"tcp://{settings.Host}:{actualPort}", legacySseAvailable: false);

						while (!cancellationToken.IsCancellationRequested) {
							TcpClient client;
							try {
								client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
							}
							catch (ObjectDisposedException) {
								break;
							}
							catch (SocketException) {
								if (cancellationToken.IsCancellationRequested)
									break;
								throw;
							}

							_ = HandleTcpClientAsync(client, cancellationToken);
						}

						break;
					}
					catch (SocketException ex) {
						if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) {
							McpLogger.Warning($"Port {currentPort} unavailable: {ex.Message}");
							listener?.Stop();
							continue;
						}
						McpLogger.Exception(ex, $"Error starting TcpListener on port {currentPort}");
						listener?.Stop();
						break;
					}
					catch (Exception ex) {
						McpLogger.Exception(ex, $"Error starting TcpListener on port {currentPort}");
						listener?.Stop();
						break;
					}
				}
			}, cts!.Token);
		}

		async Task HandleHttpListenerRequestAsync(HttpListenerContext context, CancellationToken cancellationToken) {
			try {
				ApplyCorsHeaders(context.Response);

				var path = context.Request.Url?.AbsolutePath ?? "/";
				if (path != "/health" && !IsRequestAuthorized(CopyHeaders(context.Request.Headers))) {
					var unauthorized = McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.Unauthorized, new {
						error = "Unauthorized",
					});
					ApplyCorsHeaders(unauthorized);
					await McpProtocolHelpers.WriteResponseAsync(context.Response, unauthorized, cancellationToken).ConfigureAwait(false);
					return;
				}

				if (context.Request.HttpMethod == "GET" && (path == "/" || path == "/sse" || path == "/events")) {
					await legacySseTransport.HandleSseAsync(context, cancellationToken).ConfigureAwait(false);
					return;
				}

				if (context.Request.HttpMethod == "POST" && (path == "/message" || path == "/messages")) {
					await legacySseTransport.HandleMessageAsync(context, cancellationToken).ConfigureAwait(false);
					return;
				}

				var request = await CreateRequestDataAsync(context.Request, cancellationToken).ConfigureAwait(false);
				var response = await HandleRequestAsync(request, allowLegacySse: true, cancellationToken).ConfigureAwait(false);
				await McpProtocolHelpers.WriteResponseAsync(context.Response, response, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, $"Unhandled HTTP request failure method={context.Request.HttpMethod} path={context.Request.Url?.AbsolutePath}");
				try {
					if (context.Response.OutputStream.CanWrite) {
						var errorResponse = McpProtocolHelpers.CreateJsonRpcErrorResponse(HttpStatusCode.InternalServerError, null, -32603, "Internal error", ex.Message);
						ApplyCorsHeaders(errorResponse);
						await McpProtocolHelpers.WriteResponseAsync(context.Response, errorResponse, cancellationToken).ConfigureAwait(false);
					}
				}
				catch {
				}
			}
		}

		async Task HandleTcpClientAsync(TcpClient client, CancellationToken cancellationToken) {
			try {
				using (client) {
					client.NoDelay = true;
					using (var stream = client.GetStream()) {
						McpHttpRequestData? request;
						try {
							request = await ReadTcpRequestAsync(stream, client.Client.RemoteEndPoint?.ToString(), cancellationToken).ConfigureAwait(false);
						}
						catch (Exception ex) when (!(ex is OperationCanceledException)) {
							var badRequest = McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.BadRequest, new {
								error = "Bad Request",
								message = ex.Message,
							});
							ApplyCorsHeaders(badRequest);
							await WriteTcpResponseAsync(stream, badRequest, cancellationToken).ConfigureAwait(false);
							return;
						}

						if (request == null)
							return;

						var response = await HandleRequestAsync(request, allowLegacySse: false, cancellationToken).ConfigureAwait(false);
						await WriteTcpResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
					}
				}
			}
			catch (OperationCanceledException) {
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, $"Unhandled TCP request failure remote={client.Client.RemoteEndPoint}");
			}
		}

		async Task<McpHttpResponseData> HandleRequestAsync(McpHttpRequestData request, bool allowLegacySse, CancellationToken cancellationToken) {
			if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase)) {
				var optionsResponse = new McpHttpResponseData {
					StatusCode = (int)HttpStatusCode.OK,
					ContentType = McpProtocolHelpers.JsonContentType,
					ContentEncoding = McpProtocolHelpers.Utf8NoBom,
					BodyBytes = Array.Empty<byte>(),
				};
				ApplyCorsHeaders(optionsResponse);
				return optionsResponse;
			}

			if (request.Path != "/health" && !IsRequestAuthorized(request.Headers)) {
				var unauthorized = McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.Unauthorized, new {
					error = "Unauthorized",
				});
				ApplyCorsHeaders(unauthorized);
				return unauthorized;
			}

			McpHttpResponseData response;
			switch (request.Method.ToUpperInvariant()) {
			case "GET" when request.Path == "/health":
				response = McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.OK, new {
					status = "ok",
					service = "dnSpy MCP Server",
					port = actualPort,
					listenerMode = McpConfig.ToConfigValue(activeListenerMode),
				});
				break;

			case "GET" when request.Path == "/" || request.Path == "/sse" || request.Path == "/events":
				response = allowLegacySse
					? McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.MethodNotAllowed, new {
						error = "Legacy SSE routing is only available through the HttpListener backend.",
					})
					: McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.NotImplemented, new {
						error = "Legacy SSE transport is not available in tcpListener mode. Switch listenerMode to httpListener to use /sse and /message.",
					});
				break;

			case "POST" when request.Path == "/message" || request.Path == "/messages":
				response = allowLegacySse
					? McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.MethodNotAllowed, new {
						error = "Legacy SSE message routing is only available through the HttpListener backend.",
					})
					: McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.NotImplemented, new {
						error = "Legacy SSE transport is not available in tcpListener mode. Switch listenerMode to httpListener to use /sse and /message.",
					});
				break;

			case "GET" when request.Path == "/mcp":
				response = await streamableHttpTransport.HandleGetAsync(request, cancellationToken).ConfigureAwait(false);
				break;

			case "POST" when request.Path == "/mcp":
				response = await streamableHttpTransport.HandlePostAsync(request, cancellationToken).ConfigureAwait(false);
				break;

			case "DELETE" when request.Path == "/mcp":
				response = await streamableHttpTransport.HandleDeleteAsync(request, cancellationToken).ConfigureAwait(false);
				break;

			default:
				response = McpProtocolHelpers.CreateJsonResponse((int)HttpStatusCode.NotFound, new {
					error = "Route not found",
					method = request.Method,
					path = request.Path,
				});
				break;
			}

			ApplyCorsHeaders(response);
			return response;
		}

		async Task<McpHttpRequestData> CreateRequestDataAsync(HttpListenerRequest request, CancellationToken cancellationToken) {
			var data = new McpHttpRequestData {
				Method = request.HttpMethod,
				Path = request.Url?.AbsolutePath ?? "/",
				RemoteEndPoint = request.RemoteEndPoint?.ToString(),
			};
			foreach (var key in request.Headers.AllKeys) {
				if (key == null)
					continue;
				data.Headers[key] = request.Headers[key] ?? string.Empty;
			}

			if (string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ||
			    string.Equals(request.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase) ||
			    string.Equals(request.HttpMethod, "PATCH", StringComparison.OrdinalIgnoreCase)) {
				data.Body = await McpProtocolHelpers.ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
			}

			return data;
		}

		async Task<McpHttpRequestData?> ReadTcpRequestAsync(NetworkStream stream, string? remoteEndPoint, CancellationToken cancellationToken) {
			var headerBuffer = new MemoryStream();
			var readBuffer = new byte[4096];
			var headerEndIndex = -1;

			while (headerEndIndex < 0) {
				var read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken).ConfigureAwait(false);
				if (read == 0)
					return headerBuffer.Length == 0 ? null : throw new IOException("Connection closed before HTTP headers were fully received.");

				headerBuffer.Write(readBuffer, 0, read);
				headerEndIndex = IndexOfHeaderTerminator(headerBuffer.GetBuffer(), (int)headerBuffer.Length);
				if (headerBuffer.Length > 64 * 1024)
					throw new InvalidOperationException("HTTP request headers exceed 64KB.");
			}

			var raw = headerBuffer.GetBuffer();
			var headerText = Encoding.ASCII.GetString(raw, 0, headerEndIndex);
			var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
			if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
				throw new InvalidOperationException("HTTP request line is missing.");

			var requestLineParts = lines[0].Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
			if (requestLineParts.Length < 2)
				throw new InvalidOperationException($"Invalid HTTP request line: {lines[0]}");

			var request = new McpHttpRequestData {
				Method = requestLineParts[0].Trim(),
				Path = NormalizeRequestPath(requestLineParts[1].Trim()),
				RemoteEndPoint = remoteEndPoint,
			};

			for (var i = 1; i < lines.Length; i++) {
				var line = lines[i];
				if (string.IsNullOrEmpty(line))
					continue;

				var colonIndex = line.IndexOf(':');
				if (colonIndex <= 0)
					continue;

				var name = line.Substring(0, colonIndex).Trim();
				var value = line.Substring(colonIndex + 1).Trim();
				request.Headers[name] = value;
			}

			if (request.Headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
			    transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
				throw new InvalidOperationException("Chunked request bodies are not supported in tcpListener mode.");

			var bodyLength = 0;
			if (request.Headers.TryGetValue("Content-Length", out var contentLengthHeader) &&
			    !int.TryParse(contentLengthHeader, out bodyLength))
				throw new InvalidOperationException($"Invalid Content-Length value: {contentLengthHeader}");
			if (bodyLength < 0)
				throw new InvalidOperationException("Negative Content-Length is not allowed.");
			if (bodyLength > 10 * 1024 * 1024)
				throw new InvalidOperationException("HTTP request body exceeds 10MB.");

			var bodyBytes = new byte[bodyLength];
			var bytesAlreadyBuffered = (int)headerBuffer.Length - (headerEndIndex + 4);
			if (bytesAlreadyBuffered > 0) {
				var bytesToCopy = Math.Min(bytesAlreadyBuffered, bodyLength);
				Buffer.BlockCopy(raw, headerEndIndex + 4, bodyBytes, 0, bytesToCopy);
			}

			var bytesRead = Math.Min(bytesAlreadyBuffered, bodyLength);
			while (bytesRead < bodyLength) {
				var read = await stream.ReadAsync(bodyBytes, bytesRead, bodyLength - bytesRead, cancellationToken).ConfigureAwait(false);
				if (read == 0)
					throw new IOException("Connection closed before the full HTTP request body was received.");
				bytesRead += read;
			}

			request.Body = bodyLength == 0 ? string.Empty : McpProtocolHelpers.Utf8NoBom.GetString(bodyBytes, 0, bodyBytes.Length);
			return request;
		}

		async Task WriteTcpResponseAsync(NetworkStream stream, McpHttpResponseData response, CancellationToken cancellationToken) {
			var reasonPhrase = GetReasonPhrase(response.StatusCode);
			var headerBuilder = new StringBuilder();
			headerBuilder.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reasonPhrase).Append("\r\n");
			headerBuilder.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
			headerBuilder.Append("Content-Length: ").Append(response.BodyBytes.LongLength).Append("\r\n");
			headerBuilder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
			headerBuilder.Append("Connection: close\r\n");
			foreach (var header in response.Headers)
				headerBuilder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
			headerBuilder.Append("\r\n");

			var headerBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
			await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken).ConfigureAwait(false);
			if (response.BodyBytes.Length > 0)
				await stream.WriteAsync(response.BodyBytes, 0, response.BodyBytes.Length, cancellationToken).ConfigureAwait(false);
			await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}

		static int IndexOfHeaderTerminator(byte[] buffer, int length) {
			for (var i = 0; i <= length - 4; i++) {
				if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
					return i;
			}
			return -1;
		}

		static string NormalizeRequestPath(string rawTarget) {
			if (string.IsNullOrWhiteSpace(rawTarget))
				return "/";

			if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var absoluteUri))
				return absoluteUri.AbsolutePath;

			var queryIndex = rawTarget.IndexOf('?');
			return queryIndex >= 0 ? rawTarget.Substring(0, queryIndex) : rawTarget;
		}

		static IPAddress ResolveBindAddress(string host) {
			if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
				return IPAddress.Loopback;
			if (host == "0.0.0.0" || host == "*" || host == "+")
				return IPAddress.Any;
			if (IPAddress.TryParse(host, out var ipAddress))
				return ipAddress;

			var addresses = Dns.GetHostAddresses(host);
			foreach (var address in addresses) {
				if (address.AddressFamily == AddressFamily.InterNetwork)
					return address;
			}

			return IPAddress.Loopback;
		}

		static string GetReasonPhrase(int statusCode) {
			switch (statusCode) {
			case 200: return "OK";
			case 202: return "Accepted";
			case 400: return "Bad Request";
			case 401: return "Unauthorized";
			case 404: return "Not Found";
			case 405: return "Method Not Allowed";
			case 411: return "Length Required";
			case 500: return "Internal Server Error";
			case 501: return "Not Implemented";
			default: return "OK";
			}
		}

		static McpListenerMode ResolveListenerMode(McpConfig config) {
			var configuredMode = config.GetListenerMode();
			if (configuredMode != McpListenerMode.Auto)
				return configuredMode;

			if (IsWineLikeEnvironment())
				return McpListenerMode.TcpListener;

			return McpListenerMode.HttpListener;
		}

		static bool IsWineLikeEnvironment() {
			return
				!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEPREFIX")) ||
				!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINELOADERNOEXEC")) ||
				!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEDLLPATH")) ||
				!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CX_BOTTLE")) ||
				!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CROSSOVER_BOTTLE"));
		}

		bool IsRequestAuthorized(Dictionary<string, string> headers) {
			var config = McpConfig.Instance;
			if (!config.RequireApiKey || string.IsNullOrEmpty(config.ApiKey))
				return true;

			if (headers.TryGetValue("X-API-Key", out var apiKey) && apiKey == config.ApiKey)
				return true;

			if (headers.TryGetValue("Authorization", out var authorization) &&
			    authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
			    authorization.Substring(7) == config.ApiKey)
				return true;

			return false;
		}

		static Dictionary<string, string> CopyHeaders(NameValueCollection headers) {
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var key in headers.AllKeys) {
				if (key == null)
					continue;
				result[key] = headers[key] ?? string.Empty;
			}
			return result;
		}

		void ApplyCorsHeaders(HttpListenerResponse response) {
			response.Headers["Access-Control-Allow-Origin"] = "*";
			response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
			response.Headers["Access-Control-Allow-Headers"] = $"Content-Type, X-API-Key, Authorization, {McpProtocolHelpers.SessionHeaderName}";
			response.Headers["Access-Control-Expose-Headers"] = McpProtocolHelpers.SessionHeaderName;
		}

		void ApplyCorsHeaders(McpHttpResponseData response) {
			response.Headers["Access-Control-Allow-Origin"] = "*";
			response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
			response.Headers["Access-Control-Allow-Headers"] = $"Content-Type, X-API-Key, Authorization, {McpProtocolHelpers.SessionHeaderName}";
			response.Headers["Access-Control-Expose-Headers"] = McpProtocolHelpers.SessionHeaderName;
		}

		void LogListenerStartup(string backend, string host, int port, string bindInfo, bool legacySseAvailable) {
			McpLogger.Info("═══════════════════════════════════════════════════════");
			McpLogger.Info("Starting MCP Server");
			McpLogger.Info($"Listener: {backend}");
			McpLogger.Info($"Host: {host}");
			McpLogger.Info($"Port: {port}");
			McpLogger.Info($"Bind: {bindInfo}");
			if (legacySseAvailable)
				McpLogger.Info("Routes: GET /sse, POST /message, POST|DELETE /mcp, GET /health");
			else
				McpLogger.Info("Routes: POST|DELETE /mcp, GET /health (legacy /sse + /message unavailable in tcpListener mode)");
			McpLogger.Info("═══════════════════════════════════════════════════════");
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
