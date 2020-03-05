module AgentTest

type Message = Msg of string * AsyncReplyChannel<string>

let newAgent () = MailboxProcessor.Start(fun inbox ->
    let rec loop () = async {
        match! inbox.Receive () with
        | Msg (str, chan) ->
            do chan.Reply <| sprintf "echo: %s" str
            ()
    }
    loop ()
)

let msg chan = Msg ("hello", chan)
