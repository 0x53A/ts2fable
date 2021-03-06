module rec ts2fable.Transform

open Fable.Core
open Fable.Core.JsInterop
open TypeScript
open TypeScript.Ts
open System.Collections.Generic
open System
open ts2fable.Naming
open System.Collections
open System.Collections.Generic
open ts2fable.Syntax
open ts2fable.Keywords
open Fable

let getAllTypesFromFile fsFile =
    let tps = List []
    fsFile
    |> fixFile (fun tp -> 
        tp |> tps.Add
        tp
    ) |> ignore
    tps |> List.ofSeq

let getAllTypes fsFiles =
    fsFiles |> List.collect getAllTypesFromFile  

/// recursively fix all the FsType childen and allow the caller to decide how deep to recurse.
let rec fixTypeEx (doFix:FsType->bool) (fix: FsType -> FsType) (tp: FsType): FsType =
    
    let fixType = fixTypeEx doFix

    let fixModule (a: FsModule): FsModule =
        { a with
            Types = a.Types |> List.map (fixType fix)
        }

    let fixFile (f: FsFile): FsFile =
        { f with
            Modules = f.Modules |> List.map fixModule
        }

    let fixParam (a: FsParam): FsParam =
        let b =
            { a with
                Type = fixType fix a.Type
            }
            |> FsType.Param |> fix
        match b with
        | FsType.Param c -> c
        | _ -> failwithf "param must be mapped to param"

    // fix children first, then current type
    match tp with
    | tp when (not (doFix tp)) -> tp
    | FsType.Interface it ->
        { it with
            TypeParameters = it.TypeParameters |> List.map (fixType fix)
            Inherits = it.Inherits |> List.map (fixType fix)
            Members = it.Members |> List.map (fixType fix)
        }
        |> FsType.Interface
    | FsType.TypeLiteral tl ->
        { tl with
            Members = tl.Members |> List.map (fixType fix)
        }
        |> FsType.TypeLiteral
    | FsType.Property pr ->
        { pr with
            Index = Option.map fixParam pr.Index
            Type = fixType fix pr.Type
        }
        |> FsType.Property 
    | FsType.Param pr ->
        { pr with
            Type = fixType fix pr.Type
        }
        |> FsType.Param
    | FsType.Array ar ->
        fixType fix ar |> FsType.Array
    | FsType.Function fn ->
        { fn with
            TypeParameters = fn.TypeParameters |> List.map (fixType fix)
            Params = fn.Params |> List.map fixParam
            ReturnType = fixType fix fn.ReturnType
        }
        |> FsType.Function
    | FsType.Union un ->
        { un with
            Types = un.Types |> List.map (fixType fix)
        }
        |> FsType.Union
    | FsType.Alias al ->
        { al with
            Type = fixType fix al.Type
            TypeParameters = al.TypeParameters |> List.map (fixType fix)
        }
        |> FsType.Alias
    | FsType.Generic gn ->
        { gn with
            Type = fixType fix gn.Type
            TypeParameters = gn.TypeParameters |> List.map (fixType fix)
        }
        |> FsType.Generic
    | FsType.Tuple tp ->
        { tp with
            Types = tp.Types |> List.map (fixType fix)
        }
        |> FsType.Tuple
    | FsType.Module md ->
        fixModule md
        |> FsType.Module
     | FsType.File f ->
        fixFile f
        |> FsType.File
    | FsType.FileOut fo ->
        { fo with
            Files = fo.Files |> List.map fixFile
        }
        |> FsType.FileOut
    | FsType.Variable vb ->
        { vb with
            Type = fixType fix vb.Type
        }
        |> FsType.Variable

    | FsType.ExportAssignment _ -> tp
    | FsType.Enum _ -> tp
    | FsType.Mapped _ -> tp
    | FsType.None _ -> tp
    | FsType.TODO _ -> tp
    | FsType.StringLiteral _ -> tp
    | FsType.This -> tp
    | FsType.Import _ -> tp
    | FsType.GenericParameterDefaults gpd -> 
        { gpd with Default = fixType fix gpd.Default }
        |> FsType.GenericParameterDefaults
    |> fun t -> if doFix(t) then fix(t) else t // current type

/// recursively fix all the FsType childen
let fixType (fix: FsType -> FsType) (tp: FsType): FsType = fixTypeEx (fun _ -> true) fix tp

/// recursively fix all the FsType childen for the given FsFile and allow the caller to decide how deep to recurse.
let fixFileEx (doFix:FsType->bool) (fix: FsType -> FsType) (f: FsFile): FsFile =

    { f with
        Modules = 
            f.Modules 
            |> List.map FsType.Module 
            |> List.map (fixTypeEx doFix fix) 
            |> List.choose FsType.asModule
    }

/// recursively fix all the FsType childen for the given FsFile
let fixFile (fix: FsType -> FsType) (f: FsFile): FsFile = fixFileEx (fun _ -> true) fix f

