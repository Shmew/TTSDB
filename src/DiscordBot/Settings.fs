namespace TTSDB

type UserSubstitution =
    { Name: string
      Replacement: string }

type Settings =
    { GuildId: uint64
      TextChannelId: uint64
      UserSubstitutions: UserSubstitution list }

[<RequireQualifiedAccess>]
module Settings =
    open FSharp.Json
    open System.IO
    
    let get () =
        File.ReadAllText "./settings.json"
        |> Json.deserialize<Settings>

    let buildSubMap (userSubstitutions: UserSubstitution list) =
        userSubstitutions
        |> List.map (fun us -> us.Name,us.Replacement)
        |> Map.ofList
