namespace GameServer

open Suave.Sockets
open Suave.WebSocket

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
      Agent: MailboxProcessor<SocketMessage> }

type PlayerState = { LobbyName: string option }

type LobbyParams = { Mode: GameMode; Limits: Limits; Capacity: int }

type NewLobby = { Name: string; Params: LobbyParams }

type Lobby =
    { Name: string
      ID: System.Guid
      Params: LobbyParams
      ChatNonce: int
      Host: PlayerInfo
      Players: PlayerInfo list }

type LobbyMessage =
    | Create of Lobby * AsyncReplyChannel<string option>
    | Join of Name * PlayerInfo * AsyncReplyChannel<bool>
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

type JoinResult =
    | LobbyJoined
    | NoSpace
    | NoSuchLobby

type HostResult =
    | LobbyCreated
    | NameExists
    | NameForbidden

// TODO: Figure out what I need for this, don't really need a name right?
// Need enough to make UDP connections and associate packets with the correct
// players.
type PeerInfo = { Name: string; Num: int; ID: System.Guid; IP: string }

type LobbyUpdate =
    | Arrival of System.Guid
    | Departure of System.Guid
    | ChangedParams of LobbyParams
    | PeerInfo of PeerInfo list

type ResponseSchema =
    | JoinResult of JoinResult
    | HostResult of HostResult
    | Chatter of ChatPost
    | LobbyUpdate of LobbyUpdate
