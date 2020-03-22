namespace GameServer

open Newtonsoft.Json
open Suave.Sockets
open Suave.WebSocket
open System.Net

type Name = string
type Agent<'T> = MailboxProcessor<'T>

type GameMode =
    | LastMan
    | BoostBall

type Limits = { Time: int; Score: int }

type LobbyParams = { Mode: GameMode; Limits: Limits; Capacity: int }

type NewLobby = { Name: string; Params: LobbyParams }

// NOTE: Speculative type for sending lobby info to clients.
type LobbyInfo =
    { Name: string
      Params: LobbyParams
      HostName: string
      Population: int }

type ComparisonOp = EQ | NE | LT | GT | LE | GE

type Comparison<'T> = ComparisonOp * 'T

type LobbyFilter =
    | GameMode of GameMode
    | Capacity of Comparison<int>
    | TimeLimit of Comparison<int>
    | ScoreLimit of Comparison<int>

type LobbyMessage =
    | Join of PlayerInfo * AsyncReplyChannel<LobbyRef option>
    | Leave of PlayerInfo
    | Kick of System.Guid * PlayerInfo
    | GateKeep of PlayerInfo
    | LetThemIn of PlayerInfo
    | Chat of string * PlayerInfo
    | PlayerReady of PlayerInfo
    | PlayerConnected of PlayerInfo
    | GetInfo of AsyncReplyChannel<LobbyInfo option>

and LobbyRef = { Name: string; LobbyAgent: Agent<LobbyMessage> }

and LobbyAction =
    | Joined of LobbyRef
    | Kicked
    | Closed
    | Exit

and  PlayerInfo =
    { Name: string
      ID: System.Guid
      IP: IPEndPoint
      Agent: Agent<SocketMessage> }

and SocketMessage =
    | CurrentLobby of AsyncReplyChannel<LobbyRef option>
    | UpdateLobby of LobbyAction
    | Send of Opcode * ByteSegment * bool
    | Shut

type PlayerState = { Location: LobbyRef option }

type Player = { Info: PlayerInfo; Ready: bool; Connected: bool }

type Lobby =
    { Name: string
      Params: LobbyParams
      ChatNonce: int
      Host: Player
      Players: Player list }

type ManagerMessage =
    | Create of Lobby * AsyncReplyChannel<LobbyRef option>
    | DelistLobby of Name
    | RelistLobby of LobbyRef
    | LookupLobby of Name * AsyncReplyChannel<LobbyRef option>
    | RequestList of AsyncReplyChannel<Map<Name, LobbyRef>>

type PeerInfo = { Name: string; Num: int; IP: IPEndPoint }

type JoinResult =
    | LobbyJoined
    | NoSpace
    | NoSuchLobby
    | AlreadyInLobby of string

type HostResult =
    | LobbyCreated
    | NameExists
    | NameForbidden
    | MustLeaveLobby of string

type ChatPost = { Author: string; Contents: string; Nonce: int }

type LobbyUpdate =
    | Arrival of Name * System.Guid
    | Departure of Name
    | ChangedParams of LobbyParams
    | PeerInfo of PeerInfo list
    | KickedByHost
    | LobbyClosed

type ResponseSchema =
    | JoinResult of JoinResult
    | HostResult of HostResult
    | Chatter of ChatPost
    | LobbyUpdate of LobbyUpdate
    | LobbyList of LobbyInfo list
    | BadRequest

type RequestSchema =
    | GetLobbies of LobbyFilter list
    | HostLobby of NewLobby
    | JoinLobby of string
    | LeaveLobby
    | ReadyUp
    | PeersPonged
    | KickPlayer of System.Guid
    | ChatMessage of string
    | NonConformant

// Toying around with the idea...
[<AutoOpen>]
module AgentOperators =
    let inline (<--) (a: Agent<'T>) msg = a.Post msg
    let inline (<->) (a: Agent<'T>) msg = a.PostAndReply msg
    let inline (<-?->) (a: Agent<'T>) (msg, ms) =
        a.TryPostAndReply (msg, ?timeout = Some ms)
    let inline (<=<) (chan: AsyncReplyChannel<'T>) msg = chan.Reply msg

[<AutoOpen>]
module AgentHelpers =
    let inline start (agent: Agent<'T> -> Async<unit>) = Agent<'T>.Start agent
    let inline receive (inbox: Agent<'T>) = inbox.Receive ()

[<AutoOpen>]
module SocketAgentHelpers =
    let strToBytes (str: string) =
        str |> System.Text.Encoding.ASCII.GetBytes |> ByteSegment

    let sendString (agent: Agent<SocketMessage>) =
        let msg data = Send (Text, data, true)
        strToBytes >> msg >> agent.Post

    let sendObj (agent: Agent<SocketMessage>) =
        JsonConvert.SerializeObject >> sendString agent

    let broadcast agents str =
        let send (a: Agent<SocketMessage>) =
            a <-- Send (Text, strToBytes str, true)
        List.iter send agents

    let broadcastObj agents =
        JsonConvert.SerializeObject >> broadcast agents
