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

let delayAction ms f =
    async {
        do! Async.Sleep ms
        do f ()
    } |> Async.Start

let getLocalIP () =
    use s = new Socket(AddressFamily.InterNetwork,
                       SocketType.Dgram,
                       ProtocolType.Udp)
    s.Connect("8.8.8.8", 65530)
    let endpoint = s.LocalEndPoint :?> IPEndPoint
    endpoint.Address.ToString ()

let openSocket uri = new WebSocket (uri)

let responseHandler _ws wirer = function
    | LobbyUpdate (PingPongTime peers) -> wirer <-- BeginWiring peers
    | response -> printfn "%A" response

let socketReceive ws wirer (m: MessageEventArgs) =
    try
        m.Data
        |> JsonConvert.DeserializeObject<ResponseSchema>
        |> Some
    with _ -> None
    |> function
       | Some r -> responseHandler ws wirer r
       | None -> do printfn "Failed to deserialize:\n%A" m.Data

let login uri name port =
    let ws = sprintf "%s/%s/%i" uri name port |> openSocket
    ws.Connect ()
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
let getLobbies ws filters = GetLobbies filters |> sendObj ws
let close (ws: WebSocket) = ws.Close ()

let wiringReport ws fails = PeersPonged fails |> sendObj ws

let sendingAgent (port: int) = Agent<IPEndPoint * UDPMessage>.Start(fun inbox ->
    let client = new UdpClient (port)
    let rec loop () = async {
        let! endpoint, msg = inbox.Receive ()
        do printfn "sending %A to %A" msg endpoint
        msg
        |> JsonConvert.SerializeObject
        |> strToBytes
        |> fun bs -> client.Send (bs, bs.Length, endpoint)
        |> ignore
        return! loop ()
    }
    loop ()
)

let wiringAgent (ws: WebSocket) sender maxFails = Agent.Start(fun inbox ->
    let createPeer (p: PeerInfo) =
        (p.GUID, { EndPoint = IPEndPoint (IPAddress.Parse p.IP, p.Port)
                   Status = Strike 0 })
    let strikeCheck = function | Strike s -> s | Wired -> maxFails
    let timeout id strike =
        fun () -> inbox <-- TimeOut (id, strike)
        |> delayAction 300

    let rec loop nFinished (results: Map<System.Guid, WiringPeer>) = async {
        let! msg = receive inbox
        let fin, updated =
            match msg with
            | BeginWiring peers ->
                let ps = peers |> List.map createPeer |> Map.ofList
                ps |> Map.iter (fun id p ->
                                sender <-- (p.EndPoint, WirePing (id, 1))
                                timeout id 1)
                (0, ps)
            | TimeOut (id, _) when results.[id].Status = Wired ->
                (nFinished, results)
            | TimeOut (id, s) when s = maxFails ->
                results
                |> Map.add id { results.[id] with Status = Strike maxFails }
                |> fun u -> (nFinished + 1, u)
            | TimeOut (id, s) when strikeCheck results.[id].Status < s ->
                do sender <-- (results.[id].EndPoint, WirePing (id, s + 1))
                do timeout id (s + 1)
                results
                |> Map.add id { results.[id] with Status = Strike s }
                |> fun u -> (nFinished, u)
            | Ponged (id, s) when strikeCheck results.[id].Status < s ->
                results
                |> Map.add id { results.[id] with Status = Wired }
                |> fun u -> (nFinished + 1, u)
            | _ -> (nFinished, results)  // s <= to the current (Strike num)

        if fin > 0 && fin = Map.count results then
            updated
            |> Map.toList
            |> List.collect (fun (id, p) ->
                             if p.Status <> Wired then [id] else [])
            |> wiringReport ws
            return! loop 0 updated
        else return! loop fin updated
    }
    loop 0 Map.empty
)

// TODO: _pinger will be always running latency agent.
// TODO: Needs to know what IP and port to listen to, the IP will be whatever
// the websocket is using... I thought IPAddress.Any (default) would catch
// things sent to 127.0.0.1 (localhost) but it doesn't seem to.
let receiving (port: int) sender wirer _pinger =
    // let client = new UdpClient (port)
    let client = new UdpClient (IPEndPoint (IPAddress.Parse "127.0.0.1", port))
    // let client = new UdpClient (IPEndPoint (IPAddress.Any, port))
    // let client = new UdpClient ();
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
           | WirePing (id, strike) ->
               do printfn "WirePing from %A!" result.RemoteEndPoint
               sender <-- (result.RemoteEndPoint, WirePong (id, strike))
           | WirePong (id, strike) ->
               do printfn "WirePong from %A!" result.RemoteEndPoint
               wirer <-- Ponged (id, strike)
           | InvalidMessage -> printfn "Bad UDP message."
        return! loop ()
    }
    loop () |> Async.Start

let ping (sender: Agent<IPEndPoint * UDPMessage>) port =
    sender <-- (IPEndPoint (IPAddress.Any, port), Ping)

type Client (name, uri, port) =
    let ws = login uri name port
    let udpSender = sendingAgent port
    let wirer = wiringAgent ws udpSender 3
    let _udpReceiver = receiving port udpSender wirer []
    do ws.OnMessage.Add (socketReceive ws wirer)
    member __.CreateLobby = createLobby ws
    member __.Chat = chat ws
    member __.Drop () = drop ws
    member __.Join = join ws
    member __.Kick = kick ws
    member __.Ready () = ready ws
    member __.UnReady () = unready ws
    member __.HitPlay () = hitPlay ws
    member __.GetLobbies = getLobbies ws
    member __.Close () = ws.Close ()
    member __.PlainPing remotePort = ping udpSender remotePort
