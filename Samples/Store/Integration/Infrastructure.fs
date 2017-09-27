﻿[<AutoOpen>]
module Samples.Store.Integration.Infrastructure

open Domain
open FsCheck
open System

type FsCheckGenerators =
    static member CartId = Arb.generate |> Gen.map CartId |> Arb.fromGen
    static member SkuId = Arb.generate |> Gen.map SkuId |> Arb.fromGen
    static member RequestId = Arb.generate |> Gen.map RequestId |> Arb.fromGen
    static member ContactPreferencesId =
        Arb.generate<Guid>
        |> Gen.map (fun x -> sprintf "%s@test.com" (x.ToString("N")))
        |> Gen.map ContactPreferences.Id
        |> Arb.fromGen

type AutoDataAttribute() =
    inherit FsCheck.Xunit.PropertyAttribute(Arbitrary = [|typeof<FsCheckGenerators>|], MaxTest = 1, QuietOnSuccess = true)

// Derived from https://github.com/damianh/CapturingLogOutputWithXunit2AndParallelTests
// NB VS does not surface these atm, but other test runners / test reports do
type TestOutputAdapter(testOutput : Xunit.Abstractions.ITestOutputHelper) =
    let formatter = Serilog.Formatting.Display.MessageTemplateTextFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message}{NewLine}{Exception}", null);
    let writeSeriLogEvent logEvent =
        use writer = new System.IO.StringWriter()
        formatter.Format(logEvent, writer);
        writer |> string |> testOutput.WriteLine
    member __.Subscribe(source: IObservable<Serilog.Events.LogEvent>) =
        source.Subscribe writeSeriLogEvent

[<AutoOpen>]
module SerilogHelpers =
    open Serilog
    let createLogger hookObservers =
        LoggerConfiguration()
            .WriteTo.Observers(System.Action<_> hookObservers)
            .Destructure.AsScalar<Foldunk.EventStore.Metrics.Metric>()
            .CreateLogger()

    let (|SerilogProperty|) name (logEvent : Serilog.Events.LogEvent) : Serilog.Events.LogEventPropertyValue option =
        match logEvent.Properties.TryGetValue name with
        | true, value -> Some value
        | false, _ -> None
    let (|SerilogScalar|_|) : Serilog.Events.LogEventPropertyValue -> obj option = function
        | (:? Serilog.Events.ScalarValue as x) -> Some x.Value
        | _ -> None

    type LogCaptureBuffer() =
        let captured = ResizeArray()
        member __.Subscribe(source: IObservable<Serilog.Events.LogEvent>) =
            source.Subscribe captured.Add
        member __.Clear () = captured.Clear()
        member __.Entries = captured.ToArray()
        member __.ExternalCalls =
            captured
            |> Seq.choose (function
                | SerilogProperty Foldunk.EventStore.Metrics.ExternalTag
                    (Some (SerilogScalar (:? Foldunk.EventStore.Metrics.Metric as metric))) -> Some metric.action
                | _ -> None)
            |> List.ofSeq

/// Needs an ES instance with default settings
/// TL;DR: At an elevated command prompt: choco install eventstore-oss; \ProgramData\chocolatey\bin\EventStore.ClusterNode.exe
let connectToLocalEventStoreNode () = async {
    let localhost = System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1113)
    let conn = EventStore.ClientAPI.EventStoreConnection.Create(localhost)
    do! conn.ConnectAsync() |> Async.AwaitTask
    return conn }

let createGesGateway eventStoreConnection maxBatchSize =
    let connection = Foldunk.EventStore.GesConnection(eventStoreConnection)
    Foldunk.EventStore.GesGateway(connection, Foldunk.EventStore.GesStreamPolicy(maxBatchSize = maxBatchSize))

let createGesStream<'state,'event> gateway (codec : Foldunk.EventSum.IEventSumEncoder<'event,byte[]>) streamName : Foldunk.IStream<_,_> =
    let store = Foldunk.EventStore.GesStreamStore<'state, 'event>(gateway, codec)
    Foldunk.EventStore.GesStream<'state, 'event>(store, streamName) :> _

let inline createMemStore () =
    Foldunk.MemoryStore.MemoryStreamStore()
let inline createMemStream<'state,'event> store streamName : Foldunk.IStream<'state,'event> =
    Foldunk.MemoryStore.MemoryStream(store, streamName) :> _