let mergeTypes(tps: FsType list): FsType list =
    let index = Dictionary<string,int>()
    let list = List<FsType>()
    for b in tps do
        match b with
        | FsType.Interface bi ->
            if index.ContainsKey bi.Name then
                let i = index.[bi.Name]
                let a = list.[i]
                match a with
                | FsType.Interface ai ->
                    list.[i] <-
                        { ai with
                            Inherits = List.append ai.Inherits bi.Inherits |> List.distinct
                            Members = List.append ai.Members bi.Members
                        }
                        |> FsType.Interface
                | _ -> ()

            else
                list.Add b
                index.Add(bi.Name, list.Count-1)
        | _ -> 
            list.Add b
    list |> List.ofSeq

let mergeModules(tps: FsType list): FsType list =
    let index = Dictionary<string,int>()
    let list = List<FsType>()

    for tp in tps do
        match tp with
        | FsType.Module md ->
            let md2 =
                { md with
                    Types = md.Types |> mergeTypes |> mergeModules // submodules
                }
            
            if index.ContainsKey md.Name then
                let i = index.[md.Name]
                let a = (list.[i] |> FsType.asModule).Value
                list.[i] <-
                    { a with
                        Types = a.Types @ md2.Types |> mergeTypes |> mergeModules
                    }
                    |> FsType.Module
            else
                md2 |> FsType.Module |> list.Add |> ignore
                index.Add(md2.Name, list.Count-1)
        | _ -> list.Add tp |> ignore
    
    list |> List.ofSeq

let mergeModulesInFile (f: FsFile): FsFile =
    { f with
        Modules = 
            f.Modules
            |> List.ofSeq
            |> List.map FsType.Module
            |> mergeModules
            |> List.choose FsType.asModule
    }

let engines = ["node"; "vscode"] |> Set.ofList

let rec createIExportsModule (ns: string list) (md: FsModule): FsModule * FsVariable list =
    printfn "createIExportsModule %A, %s" ns md.Name
    let typesInIExports = ResizeArray<FsType>()
    let typesGlobal = ResizeArray<FsType>()
    let typesChild = ResizeArray<FsType>()
    let typesChildExport = ResizeArray<FsType>()
    let typesOther = ResizeArray<FsType>()
    let variablesForParent = ResizeArray<FsVariable>()
    
    let variables = HashSet<FsVariable>()
    let exportAssignments = HashSet<string>()
    md.Types |> List.iter(fun tp ->
        match tp with
        | FsType.ExportAssignment ea ->
            exportAssignments.Add ea |> ignore
        | _ -> ()
    )

    md.Types |> List.iter(fun tp ->
        match tp with
        | FsType.Module smd ->
            let ns = 
                if md.Name = "" then ns 
                else 
                    let parts =
                        let name = md.Name.Replace("'","")
                        match name with 
                        | ModuleName.Normal -> [name]
                        | ModuleName.Parts parts -> parts |> List.filter((<>) ".")
                    ns @ parts
                    
            let smd, vars = createIExportsModule ns smd
            for v in vars do
                if v.Export.IsSome then v |> variables.Add |> ignore
                else v |> FsType.Variable |> typesChild.Add
            smd |> FsType.Module |> typesOther.Add
        | FsType.Variable vb ->
            if vb.HasDeclare then
                if md.Name = "" then
                    { vb with
                        Export = 
                            { IsGlobal = engines.Contains ns.[0]
                              Selector = 
                                if String.Compare(vb.Name,ns.[0],true) = 0 then "*"
                                else vb.Name
                              Path = ns.[0] } |> Some
                    }
                    |> FsType.Variable
                    |> typesGlobal.Add
                else
                    if vb.IsGlobal then
                        typesGlobal.Add tp
                    else 
                        typesInIExports.Add tp
            else
                typesInIExports.Add tp
        | FsType.Function _ -> typesInIExports.Add tp
        | FsType.Interface it ->
            if it.IsStatic then
                // add a property for accessing the static class
                {
                    Comments = []
                    Kind = FsPropertyKind.Regular
                    Index = None
                    Name = it.Name.Replace("Static","")
                    Option = false
                    Type = it.Name |> simpleType
                    IsReadonly = true
                    IsStatic = false
                    Accessibility = None
                }
                |> FsType.Property
                |> typesInIExports.Add
            typesOther.Add tp
        | _ -> typesOther.Add tp
    )

    let ns = if engines.Contains ns.[0] then ns.[1..] else ns
    let selector =
        if ns.Length = 0 then "*"
        else md.Name.Replace("'","")
    let path =
        if ns.Length = 0 then 
            md.Name.Replace("'","")
        else ns |> String.concat "/"

    if typesInIExports.Count > 0 then

        // Some JS names are all uppercase which looks really odd if we just lowercase the first letter
        let name =
            if md.Name |> Seq.forall Char.IsUpper then
                md.Name.ToLower()
            else
                md.Name |> lowerFirst

        if md.HasDeclare then
            if not <| md.IsNamespace then
                {
                    Export = { IsGlobal = false; Selector = "*"; Path = path } |> Some
                    HasDeclare = true
                    Name = name
                    Type = sprintf "%s.IExports" (fixModuleName md.Name) |> simpleType
                    IsConst = true
                    IsStatic = false
                    Accessibility = None
                }
                |> variablesForParent.Add
        else
            {
                Export = { IsGlobal = false; Selector = selector; Path = path } |> Some
                HasDeclare = true
                Name = name
                Type = sprintf "%s.IExports" (fixModuleName md.Name) |> simpleType
                IsConst = true
                IsStatic = false
                Accessibility = None
            }
            |> variablesForParent.Add

    let iexports =
        if typesInIExports.Count = 0 then []
        else
            [
                {
                    Comments = []
                    IsStatic = false
                    IsClass = false
                    Name = "IExports"
                    FullName = "IExports"
                    Inherits = []
                    TypeParameters = []
                    Members =
                        (typesInIExports |> List.ofSeq)
                    Accessibility = None
                }
                |> FsType.Interface
            ]

    // add exports assignments
    // make sure there are no conflicting globals already
    let globalNames = typesGlobal |> Seq.map getName |> Set.ofSeq
    
    md.Types |> List.iter(fun tp ->
        match tp with
        | FsType.Module smd ->
            if not <| globalNames.Contains smd.Name && exportAssignments.Contains smd.Name then
                {
                    Export = { IsGlobal = false; Selector = "*"; Path = path } |> Some
                    HasDeclare = true
                    Name = smd.Name |> lowerFirst
                    Type = sprintf "%s.IExports" (fixModuleName smd.Name) |> simpleType
                    IsConst = true
                    IsStatic = false
                    Accessibility = None
                }
                |> variables.Add |> ignore
        | _ -> ()
    )

    let newMd =
        { md with
            Types =
                (variables |> List.ofSeq |> List.map FsType.Variable)
                @ (typesGlobal |> List.ofSeq)
                @ (typesChildExport |> List.ofSeq)
                @ (typesChild |> List.ofSeq)
                @ iexports
                @ (typesOther |> List.ofSeq)
        }

    newMd, variablesForParent |> List.ofSeq

