namespace TTSDB

open Discord
open Discord.WebSocket
open FSharp.Control
open FSharp.Control.Tasks.V2.ContextInsensitive
open System
open System.Threading.Tasks

module Main =
    let log (msg: LogMessage) =
        task {
            return Console.WriteLine(msg.ToString())
        } :> Task

    let startBot () =
        task {
            let settings = Settings.get()

            use client = new DiscordSocketClient()
            client.add_Log(Func<LogMessage,Task>(log))
            
            let apiKey = getEnvFromAllOrNone "TTSDB_Discord" |> Option.defaultValue ""

            // fsharplint:disable-next-line
            do! client.LoginAsync(TokenType.Bot, apiKey, validateToken = true)
            do! client.StartAsync()
            
            let guildManager = GuildManager(client, settings.GuildId)
            use voiceOperator = new VoiceOperator(guildManager, settings)

            client.add_Ready(Func<Task>(fun () -> client.SetGameAsync("with kittens", ``type`` = ActivityType.Playing)))

            client.add_MessageReceived(Func<SocketMessage,Task>(fun msg ->
                match msg with
                | :? SocketUserMessage as msg when 
                    msg.Channel.Id = settings.TextChannelId
                    && isNull msg.Content |> not 
                    && not msg.Author.IsBot 
                    && not msg.Author.IsWebhook ->

                    task {
                        let! guild = guildManager.GetGuild()

                        return!
                            match guild.TryFindUser(msg.Author) with
                            | Some user when isNull user.VoiceChannel |> not ->
                                voiceOperator.WriteTTS user.VoiceChannel msg false
                            | _ -> Task.FromResult() :> Task
                    } :> Task
                | :? SocketUserMessage as msg when
                    List.contains msg.Author.Id settings.Owners
                    && isNull msg.Content |> not 
                    && not msg.Author.IsBot 
                    && not msg.Author.IsWebhook ->
                    
                    match msg.Channel with
                    | :? SocketDMChannel ->
                        task {
                            let! guild = guildManager.GetGuild()

                            return!
                                match guild.TryFindUser(msg.Author) with
                                | Some user when isNull user.VoiceChannel |> not ->
                                    voiceOperator.WriteTTS user.VoiceChannel msg true
                                | _ -> Task.FromResult() :> Task
                        } :> Task
                    | _ -> Task.FromResult() :> Task
                | _ -> Task.FromResult() :> Task
            ))
            
            return! Task.Delay(-1)
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<EntryPoint>]
    let main _ =
        startBot()
        
        0
