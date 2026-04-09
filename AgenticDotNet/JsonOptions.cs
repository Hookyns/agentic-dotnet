using System.Text.Json;

namespace RoslynMcpServer;

internal class JsonOptions
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
