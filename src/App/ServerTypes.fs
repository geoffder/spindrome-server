namespace GameServer

// TODO: Switch to manually serializing or using Newtonsoft Json.NET
// Apparently performance is much better. Also consider something other
// than Json.
open Chiron

type Name = string
type Cap = int

type GameMode =
    | LastMan of int
    | BoostBall of int
    static member ToJson (m: GameMode) =
        json {
            match m with
            | LastMan i -> do! Json.write "last_man" i
            | BoostBall i -> do! Json.write "boost_ball" i
        }
       
type Player =
    { Name: string; ID: System.Guid }
    static member ToJson (p: Player) =
        json {
            do! Json.write "Name" p.Name
            do! Json.write "ID" p.ID
        }

type Lobby =
    { Name: string
      ID: System.Guid
      Mode: GameMode
      Capacity: int
      Host: Player
      Players: Player list }
    static member ToJson (l: Lobby) =
        json {
            do! Json.write "Name" l.Name
            do! Json.write "ID" l.ID
            do! Json.write "Mode" l.Mode
            do! Json.write "Capacity" l.Capacity
            do! Json.write "Host" l.Host
            do! Json.write "Players" l.Players
        }

type PlayerMessage =
    | Login of Name * System.Guid * AsyncReplyChannel<string>
    | Logout of System.Guid
    | GetPlayer of System.Guid * AsyncReplyChannel<Player>

type LobbyMessage =
    | Create of Name * GameMode * Cap * Player * AsyncReplyChannel<string>
    | Join of Name * Player * AsyncReplyChannel<string>
    | RequestList of AsyncReplyChannel<Map<Name, Lobby>>
