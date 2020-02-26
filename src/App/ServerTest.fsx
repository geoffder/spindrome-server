#r "/home/geoff/.nuget/packages/suave/2.5.6/lib/netstandard2.0/Suave.dll"
#r "/home/geoff/.nuget/packages/websocketsharp/1.0.3-rc11/lib/websocket-sharp.dll"
#r "/home/geoff/.nuget/packages/newtonsoft.json/12.0.3/lib/netstandard2.0/Newtonsoft.Json.dll"

#load "ServerTypes.fs"
#load "Client.fs"

open Client

let ws = login "ws://localhost:8080/websocket" "Steve"
do createLobby ws "foo" "last_man" 10 10 4
