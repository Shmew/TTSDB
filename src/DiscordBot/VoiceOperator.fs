namespace TTSDB

open Discord.Audio
open Discord.WebSocket
open FSharp.Control
open FSharp.Control.Tasks.V2.ContextInsensitive
open Microsoft.CognitiveServices.Speech
open System
open System.IO
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module VoiceOperator =
    type Msg =
        | SpeakIn of channel:SocketVoiceChannel * msg: SocketUserMessage
        | LeaveEmptyChannels
        | Dispose of AsyncReplyChannel<unit>

type VoiceInstance =
    { AudioClient: IAudioClient
      PCMStream: AudioOutStream
      VoiceChannel: SocketVoiceChannel }

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

type VoiceOperator (?subMap: Map<string,string>, ?voice: Voice) =
    let cts = new Threading.CancellationTokenSource()

    let subMap = Option.defaultValue Map.empty subMap
    
    let voice =
        Option.defaultValue 
            { Pitch = -60
              Rate = -60
              Style = "cheerful"
              Voice = "en-US-AriaNeural"
              Volume = 100 } 
            voice

    let synthesizer =
        let apiKey = getEnvFromAllOrNone "TTSDB_Azure" |> Option.defaultValue ""

        let speechConfig = SpeechConfig.FromSubscription(apiKey, "eastus")
        
        speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw48Khz16BitMonoPcm)

        new SpeechSynthesizer(speechConfig, null)

    let createSSML (user: string) (content: string) =
        let content =
            if content.ToLower().StartsWith("http://") || content.ToLower().StartsWith("https://") then
                "posted a link"
            else sprintf "says, %s" content

        sprintf """
<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="en-US">
    <voice name="%s">
        <mstts:express-as style="%s">
            <prosody rate="%i%%" pitch="%i%%">
                %s %s
            </prosody>
        </mstts:express-as>
    </voice>
</speak>
        """ voice.Voice voice.Style voice.Rate voice.Pitch user content

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

    let speakInChannel (discord: AudioOutStream) (msg: SocketUserMessage) =
        task {
            let! audioRaw =
                task {
                    let! res =
                        let author =
                            subMap.TryFind msg.Author.Username
                            |> Option.defaultValue msg.Author.Username

                        createSSML author msg.Content
                        |> synthesizer.SpeakSsmlAsync

                    return res.AudioData |> setVolume voice.Volume
                }

            use audioStream = new MemoryStream(audioRaw)

            try 
                do! audioStream.CopyToAsync(discord)
            with _ -> ()
        }
        |> Async.AwaitTask
        |> Async.Start

    let mailbox = 
        MailboxProcessor<VoiceOperator.Msg>.Start (
            (fun inbox ->
                let rec loop (voiceChannels: Map<uint64,VoiceInstance>) =
                    async {
                        let! msg = inbox.Receive()

                        match msg with
                        | VoiceOperator.Msg.SpeakIn (channel, msg) ->
                            match Map.tryFind channel.Id voiceChannels with
                            | Some voiceInstance ->
                                speakInChannel voiceInstance.PCMStream msg

                                return! loop voiceChannels
                            | None -> 
                                let! ac = channel.ConnectAsync() |> Async.AwaitTask
                                
                                let voiceInstance =
                                    { AudioClient = ac
                                      PCMStream = ac.CreatePCMStream(AudioApplication.Mixed)
                                      VoiceChannel = channel }

                                speakInChannel voiceInstance.PCMStream msg

                                return! loop (voiceChannels |> Map.add channel.Id voiceInstance)
                        | VoiceOperator.Msg.Dispose replyChannel ->
                            do!
                                voiceChannels
                                |> Map.toList
                                |> List.map snd
                                |> AsyncSeq.ofSeq
                                |> AsyncSeq.iterAsyncParallelThrottled 5 VoiceInstance.DisposeAsync

                            replyChannel.Reply()

                            return ()
                        | VoiceOperator.Msg.LeaveEmptyChannels ->
                            let staleChannels,voiceChannels =
                                voiceChannels
                                |> Map.toList
                                |> List.partition (fun (_, voiceInstance) ->
                                    voiceInstance.VoiceChannel.Users |> List.ofSeq |> List.length < 2
                                )

                            do!
                                staleChannels
                                |> AsyncSeq.ofSeq
                                |> AsyncSeq.iterAsyncParallelThrottled 5 (fun (_, voiceInstance) ->
                                    voiceInstance.DisposeAsync()
                                )
                        
                            return! loop (Map.ofList voiceChannels)
                    }

                loop Map.empty
            ), cts.Token)
    do
        AsyncSeq.intervalMs 30000
        |> AsyncSeq.distinctUntilChanged
        |> AsyncSeq.iter (fun _ -> mailbox.Post VoiceOperator.Msg.LeaveEmptyChannels)
        |> fun a -> Async.Start(a, cts.Token)

    member _.WriteTTS (channel: SocketVoiceChannel) (msg: SocketUserMessage) =
        task {
            return
                VoiceOperator.Msg.SpeakIn(channel, msg)
                |> mailbox.Post
        } :> Task
        
    interface IDisposable with
        member _.Dispose () =
            mailbox.PostAndAsyncReply VoiceOperator.Msg.Dispose
            |> Async.RunSynchronously
            
            cts.Dispose()
