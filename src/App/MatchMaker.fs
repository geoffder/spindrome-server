module GameServer.MatchMaker

open Newtonsoft.Json
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

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

let test () = printfn "test"

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

let getLobbyFilter str : (Name -> Lobby -> bool) list =
    match str with
    | "last_man" -> [ fun _ l -> l.Mode = LastMan ]
    | "boost_ball" -> [ fun _ l -> l.Mode = BoostBall ]
    | _ -> []

let getLobbies (filterStr: string) =
    let filters =
        filterStr.Split "/"
        |> List.ofArray
        |> List.collect getLobbyFilter
    RequestList
    |> lobbyAgent.PostAndReply
    |> Map.filter (applyFiltersToMap filters)
    |> JsonConvert.SerializeObject
    |> OK

let loginPlayer name =
    let msg chan = Login ({ Name = name; ID = System.Guid.NewGuid() }, chan)
    msg |> playerAgent.PostAndReply |> OK

let server =
    choose
        [ GET >=> choose
              [ pathScan "/lobby_list/%s" getLobbies ]
          POST >=> choose
              [ pathScan "/login/%s" loginPlayer
                pathScan "/create_lobby/%s/%s/%i/%i/%i/%s" createLobby
                pathScan "/join_lobby/%s/%s" joinLobby
                pathScan "/leave_lobby/%s/%s" leaveLobby ] ]
