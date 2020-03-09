#r "/home/geoff/.nuget/packages/suave/2.5.6/lib/netstandard2.0/Suave.dll"
#r "/home/geoff/.nuget/packages/websocketsharp.standard/1.0.3/lib/netstandard2.0/websocket-sharp-standard.dll"
#r "/home/geoff/.nuget/packages/newtonsoft.json/12.0.3/lib/netstandard2.0/Newtonsoft.Json.dll"

#load "ServerTypes.fs"
#load "Client.fs"

open GameServer
open Client

let ws = login "ws://localhost:8080/websocket" "Steve"
do createLobby ws "foo" LastMan 10 10 4
ws.Close()