let createIExports (f: FsFile): FsFile =
    
    { f with
        Modules = 
            f.Modules
            |> List.ofSeq
            |> List.map (fun md ->
                let md, _ = createIExportsModule [f.ModuleName] md
                md
            )
    }

let fixTic (typeParameters: FsType list) (tp: FsType) =
    if typeParameters.Length = 0 then
        tp
    else
        let set = typeParameters |> Set.ofList
        let fix (t: FsType): FsType =
            match t with
            | FsType.Mapped mp ->
                if set.Contains t then
                    { mp with Name = sprintf "'%s" mp.Name } |> FsType.Mapped
                else t
            | _ -> t
        fixType fix tp

let addTicForGenericFunctions(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Function fn ->
            fixTic fn.TypeParameters tp
        | _ -> tp
    )

// https://github.com/Microsoft/TypeScript/blob/master/doc/spec.md#18-overloading-on-string-parameters
let fixOverloadingOnStringParameters(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Function fn ->
            if fn.HasStringLiteralParams then
                let kind = ResizeArray()
                let name = ResizeArray()
                let prms = ResizeArray()
                sprintf "$0.%s(" fn.Name.Value |> kind.Add
                sprintf "%s" fn.Name.Value |> name.Add
                let slCount = ref 0
                fn.Params |> List.iteri (fun i prm ->
                    match FsType.asStringLiteral prm.Type with
                    | None ->
                        sprintf "$%d" (i + 1 - !slCount) |> kind.Add
                        prms.Add prm
                    | Some sl ->
                        incr slCount
                        sprintf "'%s'" sl |> kind.Add
                        sprintf "_%s" sl |> name.Add
                    if i < fn.Params.Length - 1 then
                        "," |> kind.Add
                )
                ")" |> kind.Add
                { fn with
                    Kind = String.concat "" kind |> FsFunctionKind.StringParam
                    Name = String.concat "" name |> Some
                    Params = List.ofSeq prms
                }
                |> FsType.Function
            else tp
        | _ -> tp
    )

let fixNodeArray(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Generic gn ->
            match gn.Type with
            | FsType.Mapped mp ->
                if mp.Name.Equals "NodeArray" && gn.TypeParameters.Length = 1 then
                    gn.TypeParameters.[0] |> FsType.Array
                else tp
            | _ -> tp
        | _ -> tp
    )

let fixReadonlyArray(f: FsFile): FsFile =
    let fix (tp: FsType): FsType =
        match tp with
        | FsType.Generic gn ->
            match gn.Type with
            | FsType.Mapped mp ->
                if mp.Name.Equals "ReadonlyArray" && gn.TypeParameters.Length = 1 then
                    gn.TypeParameters.[0] |> FsType.Array
                else tp
            | _ -> tp
        | _ -> tp

    // only replace in functions
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Function _ -> fixType fix tp
        | _ -> tp
    )

let fixEscapeWords(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Mapped mp ->
            { mp with Name = escapeWord mp.Name } |> FsType.Mapped
        | FsType.Param pm ->
            { pm with Name = escapeWord pm.Name } |> FsType.Param
        | FsType.Function fn ->
            { fn with Name = fn.Name |> Option.map escapeWord } |> FsType.Function
        | FsType.Property pr ->
            { pr with Name = escapeWord pr.Name } |> FsType.Property
        | FsType.Interface it ->
            { it with Name = escapeWord it.Name } |> FsType.Interface
        | FsType.Module md ->
            { md with Name = fixModuleName md.Name } |> FsType.Module
        | FsType.Variable vb ->
            { vb with Name = escapeWord vb.Name } |> FsType.Variable
        | FsType.Alias al ->
            { al with Name = escapeWord al.Name } |> FsType.Alias
        | _ -> tp
    )

