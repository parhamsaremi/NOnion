﻿namespace NOnion.Cells

open System.IO

open NOnion.Extensions

type CellVersions =
    {
        Versions: seq<uint16>
    }

    static member Deserialize (reader: BinaryReader) =

        let rec readVersions versions =
            if (reader.BaseStream.Length - reader.BaseStream.Position) % 2L
               <> 0L then
                failwith
                    "Version packet payload is invalid, payload length should be divisible by 2"

            if reader.BaseStream.Length = reader.BaseStream.Position then
                versions
            else
                readVersions (
                    versions
                    @ [
                        BinaryIOExtensions.BinaryReader.ReadBigEndianUInt16
                            reader
                    ]
                )

        let versions = readVersions List.empty

        {
            Versions = versions
        }
        :> ICell

    interface ICell with

        member __.Command = 7uy

        member self.Serialize writer =
            let rec writeVersions (versions: seq<uint16>) =
                match Seq.tryHead versions with
                | None -> ()
                | Some version ->
                    version
                    |> BinaryIOExtensions.BinaryWriter.WriteUInt16BigEndian
                        writer

                    writeVersions (Seq.tail versions)

            writeVersions self.Versions