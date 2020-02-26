module Client

open GameServer

open WebSocketSharp
open Newtonsoft.Json
open System.Net
open System.Net.Sockets

let sendObj (ws: WebSocket) = JsonConvert.SerializeObject >> ws.Send

let getLocalIP () =
    use s = new Socket(AddressFamily.InterNetwork,
                       SocketType.Dgram,
                       ProtocolType.Udp)
    s.Connect("8.8.8.8", 65530)
    let endpoint = s.LocalEndPoint :?> IPEndPoint
    endpoint.Address.ToString()

let openSocket uri = new WebSocket (uri)

let socketReceive (m: MessageEventArgs) =
    try
        m.Data
        |> JsonConvert.DeserializeObject<ResponseSchema>
        |> printfn "%A"
    with
        _ -> printfn "Failed to deserialize:\n%A" m.Data

let login uri name =
    let ws = sprintf "%s/%s" uri name |> openSocket
    ws.Connect ()
    //ws.OnMessage.Add (fun m -> printfn "%A" m.Data)
    ws.OnMessage.Add socketReceive
    ws

let createLobby (ws: WebSocket) name mode time score cap =
    { Name = name
      Params = { Mode = GameMode.FromString mode
                 Limits = { Time = time; Score = score }
                 Capacity = cap } }
    |> HostLobby
    |> sendObj ws

let chat (ws: WebSocket) msg = ChatMessage msg |> sendObj ws

let drop (ws: WebSocket) = LeaveLobby |> sendObj ws

let join (ws: WebSocket) name = JoinLobby name |> sendObj ws

let kick (ws: WebSocket) name = KickPlayer name |> sendObj ws

let getLobbies (ws: WebSocket) filters = GetLobbies filters |> sendObj ws
