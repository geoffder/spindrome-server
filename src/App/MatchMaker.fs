module GameServer.MatchMaker

open Chiron
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

let newLobby name mode cap host =
    { Name = name
      ID = System.Guid.NewGuid()
      Mode = mode
      Capacity = cap
      Host = host
      Players = [host] }

let newPlayer name =
    { Name = name; ID = System.Guid.NewGuid() }

let tryJoin lobby player =
    if lobby.Players.Length < lobby.Capacity
    then Some { lobby with Players = player :: lobby.Players }
    else None

let test () = printfn "test"

let playerAgent = MailboxProcessor.Start(fun inbox ->

    let rec loop (players: Map<System.Guid, Player>) = async {
        match! inbox.Receive() with
        | Login (name, id, channel) ->
            do channel.Reply (sprintf "%s logged in" (id.ToString()))
            let p = { Name = name; ID = id }
            return! loop (Map.add id p players)
        | Logout id ->
            return! loop (Map.remove id players)
        | GetPlayer (id, channel) ->
            do channel.Reply players.[id]
            return! loop players
    }

    loop (Map<System.Guid, Player> [])
)

let lobbyAgent = MailboxProcessor.Start(fun inbox ->

    let rec loop (lobbies: Map<string, Lobby>) = async {
        match! inbox.Receive() with
        | Create (name, mode, cap, player, channel) ->
            match Map.containsKey name lobbies with
            | true ->
                do channel.Reply "Lobby with that name already exists."
                return! loop lobbies
            | false ->
                do channel.Reply "Lobby created!"
                return!
                    lobbies |> Map.add name (newLobby name mode cap player)
                    |> loop
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

let createLobby (name, mode, score, cap, (idStr: string)) =
    let game =
        match mode with
        | "last_man" -> LastMan score
        | "boost_ball" -> BoostBall score
        | _ -> LastMan score
    let id = System.Guid.Parse idStr
    let msg chan = Create (name, game, cap, (getPlayer id), chan)
    msg |> lobbyAgent.PostAndReply |> OK

let joinLobby (name, (idStr: string)) =
    let msg chan = Join (name, (getPlayer (System.Guid.Parse idStr)), chan)
    msg |> lobbyAgent.PostAndReply |> OK

let getLobbies _filters =
    // TODO: add filters on to path (only send relevant lobbies)
    RequestList
    |> lobbyAgent.PostAndReply
    |> Json.serialize
    |> Json.format
    |> OK

let loginPlayer name =
    let msg chan = Login (name, System.Guid.NewGuid(), chan)
    msg |> playerAgent.PostAndReply |> OK

let server =
    choose
        [ GET >=> choose
              [ pathScan "/lobby_list/%s" getLobbies ]
          POST >=> choose
              [ pathScan "/login/%s" loginPlayer
                pathScan "/create_lobby/%s/%s/%i/%i/%s" createLobby
                pathScan "/join_lobby/%s/%s" joinLobby ] ]
