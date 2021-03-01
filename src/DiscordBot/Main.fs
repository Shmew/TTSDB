namespace TTSDB

open Discord
open Discord.WebSocket
open FSharp.Control
open FSharp.Control.Tasks.V2.ContextInsensitive
open System
open System.Threading.Tasks

type DiscordBot () as self =
    let cts = new Threading.CancellationTokenSource()

    let log (msg: LogMessage) =
        task {
            return Console.WriteLine(msg.ToString())
        } :> Task

    let settings = Settings.get()
    let client = new DiscordSocketClient()
    let apiKey = getEnvFromAllOrNone "TTSDB_Discord" |> Option.defaultValue ""
    
    let guildManager = new GuildManager(client, settings.GuildId)
    let voiceOperator = new VoiceOperator(guildManager, settings)

    let dispose () =
        (voiceOperator :> IDisposable).Dispose()
        (guildManager :> IDisposable).Dispose()
        client.Dispose()
        cts.Cancel()
        cts.Dispose()

    do
        client.add_Log(Func<LogMessage,Task>(log))

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

    member _.Start () =
        task {
            // fsharplint:disable-next-line
            do! client.LoginAsync(TokenType.Bot, apiKey, validateToken = true)
            do! client.StartAsync()

            return! Task.Delay(-1)
        }
        |> Async.AwaitTask
        |> fun a -> Async.RunSynchronously(a, cancellationToken = cts.Token)

        0

    interface IDisposable with
        member _.Dispose () =
            dispose()

            System.GC.SuppressFinalize(self)
            
    override _.Finalize () = dispose()

module Main =
    [<EntryPoint>]
    let main _ =
        use bot = new DiscordBot()
        
        try bot.Start()
        finally (bot :> IDisposable).Dispose()