let fixDateTime(f: FsFile): FsFile =
    let replaceName name =
        if String.Equals("Date", name) then "DateTime" else name

    f |> fixFile (fun tp ->
        match tp with
        | FsType.Mapped mp ->
            { mp with Name = replaceName mp.Name } |> FsType.Mapped
        | _ -> tp
    )

let fixEnumReferences (f: FsFile): FsFile =
    // get a list of enum names
    let list = List<string>()
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Enum en ->
            list.Add en.Name |> ignore
            tp
        | _ -> tp
    ) |> ignore

    // use those as the references
    let set = Set.ofSeq list
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Mapped mp ->
            if mp.Name.Contains "." then
                let nm = mp.Name.Substring(0, mp.Name.IndexOf ".")
                if set.Contains nm then
                    // { mp with Name = nm } |> FsType.Mapped
                    simpleType nm
                else tp
            else tp
        | _ -> tp
    )

let fixDuplicatesInUnion (f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Union un ->
            let set = HashSet<_>()
            let tps = un.Types |> List.choose (fun tp -> 
                    if set.Contains tp then
                        None
                    else
                        set.Add tp |> ignore
                        Some tp
                )
            if tps.Length > 8 then
                // printfn "union has %d types, > 8, so setting as obj %A" tps.Length un
                simpleType "obj"
            else 
                { un with Types = tps } |> FsType.Union
        | _ -> tp
    )

let addTicForGenericTypes(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface it -> fixTic it.TypeParameters tp
        | FsType.Alias al -> fixTic al.TypeParameters tp
        | _ -> tp
    )

/// replaces `this` with a reference to the interface type
let fixThis(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface it ->

            let replaceThis tp = 
                match tp with
                | FsType.This ->
                    {
                        Type = simpleType it.Name
                        TypeParameters = it.TypeParameters
                    }
                    |> FsType.Generic
                | _ -> tp

            { it with
                Members = it.Members |> List.map (fixType replaceThis)
            }
            |> FsType.Interface
        | _ -> tp
    )

let fixStatic(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Module md ->
            { md with
                Types = md.Types |> List.collect (fun tp2 ->
                    match tp2 with
                    | FsType.Interface it ->
                        if it.HasStaticMembers then
                            [
                                { it with
                                    Members = it.NonStaticMembers
                                }
                                |> FsType.Interface

                                { it with
                                    IsStatic = true
                                    Name = sprintf "%sStatic" it.Name
                                    // TypeParameters = [] // remove them after the tic is added
                                    Inherits = []
                                    Members = it.StaticMembers
                                }
                                |> FsType.Interface
                            ]
                        else
                            [tp2]
                    | _ -> [tp2]
                )
            }
            |> FsType.Module
        | _ -> tp
    )
let hasTodo (tp: FsType) =
    let mutable has = false
    tp |> fixType (fun t ->
        match t with
        | FsType.TODO ->
            has <- true
            t
        | _ -> t
    ) |> ignore
    has

let removeTodoMembers(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface it ->
            { it with
                // Members = it.Members |> List.filter (not << hasTodo)
                Members = it.Members |> List.filter (fun mb ->
                    if hasTodo mb then
                        printfn "removing member with TODO: %s.%s" (getName tp) (getName mb)
                        false
                    else true
                )
            }
            |> FsType.Interface
        | _ -> tp
    )

let removeTypeParamsFromStatic(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface it ->
            { it with
                TypeParameters =
                    if it.IsStatic then [] else it.TypeParameters
            }
            |> FsType.Interface
        | _ -> tp
    )

let addConstructors  (f: FsFile): FsFile =
    // we are importing classes as interfaces
    // we need of list of classes with constructors
    let list = List<_>()
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface it ->
            if it.IsClass && it.HasConstructor then
                list.Add it |> ignore
                tp
            else tp
        | _ -> tp
    ) |> ignore

    let map = list |> Seq.map(fun it -> it.FullName, it) |> dict

    // use those as the references
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface it ->
            if it.IsClass then
                if it.HasConstructor then
                    tp
                else
                    // see if base type has constructors
                    let parent =
                        it.Inherits |> List.tryPick (fun inh ->
                            let fn = getFullName inh
                            if map.ContainsKey fn then
                                Some map.[fn]
                                else None
                        )

                    match parent with
                    | Some pt -> 
                        // copy the constructors from the parent
                        { it with Members = pt.Constructors @ it.Members } |> FsType.Interface

                    | None ->
                        let defaultCtr =
                            {
                                Comments = []
                                Kind = FsFunctionKind.Constructor
                                IsStatic = true
                                Name = Some "Create"
                                TypeParameters = it.TypeParameters
                                Params = []
                                ReturnType = FsType.This
                                Accessibility = None
                            }
                            |> FsType.Function

                        { it with Members = [defaultCtr] @ it.Members } |> FsType.Interface

            else tp
        | _ -> tp
    )

