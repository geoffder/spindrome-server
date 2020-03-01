namespace GameServer

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
    | Chat of string * PlayerInfo
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

type Lobby =
    { Name: string
      Params: LobbyParams
      ChatNonce: int
      Host: PlayerInfo
      Players: PlayerInfo list }

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

type RequestSchema =
    | GetLobbies of LobbyFilter list
    | HostLobby of NewLobby
    | JoinLobby of string
    | LeaveLobby
    | KickPlayer of System.Guid
    | ChatMessage of string
