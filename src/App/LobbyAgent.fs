module GameServer.LobbyAgent

// This exists as an alternative, and to help me decide if I think it actually
// is a more readable/maintainable solution (compared to the lobbyAgent in
// MatchMaker which I find a bit unwieldly).

// open GameServer

let getAgents players = List.map (fun p -> p.Info.Agent) players

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
           do
              Departure player.Name
              |> LobbyUpdate
              |> broadcastObj (getAgents ps)
           Some { lobby with Players = ps }

let exitLobby player lobby =
    if player <> lobby.Host.Info then
        do player.Agent <-- UpdateLobby Exit
        dropPlayer player lobby
    else
        do
            lobby.Players
            |> getAgents
            |> List.iter (fun a -> a <-- UpdateLobby Closed)
        None

let kickPlayer player lobby =
    do player.Agent <-- UpdateLobby Kicked
    dropPlayer player lobby

let setConnected l connector =
    let connect p =
        if p.Info.ID = connector.ID then { p with Connected = true } else p
    { l with Players = List.map connect l.Players }

let connectAgent parent (l: Lobby) = Agent.Start(fun inbox ->
    let rec loop fails = async {
        let! pName, failures = receive inbox
        match Map.add pName failures fails with
        | fs when fs.Count < l.Players.Length -> return! loop fs
        | fs -> return ()  // determine if ready to roll, or kicking required.
    }
    loop (Map<string, string list> [])
)

let inline join l inbox (p: PlayerInfo) channel =
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

let inline kick l id kicker =
    if l.Host.Info.ID = kicker.ID then
       l.Players
       |> List.tryFind (fun i -> i.Info.ID = id)
       |> Option.bind (fun p -> kickPlayer p.Info l)
       |> function
          | None -> l
          | Some updated -> updated
    else l

let inline gateKeep l man host =
    do
        if l.Host.Info.ID = host.ID then man <-- DelistLobby l.Name
        else ()
    l

let inline letThemIn l man inbox host =
    do
        if l.Host.Info.ID = host.ID
        then man <-- RelistLobby { Name = l.Name; LobbyAgent = inbox }
        else ()
    l

let inline chat l msg (player: PlayerInfo) =
    do
        { Author = player.Name; Contents = msg; Nonce = l.ChatNonce }
        |> Chatter
        |> broadcastObj (getAgents l.Players)
    { l with ChatNonce = l.ChatNonce + 1 }

let inline setReady l readyer =
    let ready p =
        if p.Info.ID = readyer.ID then { p with Ready = true } else p
    { l with Players = List.map ready l.Players }

let inline peerWiring l man id fails =
    match Map.add id fails l.WiringResults with
    | fs when fs.Count < l.Players.Length -> { l with WiringResults = fs }
    | fs ->
        do
            // logic goes here.
            printfn "Go? Or rollback and kick bad apple."
        { l with WiringResults = Map.empty }



let inline getInfo l channel =
    do
        if l.Players.Length < l.Params.Capacity
        then channel <=< Some (getLobbyInfo l)
        else channel <=< None
    l

let agent (man: Agent<ManagerMessage>) initial inbox =
    let rec loop l = async {
        match! receive inbox with
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
        | PlayerReady p -> return! loop <| setReady l p
        | WiringReport (id, fs) -> return! loop <| peerWiring l man id fs
        | GetInfo channel -> return! loop <| getInfo l channel
    }
    loop initial

let spinUp manager initial = start <| agent manager initial
