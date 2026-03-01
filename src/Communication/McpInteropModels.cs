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
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Communication
{
    /// <summary>
    /// Interfaces y modelos para herramientas de interoperabilidad nativa.
    /// </summary>
    public interface IMcpInteropTools
    {
        CallToolResult FindPInvokeSignatures(Dictionary<string, object>? arguments);
        CallToolResult AnalyzeMarshallingAndLayout(Dictionary<string, object>? arguments);
    }
}