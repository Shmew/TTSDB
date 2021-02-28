namespace TTSDB

open Discord.WebSocket
open FSharp.Control

[<RequireQualifiedAccess>]
module GuildManager =
    type Msg =
        | GetGuild of replyChannel:AsyncReplyChannel<SocketGuild>
        
type GuildManager (client: DiscordSocketClient, guildId: uint64) =
    let mailbox = 
        MailboxProcessor<GuildManager.Msg>.Start <| fun inbox ->
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

    member _.GetGuild () =
        mailbox.PostAndAsyncReply GuildManager.Msg.GetGuild
        |> Async.StartAsTask
