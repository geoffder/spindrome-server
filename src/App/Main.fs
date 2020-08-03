open GameServer.MatchMaker
open Suave

let config =
  { defaultConfig with
      bindings = [ HttpBinding.createSimple HTTP "0.0.0.0" 8080 ] }

[<EntryPoint>]
let main _argv =
  startWebServer config server
  0
