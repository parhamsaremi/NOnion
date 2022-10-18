namespace NOnion.Network

open System
open System.Threading.Tasks
open System.Threading.Tasks.Dataflow

open FSharpx.Collections

open NOnion
open NOnion.Cells.Relay
open NOnion.Utility
open System.Net.Sockets

type internal StreamReceiveMessage =
    {
        StreamBuffer: array<byte>
        BufferOffset: int
        BufferLength: int
        ReplyChannel: AsyncReplyChannel<OperationResult<int>>
    }

type internal StreamContolCommand =
    | End of AsyncReplyChannel<OperationResult<unit>>
    | Send of array<byte> * AsyncReplyChannel<OperationResult<unit>>
    | StartServiceConnectionProcess of int * ITorStream * AsyncReplyChannel<OperationResult<Task<uint16>>>
    | StartDirectoryConnectionProcess of ITorStream * AsyncReplyChannel<OperationResult<Task<uint16>>>
    | RegisterStream of ITorStream * uint16 * AsyncReplyChannel<OperationResult<unit>>
    | HandleRelayConnected of AsyncReplyChannel<OperationResult<unit>>
    | HandleRelayEnd of RelayData * EndReason * AsyncReplyChannel<OperationResult<unit>>
    | SendSendMe of AsyncReplyChannel<OperationResult<unit>>

type internal StreamControlMessage<'T> =
    {
        Command: StreamContolCommand
    }

module StreamContolHandleError = 
    let HandleError<'T> (exn, replyChannel: AsyncReplyChannel<OperationResult<'T>>) = 
        match FSharpUtil.FindException<SocketException> exn with
        | Some socketExn ->
            NOnionSocketException socketExn :> exn
            |> OperationResult.Failure
            |> replyChannel.Reply
        | None -> OperationResult.Failure exn |> replyChannel.Reply

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
        (inbox: MailboxProcessor<StreamControlMessage<'T>>)
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
                 } = inbox.Receive()

            match command with
            | End replyChannel ->
                try
                    do! safeEnd()
                    OperationResult.Ok() |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
            | Send(data, replyChannel) ->
                try
                    do! safeSend data
                    OperationResult.Ok() |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
            | StartServiceConnectionProcess(port, streamObj, replyChannel) ->
                try
                    let! task = startServiceConnectionProcess(port, streamObj)
                    OperationResult.Ok task |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
            | StartDirectoryConnectionProcess(streamObj, replyChannel) ->
                try
                    let! task = startDirectoryConnectionProcess(streamObj)
                    OperationResult.Ok task |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
            | RegisterStream(streamObj, streamId, replyChannel) ->
                try
                    registerProcess(streamObj, streamId)
                    OperationResult.Ok() |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
            | HandleRelayConnected replyChannel ->
                try
                    handleRelayConnected()
                    OperationResult.Ok() |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
            | HandleRelayEnd(message, reason, replyChannel) ->
                try
                    handleRelayEnd(message, reason)
                    OperationResult.Ok() |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
            | SendSendMe replyChannel ->
                try
                    do! sendSendMe()
                    OperationResult.Ok() |> replyChannel.Reply
                with
                | exn -> 
                    StreamContolHandleError.HandleError(exn, replyChannel)
                

            return! StreamControlMailBoxProcessor inbox
        }

    let streamControlMailBox =
        MailboxProcessor.Start(StreamControlMailBoxProcessor)

    let rec StreamReceiveMailBoxProcessor
        (inbox: MailboxProcessor<StreamReceiveMessage>)
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
                                    Command = StreamContolCommand.SendSendMe replyChannel
                                }
                            )

                        match sendResult with
                        | OperationResult.Ok _ -> return ()
                        | OperationResult.Failure exn ->
                            return raise <| FSharpUtil.ReRaise exn

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
                let! received = safeReceive(buffer, offset, length)
                OperationResult.Ok received |> replyChannel.Reply
            with
            | exn ->
                match FSharpUtil.FindException<SocketException> exn with
                | Some socketExn ->
                    NOnionSocketException socketExn :> exn
                    |> OperationResult.Failure
                    |> replyChannel.Reply
                | None -> OperationResult.Failure exn |> replyChannel.Reply

            return! StreamReceiveMailBoxProcessor inbox
        }

    let streamReceiveMailBox =
        MailboxProcessor.Start(StreamReceiveMailBoxProcessor)

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
                        Command = StreamContolCommand.End replyChannel
                    }
                )

            match sendResult with
            | OperationResult.Ok _ -> return ()
            | OperationResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
        }

    member self.EndAsync() =
        self.End() |> Async.StartAsTask


    member __.SendData(data: array<byte>) =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command = StreamContolCommand.Send(data, replyChannel)
                    }
                )

            match sendResult with
            | OperationResult.Ok _ -> return ()
            | OperationResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
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
                                self,
                                replyChannel
                            )
                    }
                )

            match sendResult with
            | OperationResult.Ok connectionProcessTcs ->
                return!
                    connectionProcessTcs
                    |> Async.AwaitTask
                    |> FSharpUtil.WithTimeout Constants.StreamCreationTimeout
            | OperationResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
        }

    member self.ConnectToDirectory() =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command =
                            StreamContolCommand.StartDirectoryConnectionProcess(
                                self,
                                replyChannel
                            )
                    }
                )

            match sendResult with
            | OperationResult.Ok connectionProcessTcs ->
                return!
                    connectionProcessTcs
                    |> Async.AwaitTask
                    |> FSharpUtil.WithTimeout Constants.StreamCreationTimeout
            | OperationResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
        }

    member self.ConnectToDirectoryAsync() =
        self.ConnectToDirectory() |> Async.StartAsTask

    member private self.RegisterIncomingStream(streamId: uint16) =
        async {
            let! sendResult =
                streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        Command =
                            StreamContolCommand.RegisterStream(self, streamId, replyChannel)
                    }
                )

            match sendResult with
            | OperationResult.Ok _ -> return ()
            | OperationResult.Failure exn ->
                return raise <| FSharpUtil.ReRaise exn
        }

    member self.Receive (buffer: array<byte>) (offset: int) (length: int) =
        async {
            let! sendResult =
                streamReceiveMailBox.PostAndAsyncReply(fun replyChannel ->
                    {
                        StreamBuffer = buffer
                        BufferOffset = offset
                        BufferLength = length
                        ReplyChannel = replyChannel
                    }
                )

            match sendResult with
            | OperationResult.Ok receiveAmount -> return receiveAmount
            | OperationResult.Failure exn ->
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
                                    StreamContolCommand.HandleRelayConnected replyChannel
                            }
                        )

                    match sendResult with
                    | OperationResult.Ok _ -> return ()
                    | OperationResult.Failure exn ->
                        return raise <| FSharpUtil.ReRaise exn
                | RelayData _ -> incomingCells.Post message |> ignore<bool>
                | RelaySendMe _ -> window.PackageIncrease()
                | RelayEnd reason ->
                    let! sendResult =
                        streamControlMailBox.PostAndAsyncReply(fun replyChannel ->
                            {
                                Command =
                                    StreamContolCommand.HandleRelayEnd(
                                        message,
                                        reason,
                                        replyChannel
                                    )
                            }
                        )

                    match sendResult with
                    | OperationResult.Ok _ -> return ()
                    | OperationResult.Failure exn ->
                        return raise <| FSharpUtil.ReRaise exn
                | _ -> ()
            }
