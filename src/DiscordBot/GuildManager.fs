namespace TTSDB

open Discord.WebSocket
open FSharp.Control
open System

[<RequireQualifiedAccess>]
module GuildManager =
    type Msg =
        | GetGuild of replyChannel:AsyncReplyChannel<SocketGuild>
        
type GuildManager (client: DiscordSocketClient, guildId: uint64) as self =
    let cts = new Threading.CancellationTokenSource()

    let mailbox = 
        MailboxProcessor<GuildManager.Msg>.Start (
            (fun inbox ->
                let rec loop (guild: SocketGuild option) =
                    async {
                        let! msg = inbox.Receive()

                        match msg with
                        | GuildManager.Msg.GetGuild replyChannel ->
                            match guild with
                            | Some g ->
                                replyChannel.Reply(g)

                                return! loop guild
                            | None ->
                                let guild =
                                    client.Guilds 
                                    |> Seq.cast 
                                    |> Seq.filter (fun (g: SocketGuild) -> g.Id = guildId) 
                                    |> Seq.exactlyOne
                            
                                replyChannel.Reply(guild)

                                return! loop (Some guild)
                    }

                loop None
            ), cts.Token)

    let dispose () =
        cts.Cancel()
        cts.Dispose()

    member _.GetGuild () =
        mailbox.PostAndAsyncReply GuildManager.Msg.GetGuild
        |> Async.StartAsTask

    member this.GetGuildAsync () =
        this.GetGuild() |> Async.AwaitTask

    member val GuildId : uint64 = guildId
    
    interface IDisposable with
        member _.Dispose () =
            dispose()

            System.GC.SuppressFinalize(self)

    override _.Finalize () = dispose()
