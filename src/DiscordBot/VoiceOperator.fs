﻿namespace TTSDB

open Discord.Audio
open Discord.WebSocket
open FSharp.Control
open FSharp.Control.Reactive
open FSharp.Control.Tasks.V2.ContextInsensitive
open Microsoft.CognitiveServices.Speech
open System
open System.IO
open System.Reactive
open System.Threading.Tasks
open TTSDB.MemoryCache

[<RequireQualifiedAccess>]
module VoiceOperator =
    type Msg =
        | SpeakIn of channel:SocketVoiceChannel * msg:SocketUserMessage * isOwnerMsg:bool
        | LeaveEmptyChannels
        | Dispose of AsyncReplyChannel<unit>

type VoiceInstance =
    { AudioClient: IAudioClient
      PCMStream: AudioOutStream
      VoiceChannel: SocketVoiceChannel }

    member this.Id = this.VoiceChannel.Id

    member this.DisposeAsync () =
        task {
            do! this.AudioClient.StopAsync()
        
            this.AudioClient.Dispose()
        
            do! this.PCMStream.FlushAsync()
            do! this.PCMStream.DisposeAsync()

            return! this.VoiceChannel.DisconnectAsync() 
        }
        |> Async.AwaitTask

    static member DisposeAsync (voiceInstance: VoiceInstance) =
        voiceInstance.DisposeAsync()

