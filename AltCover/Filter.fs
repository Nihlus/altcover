﻿namespace AltCover

open System
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Runtime.CompilerServices
open System.Text.RegularExpressions

open Mono.Cecil
open AltCover.Augment

[<ExcludeFromCodeCoverage>]
type internal FilterClass =
  | File of Regex
  | Assembly of Regex
  | Module of Regex
  | Type of Regex
  | Method of Regex
  | Attribute of Regex

module Filter =

  let private MatchAttribute (name:Regex) (nameProvider:Object) =
    match nameProvider with
                        | :? ICustomAttributeProvider as attributeProvider ->
                            attributeProvider.HasCustomAttributes &&
                                 attributeProvider.CustomAttributes
                                 |> Seq.cast<CustomAttribute>
                                 |> Seq.exists (fun attr ->
                                    name.IsMatch attr.Constructor.DeclaringType.FullName)
                        | _ -> false

  let MatchItem<'a>  (name:Regex) (nameProvider:Object) (toName: 'a -> string) =
    match nameProvider with
    | :? 'a as item -> item |> toName |> name.IsMatch
    | _ -> false

  let internal Match (nameProvider:Object) (filter:FilterClass) =
    match filter with
    | File name -> MatchItem<string> name nameProvider Path.GetFileName
    | Assembly name -> MatchItem<AssemblyDefinition> name nameProvider (fun assembly -> assembly.Name.Name)
    | Module name -> MatchItem<ModuleDefinition> name nameProvider (fun ``module`` -> ``module``.Assembly.Name.Name)
    | Type name -> MatchItem<TypeDefinition> name nameProvider (fun typeDef -> typeDef.FullName)
    | Method name -> MatchItem<MethodDefinition> name nameProvider (fun methodDef -> methodDef.Name)
    | Attribute name -> MatchAttribute name nameProvider

  let internal IsCSharpAutoProperty (m:MethodDefinition) =
      (m.IsSetter || m.IsGetter) && m.HasCustomAttributes &&
        m.CustomAttributes
        |> Seq.exists (fun x -> x.AttributeType.FullName = typeof<CompilerGeneratedAttribute>.FullName)

  let internal IsFSharpInternal (m:MethodDefinition) =

    // Discriminated Union/Sum/Algebraic data types are implemented as
    // subtypes nested in the base type
    // Algebraic types have debug proxies nested in the base type which are not attributed at the type level
    let baseType = Option.nullable m.DeclaringType.DeclaringType
                  |> Option.filter (fun t -> t.HasCustomAttributes)
                  |> Option.map (fun t -> t.CustomAttributes :> seq<CustomAttribute>)
                  |> Option.filter (Seq.isEmpty >> not)
                  |> Option.getOrElse Seq.empty<CustomAttribute>

    let thisType = Some m.DeclaringType
                  |> Option.filter (fun t -> t.HasCustomAttributes)
                  |> Option.map (fun t -> t.CustomAttributes :> seq<CustomAttribute>)
                  |> Option.filter (Seq.isEmpty >> not)
                  |> Option.getOrElse Seq.empty<CustomAttribute>

    // Use string literals since Mono doesn't return a Type
    let mappings = Seq.concat [baseType; thisType]
                   |> Seq.filter (fun x -> x.AttributeType.FullName = "Microsoft.FSharp.Core.CompilationMappingAttribute")
                   |> Seq.exists (fun x -> let arg1 = Enum.ToObject(typeof<SourceConstructFlags>, x.GetBlob() |> Seq.skip 2 |> Seq.head)   // (x.ConstructorArguments |> Seq.head).Value
                                           match (arg1 :?> SourceConstructFlags) &&& SourceConstructFlags.KindMask with
                                           | SourceConstructFlags.SumType
                                           | SourceConstructFlags.RecordType -> true
                                           | _ -> false)

    // record type has getters marked as field
    let fieldGetter = match m.IsGetter with
                      | false -> false
                      | _ -> let owner = m.DeclaringType.Properties
                                         |> Seq.filter (fun x -> x.GetMethod = m)
                                         |> Seq.head
                             if owner.HasCustomAttributes then
                                owner.CustomAttributes
                                |> Seq.filter (fun x -> x.AttributeType.FullName = "Microsoft.FSharp.Core.CompilationMappingAttribute")
                                |> Seq.exists (fun x -> let arg1 = Enum.ToObject(typeof<SourceConstructFlags>, x.GetBlob() |> Seq.skip 2 |> Seq.head)   // (x.ConstructorArguments |> Seq.head).Value
                                                        (arg1 :?> SourceConstructFlags) &&& SourceConstructFlags.KindMask = SourceConstructFlags.Field)
                             else false

    mappings &&
      (fieldGetter || m.IsConstructor ||
         (m.HasCustomAttributes && m.CustomAttributes
                                   |> Seq.exists (fun x -> let fullName = x.AttributeType.FullName
                                                           fullName = typeof<CompilerGeneratedAttribute>.FullName ||
                                                           fullName = typeof<DebuggerNonUserCodeAttribute>.FullName ||
                                                           fullName = typeof<CompilationMappingAttribute>.FullName)))