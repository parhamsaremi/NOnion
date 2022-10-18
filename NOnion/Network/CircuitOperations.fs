namespace NOnion.Network

open System.Net
open System.Threading.Tasks

open Org.BouncyCastle.Crypto
open Org.BouncyCastle.Crypto.Parameters

open NOnion.Utility
open NOnion.Cells.Relay

type CircuitNodeDetail =
    | FastCreate
    | Create of
        EndPoint: IPEndPoint *
        NTorOnionKey: array<byte> *
        IdentityKey: array<byte>

type internal RequestCreationResult = OperationResult<Task<uint16>>

type internal RequestExtensionResult = OperationResult<Task<uint16>>

module internal CircuitOperations =
    [<RequireQualifiedAccess>]
    type Operation =
        | GetCircuitLastNode of
            replyChannel: AsyncReplyChannel<OperationResult<TorCircuitNode>>
        | SendRelayCell of
            streamId: uint16 *
            relayData: RelayData *
            customDestinationOpt: Option<TorCircuitNode> *
            replyChannel: AsyncReplyChannel<OperationResult<unit>>
        | Create of
            circuitObj: ITorCircuit *
            guardDetailInfo: CircuitNodeDetail *
            replyChannel: AsyncReplyChannel<RequestCreationResult>
        | Extend of
            guardDetailInfo: CircuitNodeDetail *
            replyChannel: AsyncReplyChannel<RequestExtensionResult>
        | RegisterAsIntroductionPoint of
            authKeyPairOpt: Option<AsymmetricCipherKeyPair> *
            callback: (RelayIntroduce -> Async<unit>) *
            replyChannel: AsyncReplyChannel<OperationResult<Task<unit>>>
        | RegisterAsRendezvousPoint of
            cookie: array<byte> *
            replyChannel: AsyncReplyChannel<OperationResult<Task<unit>>>
        | SendIntroduceRequest of
            introduceMsg: RelayIntroduce *
            replyChannel: AsyncReplyChannel<OperationResult<Task<RelayIntroduceAck>>>
        | WaitForRendezvous of
            clientRandomPrivateKey: X25519PrivateKeyParameters *
            clientRandomPublicKey: X25519PublicKeyParameters *
            introAuthPublicKey: Ed25519PublicKeyParameters *
            introEncPublicKey: X25519PublicKeyParameters *
            replyChannel: AsyncReplyChannel<OperationResult<Task<unit>>>
        | SendRendezvousRequest of
            cookie: array<byte> *
            clientRandomKey: X25519PublicKeyParameters *
            introAuthPublicKey: Ed25519PublicKeyParameters *
            introEncPrivateKey: X25519PrivateKeyParameters *
            introEncPublicKey: X25519PublicKeyParameters *
            AsyncReplyChannel<OperationResult<unit>>
