@echo off
setlocal
set SCRIPT_DIR=%~dp0
if "%~1"=="" (
  "%SCRIPT_DIR%dnSpy.MCP.StdioProxy.exe" --url http://localhost:3100/mcp
) else (
  "%SCRIPT_DIR%dnSpy.MCP.StdioProxy.exe" %*
)
