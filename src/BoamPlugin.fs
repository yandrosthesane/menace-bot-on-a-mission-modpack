namespace BOAM

open System
open MelonLoader
open Menace.ModpackLoader
open BOAM.Domain

type BoamPlugin() =
    let mutable log: MelonLogger.Instance = null
    let mutable harmony: HarmonyLib.Harmony = null

    interface IModpackPlugin with
        member _.OnInitialize(logger, h) =
            log <- logger
            harmony <- h
            log.Msg "BOAM initialized"

            let nodes = [
                { Name = "BooAPeek.Filter"
                  Hook = OnTurnStart
                  Timing = Prefix
                  Reads = ["Opponents"]
                  Writes = ["Visible"; "Removed"] }

                { Name = "BooAPeek.InjectGhost"
                  Hook = ConsiderZones
                  Timing = Postfix
                  Reads = ["Ghost"; "Calibration"]
                  Writes = ["UtilityScore"] }
            ]

            for n in nodes do
                log.Msg(sprintf "[BOAM] Registered node: %s on %A.%A (reads: %s, writes: %s)"
                    n.Name n.Hook n.Timing
                    (String.concat ", " n.Reads)
                    (String.concat ", " n.Writes))

            log.Msg(sprintf "[BOAM] %d nodes registered" nodes.Length)

        member _.OnSceneLoaded(_buildIndex, _sceneName) = ()
        member _.OnUpdate() = ()
        member _.OnGUI() = ()
