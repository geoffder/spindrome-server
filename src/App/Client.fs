module Client

open GameServer
open GameServer.Helpers
open GameServer.AgentOperators
open GameServer.AgentHelpers

open WebSocketSharp
open Newtonsoft.Json
open System.Net
open System.Net.Sockets

type UDPMessage =
    | Ping
    | Pong
    | WirePing of System.Guid * int
    | WirePong of System.Guid * int
    | InvalidMessage
type WiringMessage =
    | BeginWiring of PeerInfo list
    | Ponged of System.Guid * int
    | TimeOut of System.Guid * int
type WiringStatus = Strike of int | Wired
type WiringPeer = { EndPoint: IPEndPoint; Status: WiringStatus }

let sendObj (ws: WebSocket) = JsonConvert.SerializeObject >> ws.Send

let getLocalIP () =
    use s = new Socket(AddressFamily.InterNetwork,
                       SocketType.Dgram,
                       ProtocolType.Udp)
    s.Connect("8.8.8.8", 65530)
    let endpoint = s.LocalEndPoint :?> IPEndPoint
    endpoint.Address.ToString ()

let openSocket uri = new WebSocket (uri)

let responseHandler ws = function
    | LobbyUpdate (PingPongTime peers) ->
        peers |> printfn "Play pingpong with:\n%A"
    | response -> printfn "%A" response

let socketReceive ws (m: MessageEventArgs) =
    try
        m.Data
        |> JsonConvert.DeserializeObject<ResponseSchema>
        |> Some
    with _ -> None
    |> function
       | Some r -> responseHandler ws r
       | None -> do printfn "Failed to deserialize:\n%A" m.Data

let login uri name =
    let ws = sprintf "%s/%s" uri name |> openSocket
    ws.Connect ()
    ws.OnMessage.Add (socketReceive ws)
    ws

let createLobby (ws: WebSocket) name mode time score cap =
    { Name = name
      Params = { Mode = mode
                 Limits = { Time = time; Score = score }
                 Capacity = cap } }
    |> HostLobby
    |> sendObj ws

let chat ws msg = ChatMessage msg |> sendObj ws
let drop ws = LeaveLobby |> sendObj ws
let join ws name = JoinLobby name |> sendObj ws
let kick ws (id: string) = KickPlayer (System.Guid.Parse id) |> sendObj ws
let ready ws = ReadyUp true |> sendObj ws
let unready ws = ReadyUp false |> sendObj ws
let hitPlay ws = HitPlay |> sendObj ws
let wiringReport ws fails = PeersPonged fails |> sendObj ws
let getLobbies ws filters = GetLobbies filters |> sendObj ws

let close (ws: WebSocket) = ws.Close ()

let sendingAgent (port: int) = Agent<IPEndPoint * UDPMessage>.Start(fun inbox ->
    let client = new UdpClient (port)
    let rec loop () = async {
        let! endpoint, msg = inbox.Receive ()
        msg
        |> JsonConvert.SerializeObject
        |> strToBytes
        |> fun bs -> client.Send (bs, bs.Length, endpoint)
        |> ignore
        return! loop ()
    }
    loop ()
)

let maxFails = 3

let wiringAgent (ws: WebSocket) port sender = Agent.Start(fun inbox ->
    let createPeer (p: PeerInfo) =
        (p.GUID, { EndPoint = IPEndPoint (IPAddress (strToBytes p.IPStr), port)
                   Status = Strike 0 })
    let strikeCheck = function | Strike s -> s | Wired -> maxFails

    let rec loop nFinished (results: Map<System.Guid, WiringPeer>) = async {
        let! msg = receive inbox
        let fin, updated =
            match msg with
            | BeginWiring peers ->
                let ps = peers |> List.map createPeer |> Map.ofList
                ps |> Map.iter (fun id p ->
                                sender <-- (p.EndPoint, WirePing (id, 1)))
                (0, ps)
            | TimeOut (id, _) when results.[id].Status = Wired ->
                (nFinished, results)
            | TimeOut (id, maxFails) ->
                results
                |> Map.add id { results.[id] with Status = Strike maxFails }
                |> fun u -> (nFinished + 1, u)
            | TimeOut (id, s) when strikeCheck results.[id].Status < s ->
                do sender <-- (results.[id].EndPoint, WirePing (id, s + 1))
                results
                |> Map.add id { results.[id] with Status = Strike s }
                |> fun u -> (nFinished, u)
            | Ponged (id, s) when strikeCheck results.[id].Status < s ->
                results
                |> Map.add id { results.[id] with Status = Wired }
                |> fun u -> (nFinished + 1, u)
            | _ -> (nFinished, results)  // s <= to the current (Strike num)

        if fin = Map.count results then do
            updated
            |> Map.toList
            |> List.collect (fun (id, p) ->
                             if p.Status <> Wired then [id] else [])
            |> wiringReport ws
        return! loop fin updated
    }
    loop 0 Map.empty
)

// TODO: _pinger will be always running latency agent.
let receiving (port: int) sender wirer _pinger =
    let client = new UdpClient (port)
    let rec loop () = async {
        let! result = client.ReceiveAsync () |> Async.AwaitTask
        try
            result.Buffer
            |> bytesToStr
            |> JsonConvert.DeserializeObject<UDPMessage>
        with _ -> InvalidMessage
        |> function
           | Ping ->
               printfn "Ping from %A!" result.RemoteEndPoint
               sender <-- (result.RemoteEndPoint, Pong)
           | Pong ->
               printfn "Pong from %A!" result.RemoteEndPoint
           | InvalidMessage -> printfn "Bad UDP message."
        return! loop ()
    }
    loop () |> Async.Start

let ping (sender: Agent<IPEndPoint * UDPMessage>) port =
    sender <-- (IPEndPoint (IPAddress.Any, port), Ping)
