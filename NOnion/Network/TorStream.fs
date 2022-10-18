namespace NOnion.Network

open System
open System.Threading.Tasks
open System.Threading.Tasks.Dataflow

open FSharpx.Collections

open NOnion
open NOnion.Cells.Relay
open NOnion.Utility
open System.Net.Sockets

[<RequireQualifiedAccess>]
type internal StreamRecieveResult =
    | Ok of int
    | Failure of exn

type internal StreamRecieveMessage =
    {
        StreamBuffer: array<byte>
        BufferOffset: int
        BufferLength: int
        ReplyChannel: AsyncReplyChannel<StreamRecieveResult>
    }

[<RequireQualifiedAccess>]
type internal StreamControlResult =
    | Ok
    | OkWithTask of Task<uint16>
    | Failure of exn

type internal StreamContolCommand =
    | End
    | Send of array<byte>
    | StartServiceConnectionProcess of int * ITorStream
    | StartDirectoryConnectionProcess of ITorStream
    | RegisterStream of ITorStream * uint16
    | HandleRelayConnected
    | HandleRelayEnd of RelayData * EndReason
    | SendSendMe

type internal StreamControlMessage =
    {
        Command: StreamContolCommand
        ReplyChannel: AsyncReplyChannel<StreamControlResult>
    }

