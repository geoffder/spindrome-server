module Client

open GameServer
open GameServer.Helpers
open GameServer.AgentOperators

open WebSocketSharp
open Newtonsoft.Json
open System.Net
open System.Net.Sockets
open System.Threading

type UDPMessage = Ping | Pong | InvalidMessage

let sendObj (ws: WebSocket) = JsonConvert.SerializeObject >> ws.Send

let getLocalIP () =
    use s = new Socket(AddressFamily.InterNetwork,
                       SocketType.Dgram,
                       ProtocolType.Udp)
    s.Connect("8.8.8.8", 65530)
    let endpoint = s.LocalEndPoint :?> IPEndPoint
    endpoint.Address.ToString()

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

let socketAgent (_ws: WebSocket) = Agent.Start(fun inbox ->
        let rec loop () = async {
            match! inbox.Receive() with
            | _ -> ()
        }
        loop ()
    )

// TODO: Create a ping pong agent (always running?) that is messaged
// with (PingTime endpoint * t) and (PongTime endpoint * t). Storing a record
// with the ping and pong times in Map (for each endpoint). Then calculating the
// latency time (if pongtime is Some t, None means fail), and sending the
// WiringResults back up the socket. This wiringAgent will be told to send
// ping messages to the sending agent (wiping it's current state first),
// marking the times that it does. Also it needs to create a delayed message to
// itself as a "ping timeout".

// NOTE: An important part of the ping process is making multiple attempts
// before final timeout, incase a ping or pong packet is dropped...

// TODO: I'll probably want a separate agent that will handle background
// ping/pong-ing on a lobby by lobby level on being sent a lobby of peers to
// test (for displaying latency in lobby menus, and for matchmaking)

// NOTE: From looking, it seems that using separate ports is the safe way to go,
// since reading from a port and trying to send with it could cause an exception
// Therefore, I shouldn't be responding directly to the same endpoint? Maybe
// send on port + 1.
// Actually, just did some testing, and multiple clients using the same port to
// send did not have any problems, unless sending many messages
// near-simultaneously. Therefore a sending agent managing a single client does
// not appear to be strictly necessary. One for receiving all messages meant for
// a particular port is important though.

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

// TODO: The remaining issue I need to think about, is how the different agents
// are managed. The recieving loop needs to know about the ping/pong agent, or
// perhaps a manager that will pass things along to where they need to go...

let receiving (port: int) (sender: Agent<IPEndPoint * UDPMessage>) =
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
