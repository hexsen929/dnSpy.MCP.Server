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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Extension;
using dnSpy.MCP.Server;
using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Presentation {
	/// <summary>
	/// Main extension entry point for the MCP (Model Context Protocol) Server.
	/// This extension enables AI assistants to analyze .NET assemblies loaded in dnSpy.
	/// </summary>
	[ExportExtension]
	sealed class TheExtension : IExtension {
		readonly McpServer mcpServer;
		readonly McpSettings mcpSettings;
		readonly object attachmentLock = new();
		bool serverAttached;
		const string LogSeparator = "------------------------------------------------------------";

		/// <summary>
		/// Initializes the extension and wires settings to the server instance.
		/// </summary>
		[ImportingConstructor]
		public TheExtension(McpServer mcpServer, McpSettings mcpSettings) {
			this.mcpServer = mcpServer;
			this.mcpSettings = mcpSettings;

			McpLogger.Info(LogSeparator);
			McpLogger.Info("MCP Extension - Constructor");
			McpLogger.Info(LogSeparator);

			AttachServerToSettings();
		}

		/// <summary>
		/// Gets merged resource dictionaries. This extension does not provide any.
		/// </summary>
		public IEnumerable<string> MergedResourceDictionaries {
			get {
				yield break;
			}
		}

		/// <summary>
		/// Gets information about this extension.
		/// </summary>
		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "MCP Server for AI-assisted .NET assembly analysis and BepInEx plugin development",
		};

		/// <summary>
		/// Handles extension lifecycle events.
		/// </summary>
		/// <param name="event">The extension event type.</param>
		/// <param name="obj">Event-specific data.</param>
		public void OnEvent(ExtensionEvent @event, object? obj) {
			McpLogger.Info(LogSeparator);
			McpLogger.Info($"Extension event: {@event}");
			McpLogger.Info(LogSeparator);

			AttachServerToSettings();

			if (@event == ExtensionEvent.AppLoaded) {
				if (mcpSettings.EnableServer) {
					McpLogger.Info("AppLoaded: all services ready — starting MCP server");
					mcpServer.Start();
				}
				else {
					McpLogger.Info("AppLoaded: MCP server is disabled in settings");
				}
			}
			else if (@event == ExtensionEvent.AppExit) {
				McpLogger.Info("MCP Extension unloading");
				try {
					mcpServer.Stop();
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, "Error stopping MCP server during AppExit");
				}
			}
		}

		void AttachServerToSettings() {
			if (serverAttached)
				return;

			lock (attachmentLock) {
				if (serverAttached)
					return;

				try {
					McpLogger.Debug("Linking McpServer instance with persistent settings");
					mcpSettings.SetServer(mcpServer);
					serverAttached = true;
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, "Failed to connect McpServer to settings");
				}
			}
		}
	}
}













