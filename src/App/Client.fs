module Client

open WebSocketSharp
open Newtonsoft.Json

type GameMode =
    | LastMan
    | BoostBall
    static member FromString str =
        match str with
        | "last_man" -> LastMan
        | "boost_ball" -> BoostBall
        | _ -> LastMan

type Limits = { Time: int; Score: int }

type LobbyParams =
    { Name: string
      Mode: GameMode
      Limits: Limits
      Capacity: int }

let openSocket uri = new WebSocket (uri)

let login uri _name =
    let ws = openSocket uri
    ws.Connect ()
    ws.OnMessage.Add (fun m -> printfn "%A" m.Data)
    ws

// let player = login "ws://localhost:8080/websocket" ""

let createLobby (ws: WebSocket) name mode time score cap =
    { Name = name
      Mode = GameMode.FromString mode
      Limits = { Time = time; Score = score }
      Capacity = cap }
    |> JsonConvert.SerializeObject
    |> sprintf "HOST%s"
    |> ws.Send
