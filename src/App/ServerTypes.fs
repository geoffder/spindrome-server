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

type SocketMessage =
    | GetLobby of AsyncReplyChannel<string option>
    | UpdateLobby of string option
    | Send of Opcode * ByteSegment * bool
    | Shut

type PlayerInfo =
    { Name: string
      ID: System.Guid
      Agent: MailboxProcessor<SocketMessage> }

type PlayerState = { LobbyName: string option }

type LobbyParams =
    { Name: string
      Mode: GameMode
      Limits: Limits
      Capacity: int }

type Lobby =
    { Name: string
      ID: System.Guid
      Mode: GameMode
      Limits: Limits
      Capacity: int
      Host: PlayerInfo
      Players: PlayerInfo list }

type LobbyMessage =
    | Create of Lobby * AsyncReplyChannel<string option>
    | Join of Name * PlayerInfo * AsyncReplyChannel<bool>
    | Leave of Name * PlayerInfo
    | Kick of Name * System.Guid * PlayerInfo
    | Chat of Name * string * PlayerInfo
    | RequestList of AsyncReplyChannel<Map<Name, Lobby>>
