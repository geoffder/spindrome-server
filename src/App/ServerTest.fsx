// #r "/usr/share/dotnet/sdk/3.1.102/ref/netstandard.dll"  // for fsac?
#r "/home/geoff/.nuget/packages/suave/2.5.6/lib/netstandard2.0/Suave.dll"
#r "/home/geoff/.nuget/packages/websocketsharp.standard/1.0.3/lib/netstandard2.0/websocket-sharp-standard.dll"
#r "/home/geoff/.nuget/packages/newtonsoft.json/12.0.3/lib/netstandard2.0/Newtonsoft.Json.dll"

#load "ServerTypes.fs"
#load "Client.fs"

open GameServer
open Client

let sleep ms = async { do! Async.Sleep ms } |> Async.RunSynchronously

let gaston = new Client ("Gaston", "ws://localhost:8080/websocket", 3544)
let chad = new Client ("Chad", "ws://localhost:8080/websocket", 3074)
// let chad = new Client ("Chad", "ws://192.168.0.23:8080/websocket", 3074)
// let gaston = new Client ("Gaston", "ws://192.168.0.23:8080/websocket", 3544)

chad.CreateLobby "foo" LastMan 10 10 4
sleep 500
gaston.Join "foo"
sleep 500
chad.Ready ()
gaston.Ready ()
sleep 500
chad.HitPlay ()
sleep 2000

chad.Close ()
gaston.Close ()

// chad.PlainPing 3544
// gaston.PlainPing 3074
// sleep 1000
