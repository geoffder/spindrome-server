namespace GameServer

open Suave.Sockets
open Suave.WebSocket
open System.Net

type Name = string
type Cap = int

type GameMode =
    | LastMan
    | BoostBall
    static member FromString str =
        match str with
        | "last_man" -> LastMan
        | "boost_ball" -> BoostBall
        | _ -> LastMan

type Limits = { Time: int; Score: int }

type LobbyAction =
    | Joined of string
    | Kicked
    | Closed
    | Exit

type SocketMessage =
    | GetLobby of AsyncReplyChannel<string option>
    | UpdateLobby of LobbyAction
    | Send of Opcode * ByteSegment * bool
    | Shut

type PlayerInfo =
    { Name: string
      ID: System.Guid
      IP: IPEndPoint
      Agent: MailboxProcessor<SocketMessage> }

type PlayerState = { LobbyName: string option }

type LobbyParams = { Mode: GameMode; Limits: Limits; Capacity: int }

type NewLobby = { Name: string; Params: LobbyParams }

type Lobby =
    { Name: string
      Params: LobbyParams
      ChatNonce: int
      Host: PlayerInfo
      Players: PlayerInfo list }

// NOTE: Speculative type for sending lobby info to clients.
type LobbyInfo =
    { Name: string
      Params: LobbyParams
      HostName: string
      Population: int }

type JoinResult =
    | LobbyJoined
    | JoinFailed
    | AlreadyInLobby

type LobbyMessage =
    | Create of Lobby * AsyncReplyChannel<string option>
    | Join of Name * PlayerInfo * AsyncReplyChannel<JoinResult>
    | Leave of Name * PlayerInfo
    | Kick of Name * System.Guid * PlayerInfo
    | Chat of Name * string * PlayerInfo
    | RequestList of AsyncReplyChannel<Map<Name, Lobby>>

type ChatPost = { Author: string; Contents: string; Nonce: int }

type RequestSchema =
    | GetLobbies of string list
    | HostLobby of NewLobby
    | JoinLobby of string
    | LeaveLobby
    | KickPlayer of System.Guid
    | ChatMessage of string

type HostResult =
    | LobbyCreated
    | NameExists
    | NameForbidden

type PeerInfo = { Name: string; Num: int; IP: IPEndPoint }

type LobbyUpdate =
    | Arrival of Name
    | Departure of Name
    | ChangedParams of LobbyParams
    | PeerInfo of PeerInfo list

type ResponseSchema =
    | JoinResult of JoinResult
    | HostResult of HostResult
    | Chatter of ChatPost
    | LobbyUpdate of LobbyUpdate
