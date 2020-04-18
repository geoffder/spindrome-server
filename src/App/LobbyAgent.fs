module GameServer.LobbyAgent

open AgentHelpers
open AgentOperators
open SocketAgentHelpers

let getAgents players = List.map (fun p -> p.Info.Agent) players

let getPlayerInfosByIDs ps ids =
    List.map (fun p -> p.Info) ps
    |> List.filter (fun info -> List.contains info.ID ids)

let getLobbyInfo (l: Lobby) =
    { Name = l.Name
      Params = l.Params
      HostName = l.Host.Info.Name
      Population = l.Players.Length }

let getPeerInfo (p: PlayerInfo) =
    { Name = p.Name; GUID = p.ID; IP = p.IP; Port = p.Port }

let broadcastPingPongTime players =
    let info = List.map (fun p -> getPeerInfo p.Info) players
    let sendUpdate peers player =
        peers
        |> List.filter (fun p -> p.GUID <> player.Info.ID)
        |> PingPongTime
        |> LobbyUpdate
        |> sendObj player.Info.Agent
    List.iter (sendUpdate info) players

let refreshLobby l =
    { l with
        Players = List.map (fun p -> { p with Ready = false }) l.Players
        WiringResults = Map.empty }

let dropPlayer player lobby =
    lobby.Players
    |> List.filter (fun p -> p.Info.ID <> player.ID)
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

let prunePeers lobby =
    let nPeers = (List.length lobby.Players) - 1
    let tallyer (strikes, toKick) id =
        if Set.contains id strikes
        then (strikes, Set.add id toKick)
        else (Set.add id strikes, toKick)
    let pruner (strikes, toKick) id fails =
        match fails with
        | [] -> (strikes, toKick)
        | fs when (List.length fs) = nPeers -> (strikes, Set.add id toKick)
        | fs -> List.fold tallyer (strikes, toKick) fs
    Map.fold pruner (Set.empty, Set.empty) lobby.WiringResults
    |> fun (ss, ks) ->
        Set.difference ss ks
        |> Set.toList
        |> List.head  // Arbitrarily add one player with a strike to kick list.
        |> fun h -> h :: Set.toList ks
    |> getPlayerInfosByIDs lobby.Players
    |> function
       | ps when List.contains lobby.Host.Info ps ->
           exitLobby lobby.Host.Info lobby
       | ps ->
           List.fold (fun l p -> Option.bind (kickPlayer p) l) (Some lobby) ps

let setConnected l connector =
    let connect p =
        if p.Info.ID = connector.ID then { p with Connected = true } else p
    { l with Players = List.map connect l.Players }

let inline join l inbox (player: PlayerInfo) channel =
    if l.Players.Length < l.Params.Capacity then
        do
            channel <=< Some { Name = l.Name; LobbyAgent = inbox }
            Arrival (getPeerInfo player)
            |> LobbyUpdate
            |> broadcastObj (getAgents l.Players)
        { Info = player; Ready = false; Connected = false }
        |> fun p -> { l with Players = p :: l.Players }
    else
        do channel <=< None
        l

let inline kick l id kickerID =
    if l.Host.Info.ID = kickerID then
       l.Players
       |> List.tryFind (fun i -> i.Info.ID = id)
       |> Option.bind (fun p -> kickPlayer p.Info l)
       |> function
          | None -> l
          | Some updated -> updated
    else l

let inline gateKeep l man id =
    do
        if l.Host.Info.ID = id then man <-- DelistLobby l.Name
        else ()
    l

let inline letThemIn l man inbox id =
    do
        if l.Host.Info.ID = id
        then man <-- RelistLobby { Name = l.Name; LobbyAgent = inbox }
        else ()
    l

let inline chat l name msg =
    do
        { Author = name; Contents = msg; Nonce = l.ChatNonce }
        |> Chatter
        |> broadcastObj (getAgents l.Players)
    { l with ChatNonce = l.ChatNonce + 1 }

let inline setReady l id up =
    let ready p =
        if p.Info.ID = id then { p with Ready = up } else p
    do Readied (id, up) |> LobbyUpdate |> broadcastObj (getAgents l.Players)
    { l with Players = List.map ready l.Players }

let inline initiateWiring l man host =
    do
        if l.Host.Info.ID = host.ID
           && List.length l.Players > 1
           && List.forall (fun p -> p.Ready) l.Players
           && Map.isEmpty l.WiringResults // NOTE: Just wipe results instead?
        then
            man <-- DelistLobby l.Name
            broadcastPingPongTime l.Players
        else ()
    l

// TODO: Currently no custom messaging about failure condition.
// Should send messages to clients. Maybe add reason sum type to kick functions?
let inline peerWiring l box man id fails =
    match Map.add id fails l.WiringResults with
    | fs when fs.Count < l.Players.Length -> Some { l with WiringResults = fs }
    | fs ->
        if Map.forall (fun _ l -> List.isEmpty l) fs then
            do
                GameTime l.Params
                |> LobbyUpdate
                |> broadcastObj (getAgents l.Players)
            Some l
        else
            match prunePeers l with
            | None -> None
            | someUpdated ->
                do man <-- RelistLobby { Name = l.Name; LobbyAgent = box }
                someUpdated
        |> function
           | None -> None
           | Some lobby -> Some (refreshLobby lobby)

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
        | Kick (id, kickerID) -> return! loop <| kick l id kickerID
        | GateKeep id -> return! loop <| gateKeep l man id
        | LetThemIn id -> return! loop <| letThemIn l man inbox id
        | Chat (name, msg) -> return! loop <| chat l name msg
        | PlayerReady (id, up) -> return! loop <| setReady l id up
        | InitiateWiring host -> return! loop <| initiateWiring l man host
        | WiringReport (id, fs) ->
            match peerWiring l inbox man id fs with
            | Some lob -> return! loop lob
            | None -> return ()
        | GetInfo channel -> return! loop <| getInfo l channel
    }
    loop initial

let spinUp manager initial = start <| agent manager initial
