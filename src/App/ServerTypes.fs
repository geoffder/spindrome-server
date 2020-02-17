namespace GameServer

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
