using System.Text.Json;

namespace AgenticDotNet;

internal class JsonOptions
{
	internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
