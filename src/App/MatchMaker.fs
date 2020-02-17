module GameServer.MatchMaker

open Newtonsoft.Json

open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

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

let playerAgent = MailboxProcessor.Start(fun inbox ->

    let rec loop (players: Map<System.Guid, Player>) = async {
        match! inbox.Receive() with
        | Login (player, channel) ->
            do channel.Reply (sprintf "%s logged in" (player.ID.ToString()))
            return! loop (Map.add player.ID player players)
        | Logout id -> return! loop (Map.remove id players)
        | GetPlayer (id, channel) ->
            do channel.Reply (Map.tryFind id players)
            return! loop players
    }

    loop (Map<System.Guid, Player> [])
)

let lobbyAgent = MailboxProcessor.Start(fun inbox ->

    let rec loop (lobbies: Map<string, Lobby>) = async {
        match! inbox.Receive() with
        | Create (lobby, channel) ->
            match Map.containsKey lobby.Name lobbies with
            | true ->
                do channel.Reply "Lobby with that name already exists."
                return! loop lobbies
            | false ->
                do channel.Reply "Lobby created!"
                return! lobbies |> Map.add lobby.Name lobby |> loop
        | Join (name, player, channel) ->
            match tryJoin lobbies.[name] player with
            | Some lobby ->
                do channel.Reply "Succesfully joined lobby!"
                return! loop (Map.add name lobby lobbies)
            | None ->
                do channel.Reply "All full up!"
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

let getPlayer id =
    let msg chan = GetPlayer (id, chan)
    playerAgent.PostAndReply msg

let createLobby (name, mode, time, score, cap, id: string) =
    let buildLobby host =
        { Name = name
          ID = System.Guid.NewGuid()
          Mode = GameMode.FromString mode
          Limits = { Time = time; Score = score }
          Capacity = cap
          Host = host
          Players = [host] }
    let msg lobby chan = Create (lobby, chan)
    let sendNewLobby = buildLobby >> msg >> lobbyAgent.PostAndReply
    match getPlayer (System.Guid.Parse id) with
    | Some p -> p |> sendNewLobby |> OK
    | None -> OK "No such player ID is logged in."

let joinLobby (name, id: string) =
    let msg player chan = Join (name, player, chan)
    match getPlayer (System.Guid.Parse id) with
    | Some p -> p |> msg |> lobbyAgent.PostAndReply |> OK
    | None -> OK "No such player ID is logged in."

// TODO: ? Add a player to lobby lookup to enable dropping from lobby when
// connection is lost ?
let leaveLobby (id: string, lobbyName) =
    match getPlayer (System.Guid.Parse id) with
    | Some p -> Leave (lobbyName, p) |> lobbyAgent.Post
    | None -> ()
    OK "Lobby exited."

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

// NOTE: Example Suave websocket code.
let ws (webSocket : WebSocket) (_context: HttpContext) =
    socket {
        let mutable loop = true
        while loop do
            match! webSocket.read() with
            | (Text, data, true) ->
                let str = UTF8.toString data
                let response = sprintf "response to %s" str
                let byteResponse =
                    response
                    |> System.Text.Encoding.ASCII.GetBytes
                    |> ByteSegment
                do! webSocket.send Text byteResponse true
            | (Close, _, _) ->
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Close emptyResponse true
                loop <- false
            | _ -> ()
    }

let webSock onConnect onDisconnect (ws : WebSocket) (ctx: HttpContext) =
    let rec loop () = socket {
        match! ws.read() with
        | (Text, data, true) ->
            let byteResponse =
                UTF8.toString data
                |> sprintf "response to %s"
                |> System.Text.Encoding.ASCII.GetBytes
                |> ByteSegment
            do! ws.send Text byteResponse true
            return! loop ()
        | (Close, _, _) ->
            let emptyResponse = [||] |> ByteSegment
            do! ws.send Close emptyResponse true
            return ()
        | _ -> return! loop ()
    }

    socket {
        // Doesn't need to be injected function, could just be sending the
        // websocket and context to something that will hold it?
        onConnect ws ctx
        try do! loop ()
        // function injected that will make sure housekeeping is done following
        // loss of connection.
        finally onDisconnect ctx
    }

// Dummy functions, webSock testing...
let onSock _ws _ctx = printfn "Socket connected."
let offSock _ctx = printfn "Socket disconnected."

// TODO: Screwing around.
let wsAgent (ws: WebSocket) (ctx: HttpContext) =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            ()
        }
        loop ()
    )

// NOTE: This pattern will let me use path scan and do other things with the
// context while returning what the client needs for the socket.
let wsTest _str = fun ctx ->
    handShake ws ctx

let loginPlayer name =
    let msg chan = Login ({ Name = name; ID = System.Guid.NewGuid() } , chan)
    msg |> playerAgent.PostAndReply |> OK

let server =
    choose
        [ // pathScan "/websocket/%s" wsTest
          path "/websocket" >=> handShake (webSock onSock offSock)
          GET >=> choose
              [ pathScan "/lobby_list/%s" getLobbies ]
          POST >=> choose
              [ pathScan "/login/%s" loginPlayer
                pathScan "/create_lobby/%s/%s/%i/%i/%i/%s" createLobby
                pathScan "/join_lobby/%s/%s" joinLobby
                pathScan "/leave_lobby/%s/%s" leaveLobby ] ]
