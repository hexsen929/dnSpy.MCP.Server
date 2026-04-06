using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Core {
	public static class McpProtocolHelpers {
		public const string JsonContentType = "application/json; charset=utf-8";
		public const string SseContentType = "text/event-stream; charset=utf-8";
		public const string SessionHeaderName = "Mcp-Session-Id";
		public const string DefaultProtocolVersion = "2024-11-05";

		public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
			PropertyNameCaseInsensitive = true,
		};

		public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

		public static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken) {
			using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Utf8NoBom, true, 4096, false)) {
				cancellationToken.ThrowIfCancellationRequested();
				return await reader.ReadToEndAsync().ConfigureAwait(false);
			}
		}

		public static McpHttpResponseData CreateJsonResponse(int statusCode, object payload) {
			return new McpHttpResponseData {
				StatusCode = statusCode,
				ContentType = JsonContentType,
				ContentEncoding = Utf8NoBom,
				BodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
			};
		}

		public static bool TryDeserializeRequest(string body, out McpRequest? request, out string? errorMessage) {
			request = null;
			errorMessage = null;

			try {
				request = JsonSerializer.Deserialize<McpRequest>(body, JsonOptions);
			}
			catch (JsonException ex) {
				errorMessage = ex.Message;
				return false;
			}

			if (request == null) {
				errorMessage = "Request body could not be parsed.";
				return false;
			}

			if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal)) {
				errorMessage = "Only JSON-RPC 2.0 requests are supported.";
				return false;
			}

			if (string.IsNullOrWhiteSpace(request.Method)) {
				errorMessage = "Request method is required.";
				return false;
			}

			return true;
		}

		public static bool IsInitializeRequest(McpRequest request) => string.Equals(request.Method, "initialize", StringComparison.Ordinal);

		public static Dictionary<string, object>? ConvertJsonObjectToDictionary(object? value) {
			if (value is Dictionary<string, object> dict)
				return dict;
			if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
				return JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText(), JsonOptions);
			return null;
		}

		public static string? GetString(Dictionary<string, object>? parameters, string key) {
			if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
				return null;
			if (value is string str)
				return str;
			if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
				return element.GetString();
			return value.ToString();
		}

		public static Dictionary<string, object>? GetObject(Dictionary<string, object>? parameters, string key) {
			if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
				return null;
			return ConvertJsonObjectToDictionary(value);
		}

		public static McpResponse CreateSuccess(object? id, object? result) => new McpResponse {
			JsonRpc = "2.0",
			Id = id,
			Result = result,
		};

		public static McpResponse CreateError(object? id, int code, string message, object? data = null) => new McpResponse {
			JsonRpc = "2.0",
			Id = id,
			Error = new McpError {
				Code = code,
				Message = message,
				Data = data,
			},
		};

		public static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload, CancellationToken cancellationToken) {
			await WriteResponseAsync(response, CreateJsonResponse(statusCode, payload), cancellationToken).ConfigureAwait(false);
		}

		public static Task WriteJsonRpcErrorAsync(HttpListenerResponse response, HttpStatusCode statusCode, object? id, int code, string message, object? data, CancellationToken cancellationToken) {
			var payload = CreateError(id, code, message, data);
			return WriteJsonAsync(response, (int)statusCode, payload, cancellationToken);
		}

		public static McpHttpResponseData CreateJsonRpcErrorResponse(HttpStatusCode statusCode, object? id, int code, string message, object? data) {
			var payload = CreateError(id, code, message, data);
			return CreateJsonResponse((int)statusCode, payload);
		}

		public static async Task WriteResponseAsync(HttpListenerResponse response, McpHttpResponseData payload, CancellationToken cancellationToken) {
			response.StatusCode = payload.StatusCode;
			response.ContentType = payload.ContentType;
			response.ContentEncoding = payload.ContentEncoding;
			foreach (var kvp in payload.Headers)
				response.Headers[kvp.Key] = kvp.Value;
			response.ContentLength64 = payload.BodyBytes.LongLength;
			if (payload.BodyBytes.Length > 0)
				await response.OutputStream.WriteAsync(payload.BodyBytes, 0, payload.BodyBytes.Length, cancellationToken).ConfigureAwait(false);
			response.Close();
		}

		public static void PrepareSseResponse(HttpListenerResponse response) {
			response.StatusCode = (int)HttpStatusCode.OK;
			response.ContentType = SseContentType;
			response.ContentEncoding = Utf8NoBom;
			response.SendChunked = true;
			response.Headers["Cache-Control"] = "no-cache";
			response.Headers["Connection"] = "keep-alive";
		}
	}
}
