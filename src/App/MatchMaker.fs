module GameServer.MatchMaker

open Newtonsoft.Json
open Suave
open Suave.Filters
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open System.Net

let strToBytes (str: string) =
    str |> System.Text.Encoding.ASCII.GetBytes |> ByteSegment

let sendString (agent: Agent<SocketMessage>) =
    let msg data = Send (Text, data, true)
    strToBytes >> msg >> agent.Post

let sendObj (agent: Agent<SocketMessage>) =
    JsonConvert.SerializeObject >> sendString agent

let broadcast agents str =
    let send (a: Agent<SocketMessage>) =
        a.Post <| Send (Text, strToBytes str, true)
    List.iter send agents

let broadcastObj agents =
    JsonConvert.SerializeObject >> broadcast agents

let getAgents players = List.map (fun p -> p.Agent) players

let newLobby (l: NewLobby) host =
    { Name = l.Name
      Params = l.Params
      ChatNonce = 0
      Host = host
      Players = [host] }

let getLobbyInfo (l: Lobby) =
    { Name = l.Name
      Params = l.Params
      HostName = l.Host.Name
      Population = l.Players.Length }

let dropPlayer player lobby =
    match List.except [player] lobby.Players with
    | [] -> None
    | ps ->
        do Departure player.Name |> LobbyUpdate |> broadcastObj (getAgents ps)
        Some { lobby with Players = ps }

let exitLobby player lobby =
    if player <> lobby.Host then
        do player.Agent.Post <| UpdateLobby Exit
        dropPlayer player lobby
    else
        do getAgents (List.except [player] lobby.Players)
        |> List.iter (fun a -> a.Post <| UpdateLobby Closed)
        None

let kickPlayer player lobby =
    do player.Agent.Post <| UpdateLobby Kicked
    dropPlayer player lobby

let lobbyAgent (man: Agent<ManagerMessage>) initial = Agent.Start(fun inbox ->
    let rec loop l = async {
        match! inbox.Receive() with
        | Join (player, channel) ->
            if l.Players.Length < l.Params.Capacity then
                do channel.Reply <| Some { Name = l.Name; LobbyAgent = inbox }
                do LobbyUpdate <| Arrival (player.Name, player.ID)
                |> broadcastObj (getAgents l.Players)
                return! loop { l with Players = player :: l.Players }
            else
                do channel.Reply None
                return! loop l
        | Leave player ->
            match exitLobby player l with
            | Some lob -> return! loop lob
            | None ->
                do man.Post <| DelistLobby l.Name
                return ()  // Say Goodnight.
        | Kick (id, host) ->
            if l.Host.ID = host.ID then
                return!
                    l.Players
                    |> List.tryFind (fun i -> i.ID = id)
                    |> Option.bind (fun p -> kickPlayer p l)
                    |> function
                       | Some lob -> loop lob
                       | None -> loop l
            else return! loop l
        | GateKeep host ->
            if l.Host.ID = host.ID then do man.Post <| DelistLobby l.Name
            else do ()
            return! loop l
        | LetThemIn host ->
            if l.Host.ID = host.ID
            then do man.Post <| RelistLobby { Name = l.Name; LobbyAgent = inbox }
            else do ()
            return! loop l
        | Chat (msg, player) ->
            do { Author = player.Name; Contents = msg; Nonce = l.ChatNonce }
            |> Chatter
            |> broadcastObj (getAgents l.Players)
            return! loop { l with ChatNonce = l.ChatNonce + 1 }
        | GetInfo channel ->
            if l.Players.Length < l.Params.Capacity
            then do channel.Reply <| Some (getLobbyInfo l)
            else do channel.Reply None
            return! loop l
    }
    loop initial
)

let lobbyManager = Agent.Start(fun inbox ->
    let rec loop (lobbies: Map<string, LobbyRef>) = async {
        match! inbox.Receive() with
        | Create (lobby, channel) ->
            match Map.containsKey lobby.Name lobbies with
            | true ->
                do channel.Reply None
                return! loop lobbies
            | false ->
                let l = { Name = lobby.Name
                          LobbyAgent = lobbyAgent inbox lobby }
                do channel.Reply <| Some l
                return! lobbies |> Map.add lobby.Name l |> loop
        | DelistLobby name -> return! lobbies |> Map.remove name |> loop
        | RelistLobby l -> return! lobbies |> Map.add l.Name l |> loop
        | LookupLobby (name, channel) ->
            do channel.Reply <| Map.tryFind name lobbies
            return! loop lobbies
        | RequestList channel ->
            do channel.Reply lobbies
            return! loop lobbies
    }
    loop (Map<string, LobbyRef> [])
)

// TODO: Actually look for forbidden words within the prospective name, rather
// than this stand-in where I check whether the entire name in the forbidden set
// (want to catch forbidden words anywhere in the name.) Regex?
let forbidden = Set.ofList [ "the n-word (hard R)" ]

let createLobby (specs: NewLobby) host =
    match host.Agent.PostAndReply CurrentLobby with
    | Some l -> MustLeaveLobby l.Name
    | None when not (Set.contains specs.Name forbidden) ->
        newLobby specs host
        |> fun lobby chan -> Create (lobby, chan)
        |> lobbyManager.PostAndReply
        |> function
           | Some l ->
               do host.Agent.Post <| UpdateLobby (Joined l)
               LobbyCreated
           | None -> NameExists
    | None -> NameForbidden
    |> HostResult
    |> sendObj host.Agent

