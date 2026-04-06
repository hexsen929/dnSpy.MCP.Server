using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.MCP.Server.Application;
using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Tools {
	[Export(typeof(ToolRegistry))]
	public sealed class ToolRegistry {
		static readonly IReadOnlyDictionary<string, string> LegacyToolAliases = new Dictionary<string, string>(StringComparer.Ordinal) {
			["Get_Class_Sourcecode"] = "get_class_sourcecode",
			["Get_Method_SourceCode"] = "get_method_sourcecode",
			["Get_Function_Opcodes"] = "get_function_opcodes",
			["Set_Function_Opcodes"] = "set_function_opcodes",
			["Overwrite_Full_Func_Opcodes"] = "overwrite_full_function_opcodes",
			["Update_Method_SourceCode"] = "update_method_sourcecode",
		};

		readonly McpTools tools;
		readonly BepInExResources resources;

		[ImportingConstructor]
		public ToolRegistry(McpTools tools, BepInExResources resources) {
			this.tools = tools;
			this.resources = resources;
		}

		public IReadOnlyList<ToolInfo> ListTools(ToolCatalogFilter? filter = null) => tools.GetAvailableTools(filter);

		public string NormalizeToolName(string toolName) {
			if (LegacyToolAliases.TryGetValue(toolName, out var canonicalName))
				return canonicalName;
			return toolName;
		}

		public bool ContainsTool(string toolName) {
			var normalizedToolName = NormalizeToolName(toolName);
			return tools.GetAvailableTools().Any(a => string.Equals(a.Name, normalizedToolName, StringComparison.Ordinal));
		}

		public CallToolResult ExecuteTool(string toolName, Dictionary<string, object>? arguments) =>
			tools.ExecuteTool(NormalizeToolName(toolName), arguments);

		public IReadOnlyList<ResourceInfo> ListResources() => resources.GetResources();

		public string? ReadResource(string uri) => resources.ReadResource(uri);
	}
}
