using System;
using System.Text.Json;

namespace BOAM;

/// <summary>
/// Symmetric protocol client: sends POST /query and POST /command to the F# engine.
/// Uses WebClient (sync) to avoid async deadlocks under Wine CLR.
/// Coexists with EngineClient during migration.
/// </summary>
internal static class QueryCommandClient
{
    private const string BaseUrl = "http://127.0.0.1:7660";

    /// <summary>Send a query to the engine. Returns the response JSON, or null on failure.</summary>
    internal static string Query(string type, string payloadJson = "{}")
    {
        try
        {
            var body = InjectType(type, payloadJson);
            using var client = new System.Net.WebClient();
            client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
            return client.UploadString(BaseUrl + "/query", body);
        }
        catch { return null; }
    }

    /// <summary>Send a command to the engine. Returns the response JSON, or null on failure.</summary>
    internal static string Command(string type, string payloadJson = "{}")
    {
        try
        {
            var body = InjectType(type, payloadJson);
            using var client = new System.Net.WebClient();
            client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
            return client.UploadString(BaseUrl + "/command", body);
        }
        catch { return null; }
    }

    /// <summary>Ensure the payload has a "type" field. If payloadJson is a bare object, injects it.</summary>
    private static string InjectType(string type, string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson) || payloadJson == "{}")
            return JsonSerializer.Serialize(new { type });

        // Parse existing payload and add type field
        var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("type", out _))
            return payloadJson; // already has type

        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", type);
        foreach (var prop in root.EnumerateObject())
            prop.WriteTo(writer);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
