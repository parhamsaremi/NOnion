namespace NOnion.Utility

// RequireQualifiedAccess is needed to prevent collision with
// Failure function that creates general exceptions

[<RequireQualifiedAccess>]
type OperationResult<'T> =
    | Ok of 'T
    | Failure of exn
