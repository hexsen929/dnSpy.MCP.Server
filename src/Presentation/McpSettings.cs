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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Text;

using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Presentation {
	/// <summary>
	/// Settings for the MCP server extension, including server configuration and logging.
	/// </summary>
	public class McpSettings : ViewModelBase {
		/// <summary>
		/// Allows derived implementations to attach a server instance so lifecycle changes can be propagated.
		/// Base implementation is a no-op.
		/// </summary>
		/// <param name="server">The MCP server instance to attach.</param>
		internal virtual void SetServer(McpServer server) {
			// Default implementation intentionally left blank.
		}

		/// <summary>
		/// Gets or sets whether the MCP server is enabled.
		/// </summary>
		public bool EnableServer {
			get => enableServer;
			set {
				if (enableServer != value) {
					enableServer = value;
					OnPropertyChanged(nameof(EnableServer));
				}
			}
		}
		bool enableServer = false;

		/// <summary>
		/// Gets or sets the server host (default: 127.0.0.1).
		/// </summary>
		public string Host {
			get => host;
			set {
				if (host != value) {
					host = value;
					OnPropertyChanged(nameof(Host));
				}
			}
		}
		string host = "127.0.0.1";

		/// <summary>
		/// Gets or sets the server port (default: 3100 - to avoid conflicts with Docker/Node).
		/// </summary>
		public int Port {
			get => port;
			set {
				if (port != value) {
					port = value;
					OnPropertyChanged(nameof(Port));
				}
			}
		}
		int port = 3100;

		/// <summary>
		/// Gets the collection of log messages (limited to last 100 messages).
		/// </summary>
		public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

		/// <summary>
		/// Gets or sets the combined log text for easy copying.
		/// </summary>
		string logText = string.Empty;
		public string LogText {
			get => logText;
			set {
				if (logText != value) {
					logText = value;
					OnPropertyChanged(nameof(LogText));
				}
			}
		}

		/// <summary>
		/// Adds a log message to the log collection and forwards it to the centralized logger.
		/// </summary>
		/// <param name="message">The log message to add.</param>
		public void Log(string message) {
			// Use centralized logger (includes timestamp and file logging)
			McpLogger.Info(message);

			// Keep and update the in-UI log collection (used by the settings UI).
			var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
			var logEntry = $"[{timestamp}] {message}";

			if (System.Windows.Application.Current?.Dispatcher != null) {
				System.Windows.Application.Current.Dispatcher.Invoke(() => {
					LogMessages.Add(logEntry);
					while (LogMessages.Count > 100)
						LogMessages.RemoveAt(0);
					LogText = string.Join(Environment.NewLine, LogMessages);
				});
			} else {
				LogMessages.Add(logEntry);
				while (LogMessages.Count > 100)
					LogMessages.RemoveAt(0);
				LogText = string.Join(Environment.NewLine, LogMessages);
			}
		}

		/// <summary>
		/// Log an informational message.
		/// </summary>
		public void LogInfo(string message) {
			McpLogger.Info(message);
			AddToUILog("INFO", message);
		}

		/// <summary>
		/// Log a warning message.
		/// </summary>
		public void LogWarn(string message) {
			McpLogger.Warning(message);
			AddToUILog("WARN", message);
		}

		/// <summary>
		/// Log an error message.
		/// </summary>
		public void LogError(string message) {
			McpLogger.Error(message);
			AddToUILog("ERROR", message);
		}

		/// <summary>
		/// Adds a message to the UI log collection.
		/// </summary>
		void AddToUILog(string level, string message) {
			var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
			var logEntry = $"[{timestamp}] [{level}] {message}";

			if (System.Windows.Application.Current?.Dispatcher != null) {
				System.Windows.Application.Current.Dispatcher.Invoke(() => {
					LogMessages.Add(logEntry);
					while (LogMessages.Count > 100)
						LogMessages.RemoveAt(0);
					LogText = string.Join(Environment.NewLine, LogMessages);
				});
			} else {
				LogMessages.Add(logEntry);
				while (LogMessages.Count > 100)
					LogMessages.RemoveAt(0);
				LogText = string.Join(Environment.NewLine, LogMessages);
			}
		}

		/// <summary>
		/// Clears all log messages from the UI.
		/// </summary>
		public void ClearLogs() {
			// Clear on UI thread if available
			if (System.Windows.Application.Current?.Dispatcher != null) {
				System.Windows.Application.Current.Dispatcher.Invoke(() => {
					LogMessages.Clear();
					LogText = string.Empty;
				});
			} else {
				LogMessages.Clear();
				LogText = string.Empty;
			}
		}
		/// <summary>
		/// Creates a copy of these settings.
		/// </summary>
		public McpSettings Clone() => CopyTo(new McpSettings());

		/// <summary>
		/// Copies these settings to another instance.
		/// </summary>
		public McpSettings CopyTo(McpSettings other) {
			other.EnableServer = EnableServer;
			other.Host = Host;
			other.Port = Port;
			return other;
		}
	}

	/// <summary>
	/// Implementation of MCP settings with persistence support.
	/// </summary>
	[Export(typeof(McpSettings))]
	sealed class McpSettingsImpl : McpSettings {
		static readonly Guid SETTINGS_GUID = new Guid("352907A0-9DF5-4B2B-B47B-95E504CAC301");

		readonly ISettingsService settingsService;
		McpServer? mcpServer;

		[ImportingConstructor]
		McpSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			// Host and Port are read from mcp-config.json so that the bind address can be
			// changed by editing that file without touching dnSpy's internal settings.
			// The JSON config file is created with defaults on first run if it does not exist.
			var cfg = Configuration.McpConfig.Instance;
			Host = cfg.Host;
			Port = cfg.Port;

			// EnableServer is the only property persisted in dnSpy's own settings store
			// (it controls the on/off toggle in the UI and survives dnSpy restarts).
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			EnableServer = sect.Attribute<bool?>(nameof(EnableServer)) ?? EnableServer;

			PropertyChanged += McpSettingsImpl_PropertyChanged;
		}

		/// <summary>
		/// Stores the server reference so that property-change events (Enable/Disable toggle)
		/// can control the server lifecycle at runtime.
		/// The initial start is deferred to the AppLoaded event in TheExtension so that
		/// all MEF services and documents are fully initialized before the server begins
		/// accepting connections.
		/// </summary>
		internal override void SetServer(McpServer server) {
			mcpServer = server;
		}

		void McpSettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			// Only EnableServer is persisted — Host and Port are owned by mcp-config.json
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(EnableServer), EnableServer);

			// Handle server enable/disable dynamically (no restart required)
			if (e.PropertyName == nameof(EnableServer) && mcpServer != null) {
				if (EnableServer) {
					Log("Starting MCP server");
					mcpServer.Start();

					// Verify asynchronously that the server started and report to output
					System.Threading.Tasks.Task.Run(async () => {
						await System.Threading.Tasks.Task.Delay(300);
						try {
							if (mcpServer.IsRunning)
								Log("MCP server is running");
							else
								Log("MCP server failed to start");
						}
						catch (Exception ex) {
							Log($"Error checking server status: {ex.Message}");
						}
					});
				} else {
					Log("Stopping MCP server");
					mcpServer.Stop();

					// Verify asynchronously that the server stopped and report to output
					System.Threading.Tasks.Task.Run(async () => {
						await System.Threading.Tasks.Task.Delay(200);
						try {
							if (mcpServer.IsRunning)
								Log("MCP server is still running");
							else
								Log("MCP server stopped");
						}
						catch (Exception ex) {
							Log($"Error checking server status: {ex.Message}");
						}
					});
				}
			}
		}
	}
}