let joinLobby name player =
    let joinMsg chan = Join (player, chan)
    let tryJoin l = l.LobbyAgent.TryPostAndReply (joinMsg, ?timeout = Some 1000)
    match player.Agent.PostAndReply CurrentLobby with
    | Some l -> AlreadyInLobby l.Name
    | None ->
        fun chan -> LookupLobby (name, chan)
        |> lobbyManager.PostAndReply
        |> Option.bind tryJoin
        |> function
           | Some (Some l) ->
               do player.Agent.Post <| UpdateLobby (Joined l)
               LobbyJoined
           | Some None -> NoSpace
           | None -> NoSuchLobby
    |> JoinResult
    |> sendObj player.Agent

let leaveLobby player =
    match player.Agent.PostAndReply CurrentLobby with
    | Some l -> Leave player |> l.LobbyAgent.Post
    | None -> ()

let kickFromLobby kickID player =
    match player.Agent.PostAndReply CurrentLobby with
    | Some l -> Kick (kickID, player) |> l.LobbyAgent.Post
    | None -> ()

let postChat msg player =
    match player.Agent.PostAndReply CurrentLobby with
    | Some l -> Chat (msg, player) |> l.LobbyAgent.Post
    | None -> ()

let getCompare = function
    | EQ -> (=) | NE -> (<>) | LT -> (<) | GT -> (>) | LE -> (<=) | GE -> (>=)

let createChooser filter : (LobbyInfo -> LobbyInfo option) =
    match filter with
    | GameMode m ->
        fun l -> if l.Params.Mode = m then Some l else None
    | Capacity (op, i) ->
        fun l -> if (getCompare op) l.Params.Capacity i then Some l else None
    | TimeLimit (op, i) ->
        fun l -> if (getCompare op) l.Params.Limits.Time i then Some l else None
    | ScoreLimit (op, i) ->
        fun l -> if (getCompare op) l.Params.Limits.Score i then Some l else None

let tryGetLobbyInfo l =
    l.LobbyAgent.TryPostAndReply (GetInfo, ?timeout = Some 1000)
    |> function | Some info -> info | None -> None

let getLobbies fs (wsAgent: Agent<SocketMessage>) =
    let sieve = function
        | [] -> Some
        | fs -> List.map createChooser fs
                |> List.reduce (fun f g -> f >> Option.bind g)
    RequestList
    |> lobbyManager.PostAndReply
    |> Map.toList
    |> List.choose (fun (_, l) -> tryGetLobbyInfo l |> Option.bind (sieve fs))
    |> LobbyList
    |> sendObj wsAgent

let socketAgent (ws: WebSocket) = Agent.Start(fun inbox ->
    let wsSendObj =
        JsonConvert.SerializeObject
        >> strToBytes
        >> (fun data -> ws.send Text data true)
    let rec loop state = async {
        match! inbox.Receive() with
        | CurrentLobby chan ->
            do chan.Reply state.Location
            return! loop state
        | UpdateLobby change ->
            match change with
            | Joined lobby -> return! loop { state with Location = Some lobby }
            | Kicked ->
                let! _ = wsSendObj (LobbyUpdate KickedByHost)
                return! loop { state with Location = None }
            | Closed ->
                let! _ = wsSendObj (LobbyUpdate LobbyClosed)
                return! loop { state with Location = None }
            | Exit -> return! loop { state with Location = None }
        | Send (op, data, fin) ->
            let! _ = ws.send op data fin
            return! loop state
        | Shut ->
            let! _ = ws.send Close (ByteSegment [||]) true
            return ()
    }
    loop { Location = None; Ready = false; Connected = false }
)

let playerSocket name ws (ctx: HttpContext) =
    let agent = socketAgent ws

    let info =
        { Name = name
          ID = System.Guid.NewGuid()
          IP = IPEndPoint (ctx.clientIpTrustProxy, 3047)
          Agent = agent }

    let rec loop () = socket {
        match! ws.read() with
        | (Text, data, true) ->
            data
            |> UTF8.toString
            |> JsonConvert.DeserializeObject<RequestSchema>
            |> function
               | GetLobbies filters -> getLobbies filters info.Agent
               | HostLobby specs -> createLobby specs info
               | JoinLobby name -> joinLobby name info
               | LeaveLobby -> leaveLobby info
               | KickPlayer id -> kickFromLobby id info
               | ChatMessage msg -> postChat msg info
            return! loop ()
        | (Close, _, _) -> return ()
        | _ -> return! loop ()
    }

    socket {
        do printfn "%s Connected!" name
        try do! loop ()
        finally do
            leaveLobby info
            agent.Post Shut
            printfn "%s Disconnected" name
    }

let connectToPlayer name = fun ctx ->
    handShake (playerSocket name) ctx

let server =
    choose [ pathScan "/websocket/%s" connectToPlayer ]

// let connectToPlayer (name, (addr: string), port) = fun ctx ->
//     let ip = IPEndPoint (IPAddress.Parse addr, port)
//     handShake (playerSocket name ip) ctx

// let server =
//     choose [ pathScan "/websocket/%s/%s/%i" connectToPlayer ]
