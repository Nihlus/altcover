﻿namespace AltCover

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression

open Mono.Cecil
open Mono.Options
open Augment

[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
type Tracer = { Tracer : string }

type MonoTypeBinder (``type``:Type) =
  inherit System.Runtime.Serialization.SerializationBinder()
  override self.BindToType (_:string, _:string) =
    ``type``

module Runner =

  let mutable internal recordingDirectory : Option<string> = None
  let mutable internal workingDirectory : Option<string> = None
  let mutable internal executable : Option<string> ref = ref None

  let internal DeclareOptions () =
    [ ("r|recorderDirectory=",
       (fun x -> if not (String.IsNullOrWhiteSpace(x)) && Directory.Exists(x) then
                    if Option.isSome recordingDirectory then
                      CommandLine.error <- true
                    else
                      recordingDirectory <- Some (Path.GetFullPath x)
                 else CommandLine.error <- true))
      ("w|workingDirectory=",
       (fun x -> if not (String.IsNullOrWhiteSpace(x)) && Directory.Exists(x) then
                    if Option.isSome workingDirectory then
                      CommandLine.error <- true
                    else
                      workingDirectory <- Some (Path.GetFullPath x)
                 else CommandLine.error <- true))
      ("x|executable=",
       (fun x -> if not (String.IsNullOrWhiteSpace(x)) then
                    if Option.isSome !executable then
                      CommandLine.error <- true
                    else
                      executable := Some x
                 else CommandLine.error <- true))
      ("?|help|h", (fun x -> CommandLine.help <- not (isNull x)))
      ("<>", (fun x -> CommandLine.error <- true))         ]// default end stop
      |> List.fold (fun (o:OptionSet) (p, a) -> o.Add(p, CommandLine.resources.GetString(p), new System.Action<string>(a))) (OptionSet())

  let HandleBadArguments arguments intro options1 options =
        String.Join (" ", arguments |> Seq.map (sprintf "%A"))
        |> CommandLine.WriteErr
        CommandLine.Usage intro options1 options

  let internal RequireExe (parse:(Either<string*OptionSet, string list*OptionSet>)) =
    match parse with
    | Right (l, options) -> match !executable with
                            | None -> Left ("UsageError", options)
                            | Some exe -> Right (exe::l, options)
    | fail -> fail

  let internal RequireRecorder (parse:(Either<string*OptionSet, string list*OptionSet>)) =
    match parse with
    | Right (_, options) -> match recordingDirectory with
                            | None -> Left ("UsageError", options)
                            | Some path -> let dll = Path.Combine (path, "AltCover.Recorder.g.dll")
                                           if File.Exists dll then parse
                                           else Left ("UsageError", options)
    | fail -> fail

  let internal RequireWorker (parse:(Either<string*OptionSet, string list*OptionSet>)) =
    match parse with
    | Right _ -> match workingDirectory with
                 | None -> workingDirectory <- Directory.GetCurrentDirectory() |> Some
                 | _ -> ()
                 parse
    | fail -> fail

  // mocking point
  let mutable internal RecorderName = "AltCover.Recorder.g.dll"

  let RecorderInstance () =
    let recorderPath = Path.Combine (Option.get recordingDirectory, RecorderName)
    let definition = AssemblyDefinition.ReadAssembly recorderPath
    definition.MainModule.GetType("AltCover.Recorder.Instance")

  let GetMethod (t:TypeDefinition) (name:string) =
    t.Methods
    |> Seq.filter (fun m -> m.Name = name)
    |> Seq.head

  let GetFirstOperandAsString (m:MethodDefinition) =
     m.Body.Instructions
     |> Seq.filter (fun i -> i.OpCode = Cil.OpCodes.Ldstr)
     |> Seq.map (fun i -> i.Operand :?> string)
     |> Seq.head

  let PayloadBase (rest:string list)  =
    CommandLine.doPathOperation (fun () ->
        CommandLine.ProcessTrailingArguments rest (DirectoryInfo(Option.get workingDirectory)))

  let WriteResource =
    CommandLine.resources.GetString >> Console.WriteLine

  let WriteResourceWithFormatItems s x =
    Console.WriteLine (s |> CommandLine.resources.GetString, x)

  let MonitorBase (hits:ICollection<(string*int)>) report (payload: string list -> unit) (args : string list) =
      let binpath = report + ".bin"
      do
        use stream = File.Create(binpath)
        ()

      "Beginning run..." |> WriteResource
      payload args
      "Getting results..."  |> WriteResource

      let formatter = System.Runtime.Serialization.Formatters.Binary.BinaryFormatter()
      formatter.Binder <- MonoTypeBinder(typeof<(string*int)>) // anything else is an error

      Directory.GetFiles( Path.GetDirectoryName(report),
                          Path.GetFileName(report) + ".*.bin")
      |> Seq.iter (fun f ->
          printfn "... %s" f
          use results = new DeflateStream(File.OpenRead f, CompressionMode.Decompress)
          let rec sink() =
            let hit = try formatter.Deserialize(results) :?> (string*int)
                      with | :? System.Runtime.Serialization.SerializationException as x -> (null, -1)
            if hit|> fst |> String.IsNullOrWhiteSpace  |> not then
              hit |> hits.Add
              sink()
          sink()
      )

      WriteResourceWithFormatItems "%d visits recorded" [|hits.Count|]

  let WriteReportBase (hits:ICollection<(string*int)>) report =
    let counts = Dictionary<string, Dictionary<int, int>>()
    hits |> Seq.iter(fun (moduleId, hitPointId) ->
                        AltCover.Base.Counter.AddVisit counts moduleId hitPointId)
    AltCover.Base.Counter.DoFlush true counts report

  // mocking points
  let mutable internal GetPayload = PayloadBase
  let mutable internal GetMonitor = MonitorBase
  let mutable internal DoReport = WriteReportBase

  let DoCoverage arguments options1 =
    let check1 = DeclareOptions ()
                 |> CommandLine.ParseCommandLine (arguments |> Array.skip 1)
                 |> CommandLine.ProcessHelpOption
                 |> RequireExe
                 |> RequireRecorder
                 |> RequireWorker
    match check1 with
    | Left (intro, options) -> HandleBadArguments arguments intro options1 options
    | Right (rest, _) ->
          let instance = RecorderInstance()
          let report = (GetMethod instance "get_ReportFile")
                       |> GetFirstOperandAsString
                       |> Path.GetFullPath
          let hits = List<(string*int)>()

          let payload = GetPayload
          GetMonitor hits report payload rest
          let delta = DoReport hits report
          WriteResourceWithFormatItems "Coverage statistics flushing took {0:N} seconds" [|delta.TotalSeconds|]

          // And tidy up after everything's done
          File.Delete (report + ".bin")
          Directory.GetFiles( Path.GetDirectoryName(report),
                              Path.GetFileName(report) + ".*.bin")
          |> Seq.iter File.Delete