let removeInternalModules(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Module md ->
            { md with
                Types = md.Types |> List.collect (fun tp ->
                    match tp with
                    | FsType.Module smd when smd.Name = "internal" ->
                        smd.Types
                    | _ -> [tp]
                )
            }
            |> FsType.Module
        | _ -> tp
    )

let removePrivatesFromClasses(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface c when c.IsClass ->
            { c with
                Members = c.Members |> List.filter (fun m -> getAccessibility m <> Some FsAccessibility.Private)
            }
            |> FsType.Interface
        | _ -> tp
    )

let removeDuplicateFunctions(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Interface it ->
            let set = HashSet<_>()
            { it with
                Members = it.Members |> List.collect (fun mb ->
                    match mb with
                    | FsType.Function fn ->
                        // compare without comments or a return type
                        let fn2 = { fn with Comments = []; ReturnType = FsType.None }
                        if set.Add fn2 then
                            [mb]
                        else
                            // printfn "mb is duplicate %A" mb
                            []
                    | _ -> [mb]
                )
            }
            |> FsType.Interface
        | _ -> tp
    )

let removeDuplicateOptions(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Property pr when pr.Option ->
            match pr.Type with 
            | FsType.Union un when un.Option ->
                { pr with 
                    Type = { un  with Option = false } |> FsType.Union 
                }
                |> FsType.Property
            | _ -> tp

        | _ -> tp
    )

/// this converts `(?x : int option)` to `(?x : int)`
let removeDuplicateOptionsFromParameters(f: FsFile): FsFile =

    // consider two TS cases:
    //   1) (?p : int | null)
    //   2) type Nullable<T> = T | null
    //      (?p : Nullable<int>)

    let allTypes = lazy (getAllTypesFromFile f |> List.toArray)

    let rec isAliasToOption (name: string) =
        let typFromName = allTypes.Value |> Seq.tryFind (fun t ->
            match t with
            | FsType.Alias { Name = aliasName }
                when (aliasName = name) -> true
            | _ -> false
        )
        match typFromName with
        // simple: the alias is itself a union with option
        | Some (FsType.Alias { Type = FsType.Union { Option = true; Types = [ t ] }; TypeParameters = [ t2 ] })
            when (t = t2) -> true
        // here we could check if the alias is another alias to option, but that seems like an unlikely pattern
        | _ -> false
        

    f |> fixFile (fun tp ->

        match tp with
        | FsType.Param pr when pr.Optional ->

            match pr.Type with 
            // case 1: simple
            | FsType.Union { Option = true; Types = [ t ] } -> { pr with Type = t } |> FsType.Param
            // not tested: I assume this is hit with (?p : int | string | null)
            | FsType.Union un when un.Option ->
                { pr with Type = { un  with Option = false } |> FsType.Union } |> FsType.Param
            // case 2: alias
            | FsType.Generic { Type = FsType.Mapped { Name = name; FullName = "" }; TypeParameters = [ t ] }
                when (isAliasToOption name) ->
                    { pr with Type = t } |> FsType.Param
                
            | _ -> tp

        | _ -> tp
    )