type TorStream(circuit: TorCircuit) =

    let mutable streamState: StreamState = StreamState.Initialized

    let window: TorWindow = TorWindow Constants.DefaultStreamLevelWindowParams

    let mutable currentBuffer: array<byte> =
        Array.zeroCreate Constants.MaximumRelayPayloadLength

    let mutable bufferOffset: int = 0
    let mutable bufferLength: int = 0
    let mutable isEOF: bool = false

    let incomingCells: BufferBlock<RelayData> = BufferBlock<RelayData>()

    let rec StreamControlMailBoxProcessor
        (inbox: MailboxProcessor<StreamControlMessage>)
        =
        let safeEnd() =
            async {
                match streamState with
                | Connected streamId ->
                    do!
                        circuit.SendRelayCell
                            streamId
                            (RelayEnd EndReason.Done)
                            None

                    sprintf
                        "TorStream[%i,%i]: sending stream end packet"
                        streamId
                        circuit.Id
                    |> TorLogger.Log
                | _ -> failwith "Unexpected state when trying to end the stream"
            }

        let safeSend(data: array<byte>) =
            async {
                match streamState with
                | Connected streamId ->
                    let dataChunks =
                        SeqUtils.Chunk Constants.MaximumRelayPayloadLength data

                    let rec sendChunks dataChunks =
                        async {
                            match Seq.tryHeadTail dataChunks with
                            | None -> ()
                            | Some(head, nextDataChunks) ->
                                circuit.LastNode.Window.PackageDecrease()

                                do!
                                    circuit.SendRelayCell
                                        streamId
                                        (head
                                         |> Array.ofSeq
                                         |> RelayData.RelayData)
                                        None

                                window.PackageDecrease()
                                do! nextDataChunks |> sendChunks
                        }

                    do! sendChunks dataChunks
                | _ ->
                    failwith
                        "Unexpected state when trying to send data over stream"
            }

        let startServiceConnectionProcess(port: int, streamObj: ITorStream) =
            async {
                let streamId = circuit.RegisterStream streamObj None

                let tcs = TaskCompletionSource()

                streamState <- Connecting(streamId, tcs)

                sprintf
                    "TorStream[%i,%i]: creating a hidden service stream"
                    streamId
                    circuit.Id
                |> TorLogger.Log

                do!
                    circuit.SendRelayCell
                        streamId
                        (RelayBegin
                            {
                                RelayBegin.Address = (sprintf ":%i" port)
                                Flags = 0u
                            })
                        None

                return tcs.Task
            }

        let startDirectoryConnectionProcess streamObj =
            async {
                let streamId = circuit.RegisterStream streamObj None

                let tcs = TaskCompletionSource()

                streamState <- Connecting(streamId, tcs)

                sprintf
                    "TorStream[%i,%i]: creating a directory stream"
                    streamId
                    circuit.Id
                |> TorLogger.Log

                do!
                    circuit.SendRelayCell
                        streamId
                        RelayData.RelayBeginDirectory
                        None

                return tcs.Task
            }

        let registerProcess(streamObj: ITorStream, streamId: uint16) =
            streamState <-
                circuit.RegisterStream streamObj (Some streamId) |> Connected

        let handleRelayConnected() =
            match streamState with
            | Connecting(streamId, tcs) ->
                streamState <- Connected streamId
                tcs.SetResult streamId

                sprintf "TorStream[%i,%i]: connected!" streamId circuit.Id
                |> TorLogger.Log
            | _ ->
                failwith "Unexpected state when receiving RelayConnected cell"

        let handleRelayEnd(message: RelayData, reason: EndReason) =
            match streamState with
            | Connecting(streamId, tcs) ->
                sprintf
                    "TorStream[%i,%i]: received end packet while connecting"
                    streamId
                    circuit.Id
                |> TorLogger.Log

                Failure(
                    sprintf
                        "Stream connection process failed! Reason: %s"
                        (reason.ToString())
                )
                |> tcs.SetException
            | Connected streamId ->
                sprintf
                    "TorStream[%i,%i]: received end packet while connected"
                    streamId
                    circuit.Id
                |> TorLogger.Log

                incomingCells.Post message |> ignore<bool>
            | _ -> failwith "Unexpected state when receiving RelayEnd cell"

        let sendSendMe() =
            async {
                match streamState with
                | Connected streamId ->
                    return!
                        circuit.SendRelayCell
                            streamId
                            RelayData.RelaySendMe
                            None
                | _ ->
                    failwith "Unexpected state when sending stream-level sendme"
            }

        async {
            let! cancellationToken = Async.CancellationToken
            cancellationToken.ThrowIfCancellationRequested()

            let! {
                     Command = command
                     ReplyChannel = replyChannel
                 } = inbox.Receive()

            try
                match command with
                | End ->
                    do! safeEnd()
                    StreamControlResult.Ok |> replyChannel.Reply
                | Send data ->
                    do! safeSend data
                    StreamControlResult.Ok |> replyChannel.Reply
                | StartServiceConnectionProcess(port, streamObj) ->
                    let! task = startServiceConnectionProcess(port, streamObj)
                    StreamControlResult.OkWithTask task |> replyChannel.Reply
                | StartDirectoryConnectionProcess streamObj ->
                    let! task = startDirectoryConnectionProcess(streamObj)
                    StreamControlResult.OkWithTask task |> replyChannel.Reply
                | RegisterStream(streamObj, streamId) ->
                    registerProcess(streamObj, streamId)
                    StreamControlResult.Ok |> replyChannel.Reply
                | HandleRelayConnected ->
                    handleRelayConnected()
                    StreamControlResult.Ok |> replyChannel.Reply
                | HandleRelayEnd(message, reason) ->
                    handleRelayEnd(message, reason)
                    StreamControlResult.Ok |> replyChannel.Reply
                | SendSendMe ->
                    do! sendSendMe()
                    StreamControlResult.Ok |> replyChannel.Reply
            with
            | exn ->
                match FSharpUtil.FindException<SocketException> exn with
                | Some socketExn ->
                    NOnionSocketException socketExn :> exn
                    |> StreamControlResult.Failure
                    |> replyChannel.Reply
                | None -> StreamControlResult.Failure exn |> replyChannel.Reply

            return! StreamControlMailBoxProcessor inbox
        }

    let streamControlMailBox =
        MailboxProcessor.Start(StreamControlMailBoxProcessor)

    let rec StreamRecieveMailBoxProcessor
        (inbox: MailboxProcessor<StreamRecieveMessage>)
        =
        let currentBufferHasRemainingBytes() =
            bufferLength > bufferOffset

        let currentBufferRemainingBytes() =
            bufferLength - bufferOffset

        let readFromCurrentBuffer
            (buffer: array<byte>)
            (offset: int)
            (len: int)
            =
            let readLength = min len (currentBufferRemainingBytes())
            Array.blit currentBuffer bufferOffset buffer offset readLength
            bufferOffset <- bufferOffset + readLength

            readLength

        let processIncomingCell() =
            async {
                let! nextCell = incomingCells.ReceiveAsync() |> Async.AwaitTask

                match nextCell with
                | RelayData data ->
                    Array.blit data 0 currentBuffer 0 data.Length
                    bufferOffset <- 0
                    bufferLength <- data.Length

                    window.DeliverDecrease()

                    if window.NeedSendme() then

                        let! sendResult =
                            streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                                {
                                    Command = StreamContolCommand.SendSendMe
                                    ReplyChannel = replyChannel
                                }
                            )

                        match sendResult with
                        | StreamControlResult.Ok -> return ()
                        | StreamControlResult.Failure exn ->
                            return raise <| FSharpUtil.ReRaise exn
                        | _ ->
                            return
                                failwith "Unexpected return state from mailbox"

                | RelayEnd reason when reason = EndReason.Done ->
                    TorLogger.Log(
                        sprintf
                            "TorStream[%i]: pushed EOF to consumer"
                            circuit.Id
                    )

                    currentBuffer <- Array.empty
                    bufferOffset <- 0
                    bufferLength <- 0
                    isEOF <- true

                | RelayEnd reason ->
                    return
                        failwithf
                            "Stream closed unexpectedly, reason = %s"
                            (reason.ToString())
                | _ ->
                    return
                        failwith "IncomingCells should not keep unrelated cells"
            }

        let rec fillBuffer() =
            async {
                do! processIncomingCell()

                if isEOF || currentBufferHasRemainingBytes() then
                    return ()
                else
                    return! fillBuffer()
            }

        let refillBufferIfNeeded() =
            async {
                if not isEOF then
                    if currentBufferHasRemainingBytes() then
                        return ()
                    else
                        return! fillBuffer()
            }


        let safeReceive(buffer: array<byte>, offset: int, length: int) =
            async {
                if length = 0 then
                    return 0
                else
                    do! refillBufferIfNeeded()

                    if isEOF then
                        return -1
                    else
                        let rec tryRead bytesRead bytesRemaining =
                            async {
                                if bytesRemaining > 0 && not isEOF then
                                    do! refillBufferIfNeeded()

                                    let newBytesRead =
                                        bytesRead
                                        + (readFromCurrentBuffer
                                            buffer
                                            (offset + bytesRead)
                                            (length - bytesRead))

                                    let newBytesRemaining =
                                        length - newBytesRead

                                    if incomingCells.Count = 0 then
                                        return newBytesRead
                                    else
                                        return!
                                            tryRead
                                                newBytesRead
                                                newBytesRemaining
                                else
                                    return bytesRead
                            }

                        return! tryRead 0 length
            }


        async {
            let! cancellationToken = Async.CancellationToken
            cancellationToken.ThrowIfCancellationRequested()

            let! {
                     StreamBuffer = buffer
                     BufferOffset = offset
                     BufferLength = length
                     ReplyChannel = replyChannel
                 } = inbox.Receive()

            try
                let! recieved = safeReceive(buffer, offset, length)
                StreamRecieveResult.Ok recieved |> replyChannel.Reply
            with
            | exn ->
                match FSharpUtil.FindException<SocketException> exn with
                | Some socketExn ->
                    NOnionSocketException socketExn :> exn
                    |> StreamRecieveResult.Failure
                    |> replyChannel.Reply
                | None -> StreamRecieveResult.Failure exn |> replyChannel.Reply

            return! StreamRecieveMailBoxProcessor inbox
        }

    let streamRecieveMailBox =
        MailboxProcessor.Start(StreamRecieveMailBoxProcessor)

    static member Accept (streamId: uint16) (circuit: TorCircuit) =
        async {
            let stream = TorStream circuit
            do! stream.RegisterIncomingStream streamId

            do! circuit.SendRelayCell streamId (RelayConnected Array.empty) None

            sprintf
                "TorStream[%i,%i]: incoming stream accepted"
                streamId
                circuit.Id
            |> TorLogger.Log

            return stream
        }

    member __.End() =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command = StreamContolCommand.End
                        ReplyChannel = replyChannel
                    }
                )

            match sendResult with
            | StreamControlResult.Ok -> return ()
            | StreamControlResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
            | _ -> return failwith "Unexpected return state from mailbox"
        }

    member self.EndAsync() =
        self.End() |> Async.StartAsTask


    member __.SendData(data: array<byte>) =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command = StreamContolCommand.Send data
                        ReplyChannel = replyChannel
                    }
                )

            match sendResult with
            | StreamControlResult.Ok -> return ()
            | StreamControlResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
            | _ -> return failwith "Unexpected return state from mailbox"
        }

    member self.SendDataAsync data =
        self.SendData data |> Async.StartAsTask

    member self.ConnectToService(port: int) =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command =
                            StreamContolCommand.StartServiceConnectionProcess(
                                port,
                                self
                            )
                        ReplyChannel = replyChannel
                    }
                )

            match sendResult with
            | StreamControlResult.OkWithTask connectionProcessTcs ->
                return!
                    connectionProcessTcs
                    |> Async.AwaitTask
                    |> FSharpUtil.WithTimeout Constants.StreamCreationTimeout
            | StreamControlResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
            | _ -> return failwith "Unexpected return state from mailbox"
        }

    member self.ConnectToDirectory() =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command =
                            StreamContolCommand.StartDirectoryConnectionProcess
                                self
                        ReplyChannel = replyChannel
                    }
                )

            match sendResult with
            | StreamControlResult.OkWithTask connectionProcessTcs ->
                return!
                    connectionProcessTcs
                    |> Async.AwaitTask
                    |> FSharpUtil.WithTimeout Constants.StreamCreationTimeout
            | StreamControlResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
            | _ -> return failwith "Unexpected return state from mailbox"
        }

    member self.ConnectToDirectoryAsync() =
        self.ConnectToDirectory() |> Async.StartAsTask

    member private self.RegisterIncomingStream(streamId: uint16) =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command =
                            StreamContolCommand.RegisterStream(self, streamId)
                        ReplyChannel = replyChannel
                    }
                )

            match sendResult with
            | StreamControlResult.Ok -> return ()
            | StreamControlResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
            | _ -> return failwith "Unexpected return state from mailbox"
        }

    member self.Receive (buffer: array<byte>) (offset: int) (length: int) =
        async {
            let! sendResult =
                streamRecieveMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        StreamBuffer = buffer
                        BufferOffset = offset
                        BufferLength = length
                        ReplyChannel = replyChannel
                    }
                )

            match sendResult with
            | StreamRecieveResult.Ok recieveAmount -> return recieveAmount
            | StreamRecieveResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
        }

    member self.ReceiveAsync(buffer: array<byte>, offset: int, length: int) =
        self.Receive buffer offset length |> Async.StartAsTask

    interface ITorStream with
        member __.HandleIncomingData(message: RelayData) =
            async {
                match message with
                | RelayConnected _ ->
                    let! sendResult =
                        streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                            {
                                Command =
                                    StreamContolCommand.HandleRelayConnected
                                ReplyChannel = replyChannel
                            }
                        )

                    match sendResult with
                    | StreamControlResult.Ok -> return ()
                    | StreamControlResult.Failure exn ->
                        return raise <| FSharpUtil.ReRaise exn
                    | _ ->
                        return failwith "Unexpected return state from mailbox"
                | RelayData _ -> incomingCells.Post message |> ignore<bool>
                | RelaySendMe _ -> window.PackageIncrease()
                | RelayEnd reason ->
                    let! sendResult =
                        streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                            {
                                Command =
                                    StreamContolCommand.HandleRelayEnd(
                                        message,
                                        reason
                                    )
                                ReplyChannel = replyChannel
                            }
                        )

                    match sendResult with
                    | StreamControlResult.Ok -> return ()
                    | StreamControlResult.Failure exn ->
                        return raise <| FSharpUtil.ReRaise exn
                    | _ ->
                        return failwith "Unexpected return state from mailbox"
                | _ -> ()
            }
