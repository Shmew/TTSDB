namespace Fable.SignalR.DotNet.Tests

open Expecto

module RunTests =
    [<EntryPoint>]
    let main _ = 
        Tests.runTests defaultConfig Client.tests