let extractTypeLiterals(f: FsFile): FsFile =
    
    /// the goal is to create interface types with 'pretty' names like '$(Class)$(Method)Return'.
    let extractTypeLiterals_pass1 (f: FsFile): FsFile =
      f |> fixFile (fun tp ->
        match tp with
        | FsType.Module md ->

            let typeNames =
                md.Types
                |> List.map getName
                |> HashSet<_>

            // append an underscores until a unique name is created
            let rec newTypeName (name: string): string =
                let name = capitalize name
                if typeNames.Contains name then
                    let name = sprintf "%s_" name
                    if typeNames.Contains name then
                        newTypeName name
                    else
                        typeNames.Add name |> ignore
                        name
                else
                    typeNames.Add name |> ignore
                    name

            { md with
                Types = md.Types |> List.collect (fun tp ->
                    match tp with
                    | FsType.Interface it ->

                        let newTypes = List<FsType>()
                        let materializeInterfaceType name members =
                            let materialized = {
                                Comments = []
                                IsStatic = false
                                IsClass = false
                                Name = name
                                FullName = name
                                Inherits = []
                                Members = members
                                TypeParameters = []
                                Accessibility = None
                            }
                            newTypes.Add (FsType.Interface materialized)


                        let it2 =
                            { it with
                                Members = it.Members |> List.map (fun mb ->
                                    match mb with
                                    | FsType.Function fn ->
                                        let mapParam (prm:FsParam) : FsParam =
                                            match prm.Type with
                                            | FsType.TypeLiteral tl ->
                                                let name =
                                                    let itName = if it.Name = "IExports" then "" else it.Name.Replace("`","")
                                                    let fnName = fn.Name.Value.Replace("`","")
                                                    let pmName = prm.Name.Replace("`","")
                                                    if fnName = "Create" then
                                                        sprintf "%s%s" itName (capitalize pmName) |> newTypeName
                                                    else if fnName = pmName then
                                                        sprintf "%s%s" itName (capitalize pmName) |> newTypeName
                                                    else
                                                        sprintf "%s%s%s" itName (capitalize fnName) (capitalize pmName) |> newTypeName
                                                
                                                materializeInterfaceType name tl.Members
                                                { prm with Type = simpleType name }
                                            | _ -> prm
                                        
                                        let mapReturnType (tp:FsType) =
                                            match tp with
                                            | FsType.TypeLiteral tl ->
                                                let name =
                                                    let itName = if it.Name = "IExports" then "" else it.Name.Replace("`","")
                                                    let fnName = fn.Name.Value.Replace("`","")
                                                    sprintf "%s%sReturn" itName (capitalize fnName) |> newTypeName
                                                
                                                materializeInterfaceType name tl.Members
                                                simpleType name
                                            | _ -> tp
                                        { fn with
                                            Params = fn.Params |> List.map mapParam
                                            ReturnType = fn.ReturnType |> mapReturnType
                                        }
                                        |> FsType.Function
                                    | _ -> mb
                                )
                            }
                            |> FsType.Interface

                        [it2] @ (List.ofSeq newTypes) // append new types
                    | FsType.Alias al -> 
                        match al.Type with 
                        | FsType.Union un -> 
                            let un2 = 
                                { un with 
                                    Types = 
                                        let tps = List<FsType>()
                                        un.Types |> List.iter(fun tp -> 
                                            match tp with 
                                            | FsType.TypeLiteral tl -> tl.Members |> tps.AddRange
                                            | _ -> tp |> tps.Add
                                        )
                                        tps |> List.ofSeq    
                                }
                            {al with Type = un2 |> FsType.Union} |> FsType.Alias |> List.singleton 
                        | FsType.TypeLiteral tl -> 
                            {
                                Comments = []
                                IsStatic = false
                                IsClass = false
                                Name = al.Name
                                FullName = al.Name
                                Inherits = []
                                Members = tl.Members
                                TypeParameters = al.TypeParameters
                                Accessibility = None
                            } |> FsType.Interface |> List.singleton                                
                        | _ -> [tp]                       
                    | _ -> [tp]
                )
            }
            |> FsType.Module
        | _ -> tp
    )
    
    /// type literals can occur in many places, and it is kinda hard to account for all of them in the first pass.
    /// so do a second pass, and just replace them with interfaces with a not quite so pretty name.
    /// Note: in an ideal world with enough time, this pass would not find anything, and all TLs would be accounted for in the first pass with pretty names.
    let extractTypeLiterals_pass2 (f: FsFile): FsFile =
        let mutable i = 1 // the name of the type literal ("TypeLiteral_%02i"): use one counter over all modules.
        let extractFromModule (m:FsModule) : FsModule =

            let fixModuleEx doFix fix (m:FsModule) : FsModule =
                match fixTypeEx doFix fix (FsType.Module m) with
                | FsType.Module m2 -> m2
                | x -> failwithf "Impossible: %A" x

            /// fixup the contents of one module, but do not go into sub-modules
            let fixOneModule fix m =
                 m |> fixModuleEx (function FsType.Module m2 when (m2 <> m) -> false | _ -> true) fix

            let replacedTypeLiterals = Dictionary<FsTypeLiteral, FsInterface>()
            let replaceLiteral (tl:FsTypeLiteral) : FsInterface =
                let build() =
                    let name = sprintf "TypeLiteral_%02i" i
                    i <- i + 1
                    let generics = HashSet<FsType>()
                    FsType.TypeLiteral tl |> fixType (fun t ->
                        match t with
                        // REVIEW: better detection for generics?
                        | FsType.Mapped({Name = name}) when (name.StartsWith "'") ->
                            generics.Add t |> ignore
                            t
                        | _ -> t
                    ) |> ignore

                    let extractedInterface = {
                        Comments = []
                        IsStatic = false
                        IsClass = false
                        Name = name
                        FullName = name
                        Inherits = []
                        Members = tl.Members
                        TypeParameters = generics |> Seq.toList
                        Accessibility = None
                    }
                    extractedInterface
                match replacedTypeLiterals.TryGetValue(tl) with
                | true, i -> i
                | false, _ ->
                    let i = build()
                    replacedTypeLiterals.[tl] <- i
                    i

            m
            // 1: replace occurences of TypeLiterals with references to the generated types
            |> fixOneModule (fun tp ->
                match tp with
                | FsType.TypeLiteral tl ->

                    let extractedInterface = replaceLiteral tl
                    match extractedInterface.TypeParameters with
                    | [ ] ->
                        simpleType (extractedInterface.Name)
                    | tp -> FsType.Generic({ Type = simpleType (extractedInterface.Name); TypeParameters = tp })
                | _ -> tp
            )
            // 2: append the generated types to the module
            |> (fun m ->
                let generatedTypes = replacedTypeLiterals |> Seq.map (fun kv -> FsType.Interface kv.Value) |> Seq.toList
                { m with Types = m.Types @ generatedTypes }
            )
        
        f |> fixFile (fun t -> match t with FsType.Module m -> FsType.Module (extractFromModule m) | _ -> t)


    // run both passes
    f
    |> extractTypeLiterals_pass1
    |> extractTypeLiterals_pass2

