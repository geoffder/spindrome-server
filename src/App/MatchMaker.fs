module GameServer.MatchMaker

open Newtonsoft.Json
open Suave
open Suave.Filters
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open System.Net

open AgentHelpers
open AgentOperators
open SocketAgentHelpers

let lobbyManager = Agent.Start(fun inbox ->
    let rec loop (lobbies: Map<string, LobbyRef>) = async {
        match! receive inbox with
        | Create (lobby, channel) ->
            match Map.containsKey lobby.Name lobbies with
            | true ->
                do channel <=< None
                return! loop lobbies
            | false ->
                let l = { Name = lobby.Name
                          LobbyAgent = LobbyAgent.spinUp inbox lobby }
                do channel <=< Some l
                return! lobbies |> Map.add lobby.Name l |> loop
        | DelistLobby name -> return! lobbies |> Map.remove name |> loop
        | RelistLobby l -> return! lobbies |> Map.add l.Name l |> loop
        | LookupLobby (name, channel) ->
            do channel <=< Map.tryFind name lobbies
            return! loop lobbies
        | RequestList channel ->
            do channel <=< lobbies
            return! loop lobbies
    }
    loop Map.empty
)

let newLobby (l: NewLobby) host =
    let h = { Info = host; Ready = false; Connected = false }
    { Name = l.Name
      Params = l.Params
      ChatNonce = 0
      Host = h
      Players = [h]
      WiringResults = Map.empty }

// TODO: Actually look for forbidden words within the prospective name, rather
// than this stand-in where I check whether the entire name in the forbidden set
// (want to catch forbidden words anywhere in the name.) Regex?
let forbidden = Set.ofList [ "the n-word (hard R)" ]

let createLobby (specs: NewLobby) host =
    match host.Agent <-> CurrentLobby with
    | Some l -> MustLeaveLobby l.Name
    | None when not (Set.contains specs.Name forbidden) ->
        newLobby specs host
        |> fun lobby chan -> Create (lobby, chan)
        |> lobbyManager.PostAndReply
        |> function
           | Some l ->
               do host.Agent <-- UpdateLobby (Joined l)
               LobbyCreated
           | None -> NameExists
    | None -> NameForbidden
    |> HostResult
    |> sendObj host.Agent

let joinLobby name player =
    let joinMsg chan = Join (player, chan)
    let tryJoin l = l.LobbyAgent <-?-> (joinMsg, 1000)
    match player.Agent <-> CurrentLobby with
    | Some l -> AlreadyInLobby l.Name
    | None ->
        fun chan -> LookupLobby (name, chan)
        |> lobbyManager.PostAndReply
        |> Option.bind tryJoin
        |> function
           | Some (Some l) ->
               do player.Agent <-- UpdateLobby (Joined l)
               LobbyJoined
           | Some None -> NoSpace
           | None -> NoSuchLobby
    |> JoinResult
    |> sendObj player.Agent

let messageLobby msg playerAgent =
    match playerAgent <-> CurrentLobby with
    | Some l -> l.LobbyAgent <-- msg
    | None -> ()

let leaveLobby p = messageLobby (Leave p) p.Agent

let playerReadied p = messageLobby (PlayerReady p.ID) p.Agent

let wiringReport p fs = messageLobby (WiringReport (p.ID, fs)) p.Agent

let kickFromLobby id host = messageLobby (Kick (id, host.ID)) host.Agent

let postChat post (p: PlayerInfo) = messageLobby (Chat (p.Name, post)) p.Agent

let getCompare = function
    | EQ -> (=) | NE -> (<>) | LT -> (<) | GT -> (>) | LE -> (<=) | GE -> (>=)

let createChooser filter : (LobbyInfo -> LobbyInfo option) =
    match filter with
    | GameMode m ->
        fun l -> if l.Params.Mode = m then Some l else None
    | Capacity (op, i) ->
        fun l -> if (getCompare op) l.Params.Capacity i then Some l else None
    | TimeLimit (op, i) ->
        fun l -> if (getCompare op) l.Params.Limits.Time i then Some l else None
    | ScoreLimit (op, i) ->
        fun l -> if (getCompare op) l.Params.Limits.Score i then Some l else None

let tryGetLobbyInfo l =
    l.LobbyAgent <-?-> (GetInfo, 1000)
    |> function | Some info -> info | None -> None

let getLobbies fs (wsAgent: Agent<SocketMessage>) =
    let sieve =
        List.map createChooser fs
        |> List.fold (fun f g -> f >> Option.bind g) Some
    RequestList
    |> lobbyManager.PostAndReply
    |> Map.toList
    |> List.choose (fun (_, l) -> tryGetLobbyInfo l |> Option.bind sieve)
    |> LobbyList
    |> sendObj wsAgent

let socketAgent (ws: WebSocket) = Agent.Start(fun inbox ->
    let wsSendObj =
        JsonConvert.SerializeObject
        >> strToBytes
        >> (fun data -> ws.send Text data true)
    let rec loop state = async {
        match! receive inbox with
        | CurrentLobby chan ->
            do chan <=< state.Location
            return! loop state
        | UpdateLobby change ->
            match change with
            | Joined lobby -> return! loop { state with Location = Some lobby }
            | Kicked ->
                let! _ = wsSendObj (LobbyUpdate KickedByHost)
                return! loop { state with Location = None }
            | Closed ->
                let! _ = wsSendObj (LobbyUpdate LobbyClosed)
                return! loop { state with Location = None }
            | Exit -> return! loop { state with Location = None }
        | Send (op, data, fin) ->
            let! _ = ws.send op data fin
            return! loop state
        | Shut ->
            let! _ = ws.send Close (ByteSegment [||]) true
            return ()
    }
    loop { Location = None }
)

let playerSocket name ws (ctx: HttpContext) =
    let info =
        { Name = name
          ID = System.Guid.NewGuid()
          IP = IPEndPoint (ctx.clientIpTrustProxy, 3047)
          Agent = socketAgent ws }

    let rec loop () = socket {
        match! ws.read () with
        | (Text, data, true) ->
            try
                data
                |> UTF8.toString
                |> JsonConvert.DeserializeObject<RequestSchema>
            with _ -> NonConformant
            |> function
               | GetLobbies filters -> getLobbies filters info.Agent
               | HostLobby specs -> createLobby specs info
               | JoinLobby name -> joinLobby name info
               | LeaveLobby -> leaveLobby info
               | ReadyUp -> playerReadied info
               | PeersPonged fails -> wiringReport info fails
               | KickPlayer id -> kickFromLobby id info
               | ChatMessage msg -> postChat msg info
               | NonConformant -> sendObj info.Agent BadRequest
            return! loop ()
        | (Close, _, _) -> return ()
        | _ -> return! loop ()
    }

    socket {
        do printfn "%s Connected!" name
        try do! loop ()
        finally do
            leaveLobby info
            info.Agent <-- Shut
            printfn "%s Disconnected!" name
    }

let connectToPlayer name = fun ctx ->
    handShake (playerSocket name) ctx

let server =
    choose [ pathScan "/websocket/%s" connectToPlayer ]
