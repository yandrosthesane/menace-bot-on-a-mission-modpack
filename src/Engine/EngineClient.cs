namespace BOAM;

/// <summary>
/// Synchronous HTTP client for communicating with the F# tactical engine.
/// Uses WebClient (not HttpClient) to avoid async deadlocks under Wine CLR.
/// </summary>
internal static class EngineClient
{
    private const string BaseUrl = "http://127.0.0.1:7660";

    internal static string Get(string path)
    {
        try
        {
            using var client = new System.Net.WebClient();
            return client.DownloadString(BaseUrl + path);
        }
        catch { return null; }
    }

    internal static string Post(string path, string json)
    {
        try
        {
            using var client = new System.Net.WebClient();
            client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
            return client.UploadString(BaseUrl + path, json);
        }
        catch { return null; }
    }
}
