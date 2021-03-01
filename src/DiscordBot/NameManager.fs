namespace TTSDB

open Discord.WebSocket
open System
open TTSDB.MemoryCache

type NameManager (guildManager: GuildManager, ?settings: Settings) =
    let subMap = 
        settings
        |> Option.map (Settings.userSubstitutions >> Settings.buildSubMap)
        |> Option.defaultValue Map.empty

    let mentionedMap = 
        settings
        |> Option.map (Settings.userMentionedSubstitutions >> Settings.buildSubMap)
        |> Option.defaultValue Map.empty

    let mentionedNameCache = MemoryCache<uint64,string>(CacheExpirationPolicy.AbsoluteExpiration(TimeSpan(1, 0, 0)))

    let lookupName (user: SocketUser) =
        async {
            let! guild = guildManager.GetGuildAsync()
            
            return
                match guild.TryFindUser(user) with
                | Some user ->
                    match mentionedMap.TryFind user.Username with
                    | Some name -> Some name
                    | None -> subMap.TryFind user.Username
                | None -> None
                |> Option.defaultValue user.Username
        }

    member _.TryFindSub (username: string) = subMap.TryFind username

    member _.FindMentionSub (user: SocketUser) =
        lookupName user
        |> mentionedNameCache.GetOrAddAsync user.Id