let addAliasUnionHelpers(f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Module md ->
            { md with
                Types = 
                    (md.Types |> List.collect(fun tp2 ->
                        match tp2 with
                        | FsType.Alias al ->
                            match al.Type with
                            | FsType.Union un ->
                                if un.Types.Length > 1 then
                                    [tp2] @
                                    [
                                        {
                                            Attributes = ["RequireQualifiedAccess"; "CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix"]
                                            HasDeclare = false
                                            IsNamespace = false
                                            Name = al.Name
                                            Types = []
                                            HelperLines =
                                                let mutable i = 0
                                                un.Types |> List.collect (fun tp3 ->
                                                    let n = un.Types.Length
                                                    i <- i + 1
                                                    let name = getName tp3
                                                    let name = if name = "" then sprintf "Case%d" i else name
                                                    let name = name.Replace("'","") // strip generics
                                                    let name = capitalize name
                                                    let aliasNameWithTypes = sprintf "%s%s" al.Name (Print.printTypeParameters al.TypeParameters)
                                                    if un.Option then
                                                        [
                                                            sprintf "let of%sOption v: %s = v |> Option.map U%d.Case%d" name aliasNameWithTypes n i
                                                            sprintf "let of%s v: %s = v |> U%d.Case%d |> Some" name aliasNameWithTypes n i
                                                            sprintf "let is%s (v: %s) = match v with None -> false | Some o -> match o with U%d.Case%d _ -> true | _ -> false" name aliasNameWithTypes n i
                                                            sprintf "let as%s (v: %s) = match v with None -> None | Some o -> match o with U%d.Case%d o -> Some o | _ -> None" name aliasNameWithTypes n i
                                                        ]
                                                    else
                                                        [
                                                            sprintf "let of%s v: %s = v |> U%d.Case%d" name aliasNameWithTypes n i
                                                            sprintf "let is%s (v: %s) = match v with U%d.Case%d _ -> true | _ -> false" name aliasNameWithTypes n i
                                                            sprintf "let as%s (v: %s) = match v with U%d.Case%d o -> Some o | _ -> None" name aliasNameWithTypes n i
                                                        ]
                                                )
                                        }
                                        |> FsType.Module
                                    ]
                                else [tp2]
                            | _ -> [tp2]
                        | _ -> [tp2]
                    ))
            }
            |> FsType.Module
        | _ -> tp
    )

let fixNamespace (f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Mapped mp ->
            { mp with Name = fixNamespaceString mp.Name } |> FsType.Mapped
        | FsType.Import im ->
            match im with
            | FsImport.Module immd ->
                { immd with
                    Module = fixModuleName immd.Module
                    SpecifiedModule = fixModuleName immd.SpecifiedModule
                }
                |> FsImport.Module
            | FsImport.Type imtp ->
                { imtp with 
                    SpecifiedModule = 
                        match f.Kind with 
                        | FsFileKind.Index ->
                            fixModuleName imtp.SpecifiedModule
                        | FsFileKind.Extra _ -> imtp.SpecifiedModule
                }
                |> FsImport.Type
            |> FsType.Import
        | _ -> tp
    )

let aliasToInterfacePartly (f: FsFile): FsFile =
    let compileAliasHasOnlyFunctionToInterface f = 
        f |> fixFile (fun tp ->
            match tp with 
            | FsType.Alias al ->
                match al.Type with 
                | FsType.Function f ->
                    {
                        Comments = f.Comments
                        IsStatic = false
                        IsClass = false
                        Name = al.Name
                        FullName = al.Name
                        Inherits = []
                        Members = { f with Name = Some "Invoke"; Kind = FsFunctionKind.Call } |> FsType.Function |> List.singleton
                        TypeParameters = al.TypeParameters
                        Accessibility = None
                    } |> FsType.Interface
                | _ -> tp    
            | _ -> tp     
    )  

    let compileAliasHasIntersectionToInterface f = 
        f |> fixFile(fun tp ->
            match tp with 
            | FsType.Alias al ->
                match al.Type with 
                | FsType.Tuple tu ->
                    match tu.Kind with 
                    | FsTupleKind.Intersection ->
                        {
                            Comments = []
                            IsStatic = false
                            IsClass = false
                            Name = al.Name
                            FullName = al.Name
                            Inherits = []
                            Members = []
                            TypeParameters = al.TypeParameters
                            Accessibility = None
                        } |> FsType.Interface
                    | _ -> tp
                | _ -> tp        
            | _ -> tp    
        )

    let compileAliasHasMappedToInterface f = 
        f |> fixFile (fun tp ->
            match tp with 
            | FsType.Alias al -> 
                match al.Type with 
                | FsType.Tuple tu when tu.Kind = FsTupleKind.Mapped -> 
                    {
                        Comments = []
                        IsStatic = false
                        IsClass = false
                        Name = al.Name
                        FullName = al.Name
                        Inherits = []
                        Members = []
                        TypeParameters = al.TypeParameters
                        Accessibility = None
                    } |> FsType.Interface  
                | _ -> tp
            | _ -> tp                          
        )

    //we don't want to print intersection and mapped types, so compile them to simpleType "obj"
    let flatten f = 
        f |> fixFile(fun tp ->
            match tp with 
            | FsType.Tuple tu ->
                match tu.Kind with 
                | FsTupleKind.Intersection | FsTupleKind.Mapped -> simpleType "obj"
                | _ -> tp
            | _ -> tp
        )    

    f 
    |> compileAliasHasOnlyFunctionToInterface
    |> compileAliasHasIntersectionToInterface
    |> compileAliasHasMappedToInterface
    |> flatten

