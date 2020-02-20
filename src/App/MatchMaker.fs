module GameServer.MatchMaker

open Newtonsoft.Json

open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

let strToBytes (str: string) =
    str |> System.Text.Encoding.ASCII.GetBytes |> ByteSegment

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
        match List.except [player] lobby.Players with
        | [] -> None
        | ps -> Some { lobby with Players = ps }
    else None

let applyFiltersToMap filters key value =
    let rec apply = function
        | f :: t -> if f key value then apply t else false
        | [] -> true
    apply filters

// let playerAgent = MailboxProcessor.Start(fun inbox ->
//
//     let rec loop (players: Map<System.Guid, Player>) = async {
//         match! inbox.Receive() with
//         | Login (player, channel) ->
//             do channel.Reply (sprintf "%s logged in" (player.ID.ToString()))
//             return! loop (Map.add player.ID player players)
//         | Logout id -> return! loop (Map.remove id players)
//         | GetPlayer (id, channel) ->
//             do channel.Reply (Map.tryFind id players)
//             return! loop players
//     }
//
//     loop (Map<System.Guid, Player> [])
// )

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
            | Some lobby ->
                do channel.Reply true
                return! loop (Map.add name lobby lobbies)
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
        | RequestList channel ->
            do channel.Reply lobbies
            return! loop lobbies
    }

    loop (Map<string, Lobby> [])
)

// let getPlayer id =
//     let msg chan = GetPlayer (id, chan)
//     playerAgent.PostAndReply msg

// let createLobby (name, mode, time, score, cap, id: string) =
//     let buildLobby host =
//         { Name = name
//           ID = System.Guid.NewGuid()
//           Mode = GameMode.FromString mode
//           Limits = { Time = time; Score = score }
//           Capacity = cap
//           Host = host
//           Players = [host] }
//     let msg lobby chan = Create (lobby, chan)
//     let sendNewLobby = buildLobby >> msg >> lobbyAgent.PostAndReply
//     match getPlayer (System.Guid.Parse id) with
//     | Some p -> p |> sendNewLobby |> OK
//     | None -> OK "No such player ID is logged in."

// TODO: Something like this as the new patter? Make type matching
// json schema to use automatic Newtonsoft deserialization?
let createLobby2 json host =
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
    |> strToBytes
    |> fun data -> host.Agent.Post (Send (Text, data, true))

// let joinLobby (name, id: string) =
//     let msg player chan = Join (name, player, chan)
//     match getPlayer (System.Guid.Parse id) with
//     | Some p -> p |> msg |> lobbyAgent.PostAndReply |> OK
//     | None -> OK "No such player ID is logged in."

let joinLobby2 name player =
    fun chan -> Join (name, player, chan)
    |> lobbyAgent.PostAndReply
    |> function
       | true ->
           do player.Agent.Post (UpdateLobby (Some name))
           sprintf "Joined %s!" name
       | false -> "Failed to join."
    |> strToBytes
    |> fun data -> player.Agent.Post (Send (Text, data, true))

// TODO: ? Add a player to lobby lookup to enable dropping from lobby when
// connection is lost ?
// let leaveLobby (id: string, lobbyName) =
//     match getPlayer (System.Guid.Parse id) with
//     | Some p -> Leave (lobbyName, p) |> lobbyAgent.Post
//     | None -> ()
//     OK "Lobby exited."

let leaveLobby2 player =
    match player.Agent.PostAndReply GetLobby with
    | Some name -> Leave (name, player) |> lobbyAgent.Post
    | None -> ()

// TODO: Replace with has space? (local duo for example)
let lobbyNotFull _name l = l.Players.Length < l.Capacity

let getLobbyFilter str : (Name -> Lobby -> bool) list =
    match str with
    | "last_man" -> [ fun _ l -> l.Mode = LastMan ]
    | "boost_ball" -> [ fun _ l -> l.Mode = BoostBall ]
    | _ -> []

let getLobbies (filterStr: string) =
    let filters =
        filterStr.Split "/"
        |> List.ofArray
        |> List.distinct
        |> List.collect getLobbyFilter
    RequestList
    |> lobbyAgent.PostAndReply
    |> Map.filter (applyFiltersToMap (lobbyNotFull :: filters))
    |> JsonConvert.SerializeObject
    |> OK

let getLobbies2 bytes (wsAgent: MailboxProcessor<SocketMessage>) =
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
    |> strToBytes
    |> fun data -> Send (Text, data, true)
    |> wsAgent.Post

// TODO: Add some player state to this (e.g. what lobby)
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

let playerSocket2 (ws : WebSocket) _ctx =
    let agent = socketAgent ws

    let info = { Name = "dummy"; ID = System.Guid.NewGuid(); Agent = agent }

    let rec loop () = socket {
        match! ws.read() with
        | (Text, data, true) ->
            // TODO: Pass in agent so that state can be updated?
            // e.g. which lobby they are in.
            match UTF8.toString data.[0..3] with
            | "HOST" -> createLobby2 (UTF8.toString data.[4..]) info
            | "JOIN" -> joinLobby2 (UTF8.toString data.[4..]) info
            | "DROP" -> leaveLobby2 info
            // | 52uy -> kickFromLobby (UTF8.toString data.[1..])
            // | 53uy -> postChat (UTF8.toString data.[1..])
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

let playerSocket (ws : WebSocket) (_ctx: HttpContext) =
    let agent = socketAgent ws

    let rec loop () = socket {
        match! ws.read() with
        | (Text, data, true) ->
            let response =
                UTF8.toString data
                |> sprintf "response to %s"
                |> System.Text.Encoding.ASCII.GetBytes
                |> ByteSegment
            do agent.Post (Send (Text, response, true))
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

// NOTE: This pattern will let me use path scan and do other things with the
// context while returning what the client needs for the socket.
let wsTest _str = fun ctx ->
    handShake playerSocket ctx

// let loginPlayer name =
//     let msg chan = Login ({ Name = name; ID = System.Guid.NewGuid() } , chan)
//     msg |> playerAgent.PostAndReply |> OK

let server =
    choose [ path "/websocket" >=> handShake playerSocket2 ]

// let server =
//     choose
//         [ // pathScan "/websocket/%s" wsTest
//           // path "/websocket" >=> handShake (webSock onSock offSock)
//           path "/websocket" >=> handShake playerSocket
//           GET >=> choose
//               [ pathScan "/lobby_list/%s" getLobbies ]
//           POST >=> choose
//               [ pathScan "/login/%s" loginPlayer
//                 pathScan "/create_lobby/%s/%s/%i/%i/%i/%s" createLobby
//                 pathScan "/join_lobby/%s/%s" joinLobby
//                 pathScan "/leave_lobby/%s/%s" leaveLobby ] ]
