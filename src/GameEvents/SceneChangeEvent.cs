using System.Text.Json;
using System.Threading;

namespace BOAM.GameEvents;

static class SceneChangeEvent
{
    internal static bool IsActive => Boundary.GameEvents.SceneChange;

    internal static void Process(string sceneName)
    {
        if (!IsActive || string.IsNullOrEmpty(sceneName)) return;
        var payload = JsonSerializer.Serialize(new { scene = sceneName });
        ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("scene-change", payload));
    }
}