type VoiceOperator (guildManager: GuildManager, ?settings: Settings) as self =
    let cts = new Threading.CancellationTokenSource()

    let nameManager = NameManager(guildManager, ?settings = settings)

    let voice =
        settings
        |> Option.map Settings.voice
        |> Option.defaultValue 
            { Pitch = -60
              Rate = -60
              Style = "cheerful"
              Voice = "en-US-AriaNeural"
              Volume = 100 } 

    let synthesizer =
        let apiKey = getEnvFromAllOrNone "TTSDB_Azure" |> Option.defaultValue ""

        let speechConfig = SpeechConfig.FromSubscription(apiKey, "eastus")
        
        speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw48Khz16BitMonoPcm)

        new SpeechSynthesizer(speechConfig, null)

    let createSSML (content: string) =
        async {
            return
                sprintf """
<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="en-US">
    <voice name="%s">
        <mstts:express-as style="%s">
            <prosody rate="%i%%" pitch="%i%%">
                %s
            </prosody>
        </mstts:express-as>
    </voice>
</speak>
                """ voice.Voice voice.Style voice.Rate voice.Pitch content
        }
    
    let createUserSSML (msg: SocketUserMessage) =
        async {
            let author =
                nameManager.TryFindSub msg.Author.Username
                |> Option.defaultValue msg.Author.Username

            let! content =
                if msg.Content.ToLower().Contains("http://") || msg.Content.ToLower().Contains("https://") then
                    Async.lift "posted a link"
                else
                    let content = sprintf "says, %s" msg.Content

                    if msg.MentionedUsers.Count = 0 then 
                        Async.lift content
                    else 
                        msg.MentionedUsers 
                        |> Seq.cast
                        |> AsyncSeq.ofSeq
                        |> AsyncSeq.foldAsync (fun content (user: SocketUser) ->
                            nameManager.FindMentionSub user
                            |> Async.map (fun name ->
                                content.Replace(sprintf "<@!%i>" user.Id, name)
                            )
                        ) content

            return!
                sprintf "%s %s" author content
                |> createSSML
        }

    let setVolume (volume: int) (audio: byte []) =
        if volume >= 100 || Array.isEmpty audio || audio.Length % 2 <> 0 then audio
        else
            let percVol = (float volume) / 100.

            audio
            |> Array.chunkBySize 2
            |> Array.collect (fun arr ->
                BitConverter.ToInt16(arr, 0)
                |> float
                |> fun b -> b * percVol
                |> int16
                |> BitConverter.GetBytes
            )

    let speakInChannel (discord: AudioOutStream) (getSsml: Async<string>) =
        async {
            let! audioRaw =
                getSsml
                |> Async.bind (synthesizer.SpeakSsmlAsync >> Async.AwaitTask)
                |> Async.map (fun res -> res.AudioData |> setVolume voice.Volume)

            use audioStream = new MemoryStream(audioRaw)

            return! audioStream.CopyToAsync(discord) |> Async.AwaitTask
        }
        |> Async.protect
        |> Async.Ignore

    let messageQueue = new Subjects.Subject<AudioOutStream * SocketUserMessage * bool>()

    let mailbox = 
        MailboxProcessor<VoiceOperator.Msg>.Start (
            (fun inbox ->
                let rec loop (voiceInstance: VoiceInstance option) =
                    async {
                        let! msg = inbox.Receive()

                        match msg with
                        | VoiceOperator.Msg.SpeakIn (channel, msg, isOwnerMsg) ->
                            match voiceInstance with
                            | Some voiceInstance when voiceInstance.Id = channel.Id ->
                                messageQueue.OnNext(voiceInstance.PCMStream, msg, isOwnerMsg)

                                return! loop (Some voiceInstance)
                            | Some voiceInstance ->
                                do! voiceInstance.DisposeAsync()

                                let! ac = channel.ConnectAsync() |> Async.AwaitTask
                                
                                let voiceInstance =
                                    { AudioClient = ac
                                      PCMStream = ac.CreatePCMStream(AudioApplication.Mixed)
                                      VoiceChannel = channel }

                                messageQueue.OnNext(voiceInstance.PCMStream, msg, isOwnerMsg)

                                return! loop (Some voiceInstance)

                            | None -> 
                                let! ac = channel.ConnectAsync() |> Async.AwaitTask
                                
                                let voiceInstance =
                                    { AudioClient = ac
                                      PCMStream = ac.CreatePCMStream(AudioApplication.Mixed)
                                      VoiceChannel = channel }

                                messageQueue.OnNext(voiceInstance.PCMStream, msg, isOwnerMsg)
                                
                                return! loop (Some voiceInstance)
                        | VoiceOperator.Msg.Dispose replyChannel ->
                            do!
                                match voiceInstance with
                                | Some voiceInstance -> voiceInstance.DisposeAsync()
                                | None -> Async.lift ()
                                
                            replyChannel.Reply()

                            return ()
                        | VoiceOperator.Msg.LeaveEmptyChannels ->
                            let! voiceInstance =
                                match voiceInstance with
                                | Some voiceInstance when voiceInstance.VoiceChannel.Users.Count < 3 -> 
                                    async {
                                        do! voiceInstance.DisposeAsync()
                                        
                                        return None
                                    }
                                | voiceInstance -> Async.lift voiceInstance

                            return! loop voiceInstance
                    }

                loop None
            ), cts.Token)

    let dispose () =
        mailbox.PostAndAsyncReply VoiceOperator.Msg.Dispose
        |> Async.RunSynchronously
        
        synthesizer.Dispose()
        cts.Cancel()
        cts.Dispose()

    do
        AsyncSeq.intervalMs 30000
        |> AsyncSeq.distinctUntilChanged
        |> AsyncSeq.iter (fun _ -> mailbox.Post VoiceOperator.Msg.LeaveEmptyChannels)
        |> fun a -> Async.Start(a, cts.Token)

        AsyncSeq.ofObservableBuffered messageQueue
        |> AsyncSeq.iterAsync (fun (pcmStream, msg, isOwnerMsg) -> 
            if isOwnerMsg then createSSML msg.Content
            else createUserSSML msg
            |> speakInChannel pcmStream
        )
        |> fun a -> Async.Start(a, cts.Token)

    member _.WriteTTS (channel: SocketVoiceChannel) (msg: SocketUserMessage) (isOwnerMsg: bool) =
        task {
            return
                VoiceOperator.Msg.SpeakIn(channel, msg, isOwnerMsg)
                |> mailbox.Post
        } :> Task        

    interface IDisposable with
        member _.Dispose () =
            dispose()
            
            System.GC.SuppressFinalize(self)
    
    override _.Finalize () = dispose()
