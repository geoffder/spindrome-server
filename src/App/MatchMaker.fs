module GameServer.MatchMaker

open Newtonsoft.Json

open Suave
open Suave.Filters
// open Suave.Operators
// open Suave.Successful

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

let strToBytes (str: string) =
    str |> System.Text.Encoding.ASCII.GetBytes |> ByteSegment

let sendString (agent: MailboxProcessor<SocketMessage>) =
    let msg data = Send (Text, data, true)
    strToBytes >> msg >> agent.Post

let broadcast agents str =
    let send (a: MailboxProcessor<SocketMessage>) =
        a.Post <| Send (Text, strToBytes str, true)
    List.iter send agents

let broadcastObj agents tag =
    JsonConvert.SerializeObject
    >> sprintf "%s%s" tag
    >> broadcast agents

let getAgents players = List.map (fun p -> p.Agent) players

let newLobby host (ps: LobbyParams) =
    { Name = ps.Name
      ID = System.Guid.NewGuid()
      Mode = ps.Mode
      Limits = ps.Limits
      Capacity = ps.Capacity
      Host = host
      Players = [host] }

let tryJoin lobby player =
    if lobby.Players.Length < lobby.Capacity
    then Some { lobby with Players = player :: lobby.Players }
    else None

let dropPlayer player lobby =
    if player <> lobby.Host then
        do sendString player.Agent "DROP!"
        match List.except [player] lobby.Players with
        | [] -> None
        | ps ->
            do broadcast (getAgents ps) "DROP!"
            Some { lobby with Players = ps }
    else
        do broadcast (getAgents lobby.Players) "DROP!"
        None

let applyFiltersToMap filters key value =
    let rec apply = function
        | f :: t -> if f key value then apply t else false
        | [] -> true
    apply filters

// TODO: Create a broadcast function that sends up to data lobby info
// to the players inside each time there is a change.
let lobbyAgent = MailboxProcessor.Start(fun inbox ->

    let rec loop (lobbies: Map<string, Lobby>) = async {
        match! inbox.Receive() with
        | Create (lobby, channel) ->
            match Map.containsKey lobby.Name lobbies with
            | true ->
                do channel.Reply None
                return! loop lobbies
            | false ->
                do channel.Reply (Some lobby.Name)
                return! lobbies |> Map.add lobby.Name lobby |> loop
        | Join (name, player, channel) ->
            match tryJoin lobbies.[name] player with
            | Some l ->
                do channel.Reply true
                do broadcastObj (getAgents l.Players) "LOBBY" l
                return! loop (Map.add name l lobbies)
            | None ->
                do channel.Reply false
                return! loop lobbies
        | Leave (name, player) ->
            return!
                Map.tryFind name lobbies
                |> Option.bind (fun l -> dropPlayer player l)
                |> function
                   | Some l -> loop (Map.add name l lobbies)
                   | None -> loop (Map.remove name lobbies)
        | Kick (name, id, host) ->
            let l = lobbies.[name]
            return!
                if host.ID = l.Host.ID then
                    l.Players
                    |> List.tryFind (fun i -> i.ID = id)
                    |> Option.bind (fun p -> dropPlayer p l)
                    |> function
                       | Some l -> loop (Map.add name l lobbies)
                       | None -> loop lobbies
                else loop lobbies
        | RequestList channel ->
            do channel.Reply lobbies
            return! loop lobbies
    }

    loop (Map<string, Lobby> [])
)

let createLobby json host =
    json
    |> JsonConvert.DeserializeObject<LobbyParams>
    |> newLobby host
    |> fun lobby chan -> Create (lobby, chan)
    |> lobbyAgent.PostAndReply
    |> function
       | None -> "Name is taken!"
       | someName ->
           do host.Agent.Post (UpdateLobby someName)
           "Lobby created!"
    |> sendString host.Agent

let joinLobby name player =
    fun chan -> Join (name, player, chan)
    |> lobbyAgent.PostAndReply
    |> function
       | true ->
           do player.Agent.Post <| UpdateLobby (Some name)
           sprintf "Joined %s!" name
       | false -> "Failed to join."
    |> sendString player.Agent

let leaveLobby player =
    match player.Agent.PostAndReply GetLobby with
    | Some name -> Leave (name, player) |> lobbyAgent.Post
    | None -> ()

let kickFromLobby (kickID: string) player =
    match player.Agent.PostAndReply GetLobby with
    | Some name ->
        Kick (name, System.Guid.Parse kickID, player)
        |> lobbyAgent.Post
    | None -> ()

// TODO: Replace with has space? (local duo for example)
let lobbyNotFull _name l = l.Players.Length < l.Capacity

let getLobbyFilter str : (Name -> Lobby -> bool) list =
    match str with
    | "last_man" -> [ fun _ l -> l.Mode = LastMan ]
    | "boost_ball" -> [ fun _ l -> l.Mode = BoostBall ]
    | _ -> []

let getLobbies bytes (wsAgent: MailboxProcessor<SocketMessage>) =
    let filters =
        bytes
        |> UTF8.toString
        |> fun str -> str.Split "/"
        |> List.ofArray
        |> List.distinct
        |> List.collect getLobbyFilter
    RequestList
    |> lobbyAgent.PostAndReply
    |> Map.filter (applyFiltersToMap (lobbyNotFull :: filters))
    |> JsonConvert.SerializeObject
    |> sendString wsAgent

let socketAgent (ws: WebSocket) = MailboxProcessor.Start(fun inbox ->
    let rec loop state = async {
        match! inbox.Receive() with
        | GetLobby chan ->
            do chan.Reply state.LobbyName
            return! loop state
        | UpdateLobby name -> return! loop { state with LobbyName = name }
        | Send (op, data, fin) ->
            let! _ = ws.send op data fin
            return! loop state
        | Shut ->
            let! _ = ws.send Close (ByteSegment [||]) true
            return ()
    }

    loop { LobbyName = None }
)

let playerSocket name ws _ctx =
    let agent = socketAgent ws

    let info = { Name = name; ID = System.Guid.NewGuid(); Agent = agent }

    let rec loop () = socket {
        match! ws.read() with
        | (Text, data, true) ->
            match UTF8.toString data.[0..3] with
            | "LOBS" -> getLobbies data.[4..] info.Agent
            | "HOST" -> createLobby (UTF8.toString data.[4..]) info
            | "JOIN" -> joinLobby (UTF8.toString data.[4..]) info
            | "DROP" -> leaveLobby info
            | "KICK" -> kickFromLobby (UTF8.toString data.[4..]) info
            // | "CHAT" -> postChat (UTF8.toString data.[1..]) info
            | _ -> ()
            return! loop ()
        | (Close, _, _) ->
            do agent.Post Shut
            return ()
        | _ -> return! loop ()
    }

    socket {
        printfn "Socket Connected!"
        try do! loop ()
        finally printfn "Socket Disconnected!"
    }

let connectToPlayer name = fun ctx ->
    handShake (playerSocket name) ctx

let server =
    choose [ pathScan "/websocket/%s" connectToPlayer ]