/// babylonjs contains 'type float = number;', which creates invalid f# output (type float = float)
let fixFloatAlias (f: FsFile): FsFile =
    f |> fixFile(fun tp ->
        match tp with 
        | FsType.Module m ->
            let floatNumberAlias =
                m.Types
                |> List.tryFind (function
                    | FsType.Alias { Name = "float"; Type = FsType.Mapped { Name = "float"; FullName = "float" } } -> true
                    | _ -> false)
            match floatNumberAlias with
            | Some a -> FsType.Module { m with Types = m.Types |> List.except [ a ] }
            | None -> tp
        | _ -> tp
    )    
    

let fixFsFileOut fo = 
    
    let isBrowser =
        fo.Files
        |> getAllTypes 
        |> List.choose FsType.asMapped 
        |> List.exists(fun mp -> mp.Name.StartsWith "HTML")
    
    let fixHelperLines (f: FsFile) =
        f |> fixFile (fun tp ->
            match tp with 
            | FsType.Module md -> 
                { md with
                    HelperLines =                 
                        md.HelperLines |> List.map(fun l -> 
                            l.Replace("Option.map","Microsoft.FSharp.Core.Option.map")
                ) } |> FsType.Module

            | _ -> tp )

    if isBrowser then 
        { fo with 
            Opens = fo.Opens @ ["Fable.Import.Browser"]
            Files = fo.Files |> List.map fixHelperLines }
    else fo        

let extractGenericParameterDefaults (f: FsFile): FsFile =
    let fix f = 
        let extractAliasesFromGenericParameterDefaults name tps = 
            let aliases = List<FsAlias>()

            tps |> List.choose FsType.asGenericParameterDefaults
                |> List.iteri(fun i _ ->
                    {
                        Name = name
                        Type = 
                            {
                                Type = simpleType name
                                TypeParameters = 
                                    (tps.[0 .. i] |> List.map(fun _ -> simpleType "obj"))
                                    @ tps.[i+1 ..]
                            } |> FsType.Generic
                        TypeParameters = tps.[i+1 ..]    
                    } |> aliases.Add
                )    
            aliases |> List.ofSeq |> List.map FsType.Alias 
               
        f |> fixFile(fun tp ->
            match tp with 
            | FsType.Module md ->
                { md with 
                    Types = 
                        let tps = List<FsType>()
                        md.Types |> List.iter(fun tp ->
                            match tp with 
                            | FsType.Interface it -> 
                                it.TypeParameters
                                |> extractAliasesFromGenericParameterDefaults it.Name
                                |> tps.AddRange
                                
                                tp |> tps.Add
                            | FsType.Alias al ->
                                al.TypeParameters
                                |> extractAliasesFromGenericParameterDefaults al.Name
                                |> tps.AddRange

                                tp |> tps.Add
                            | _ -> tp |> tps.Add
                        )

                        tps |> List.ofSeq
                        
                } |> FsType.Module
            | _ -> tp 
        )
        
    let flatten f =
        f |> fixFile(fun tp ->
            match tp with 
            | FsType.GenericParameterDefaults gpd -> 
                { Name = gpd.Name; FullName = gpd.FullName } |> FsType.Mapped
            | _ -> tp 
    )           
    
    f 
    |> fix
    |> flatten

let fixTypesHasESKeywords  (f: FsFile): FsFile =
    f |> fixFile (fun tp ->
        match tp with
        | FsType.Generic gn ->
            esKeywords 
            |> Set.contains (getName tp) 
            |> function 
                | true -> { gn with Type = simpleType "obj"; TypeParameters = []} |> FsType.Generic
                | _ -> 
                    { gn with 
                        TypeParameters = gn.TypeParameters |> List.map(fun tp2 ->
                        match tp2 with 
                        | FsType.Mapped mp -> 
                            if esKeywords.Contains mp.Name then simpleType "obj"
                            else tp2
                        | _ -> tp2    
                    )
                    } |> FsType.Generic
        | _ -> tp
    )    

let extractTypesInGlobalModules  (f: FsFile): FsFile =
    { f with 
        Modules = f.Modules |> List.map(fun md ->
            let tps = List []
            md.Types |> List.iter(fun tp -> 
                match tp with 
                | FsType.Module md2 -> 
                    if md2.Name = "global" then md2.Types |> tps.AddRange
                    else tp |> tps.Add
                | _ -> tp |> tps.Add  
            )
            { md with Types = tps |> List.ofSeq }    
        ) 
    }
