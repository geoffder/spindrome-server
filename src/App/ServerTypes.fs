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

// type Player = { Name: string; ID: System.Guid; Socket: WebSocket }
type Player = { Name: string; ID: System.Guid }

type Lobby =
    { Name: string
      ID: System.Guid
      Mode: GameMode
      Limits: Limits
      Capacity: int
      Host: Player
      Players: Player list }

type PlayerMessage =
    | Login of Player * AsyncReplyChannel<string>
    | Logout of System.Guid
    | GetPlayer of System.Guid * AsyncReplyChannel<Player option>

type LobbyMessage =
    | Create of Lobby * AsyncReplyChannel<string>
    | Join of Name * Player * AsyncReplyChannel<string>
    | Leave of Name * Player
    | RequestList of AsyncReplyChannel<Map<Name, Lobby>>

type SocketMessage =
    | Send of Opcode * ByteSegment * bool
    | Shut

// TODO: Like so, and use a function to match byte arrays in to a SocketData?
// Or just do an active pattern, that way there is one less place to change?
// There could be a lot of cases though so it might be a bit ugly for an active.
type SocketData =
    | CreateLobby of string
    | JoinLobby of Name
    | LeaveLobby
    | KickFromLobby of Name
    | Chat of string
    | NoByteFlagMatch
