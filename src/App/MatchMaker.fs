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

let test () = printfn "test"

let playerAgent = MailboxProcessor.Start(fun inbox ->

    let rec loop (players: Map<System.Guid, Player>) = async {
        match! inbox.Receive() with
        | Login (player, channel) ->
            do channel.Reply (sprintf "%s logged in" (player.ID.ToString()))
            return! loop (Map.add player.ID player players)
        | Logout id ->
            return! loop (Map.remove id players)
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
        | Join (lobName, player, channel) ->
            match tryJoin lobbies.[lobName] player with
            | Some lobby ->
                do channel.Reply "Succesfully joined lobby!"
                return! loop (Map.add lobName lobby lobbies)
            | None ->
                do channel.Reply "All full up!"
                return! loop lobbies
        | RequestList (channel) ->
            do channel.Reply lobbies
            return! loop lobbies
    }

    loop (Map<string, Lobby> [])
)

let getPlayer id =
    let msg chan = GetPlayer (id, chan)
    playerAgent.PostAndReply msg

let createLobby (name, mode, time, score, cap, (idStr: string)) =
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
    match getPlayer (System.Guid.Parse idStr) with
    | Some p -> p |> sendNewLobby |> OK
    | None -> OK "No such player ID is logged in."

let joinLobby (name, (idStr: string)) =
    let msg player chan = Join (name, player, chan)
    match getPlayer (System.Guid.Parse idStr) with
    | Some p -> p |> msg |> lobbyAgent.PostAndReply |> OK
    | None -> OK "No such player ID is logged in."

let getLobbies _filters =
    // TODO: add filters on to path (only send relevant lobbies)
    RequestList
    |> lobbyAgent.PostAndReply
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
                pathScan "/join_lobby/%s/%s" joinLobby ] ]
