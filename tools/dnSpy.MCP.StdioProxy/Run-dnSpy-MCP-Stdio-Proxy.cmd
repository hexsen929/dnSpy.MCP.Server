@echo off
setlocal
set SCRIPT_DIR=%~dp0
if "%~1"=="" (
  "%SCRIPT_DIR%dnSpy.MCP.StdioProxy.exe" --url http://127.0.0.1:3100/mcp
) else (
  "%SCRIPT_DIR%dnSpy.MCP.StdioProxy.exe" %*
)
