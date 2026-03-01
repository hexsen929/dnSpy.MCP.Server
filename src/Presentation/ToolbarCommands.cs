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
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.ToolBars;
using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Presentation {
	static class McpToolBarConstants {
		public const string GROUP_APP_TB_MCP = "5050,7A9E1601-0E90-4A3A-B5A9-7F9C094C4E29";
	}

	/// <summary>
	/// Toolbar button that allows manually starting the MCP server from dnSpy's main toolbar.
	/// </summary>
	[ExportToolBarButton(
		OwnerGuid = ToolBarConstants.APP_TB_GUID,
		Group = McpToolBarConstants.GROUP_APP_TB_MCP,
		Icon = DsImagesAttribute.RunOutline,
		Order = 50)]
	public sealed class StartMcpServerToolBarButton : ToolBarButtonBase {
		readonly Lazy<McpServer> mcpServer;
		readonly McpSettings mcpSettings;

		[ImportingConstructor]
		public StartMcpServerToolBarButton(Lazy<McpServer> mcpServer, McpSettings mcpSettings) {
			this.mcpServer = mcpServer;
			this.mcpSettings = mcpSettings;
		}

		public override bool IsEnabled(IToolBarItemContext context) {
			try {
				return !mcpServer.Value.IsRunning;
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed querying MCP server state from toolbar");
				return false;
			}
		}

		public override void Execute(IToolBarItemContext context) {
			try {
				mcpSettings.EnableServer = true;
				mcpServer.Value.Start();
				McpLogger.Info("Toolbar start requested - MCP server start initiated");
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Toolbar start failed");
			}
		}

		public override string? GetToolTip(IToolBarItemContext context) =>
			"Start the MCP server (manual trigger)";
	}

	[ExportToolBarButton(
		OwnerGuid = ToolBarConstants.APP_TB_GUID,
		Group = McpToolBarConstants.GROUP_APP_TB_MCP,
		Icon = DsImagesAttribute.Stop,
		Order = 60)]
	public sealed class StopMcpServerToolBarButton : ToolBarButtonBase {
		readonly Lazy<McpServer> mcpServer;
		readonly McpSettings mcpSettings;

		[ImportingConstructor]
		public StopMcpServerToolBarButton(Lazy<McpServer> mcpServer, McpSettings mcpSettings) {
			this.mcpServer = mcpServer;
			this.mcpSettings = mcpSettings;
		}

		public override bool IsEnabled(IToolBarItemContext context) {
			try {
				return mcpServer.Value.IsRunning;
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed querying MCP server state from toolbar (stop)");
				return false;
			}
		}

		public override void Execute(IToolBarItemContext context) {
			try {
				mcpSettings.EnableServer = false;
				mcpServer.Value.Stop();
				McpLogger.Info("Toolbar stop requested - MCP server stop initiated");
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Toolbar stop failed");
			}
		}

		public override string? GetToolTip(IToolBarItemContext context) =>
			"Stop the MCP server";
	}

	[ExportMenuItem(
		OwnerGuid = MenuConstants.APP_MENU_DEBUG_GUID,
		Group = MenuConstants.GROUP_APP_MENU_DEBUG_START,
		Order = 4500,
		Icon = DsImagesAttribute.RunOutline,
		Header = "Start MCP Server")]
	public sealed class StartMcpServerMenuCommand : MenuItemBase {
		readonly Lazy<McpServer> mcpServer;
		readonly McpSettings mcpSettings;

		[ImportingConstructor]
		public StartMcpServerMenuCommand(Lazy<McpServer> mcpServer, McpSettings mcpSettings) {
			this.mcpServer = mcpServer;
			this.mcpSettings = mcpSettings;
		}

		public override bool IsEnabled(IMenuItemContext context) {
			try {
				return !mcpServer.Value.IsRunning;
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed querying MCP server state from menu");
				return false;
			}
		}

		public override void Execute(IMenuItemContext context) {
			try {
				mcpSettings.EnableServer = true;
				mcpServer.Value.Start();
				McpLogger.Info("Debug menu start requested - MCP server start initiated");
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Debug menu start failed");
			}
		}
	}

	[ExportMenuItem(
		OwnerGuid = MenuConstants.APP_MENU_DEBUG_GUID,
		Group = MenuConstants.GROUP_APP_MENU_DEBUG_START,
		Order = 4510,
		Icon = DsImagesAttribute.Stop,
		Header = "Stop MCP Server")]
	public sealed class StopMcpServerMenuCommand : MenuItemBase {
		readonly Lazy<McpServer> mcpServer;
		readonly McpSettings mcpSettings;

		[ImportingConstructor]
		public StopMcpServerMenuCommand(Lazy<McpServer> mcpServer, McpSettings mcpSettings) {
			this.mcpServer = mcpServer;
			this.mcpSettings = mcpSettings;
		}

		public override bool IsEnabled(IMenuItemContext context) {
			try {
				return mcpServer.Value.IsRunning;
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed querying MCP server state from menu (stop)");
				return false;
			}
		}

		public override void Execute(IMenuItemContext context) {
			try {
				mcpSettings.EnableServer = false;
				mcpServer.Value.Stop();
				McpLogger.Info("Debug menu stop requested - MCP server stop initiated");
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Debug menu stop failed");
			}
		}
	}
}
