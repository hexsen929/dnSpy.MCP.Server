using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace dnSpy.MCP.StdioProxy
{
    static class Program
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly Regex MethodRegex = new Regex("\"method\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        static readonly Regex IdRegex = new Regex("\"id\"\\s*:\\s*(?<value>null|\"(?:\\\\.|[^\"\\\\])*\"|-?\\d+(?:\\.\\d+)?(?:[eE][+\\-]?\\d+)?)", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                LogError("Fatal error", ex);
                return 1;
            }
        }

        static int Run(string[] args)
        {
            var options = ProxyOptions.Parse(args);
            LogInfo("Starting stdio proxy -> " + options.Url);

            using (var proxy = new HttpMcpProxy(options))
            using (var input = Console.OpenStandardInput())
            using (var output = Console.OpenStandardOutput())
            {
                while (true)
                {
                    var frame = StdioFraming.ReadFrame(input);
                    if (frame == null)
                        break;

                    var body = Utf8NoBom.GetString(frame);
                    if (string.IsNullOrWhiteSpace(body))
                        continue;

                    if (TryHandleLocalControlMessage(body, output))
                        break;

                    try
                    {
                        var responseBody = proxy.Forward(body);
                        if (!string.IsNullOrEmpty(responseBody))
                            StdioFraming.WriteFrame(output, responseBody);
                    }
                    catch (Exception ex)
                    {
                        var id = TryGetIdRaw(body);
                        if (!string.IsNullOrEmpty(id) && !string.Equals(id, "null", StringComparison.Ordinal))
                        {
                            var errorJson = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32603,\"message\":\"Proxy transport error\",\"data\":\"" + JsonEscape(ex.Message) + "\"}}";
                            StdioFraming.WriteFrame(output, errorJson);
                        }
                        else
                        {
                            LogError("Request forwarding failed", ex);
                        }
                    }
                }

                proxy.TryCloseSession();
            }

            LogInfo("Stdio proxy exited");
            return 0;
        }

        static bool TryHandleLocalControlMessage(string body, Stream output)
        {
            var method = TryGetMethod(body);
            if (string.Equals(method, "shutdown", StringComparison.Ordinal))
            {
                var id = TryGetIdRaw(body) ?? "null";
                StdioFraming.WriteFrame(output, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}");
                return false;
            }

            if (string.Equals(method, "exit", StringComparison.Ordinal))
                return true;

            return false;
        }

        static string TryGetMethod(string json)
        {
            var match = MethodRegex.Match(json);
            return match.Success ? Regex.Unescape(match.Groups["value"].Value) : string.Empty;
        }

        static string TryGetIdRaw(string json)
        {
            var match = IdRegex.Match(json);
            return match.Success ? match.Groups["value"].Value : null;
        }

        static void LogInfo(string message)
        {
            Console.Error.WriteLine("[dnSpy.MCP.StdioProxy] " + message);
        }

        static void LogError(string message, Exception ex)
        {
            Console.Error.WriteLine("[dnSpy.MCP.StdioProxy] " + message + ": " + ex.GetType().Name + ": " + ex.Message);
        }

        static string JsonEscape(string value)
        {
            if (value == null)
                return string.Empty;

            var builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(value[i]); break;
                }
            }
            return builder.ToString();
        }

        sealed class ProxyOptions
        {
            public string Url { get; private set; }
            public string ApiKey { get; private set; }

            ProxyOptions(string url, string apiKey)
            {
                Url = url;
                ApiKey = apiKey;
            }

            public static ProxyOptions Parse(string[] args)
            {
                string url = Environment.GetEnvironmentVariable("DNSPY_MCP_URL") ?? "http://127.0.0.1:3100/mcp";
                string apiKey = Environment.GetEnvironmentVariable("DNSPY_MCP_API_KEY") ?? string.Empty;

                for (int i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (string.Equals(arg, "--url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        url = args[++i];
                    }
                    else if (string.Equals(arg, "--api-key", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        apiKey = args[++i];
                    }
                }

                return new ProxyOptions(url, apiKey);
            }
        }

        sealed class HttpMcpProxy : IDisposable
        {
            readonly ProxyOptions options;
            readonly HttpClient client;
            string sessionId;

            public HttpMcpProxy(ProxyOptions options)
            {
                this.options = options;
                client = new HttpClient();
            }

            public string Forward(string jsonBody)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, options.Url))
                {
                    request.Content = new StringContent(jsonBody, Utf8NoBom, "application/json");
                    if (!string.IsNullOrWhiteSpace(sessionId))
                        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
                    if (!string.IsNullOrWhiteSpace(options.ApiKey))
                        request.Headers.TryAddWithoutValidation("X-API-Key", options.ApiKey);

                    using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                    {
                        IEnumerable<string> values;
                        if (response.Headers.TryGetValues("Mcp-Session-Id", out values))
                            sessionId = FirstOrDefault(values) ?? sessionId;

                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (response.StatusCode == System.Net.HttpStatusCode.Accepted && string.IsNullOrEmpty(body))
                            return null;
                        return body;
                    }
                }
            }

            public void TryCloseSession()
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                    return;

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Delete, options.Url))
                    {
                        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
                        if (!string.IsNullOrWhiteSpace(options.ApiKey))
                            request.Headers.TryAddWithoutValidation("X-API-Key", options.ApiKey);
                        using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            static string FirstOrDefault(IEnumerable<string> values)
            {
                foreach (var value in values)
                    return value;
                return null;
            }

            public void Dispose()
            {
                client.Dispose();
            }
        }

        static class StdioFraming
        {
            public static byte[] ReadFrame(Stream input)
            {
                var headerBytes = new List<byte>();
                while (true)
                {
                    int value = input.ReadByte();
                    if (value < 0)
                    {
                        if (headerBytes.Count == 0)
                            return null;
                        throw new EndOfStreamException("Unexpected EOF while reading MCP stdio headers.");
                    }

                    headerBytes.Add((byte)value);
                    if (EndsWithHeaderTerminator(headerBytes))
                        break;
                }

                var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                var contentLength = ParseContentLength(headerText);
                if (contentLength < 0)
                    throw new InvalidDataException("MCP stdio message is missing a valid Content-Length header.");

                var body = new byte[contentLength];
                ReadExactly(input, body, 0, body.Length);
                return body;
            }

            public static void WriteFrame(Stream output, string jsonBody)
            {
                var bodyBytes = Utf8NoBom.GetBytes(jsonBody);
                var headerBytes = Encoding.ASCII.GetBytes("Content-Length: " + bodyBytes.Length + "\r\n\r\n");
                output.Write(headerBytes, 0, headerBytes.Length);
                output.Write(bodyBytes, 0, bodyBytes.Length);
                output.Flush();
            }

            static bool EndsWithHeaderTerminator(List<byte> bytes)
            {
                int count = bytes.Count;
                if (count >= 4 &&
                    bytes[count - 4] == '\r' &&
                    bytes[count - 3] == '\n' &&
                    bytes[count - 2] == '\r' &&
                    bytes[count - 1] == '\n')
                    return true;

                return count >= 2 &&
                       bytes[count - 2] == '\n' &&
                       bytes[count - 1] == '\n';
            }

            static int ParseContentLength(string headerText)
            {
                using (var reader = new StringReader(headerText))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!line.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase))
                            continue;

                        int index = line.IndexOf(':');
                        if (index < 0)
                            continue;

                        int value;
                        if (int.TryParse(line.Substring(index + 1).Trim(), out value))
                            return value;
                    }
                }

                return -1;
            }

            static void ReadExactly(Stream input, byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    int read = input.Read(buffer, offset, count);
                    if (read <= 0)
                        throw new EndOfStreamException("Unexpected EOF while reading MCP stdio body.");
                    offset += read;
                    count -= read;
                }
            }
        }
    }
}
