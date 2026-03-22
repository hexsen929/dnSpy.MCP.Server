using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Core {
	[Export(typeof(McpServerCore))]
	public sealed class McpServerCore {
		readonly McpRequestDispatcher dispatcher;

		[ImportingConstructor]
		public McpServerCore(McpRequestDispatcher dispatcher) {
			this.dispatcher = dispatcher;
		}

		public Task<McpResponse?> ProcessAsync(McpRequest request, McpRequestContext context) {
			if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal))
				throw new McpProtocolException(-32600, "Only JSON-RPC 2.0 requests are supported.");

			return dispatcher.DispatchAsync(context);
		}
	}
}
