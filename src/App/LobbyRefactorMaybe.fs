module LobbyRefactor

// This exists as an alternative, and to help me decide if I think it actually
// is a more readable/maintainable solution (compared to the lobbyAgent in
// MatchMaker which I find a bit unwieldly).

open GameServer

let getLobbyInfo (l: Lobby) =
    { Name = l.Name
      Params = l.Params
      HostName = l.Host.Info.Name
      Population = l.Players.Length }

let dropPlayer player lobby =
    lobby.Players
    |> List.filter (fun p -> p.Info <> player)
    |> function
       | [] -> None
       | ps ->
           do Departure player.Name
           |> LobbyUpdate |> broadcastObj (getAgents ps)
           Some { lobby with Players = ps }

let exitLobby player lobby =
    if player <> lobby.Host.Info then
        do player.Agent.Post <| UpdateLobby Exit
        dropPlayer player lobby
    else
        do lobby.Players
        |> getAgents
        |> List.iter (fun a -> a.Post <| UpdateLobby Closed)
        None

let kickPlayer player lobby =
    do player.Agent.Post <| UpdateLobby Kicked
    dropPlayer player lobby

let setReady readyer players =
    let ready p =
        if p.Info.ID = readyer.ID then { p with Ready = true } else p
    List.map ready players

let setConnected connector players =
    let connect p =
        if p.Info.ID = connector.ID then { p with Connected = true } else p
    List.map connect players

let join inbox l (p: PlayerInfo) channel =
    if l.Players.Length < l.Params.Capacity then
        do
            channel <=< Some { Name = l.Name; LobbyAgent = inbox }
            LobbyUpdate (Arrival (p.Name, p.ID))
            |> broadcastObj (getAgents l.Players)
        { Info = p; Ready = false; Connected = false }
        |> fun p -> { l with Players = p :: l.Players }
    else
        do channel <=< None
        l

let kick l id kicker =
    if l.Host.Info.ID = kicker.ID then
       l.Players
       |> List.tryFind (fun i -> i.Info.ID = id)
       |> Option.bind (fun p -> kickPlayer p.Info l)
       |> function
          | None -> l
          | Some updated -> updated
    else l

let gateKeep l man host =
    do
        if l.Host.Info.ID = host.ID then man <-- DelistLobby l.Name
        else ()
    l

let letThemIn l man inbox host =
    do
        if l.Host.Info.ID = host.ID
        then man <-- RelistLobby { Name = l.Name; LobbyAgent = inbox }
        else ()
    l

let chat l msg (player: PlayerInfo) =
    do
        { Author = player.Name; Contents = msg; Nonce = l.ChatNonce }
        |> Chatter
        |> broadcastObj (getAgents l.Players)
    { l with ChatNonce = l.ChatNonce + 1 }

let getInfo l channel =
    do
        if l.Players.Length < l.Params.Capacity
        then channel <=< Some (getLobbyInfo l)
        else channel <=< None
    l

let lobbyAgent (man: Agent<ManagerMessage>) initial = Agent.Start(fun inbox ->
    let rec loop l = async {
        match! inbox.Receive() with
        | Join (player, channel) -> return! loop <| join l inbox player channel
        | Leave player ->
            match exitLobby player l with
            | Some lob -> return! loop lob
            | None ->
                do man <-- DelistLobby l.Name
                return ()  // Say Goodnight.
        | Kick (id, host) -> return! loop <| kick l id host
        | GateKeep host -> return! loop <| gateKeep l man host
        | LetThemIn host -> return! loop <| letThemIn l man inbox host
        | Chat (msg, player) -> return! loop <| chat l msg player
        | PlayerReady p -> return! loop { l with Players = setReady p l.Players }
        | PlayerConnected p -> return! loop { l with Players = setConnected p l.Players }
        | GetInfo channel -> return! loop <| getInfo l channel
    }
    loop initial
)
