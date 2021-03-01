namespace TTSDB

open Discord.WebSocket
open FSharp.Control

[<AutoOpen>]
module Extensions =
    type SocketGuild with
         member this.TryFindUser (user: SocketUser) =
            this.Users
            |> Seq.cast
            |> Seq.tryFind (fun (gu: SocketGuildUser) -> gu.Id = user.Id)

[<RequireQualifiedAccess>]
module Async =
    let lift x = async { return x }
    
    let map (f: 'T -> 'U) (a: Async<'T>) =
        async {
            let! res = a
    
            return f res
        }
    
    let bind (f: 'T -> Async<'U>) (a: Async<'T>) =
        async {
            let! res = a
    
            return! f res
        }
    
    let protect (f: Async<'T>) =
        async {
            return!
                try f |> map Ok
                with err -> async { return Error err }
        }
