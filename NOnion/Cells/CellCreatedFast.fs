﻿namespace NOnion.Cells

open System.IO

open NOnion

type CellCreatedFast =
    {
        Y: array<byte>
        DerivativeKeyData: array<byte>
    }

    static member Deserialize (reader: BinaryReader) =
        let y = reader.ReadBytes Constants.HashLength
        let derivativeKeyData = reader.ReadBytes Constants.HashLength

        {
            Y = y
            DerivativeKeyData = derivativeKeyData
        }
        :> ICell

    interface ICell with

        member __.Command = 6uy

        member self.Serialize writer =
            writer.Write self.Y
            writer.Write self.DerivativeKeyData