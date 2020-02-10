#r "/home/geoff/.nuget/packages/suave/2.5.6/lib/netstandard2.0/Suave.dll"
#load "./MatchMaker.fs"

open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful

open GameServer
open GameServer.MatchMaker

let dummy =
    choose
        [ GET >=> choose
              [ path "/lobby_list" >=> getLobbies ]
          POST >=> choose
              [ pathScan "/login/%s" loginPlayer
                pathScan "/create_lobby/%s/%s/%i/%i/%s" createLobby
                pathScan "/join_lobby/%s/%s" joinLobby ] ]

// So the agents work if I create them in here, but trying to send them
// messages when they have only been defined in the .fs file causes indefinite
// hanging. I need to learn how to actually run my server as a compiled thing,
// so running the agents and launching the server in a main function.

startWebServer defaultConfig dummy
