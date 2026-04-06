using System.Reflection;

namespace dnSpy.MCP.Server
{
    static class ServerBuildInfo
    {
        public static readonly string Version = ResolveVersion();

        static string ResolveVersion()
        {
            var assembly = typeof(ServerBuildInfo).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
        }
    }
}
