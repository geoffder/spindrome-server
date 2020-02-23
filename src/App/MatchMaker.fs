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

// TODO: Very simplistic. Also, I should probably create a simplified
// lobby / player info that doesn't have the agent references for example.
// It's just extra junk getting packed in to the jsons going out.
let broadcastObj agents tag =
    JsonConvert.SerializeObject
    >> sprintf "%s%s" tag
    >> broadcast agents

let getAgents players = List.map (fun p -> p.Agent) players

let newLobby (l: NewLobby) host =
    { Name = l.Name
      ID = System.Guid.NewGuid()
      Params = l.Params
      Host = host
      Players = [host] }

let tryJoin lobby player =
    if lobby.Players.Length < lobby.Params.Capacity
    then Some { lobby with Players = player :: lobby.Players }
    else None

let dropPlayer player lobby =
    match List.except [player] lobby.Players with
    | [] -> None
    | ps ->
        do player.ID.ToString ()
        |> sprintf "DROP:%s"
        |> broadcast (getAgents ps)
        Some { lobby with Players = ps }

let exitLobby player lobby =
    if player <> lobby.Host then
        do player.Agent.Post <| UpdateLobby Exit
        dropPlayer player lobby
    else
        do getAgents lobby.Players
        |> List.iter (fun a -> a.Post <| UpdateLobby Closed)
        None

let kickPlayer player lobby =
    do player.Agent.Post <| UpdateLobby Kicked
    dropPlayer player lobby

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
                |> Option.bind (fun l -> exitLobby player l)
                |> function
                   | Some l -> loop (Map.add name l lobbies)
                   | None -> loop (Map.remove name lobbies)
        | Kick (name, id, host) ->
            let l = lobbies.[name]
            return!
                if host.ID = l.Host.ID then
                    l.Players
                    |> List.tryFind (fun i -> i.ID = id)
                    |> Option.bind (fun p -> kickPlayer p l)
                    |> function
                       | Some l -> loop (Map.add name l lobbies)
                       | None -> loop lobbies
                else loop lobbies
        | Chat (name, msg, player) ->
            do sprintf "CHAT#%s: %s" player.Name msg
            |> broadcast (getAgents lobbies.[name].Players)
            return! loop lobbies
        | RequestList channel ->
            do channel.Reply lobbies
            return! loop lobbies
    }

    loop (Map<string, Lobby> [])
)

let createLobby specs host =
    newLobby specs host
    |> fun lobby chan -> Create (lobby, chan)
    |> lobbyAgent.PostAndReply
    |> function
       | Some name ->
           do host.Agent.Post <| UpdateLobby (Joined name)
           "Lobby created!"
       | None -> "Name is taken!"
    |> sendString host.Agent

// TODO: Replace with has space? (local duo for example)
let lobbyNotFull _name l = l.Players.Length < l.Params.Capacity

// TODO: Check that player isn't already in a lobby.
let joinLobby name player =
    fun chan -> Join (name, player, chan)
    |> lobbyAgent.PostAndReply
    |> function
       | true ->
           do player.Agent.Post <| UpdateLobby (Joined name)
           sprintf "Joined %s!" name
       | false -> "Failed to join."
    |> sendString player.Agent

let leaveLobby player =
    match player.Agent.PostAndReply GetLobby with
    | Some name -> Leave (name, player) |> lobbyAgent.Post
    | None -> ()

let kickFromLobby kickID player =
    match player.Agent.PostAndReply GetLobby with
    | Some name ->
        Kick (name, kickID, player)
        |> lobbyAgent.Post
    | None -> ()

let postChat msg player =
    match player.Agent.PostAndReply GetLobby with
    | Some name -> Chat (name, msg, player) |> lobbyAgent.Post
    | None -> ()

let getLobbyFilter str : (Name -> Lobby -> bool) list =
    match str with
    | "last_man" -> [ fun _ l -> l.Params.Mode = LastMan ]
    | "boost_ball" -> [ fun _ l -> l.Params.Mode = BoostBall ]
    | _ -> []

let getLobbies strs (wsAgent: MailboxProcessor<SocketMessage>) =
    let filters = List.collect getLobbyFilter strs
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
        | UpdateLobby change ->
            match change with
            | Joined name -> return! loop { state with LobbyName = Some name }
            | Kicked ->
                let! _ = ws.send Text (strToBytes "Kicked!") true
                return! loop { state with LobbyName = None }
            | Closed ->
                let! _ = ws.send Text (strToBytes "Lobby Closed!") true
                return! loop { state with LobbyName = None }
            | Exit -> return! loop { state with LobbyName = None }
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
        | (Close, _, _) ->
            do agent.Post Shut
            return ()
        | _ -> return! loop ()
    }

    socket {
        printfn "%s Connected!" name
        try do! loop ()
        finally
            leaveLobby info
            printfn "%s Disconnected!" name
    }

let connectToPlayer name = fun ctx ->
    handShake (playerSocket name) ctx

let server =
    choose [ pathScan "/websocket/%s" connectToPlayer ]
