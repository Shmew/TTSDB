namespace TTSDB

type UserSubstitution =
    { Name: string
      Replacement: string }

type Voice =
    { Pitch: int
      Rate: int
      Style: string 
      Voice: string
      Volume: int }

type Settings =
    { GuildId: uint64
      Owners: uint64 list
      TextChannelId: uint64
      UserMentionedSubstitutions: UserSubstitution list
      UserSubstitutions: UserSubstitution list
      Voice: Voice }

[<RequireQualifiedAccess>]
module Settings =
    open FSharp.Json
    open System.IO
    
    let guildId (settings: Settings) = settings.GuildId
    let owners (settings: Settings) = settings.Owners
    let textChannelId (settings: Settings) = settings.TextChannelId
    let userMentionedSubstitutions (settings: Settings) = settings.UserMentionedSubstitutions
    let userSubstitutions (settings: Settings) = settings.UserSubstitutions
    let voice (settings: Settings) = settings.Voice

    let get () =
        File.ReadAllText "./settings.json"
        |> Json.deserialize<Settings>

    let buildSubMap (userSubstitutions: UserSubstitution list) =
        userSubstitutions
        |> List.map (fun us -> us.Name,us.Replacement)
        |> Map.ofList
