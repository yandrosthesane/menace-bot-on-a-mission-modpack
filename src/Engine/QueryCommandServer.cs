using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using MelonLoader;

namespace BOAM;

/// <summary>Port for the C# command server (engine → game direction).</summary>
public static class BridgeServer
{
    public const int Port = 7661;

    public struct ActionCommand
    {
        public string Action;    // "click", "useskill", "endturn", "select"
        public int X;
        public int Z;
        public string Skill;     // for useskill
        public string Actor;     // stable UUID — used for actor-gating
        public int DelayMs;      // delay in ms before executing the NEXT command
    }
}

/// <summary>
/// Symmetric protocol server: POST /query (read-only) and POST /command (side effects).
/// Dispatches by {"type": "..."} in payload. Handlers registered via AddQueryHandler / AddCommandHandler.
/// </summary>
public class QueryCommandServer
{
    private readonly MelonLogger.Instance _log;
    private readonly int _port;
    private HttpListener _listener;
    private Thread _listenerThread;
    private volatile bool _running;

    private readonly Dictionary<string, Func<JsonElement, string>> _queryHandlers = new();
    private readonly Dictionary<string, Func<JsonElement, string>> _commandHandlers = new();

    public QueryCommandServer(MelonLogger.Instance log, int port)
    {
        _log = log;
        _port = port;
    }

    public void AddQueryHandler(string type, Func<JsonElement, string> handler)
    {
        _queryHandlers[type] = handler;
    }

    public void AddCommandHandler(string type, Func<JsonElement, string> handler)
    {
        _commandHandlers[type] = handler;
    }

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _running = true;

            _listenerThread = new Thread(ListenLoop)
            {
                Name = "BOAM-QCServer",
                IsBackground = true
            };
            _listenerThread.Start();

            _log.Msg($"[BOAM] QueryCommand server listening on port {_port}");
        }
        catch (Exception ex)
        {
            _log.Error($"[BOAM] Failed to start QueryCommand server: {ex.Message}");
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    private void ListenLoop()
    {
        while (_running)
        {
            try
            {
                var context = _listener.GetContext();
                var request = context.Request;
                var response = context.Response;

                string responseBody;
                try
                {
                    var path = request.Url?.AbsolutePath ?? "/";
                    responseBody = path switch
                    {
                        "/query" => Dispatch(request, _queryHandlers, "query"),
                        "/command" => Dispatch(request, _commandHandlers, "command"),
                        _ => JsonSerializer.Serialize(new { error = "unknown route", path })
                    };
                }
                catch (Exception ex)
                {
                    responseBody = JsonSerializer.Serialize(new { error = ex.Message });
                }

                var buffer = System.Text.Encoding.UTF8.GetBytes(responseBody);
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            catch (HttpListenerException) when (!_running) { break; }
            catch (Exception ex)
            {
                if (_running)
                    _log.Warning($"[BOAM] QueryCommand server error: {ex.Message}");
            }
        }
    }

    private string Dispatch(HttpListenerRequest request,
        Dictionary<string, Func<JsonElement, string>> handlers, string mode)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = reader.ReadToEnd();
        var root = JsonDocument.Parse(body).RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            return JsonSerializer.Serialize(new { error = "missing 'type' field" });

        var type = typeProp.GetString() ?? "";
        if (!handlers.TryGetValue(type, out var handler))
            return JsonSerializer.Serialize(new { error = $"unknown {mode} type", type });

        return handler(root);
    }
}
