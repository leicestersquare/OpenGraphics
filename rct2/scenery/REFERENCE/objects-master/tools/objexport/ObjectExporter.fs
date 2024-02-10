﻿// objexport
// Exports objects from RCT2 to OpenRCT2 json files

namespace OpenRCT2.Legacy.ObjectExporter

module ResizeArray =
    let tryItem index (list: ResizeArray<'T>) =
        if index < 0 || index >= list.Count then None
        else Some list.[index]

module Dictionary =
    let tryItem key (dict: System.Collections.Generic.IDictionary<'TKey, 'TValue>) =
        match dict.TryGetValue key with
        | true, value -> Some value
        | _ -> None

module ObjectExporter =

    open System
    open System.Collections.Generic
    open System.ComponentModel
    open System.Diagnostics
    open System.IO
    open System.Text
    open JsonTypes
    open Microsoft.FSharp.Core
    open Newtonsoft.Json
    open Newtonsoft.Json.FSharp
    open PropertyExtractor
    open RCT2ObjectData.Objects
    open RCT2ObjectData.Objects.Types

    type ObjectExporterOptions =
        { id: string option
          authors: string list
          languageDirectory: string option
          objectType: string option
          multithreaded: bool
          splitFootpaths: bool
          storePng: bool
          outputParkobj: bool }

    let printWithColour (c: ConsoleColor) (s: string) =
        let oldColour = Console.ForegroundColor
        Console.ForegroundColor <- c
        Console.WriteLine(s)
        Console.ForegroundColor <- oldColour

    let UTF8NoBOM = new UTF8Encoding(false)

    let serializeToJson (value: 'a) =
        let sb = new StringBuilder(capacity = 256)
        let sw = new StringWriter(sb)
        let jsonSerializer = JsonSerializer.CreateDefault()
        use jsonWriter = new MyJsonWriter(sw)
        Seq.iter (jsonSerializer.Converters.Add) JsonFsharp.converters
        jsonSerializer.ContractResolver <- new ObjectContractResolver()
        jsonSerializer.Serialize(jsonWriter, value, typedefof<'a>)
        sw.ToString()

    let serializeToJsonFile path (value: 'a) =
        let json = serializeToJson value + Environment.NewLine
        File.WriteAllText(path, json, UTF8NoBOM)

    let createDirectory path =
        try
            if not (Directory.Exists path) then
                Directory.CreateDirectory(path) |> ignore
            true
        with
        | ex ->
            printfn "Unable to create '%s': %s" path ex.Message
            false

    let getObjId (groupPrefix: string) (suffix: string) (obj: ObjectData) =
        let sourcePrefix =
            match obj.Source with
            | SourceTypes.RCT2 -> "rct2"
            | SourceTypes.WW -> "rct2.ww"
            | SourceTypes.TT -> "rct2.tt"
            | _ -> "other"

        let name = obj.ObjectHeader.FileName.ToLower()

        [sourcePrefix; groupPrefix; name; suffix]
        |> List.filter (String.IsNullOrEmpty >> not)
        |> String.concat "."

    let getObjTypeName = function
        | ObjectTypes.Attraction -> "ride"
        | ObjectTypes.SceneryGroup -> "scenery_group"
        | ObjectTypes.SmallScenery -> "scenery_small"
        | ObjectTypes.LargeScenery -> "scenery_large"
        | ObjectTypes.Wall -> "scenery_wall"
        | ObjectTypes.Footpath -> "footpath"
        | ObjectTypes.PathAddition -> "footpath_item"
        | ObjectTypes.PathBanner -> "footpath_banner"
        | ObjectTypes.ParkEntrance -> "park_entrance"
        | ObjectTypes.Water -> "water"
        | _ -> "other"

    let getSourceDirectoryName = function
        | SourceTypes.RCT2 -> "rct2"
        | SourceTypes.WW -> "rct2ww"
        | SourceTypes.TT -> "rct2tt"
        | _ -> "other"

    let getLanguageName = function
        | 0 -> "en-GB"
        | 1 -> "en-US"
        | 2 -> "fr-FR"
        | 3 -> "de-DE"
        | 4 -> "es-ES"
        | 5 -> "it-IT"
        | 6 -> "nl-NL"
        | 7 -> "sv-SE"
        | 8 -> "ja-JP"
        | 9 -> "ko-KR"
        | 10 -> "zh-CN"
        | 11 -> "zh-TW"
        | 13 -> "pt-BR"
        | i -> i.ToString()

    let getOriginalObjectIdString (hdr: ObjectDataHeader) =
        String.Format("{0:X8}|{1,-8}|{2:X8}", hdr.Flags, hdr.FileName, hdr.Checksum)

    let getAuthorsForSource source =
        match source with
        | SourceTypes.RCT2 ->
            [|"Chris Sawyer"; "Simon Foster"|]
        | SourceTypes.WW
        | SourceTypes.TT ->
            [|"Frontier Studios"|]
        | _ ->
            [||]

    let getObjectStrings (obj: ObjectData) (ourStrings: IDictionary<string, IDictionary<string, string>>) =
        let getStrings index =
            let stringEntries = obj.StringTable.Entries
            match ResizeArray.tryItem index stringEntries with
            | None -> dict []
            | Some stringEntry ->
                let strings =
                    stringEntry.Strings
                    |> Seq.mapi(fun i str ->
                        let lang = getLanguageName i
                        let decoded = Localisation.decodeStringFromRCT2 lang str.Data
                        (lang, decoded.Trim()))
                    |> Seq.filter(fun (_, str) ->
                        // Decide whether the string is useful
                        match str with
                        | "" -> false
                        | s when s.StartsWith("#not translated", StringComparison.OrdinalIgnoreCase) -> false
                        | _ -> true)
                    |> Seq.toArray

                strings
                |> Seq.filter(fun (x, y) -> x = getLanguageName 0 || y <> snd strings.[0])
                |> dict

        let theirStrings =
            let entries =
                if obj.Type = ObjectTypes.Attraction then
                    [| ("name", getStrings 0);
                       ("description", getStrings 1);
                       ("capacity", getStrings 2)|]
                else
                    [| ("name", getStrings 0) |]

            entries
            |> Array.filter(fun (_, y) -> y.Count > 0)
            |> dict

        // Overlay our strings on top of the RCT2 strings
        let strings = Localisation.overlayStrings ourStrings theirStrings

        // Remove unwanted strings
        let validKeys =
            match obj.Type with
            | ObjectTypes.Attraction -> ["name"; "description"; "capacity"]
            | _ -> ["name"]
        for s in Seq.toArray strings.Keys do
            if not (List.contains s validKeys) then
                strings.Remove(s) |> ignore

        // Remove empty string collections
        strings
        |> Seq.where (fun kvp -> kvp.Value.Count = 0)
        |> Seq.toArray
        |> Seq.iter (fun kvp -> strings.Remove(kvp.Key) |> ignore)

        // Return
        strings

    let packageIntoParkobj path =
        let parkobjPath = path + ".parkobj"
        if File.Exists(parkobjPath) then
            File.Delete(parkobjPath)
        System.IO.Compression.ZipFile.CreateFromDirectory(path, parkobjPath)
        Directory.Delete(path, true)

    let gxcBuild outputPath inputPath =
        try
            let psi = new ProcessStartInfo()
            psi.FileName <- "gxc"
            psi.ArgumentList.Add("build")
            psi.ArgumentList.Add(outputPath)
            psi.ArgumentList.Add(inputPath)
            psi.RedirectStandardOutput <- true
            let proc = System.Diagnostics.Process.Start(psi)
            proc.BeginOutputReadLine() // Prevents the process from hanging
            proc.WaitForExit()
        with
        | :? Win32Exception as ex when ex.NativeErrorCode = 2 ->
            "Unable to find gxc on PATH (pass --png to skip gxc)"
            |> printWithColour ConsoleColor.Red

    let buildGxFile outputPath images =
        let lgxFilename = "images.dat"
        let lgxPath = Path.Combine(outputPath, lgxFilename)
        sprintf "Building %s" lgxFilename
        |> printWithColour ConsoleColor.DarkGray
        let imagePngPath = Path.Combine(outputPath, "images.png")
        let numImages = List.length images
        let imageJsonPath = Path.Combine(outputPath, "images.json")
        serializeToJsonFile imageJsonPath images
        gxcBuild lgxPath imageJsonPath
        File.Delete(imagePngPath)
        File.Delete(imageJsonPath)
        [sprintf "$LGX:%s[0..%d]" lgxFilename (numImages - 1)]

    let exportParkObject outputPath (ourStrings: IDictionary<string, IDictionary<string, string>>) (inputPath: string) (obj: ObjectData) (options: ObjectExporterOptions) =
        let exportSubObject splitKind objId =
            let outputPath = Path.Combine(outputPath, objId)
            let objName = obj.ObjectHeader.FileName.ToUpper()
            let outputJsonPath = Path.Combine(outputPath, "object.json")

            sprintf "Exporting %s to %s" objName (Path.GetFullPath(outputJsonPath))
            |> printWithColour ConsoleColor.DarkGray

            if createDirectory outputPath then
                let images =
                    let println = printWithColour ConsoleColor.DarkGray
                    let numImages = obj.GraphicsData.NumImages
                    let ids =
                        match splitKind with
                        | Some FootpathSurface -> 71 :: [0..50]
                        | Some FootpathQueue -> 72 :: [51..70]
                        | Some FootpathRailings ->
                            let footpathObj = obj :?> Footpath
                            let flags = footpathObj.Header.Flags
                            let hasPoleSupports = flags.HasFlag(FootpathFlags.PoleSupports)
                            let hasSupportImages = flags.HasFlag(FootpathFlags.PoleBase)
                            let hasElevatedPathImages = flags.HasFlag(FootpathFlags.OverlayPath)
                            if hasPoleSupports then
                                if hasSupportImages then
                                    71 :: [73..164]
                                else
                                    71 :: [73..145]
                            else
                                71 :: [73..167]
                        | None -> [0..numImages - 1]
                    obj |> ImageExporter.exportImages ids outputPath println

                let images =
                    match options.storePng with
                    | true -> images :> obj
                    | false -> buildGxFile outputPath images

                let properties =
                    match splitKind with
                    | Some kind -> obj :?> Footpath |> getFootpathSplit kind
                    | None -> obj |> getProperties

                let objectType =
                    match splitKind with
                    | Some FootpathSurface
                    | Some FootpathQueue -> "footpath_surface"
                    | Some FootpathRailings -> "footpath_railings"
                    | None -> getObjTypeName obj.Type

                let authors =
                    match options.authors with
                    | [] -> getAuthorsForSource obj.Source
                    | items -> items |> List.toArray

                let originalId =
                    match splitKind with
                    | Some _ -> null
                    | None -> getOriginalObjectIdString obj.ObjectHeader

                let jobj = { id = objId
                             authors = authors
                             version = "1.0"
                             originalId = originalId
                             objectType = objectType
                             properties = properties
                             images = images
                             strings = getObjectStrings obj ourStrings }

                let json = serializeToJson jobj + Environment.NewLine
                File.WriteAllText(outputJsonPath, json, UTF8NoBOM)

                if options.outputParkobj then
                    packageIntoParkobj outputPath

        if obj.ObjectHeader.Type = ObjectTypes.Footpath && options.splitFootpaths then
            let (surfaceId, queueId, railingsId) =
                match options.id with
                | Some id ->
                    let surfaceId = id + ".surface"
                    let queueId = id + ".queue"
                    let railingsId = id + ".railings"
                    (surfaceId, queueId, railingsId)
                | None ->
                    let surfaceId = obj |> getObjId "footpath_surface" ""
                    let queueId = obj |> getObjId "footpath_surface" "queue"
                    let railingsId = obj |> getObjId "footpath_railings" ""
                    (surfaceId, queueId, railingsId)

            exportSubObject (Some FootpathSurface) surfaceId
            exportSubObject (Some FootpathQueue) queueId
            exportSubObject (Some FootpathRailings) railingsId
        else
            let id =
                match options.id with
                | Some id -> id
                | None -> obj |> getObjId "" ""
            exportSubObject None id

    let exportObject outputPath (ourStrings: IDictionary<string, IDictionary<string, string>>) (inputPath: string) (obj: ObjectData) =
        let getOutputJsonPath basePath (obj: ObjectData) name =
            Path.Combine(basePath,
                         getSourceDirectoryName obj.Source,
                         getObjTypeName obj.Type,
                         name + ".json")

        let inputFileName = Path.GetFileNameWithoutExtension(inputPath).ToUpper()
        let objName = obj.ObjectHeader.FileName.ToUpper()
        let objId = obj |> getObjId "" ""
        let outputJsonPath = getOutputJsonPath outputPath obj objId

        sprintf "Exporting %s to %s" objName (Path.GetFullPath(outputJsonPath))
        |> printWithColour ConsoleColor.DarkGray

        // Get RCT2 images
        let numImages = obj.ImageDirectory.NumEntries
        let images =
            match obj.Type with
            | ObjectTypes.Water -> null
            | _ -> [| sprintf "$RCT2:OBJDATA/%s.DAT[%d..%d]" inputFileName 0 (numImages - 1) |]

        let properties = getProperties obj
        let jobj = { id = objId
                     authors = getAuthorsForSource obj.Source
                     version = "1.0"
                     originalId = getOriginalObjectIdString obj.ObjectHeader
                     objectType = getObjTypeName obj.Type
                     properties = properties
                     images = images
                     strings = getObjectStrings obj ourStrings }

        let json = serializeToJson jobj + Environment.NewLine
        Directory.CreateDirectory(Path.GetDirectoryName(outputJsonPath)) |> ignore
        File.WriteAllText(outputJsonPath, json, UTF8NoBOM)

    type FileTypeResult = Directory | File

    let exportObjects path outputPath options =
        // Load new object strings from OpenRCT2
        let objectStrings =
            match options.languageDirectory with
            | None -> dict []
            | Some dir ->
                sprintf "Reading object strings from '%s'" dir
                |> printWithColour ConsoleColor.Cyan
                Localisation.getOpenObjectStrings dir

        let shouldObjectBeProcessed =
            match options.objectType with
            | None ->
                (fun _ -> true)
            | Some includeType ->
                (fun (obj: ObjectData) -> includeType = getObjTypeName obj.Type)

        let getOverrideStringsForObject (obj: ObjectData) =
            match objectStrings.TryGetValue obj.ObjectHeader.FileName with
            | true, value -> value
            | _ -> dict []

        let getFileType path =
            if File.GetAttributes(path).HasFlag(FileAttributes.Directory) then
                Some Directory
            elif File.Exists(path) then
                Some File
            else
                None

        let measureTime fn =
            let sw = Stopwatch.StartNew()
            let result = fn ()
            sw.Stop()
            (result, sw.Elapsed.TotalSeconds)

        let tryReadObjectData path =
            try
                ObjectData.FromFile(path) |> Some
            with
            | _ ->
                None

        let exportSingleObject () =
            if not (createDirectory outputPath) then
                printfn "'%s' does not exist" path
                1
            else
                sprintf "Exporting object from '%s' to '%s'" path outputPath
                |> printWithColour ConsoleColor.Cyan
                let (_, time) = measureTime (fun () ->
                    let obj = ObjectData.FromFile(path)
                    let strings = getOverrideStringsForObject obj
                    exportParkObject outputPath strings path obj options)
                sprintf "Object exported in %.1fs" time
                |> printWithColour ConsoleColor.Green
                0

        let exportAllObjects () =
            if not (createDirectory outputPath) then
                printfn "'%s' does not exist" path
                1
            else
                let processObject path (obj: ObjectData) =
                    if not (isNull obj) && obj.Type <> ObjectTypes.ScenarioText && shouldObjectBeProcessed obj then
                        let strings = getOverrideStringsForObject obj
                        exportObject outputPath strings path obj
                        Some ()
                    else
                        None

                let readAndProcessObject path =
                    match tryReadObjectData path with
                    | Some objdata ->
                        processObject path objdata
                    | None ->
                        sprintf "Unable to read object data for %s" path
                        |> printWithColour ConsoleColor.Red
                        None

                sprintf "Exporting objects from '%s' to '%s'" path outputPath
                |> printWithColour ConsoleColor.Cyan
                let (numObj, time) = measureTime (fun () ->
                    Directory.GetFiles(path)
                    |> Array.where (fun x -> x.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    |> match options.multithreaded with
                       | true -> Array.Parallel.choose readAndProcessObject
                       | false -> Array.choose readAndProcessObject
                    |> Array.length)
                sprintf "%d objects exported in %.1fs" numObj time
                |> printWithColour ConsoleColor.Green
                0

        match getFileType path with
        | Some File ->
            exportSingleObject ()
        | Some Directory ->
            exportAllObjects ()
        | _ ->
            printfn "'%s' does not exist" path
            1
