open GameServer.MatchMaker
open Suave

[<EntryPoint>]
let main _argv =
    startWebServer defaultConfig server
    0
