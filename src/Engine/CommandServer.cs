using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// HTTP listener on port 7661 that receives action commands from the tactical engine.
/// Commands are queued and executed on the main thread by OnUpdate.
/// </summary>
public class BoamCommandServer
{
    public const int Port = 7661;

    private readonly MelonLogger.Instance _log;
    private readonly ConcurrentQueue<ActionCommand> _commandQueue = new();
    private HttpListener _listener;
    private Thread _listenerThread;
    private volatile bool _running;

    public struct ActionCommand
    {
        public string Action;    // "click", "useskill", "endturn", "select"
        public int X;
        public int Z;
        public string Skill;     // for useskill
        public string Actor;     // stable UUID — used for actor-gating
        public int DelayMs;      // delay in ms before executing the NEXT command
    }

    public BoamCommandServer(MelonLogger.Instance log)
    {
        _log = log;
    }

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _running = true;

            _listenerThread = new Thread(ListenLoop)
            {
                Name = "BOAM-CmdServer",
                IsBackground = true
            };
            _listenerThread.Start();

            _log.Msg($"[BOAM] Command server listening on port {Port}");
        }
        catch (Exception ex)
        {
            _log.Error($"[BOAM] Failed to start command server: {ex.Message}");
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    /// Peek at the next command without removing it.
    public bool TryPeek(out ActionCommand cmd)
    {
        return _commandQueue.TryPeek(out cmd);
    }

    /// Dequeue the next command.
    public bool TryDequeue(out ActionCommand cmd)
    {
        return _commandQueue.TryDequeue(out cmd);
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
                        "/execute" => HandleExecute(request),
                        "/tile-modifier" => HandleTileModifier(request),
                        "/tile-modifier/clear" => HandleTileModifierClear(),
                        "/status" => "{\"status\":\"ok\"}",
                        _ => "{\"error\":\"unknown route\"}"
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
            catch (HttpListenerException) when (!_running)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                if (_running)
                    _log.Warning($"[BOAM] Command server error: {ex.Message}");
            }
        }
    }

    private string HandleExecute(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = reader.ReadToEnd();
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        var x = root.TryGetProperty("x", out var xv) ? xv.GetInt32() : 0;
        var z = root.TryGetProperty("z", out var zv) ? zv.GetInt32() : 0;
        var skill = root.TryGetProperty("skill", out var sv) ? sv.GetString() ?? "" : "";
        var actor = root.TryGetProperty("actor", out var av) ? av.GetString() ?? "" : "";
        var delayMs = root.TryGetProperty("delay_ms", out var dv) ? dv.GetInt32() : 0;

        var cmd = new ActionCommand
        {
            Action = action,
            X = x,
            Z = z,
            Skill = skill,
            Actor = actor,
            DelayMs = delayMs
        };

        _commandQueue.Enqueue(cmd);
        _log.Msg($"[BOAM] Command queued: {action} ({x},{z}) {skill} actor={actor} delay={delayMs}ms");

        return JsonSerializer.Serialize(new { status = "queued", action, x, z, skill });
    }

    private string HandleTileModifier(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = reader.ReadToEnd();
        TileModifierStore.SetFromJson(body);
        return "{\"status\":\"ok\"}";
    }

    private string HandleTileModifierClear()
    {
        TileModifierStore.Clear();
        return "{\"status\":\"cleared\"}";
    }
}
