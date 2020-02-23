module Client

open WebSocketSharp
open Newtonsoft.Json
open GameServer

let openSocket uri = new WebSocket (uri)

let login uri name =
    let ws = sprintf "%s/%s" uri name |> openSocket
    ws.Connect ()
    ws.OnMessage.Add (fun m -> printfn "%A" m.Data)
    ws

let createLobby (ws: WebSocket) name mode time score cap =
    { Name = name
      Mode = GameMode.FromString mode
      Limits = { Time = time; Score = score }
      Capacity = cap }
    |> JsonConvert.SerializeObject
    |> sprintf "HOST%s"
    |> ws.Send

let chat (ws: WebSocket) msg = sprintf "CHAT%s" msg |> ws.Send

let drop (ws: WebSocket) = ws.Send "DROP"

let join (ws: WebSocket) name = sprintf "JOIN%s" name |> ws.Send

let kick (ws: WebSocket) name = sprintf "KICK%s" name |> ws.Send

let getLobbies (ws: WebSocket) filters =
    List.reduce (fun acc f -> sprintf "%s/%s" acc f) filters
    |> sprintf "LOBS%s"
    |> ws.Send
