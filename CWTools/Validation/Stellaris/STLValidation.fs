namespace CWTools.Validation.Stellaris
open CWTools.Validation.ValidationCore
open CWTools.Process.STLProcess
open CWTools.Process
open CWTools.Process.ProcessCore
open CWTools.Parser.Types
open CWTools.Process.STLScopes
open CWTools.Common
open CWTools.Common.STLConstants
open DotNet.Globbing
open CWTools.Games
open Newtonsoft.Json.Linq
open CWTools.Utilities.Utils
open System
open Microsoft.FSharp.Collections.Tagged
open System.Collections
open CWTools.Game.Stellaris.STLLookup


module STLValidation =
    type S = Severity
    type EntitySet<'T>(entities : struct (Entity * Lazy<'T>) list) =
        member __.GlobMatch(pattern : string) =
            let options = new GlobOptions();
            options.Evaluation.CaseInsensitive <- true;
            let glob = Glob.Parse(pattern, options)
            entities |> List.choose (fun struct (es, _) -> if glob.IsMatch(es.filepath) then Some es.entity else None)
        member this.GlobMatchChildren(pattern : string) =
            this.GlobMatch(pattern) |> List.map (fun e -> e.Children) |> List.collect id
        member __.AllOfType (entityType : EntityType) =
            entities |> List.choose(fun struct (es, d) -> if es.entityType = entityType then Some (es.entity, d)  else None)
        member this.AllOfTypeChildren (entityType : EntityType) =
            this.AllOfType(entityType) |> List.map (fun (e, d) -> e.Children) |> List.collect id
        member __.All = entities |> List.map (fun struct (es, _) -> es.entity)
        member __.AllWithData = entities |> List.map (fun struct (es, d) -> es.entity, d)
        member this.AllEffects=
            let fNode = (fun (x : Node) acc ->
                            match x with
                            | :? EffectBlock as e -> e::acc
                            | :? Option as e -> e.AsEffectBlock::acc
                            |_ -> acc
                                )

            this.All |> List.collect (foldNode7 fNode)
        member this.AllTriggers=
            let fNode = (fun (x : Node) acc ->
                            match x with
                            | :? TriggerBlock as e -> e::acc
                            |_ -> acc
                                )
            this.All |> List.collect (foldNode7 fNode)
        member this.AllModifiers=
            let fNode = (fun (x : Node) acc ->
                            match x with
                            | :? WeightModifierBlock as e -> e::acc
                            |_ -> acc
                                )
            this.All |> List.collect (foldNode7 fNode)



        member __.Raw = entities
        member this.Merge(y : EntitySet<'T>) = EntitySet(this.Raw @ y.Raw)

    type STLEntitySet = EntitySet<STLComputedData>
    type StructureValidator = EntitySet<STLComputedData> -> EntitySet<STLComputedData> -> ValidationResult
    type FileValidator = IResourceAPI<STLComputedData> -> EntitySet<STLComputedData> -> ValidationResult
    let shipName (ship : Ship) = if ship.Name = "" then Invalid (seq {yield (inv (ErrorCodes.CustomError "must have name" Severity.Error) ship)}) else OK
    let shipSize (ship : Ship) = if ship.ShipSize = "" then Invalid (seq {yield (inv (ErrorCodes.CustomError "must have size" Severity.Error) ship)}) else OK

    let validateShip : Validator<Ship>  = shipName <&> shipSize


    let getDefinedVariables (node : Node) =
        let fNode = (fun (x:Node) acc ->
                        x.Values |> List.fold (fun a n -> if n.Key.StartsWith("@", StringComparison.OrdinalIgnoreCase) then n.Key::a else a) acc
                        )
        node |> (foldNode7 fNode) |> List.ofSeq

    let checkUsedVariables (node : Node) (variables : string list) =
        let fNode = (fun (x:Node) children ->
                        let values = x.Values |> List.choose (fun v -> match v.Value with |String s when s.StartsWith("@", StringComparison.OrdinalIgnoreCase) -> Some v |_ -> None)
                        match values with
                        | [] -> children
                        | x ->
                            x |> List.map ((fun f -> f, f.Value.ToString()) >> (fun (l, v) -> if variables |> List.contains v then OK else Invalid (seq {yield inv (ErrorCodes.UndefinedVariable v) l})))
                              |> List.fold (<&&>) children)
        let fCombine = (<&&>)
        node |> (foldNode2 fNode fCombine OK)



    let validateVariables : StructureValidator =
        fun os es ->
            let globalVars = os.GlobMatch("**/common/scripted_variables/*.txt") @ es.GlobMatch("**/common/scripted_variables/*.txt")
                            |> List.map getDefinedVariables
                            |> Seq.collect id |> List.ofSeq
            es.All <&!&>
            // let x =
            //     es.All
            //     |> List.map
                    (fun node ->
                        let defined = getDefinedVariables node
                        let errors = checkUsedVariables node (defined @ globalVars)
                        errors
                    )
            //x |> List.fold (<&&>) OK

    let categoryScopeList = [
        ModifierCategory.Army, [Scope.Army; Scope.Planet; Scope.Country]
        ModifierCategory.Country, [Scope.Country]
        ModifierCategory.Leader, [Scope.Leader; Scope.Country]
        ModifierCategory.Megastructure, [Scope.Megastructure; Scope.Country]
        ModifierCategory.Planet, [Scope.Planet; Scope.Country]
        ModifierCategory.PlanetClass, [Scope.Planet; Scope.Pop; Scope.Country]
        ModifierCategory.Pop, [Scope.Pop; Scope.Planet; Scope.Country]
        ModifierCategory.PopFaction, [Scope.PopFaction; Scope.Country]
        ModifierCategory.Science, [Scope.Ship; Scope.Country]
        ModifierCategory.Ship, [Scope.Ship; Scope.Starbase; Scope.Fleet; Scope.Country]
        ModifierCategory.ShipSize, [Scope.Ship; Scope.Starbase; Scope.Country]
        ModifierCategory.Starbase, [Scope.Starbase; Scope.Country]
        ModifierCategory.Tile, [Scope.Tile; Scope.Pop; Scope.Planet; Scope.Country]
    ]

    let inline checkCategoryInScope (modifier : string) (scope : Scope) (node : ^a) (cat : ModifierCategory) =
        match List.tryFind (fun (c, _) -> c = cat) categoryScopeList, scope with
        |None, _ -> OK
        |Some _, s when s = Scope.Any -> OK
        |Some (c, ss), s -> if List.contains s ss then OK else Invalid (seq {yield inv (ErrorCodes.IncorrectStaticModifierScope modifier (s.ToString()) (ss |> List.map (fun f -> f.ToString()) |> String.concat ", ")) node})


    let inline valStaticModifier (modifiers : Modifier list) (scopes : ScopeContext) (modifier : string) (node) =
        let exists = modifiers |> List.tryFind (fun m -> m.tag = modifier && not m.core )
        match exists with
        |None -> Invalid (seq {yield inv (ErrorCodes.UndefinedStaticModifier modifier) node})
        |Some m -> m.categories <&!&>  (checkCategoryInScope modifier scopes.CurrentScope node)

    let valNotUsage (node : Node) = if (node.Values.Length + node.Children.Length) > 1 then Invalid (seq {yield inv ErrorCodes.IncorrectNotUsage node}) else OK


    let valAfterOptionBug (event : Event) =
        let fNode = (fun (x : Node) (children : bool) ->
            (x.Values |> List.exists (fun v -> v.Key == "response_text") ) || children
            )
        let hasresponse = event |> foldNode2 fNode ((||)) false
        let hasafter = event.Children |> List.exists (fun v -> v.Key == "after")
        if hasresponse && hasafter then Invalid (seq {yield inv (ErrorCodes.CustomError "This event uses after and has an option with response_text, this is bugged in 2.0.2" Severity.Warning) event}) else OK
    /// Make sure an event either has a mean_time_to_happen or is stopped from checking all the time
    /// Not mandatory, but performance reasons, suggested by Caligula
    /// Check "mean_time_to_happen", "is_triggered_only", "fire_only_once" and "trigger = { always = no }".
    /// Create issue if none are true
    let valEventVals (event : Event) =
        let isMTTH = event.Has "mean_time_to_happen"
        let isTrig = event.Has "is_triggered_only"
        let isOnce = event.Has "fire_only_once"
        let isAlwaysNo =
            match event.Child "trigger" with
            | Some t ->
                match t.Tag "always" with
                | Some (Bool b) when b = false -> true
                | _ -> false
            | None -> false
        let e =
            match isMTTH || isTrig || isOnce || isAlwaysNo with
            | false -> Invalid (seq {yield inv ErrorCodes.EventEveryTick event})
            | true -> OK
        e <&&> valAfterOptionBug event

    let valResearchLeader (area : string) (cat : string option) (node : Node) =
        let fNode = (fun (x:Node) children ->
                        let results =
                            match x.Key with
                            | "research_leader" ->
                                match x.TagText "area" with
                                | "" -> Invalid (seq {yield inv ErrorCodes.ResearchLeaderArea x})
                                | area2 when area <> area2 -> Invalid (seq {yield inv (ErrorCodes.ResearchLeaderTech area area2) x})
                                | _ -> OK
                                /// These aren't really required
                                // <&&>
                                // match cat, x.TagText "has_trait" with
                                // | None, _ -> OK
                                // | _, "" -> Invalid (seq {yield inv S.Error x "This research_leader is missing required \"has_trait\""})
                                // | Some c, t when ("leader_trait_expertise_" + c) <> t -> Invalid (seq {yield inv S.Warning x "This research_leader has the wrong expertise"})
                                // | _ -> OK
                            | _ -> OK
                        results <&&> children)
        let fCombine = (<&&>)
        node |> (foldNode2 fNode fCombine OK)

    let valTechnology : StructureValidator =
        fun _ es ->
            let techs = es.GlobMatchChildren("**/common/technology/*.txt")
            let inner =
                fun (node : Node) ->
                    let area = node.TagText "area"
                    let cat = node.Child "category" |> Option.bind (fun c -> c.All |> List.tryPick (function |LeafValueC lv -> Some (lv.Value.ToString()) |_ -> None))
                    let catres =
                        match cat with
                        | None -> Invalid (seq {yield inv ErrorCodes.TechCatMissing node})
                        | Some _ -> OK
                    catres <&&> valResearchLeader area cat node
            techs <&!&> inner
            //techs |> List.map inner |> List.fold (<&&>) OK

    let valButtonEffects : StructureValidator =
        fun os es ->
            let effects = (os.GlobMatchChildren("**/common/button_effects/*.txt"))
                            |> List.filter (fun e -> e :? Button_Effect)
                            |> List.map (fun e -> e.Key)
            let buttons = es.GlobMatchChildren("**/interface/*.gui") @ es.GlobMatchChildren("**/interface/**/*.gui")
            let fNode = (fun (x : Node) children ->
                            let results =
                                match x.Key with
                                | "effectButtonType" ->
                                    x.Leafs "effect" <&!&> (fun e -> if List.contains (e.Value.ToRawString()) effects then OK else Invalid (seq {yield inv (ErrorCodes.ButtonEffectMissing (e.Value.ToString())) e}))
                                | _ -> OK
                            results <&&> children
                                )
            let fCombine = (<&&>)
            buttons <&!&> (foldNode2 fNode fCombine OK)

    let valSprites : StructureValidator =
        //let spriteKeys = ["spriteType"; "portraitType"; "corneredTileSpriteType"; "flagSpriteType"]
        fun os es ->
            let sprites = os.GlobMatchChildren("**/interface/*.gfx") @ os.GlobMatchChildren("**/interface/*/*.gfx")
                            |> List.filter (fun e -> e.Key = "spriteTypes")
                            |> List.collect (fun e -> e.Children)
            let spriteNames = sprites |> Seq.collect (fun s -> s.TagsText "name") |> List.ofSeq
            let gui = es.GlobMatchChildren("**/interface/*.gui") @ es.GlobMatchChildren("**/interface/*/*.gui")
            let fNode = (fun (x : Node) children ->
                            let results =
                                match x.Leafs "spriteType" |> List.ofSeq with
                                | [] -> OK
                                | xs ->
                                    xs <&!&> (fun e -> if List.contains (e.Value.ToRawString()) spriteNames then OK else Invalid (seq {yield inv (ErrorCodes.SpriteMissing (e.Value.ToString())) e}))
                            results <&&> children
                                )
            let fCombine = (<&&>)
            gui <&!&> (foldNode2 fNode fCombine OK)

    let valSpriteFiles : FileValidator =
        fun rm es ->
            let sprites = es.GlobMatchChildren("**/interface/*.gfx") @ es.GlobMatchChildren("**/interface/*/*.gfx")
                            |> List.filter (fun e -> e.Key = "spriteTypes")
                            |> List.collect (fun e -> e.Children)
            let filenames = rm.GetResources() |> List.choose (function |FileResource (f, _) -> Some f |EntityResource (f, _) -> Some f)
            let inner =
                fun (x : Node) ->
                   Seq.append (x.Leafs "textureFile") (Seq.append (x.Leafs "texturefile") (x.Leafs "effectFile"))
                    <&!&> (fun l ->
                        let filename = l.Value.ToRawString().Replace("/","\\")
                        let filenamefallback = filename.Replace(".lua",".shader").Replace(".tga",".dds")
                        match filenames |> List.exists (fun f -> f.EndsWith(filename) || f.EndsWith(filenamefallback)) with
                        | true -> OK
                        | false -> Invalid (seq {yield inv (ErrorCodes.MissingFile (l.Value.ToRawString())) l}))
            sprites <&!&> inner



    let findAllSetVariables (node : Node) =
        let keys = ["set_variable"; "change_variable"; "subtract_variable"; "multiply_variable"; "divide_variable"]
        let fNode = (fun (x : Node) acc ->
                    x.Children |> List.fold (fun a n -> if List.contains (n.Key) keys then n.TagText "which" :: a else a) acc
                     )
        foldNode7 fNode node |> List.ofSeq

    let  validateUsedVariables (variables : string list) (node : Node) =
        let fNode = (fun (x : Node) children ->
                    match x.Childs "check_variable" |> List.ofSeq with
                    | [] -> children
                    | t ->
                        t <&!&> (fun node -> node |> (fun n -> n.Leafs "which" |> List.ofSeq) <&!&> (fun n -> if List.contains (n.Value.ToRawString()) variables then OK else Invalid (seq {yield inv (ErrorCodes.UndefinedScriptVariable (n.Value.ToRawString())) node}) ))
                        <&&> children
                    )
        let fCombine = (<&&>)
        foldNode2 fNode fCombine OK node

    let getDefinedScriptVariables (es : STLEntitySet) =
        let fNode = (fun (x : Node) acc ->
                    match x with
                    | (:? EffectBlock as x) -> x::acc
                    | _ -> acc
                    )
        let ftNode = (fun (x : Node) acc ->
                    match x with
                    | (:? TriggerBlock as x) -> x::acc
                    | _ -> acc
                    )
        let foNode = (fun (x : Node) acc ->
                    match x with
                    | (:? Option as x) -> x::acc
                    | _ -> acc
                    )
        let opts = es.All |> List.collect (foldNode7 foNode) |> List.map filterOptionToEffects
        let effects = es.All |> List.collect (foldNode7 fNode) |> List.map (fun f -> f :> Node)
        //effects @ opts |> List.collect findAllSetVariables
        es.AllEffects |> List.collect findAllSetVariables

    let getEntitySetVariables (e : Entity) =
        let fNode = (fun (x : Node) acc ->
                    match x with
                    | (:? EffectBlock as x) -> x::acc
                    | _ -> acc
                    )
        let foNode = (fun (x : Node) acc ->
                    match x with
                    | (:? Option as x) -> x::acc
                    | _ -> acc
                    )
        let opts = e.entity |> (foldNode7 foNode) |> List.map filterOptionToEffects |> List.map (fun n -> n :> Node)
        let effects = e.entity |> (foldNode7 fNode) |> List.map (fun f -> f :> Node)
        effects @ opts |> List.collect findAllSetVariables

    let valVariables : StructureValidator =
        fun os es ->
            let ftNode = (fun (x : Node) acc ->
                    match x with
                    | (:? TriggerBlock as x) -> x::acc
                    | _ -> acc
                    )
            let triggers = es.All |> List.collect (foldNode7 ftNode) |> List.map (fun f -> f :> Node)
            let defVars = (os.AllWithData @ es.AllWithData) |> List.collect (fun (_, d) -> d.Force().setvariables)
            //let defVars = effects @ opts |> List.collect findAllSetVariables
            triggers <&!&> (validateUsedVariables defVars)


    let valTest : StructureValidator =
        fun os es ->
            let fNode = (fun (x : Node) acc ->
                        match x with
                        | (:? EffectBlock as x) -> x::acc
                        | _ -> acc
                        )
            let ftNode = (fun (x : Node) acc ->
                        match x with
                        | (:? TriggerBlock as x) -> x::acc
                        | _ -> acc
                        )
            let foNode = (fun (x : Node) acc ->
                        match x with
                        | (:? Option as x) -> x::acc
                        | _ -> acc
                        )
            let opts = es.All |> List.collect (foldNode7 foNode) |> List.map filterOptionToEffects
            let effects = es.All |> List.collect (foldNode7 fNode) |> List.map (fun f -> f :> Node)
            let triggers = es.All |> List.collect (foldNode7 ftNode) |> List.map (fun f -> f :> Node)
            OK
            // opts @ effects <&!&> (fun x -> Invalid (seq {yield inv (ErrorCodes.CustomError "effect") x}))
            // <&&> (triggers <&!&> (fun x -> Invalid (seq {yield inv (ErrorCodes.CustomError "trigger") x})))

    let inline checkModifierInScope (modifier : string) (scope : Scope) (node : ^a) (cat : ModifierCategory) =
        match List.tryFind (fun (c, _) -> c = cat) categoryScopeList, scope with
        |None, _ -> OK
        |Some _, s when s = Scope.Any -> OK
        |Some (c, ss), s -> if List.contains s ss then OK else Invalid (seq {yield inv (ErrorCodes.IncorrectModifierScope modifier (s.ToString()) (ss |> List.map (fun f -> f.ToString()) |> String.concat ", ")) node})

    let valModifier (modifiers : Modifier list) (scope : Scope) (leaf : Leaf) =
        match modifiers |> List.tryFind (fun m -> m.tag == leaf.Key) with
        |None -> Invalid (seq {yield inv (ErrorCodes.UndefinedModifier (leaf.Key)) leaf})
        |Some m ->
            m.categories <&!&> checkModifierInScope (leaf.Key) (scope) leaf
            <&&> (leaf.Value |> (function |Value.Int x when x = 0 -> Invalid (seq {yield inv (ErrorCodes.ZeroModifier leaf.Key) leaf}) | _ -> OK))
            // match m.categories |> List.contains (modifierCategory) with
            // |true -> OK
            // |false -> Invalid (seq {yield inv (ErrorCodes.IncorrectModifierScope (leaf.Key) (modifierCategory.ToString()) (m.categories.ToString())) leaf})


    let valModifiers (modifiers : Modifier list) (node : ModifierBlock) =
        let filteredModifierKeys = ["description"; "key"]
        let filtered = node.Values |> List.filter (fun f -> not (filteredModifierKeys |> List.exists (fun k -> k == f.Key)))
        filtered <&!&> valModifier modifiers node.Scope
    let valAllModifiers (modifiers : (Modifier) list) (es : STLEntitySet) =
        let fNode = (fun (x : Node) children ->
            match x with
            | (:? ModifierBlock as x) -> valModifiers modifiers x
            | _ -> OK
            <&&> children)
        let fCombine = (<&&>)
        es.All <&!&> foldNode2 fNode fCombine OK

    let addGeneratedModifiers (modifiers : Modifier list) (es : STLEntitySet) =
        let ships = es.GlobMatchChildren("**/common/ship_sizes/*.txt")
        let shipKeys = ships |> List.map (fun f -> f.Key)
        let shipModifierCreate =
            (fun k ->
            [
                {tag = "shipsize_"+k+"_build_speed_mult"; categories = [ModifierCategory.Starbase]; core = true }
                {tag = "shipsize_"+k+"_build_cost_mult"; categories = [ModifierCategory.Starbase]; core = true }
                {tag = "shipsize_"+k+"_upkeep_mult"; categories = [ModifierCategory.Ship]; core = true }
                {tag = "shipsize_"+k+"_hull_mult"; categories = [ModifierCategory.Ship]; core = true }
                {tag = "shipsize_"+k+"_hull_add"; categories = [ModifierCategory.Ship]; core = true }
            ])
        let shipModifiers = shipKeys |> List.collect shipModifierCreate

        let stratres = es.GlobMatchChildren("**/common/strategic_resources/*.txt")
        let srKeys = stratres |> List.map (fun f -> f.Key)
        let srModifierCreate =
            (fun k ->
            [
                {tag = "static_resource_"+k+"_add"; categories = [ModifierCategory.Country]; core = true }
                {tag = "static_planet_resource_"+k+"_add"; categories = [ModifierCategory.Planet]; core = true }
                {tag = "tile_resource_"+k+"_mult"; categories = [ModifierCategory.Tile]; core = true }
                {tag = "country_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_federation_member_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_federation_member_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_subjects_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_subjects_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_strategic_resources_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_strategic_resources_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_planet_classes_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_planet_classes_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "tile_building_resource_"+k+"_add"; categories = [ModifierCategory.Tile]; core = true }
                {tag = "tile_resource_"+k+"_add"; categories = [ModifierCategory.Tile]; core = true }
                {tag = "planet_resource_"+k+"_add"; categories = [ModifierCategory.Planet]; core = true }
                {tag = "country_resource_"+k+"_add"; categories = [ModifierCategory.Country]; core = true }
                {tag = "max_"+k; categories = [ModifierCategory.Country]; core = true }
            ])
        let srModifiers = srKeys |> List.collect srModifierCreate
        let planetclasses = es.GlobMatchChildren("**/common/planet_classes/*.txt")
        let pcKeys = planetclasses |> List.map (fun f -> f.Key)
        let pcModifiers = pcKeys |> List.map (fun k -> {tag = k+"_habitability"; categories = [ModifierCategory.PlanetClass]; core = true})
        let buildingTags = es.GlobMatch("**/common/building_tags/*.txt") |> List.collect (fun f -> f.LeafValues |> List.ofSeq)
        let buildingTagModifierCreate =
            (fun k ->
            [
                {tag = k+"_construction_speed_mult"; categories = [ModifierCategory.Planet]; core = true }
                {tag = k+"_build_cost_mult"; categories = [ModifierCategory.Planet]; core = true }
            ])
        let buildingModifiers = buildingTags |> List.map (fun l -> l.Value.ToRawString())
                                             |> List.collect buildingTagModifierCreate
        let countryTypeKeys = es.GlobMatchChildren("**/common/country_types/*.txt") |> List.map (fun f -> f.Key)
        let countryTypeModifiers = countryTypeKeys |> List.map (fun k -> {tag = "damage_vs_country_type_"+k+"_mult"; categories = [ModifierCategory.Ship]; core = true})
        let speciesKeys = es.GlobMatchChildren("**/common/species_archetypes/*.txt")
                            |> List.filter (fun s -> not (s.Has "inherit_traits_from"))
                            |> List.map (fun s -> s.Key)
        let speciesModifiers = speciesKeys |> List.map (fun k -> {tag = k+"_species_trait_points_add"; categories = [ModifierCategory.Country]; core = true})
        shipModifiers @ srModifiers @ pcModifiers @ buildingModifiers @ countryTypeModifiers @ speciesModifiers @ modifiers

    let findAllSavedEventTargets (event : Node) =
        let fNode = (fun (x : Node) children ->
                        let inner (leaf : Leaf) = if leaf.Key == "save_event_target_as" || leaf.Key == "save_global_event_target_as" then Some (leaf.Value.ToRawString()) else None
                        (x.Values |> List.choose inner) @ children
                        )
        let fCombine = (@)
        event |> (foldNode2 fNode fCombine [])

    let findAllSavedEventTargetsInEntity (e : Entity) =
        let fNode = (fun (x : Node) acc ->
                    match x with
                    | (:? EffectBlock as x) -> x::acc
                    | _ -> acc
                    )
        let foNode = (fun (x : Node) acc ->
                    match x with
                    | (:? Option as x) -> x::acc
                    | _ -> acc
                    )
        let opts = e.entity |> (foldNode7 foNode) |> List.map filterOptionToEffects |> List.map (fun n -> n :> Node)
        let effects = e.entity |> (foldNode7 fNode) |> List.map (fun f -> f :> Node)
        effects @ opts |> List.collect findAllSavedEventTargets


    let computeSTLData (e : Entity) =
        let eventIds = if e.entityType = EntityType.Events then e.entity.Children |> List.choose (function | :? Event as e -> Some e.ID |_ -> None) else []
        {
            eventids = eventIds
            setvariables = getEntitySetVariables e
            savedeventtargets = findAllSavedEventTargetsInEntity e
        }

    let getTechnologies (es : STLEntitySet) =
        let techs = es.AllOfTypeChildren EntityType.Technology
        let inner =
            fun (t : Node) ->
                let name = t.Key
                let prereqs = t.Child "prerequisites" |> Option.map (fun c -> c.LeafValues |> Seq.toList |> List.map (fun v -> v.Value.ToRawString()))
                                                      |> Option.defaultValue []
                name, prereqs
        techs |> List.map inner

    let getAllTechPreqreqs (es : STLEntitySet) =
        let fNode =
            fun (t : Node) children ->
                let inner ls (l : Leaf) = if l.Key == "has_technology" then l.Value.ToRawString()::ls else ls
                t.Values |> List.fold inner children
        (es.AllTriggers |> List.map (fun t -> t :> Node)) @ (es.AllModifiers |> List.map (fun t -> t :> Node)) |> List.collect (foldNode7 fNode)


    let validateTechnologies : StructureValidator =
        fun os es ->
            let getPrereqs (b : Node) =
                match b.Child "prerequisites" with
                |None -> []
                |Some p ->
                    p.LeafValues |> List.ofSeq |> List.map (fun lv -> lv.Value.ToRawString())
            let buildingPrereqs = os.AllOfTypeChildren EntityType.Buildings @ es.AllOfTypeChildren EntityType.Buildings |> List.collect getPrereqs
            let shipsizePrereqs = os.AllOfTypeChildren EntityType.ShipSizes @ es.AllOfTypeChildren EntityType.ShipSizes |> List.collect getPrereqs
            let sectPrereqs = os.AllOfTypeChildren EntityType.SectionTemplates @ es.AllOfTypeChildren EntityType.SectionTemplates |> List.collect getPrereqs
            let compPrereqs = os.AllOfTypeChildren EntityType.ComponentTemplates @ es.AllOfTypeChildren EntityType.ComponentTemplates |> List.collect getPrereqs
            let stratResPrereqs = os.AllOfTypeChildren EntityType.StrategicResources @ es.AllOfTypeChildren EntityType.StrategicResources |> List.collect getPrereqs
            let armyPrereqs = os.AllOfTypeChildren EntityType.Armies @ es.AllOfTypeChildren EntityType.Armies |> List.collect getPrereqs
            let edictPrereqs = os.AllOfTypeChildren EntityType.Edicts @ es.AllOfTypeChildren EntityType.Edicts |> List.collect getPrereqs
            let tileBlockPrereqs = os.AllOfTypeChildren EntityType.TileBlockers @ es.AllOfTypeChildren EntityType.TileBlockers |> List.collect getPrereqs
            let allPrereqs = buildingPrereqs @ shipsizePrereqs @ sectPrereqs @ compPrereqs @ stratResPrereqs @ armyPrereqs @ edictPrereqs @ tileBlockPrereqs @ getAllTechPreqreqs os @ getAllTechPreqreqs es |> Set.ofList
            let techList = getTechnologies os @ getTechnologies es
            let techPrereqs = techList |> List.collect snd |> Set.ofList
            let techChildren = techList |> List.map (fun (name, _) -> name, Set.contains name techPrereqs)
            // let techChildren = getTechnologies os @ getTechnologies es
            //                     |> (fun l -> l |> List.map (fun (name, _) -> name, l |> List.exists (fun (_, ts2) -> ts2 |> List.contains name)))
                                |> List.filter snd
                                |> List.map fst
                                |> Set.ofList
            let techs = es.AllOfTypeChildren EntityType.Technology
            let inner (t : Node) =
                let isPreReq = t.Has "prereqfor_desc"
                let isMod = t.Has "modifier"
                let hasChildren = techChildren |> Set.contains t.Key
                let isUsedElsewhere = allPrereqs |> Set.contains t.Key
                let isWeightZero = t.Tag "weight" |> (function |Some (Value.Int 0) -> true |_ -> false)
                let isWeightFactorZero = t.Child "weight_modifier" |> Option.map (fun wm -> wm.Tag "factor" |> (function |Some (Value.Float 0.00) -> true |_ -> false)) |> Option.defaultValue false
                let hasFeatureFlag = t.Has "feature_flags"
                if isPreReq || isMod || hasChildren || isUsedElsewhere || isWeightZero || isWeightFactorZero || hasFeatureFlag then OK else Invalid (seq {yield inv (ErrorCodes.UnusedTech (t.Key)) t})
            techs <&!&> inner



    let validateShipDesigns : StructureValidator =
        fun os es ->
            let ship_designs = es.AllOfTypeChildren EntityType.GlobalShipDesigns
            let section_templates = os.AllOfTypeChildren EntityType.SectionTemplates @ es.AllOfTypeChildren EntityType.SectionTemplates
            let weapons = os.AllOfTypeChildren EntityType.ComponentTemplates @ es.AllOfTypeChildren EntityType.ComponentTemplates
            let getWeaponInfo (w : Node) =
                match w.Key with
                | "weapon_component_template" ->
                    Some (w.TagText "key", ("weapon", w.TagText "size"))
                | "strike_craft_component_template" ->
                    Some (w.TagText "key", ("strike_craft", w.TagText "size"))
                | "utility_component_template" ->
                    Some (w.TagText "key", ("utility", w.TagText "size"))
                | _ -> None
            let weaponInfo = weapons |> List.choose getWeaponInfo |> Map.ofList
            let getSectionInfo (s : Node) =
                match s.Key with
                | "ship_section_template" ->
                    let inner (n : Node) =
                        n.TagText "name", (n.TagText "slot_type", n.TagText "slot_size")
                    let component_slots = s.Childs "component_slot" |> List.ofSeq |> List.map inner
                    let createUtilSlot (prefix : string) (size : string) (i : int) =
                        List.init i (fun i -> prefix + sprintf "%i" (i+1), ("utility", size))
                    let smalls = s.Tag "small_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "SMALL_UTILITY_" "small" i |_ -> [])
                    let med = s.Tag "medium_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "MEDIUM_UTILITY_" "medium" i |_ -> [])
                    let large = s.Tag "large_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "LARGE_UTILITY_" "large" i |_ -> [])
                    let aux = s.Tag "aux_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "AUX_UTILITY_" "aux" i |_ -> [])
                    let all = (component_slots @ smalls @ med @ large @ aux ) |> Map.ofList
                    Some (s.TagText "key", all)
                | _ -> None
            let sectionInfo = section_templates |> List.choose getSectionInfo |> Map.ofList

            let validateComponent (section : string) (sectionMap : Collections.Map<string, (string * string)>) (c : Node) =
                let slot = c.TagText "slot"
                let slotFound = sectionMap |> Map.tryFind slot
                let template = c.TagText "template"
                let templateFound = weaponInfo |> Map.tryFind template
                match slotFound, templateFound with
                | None, _ -> Invalid (seq {yield inv (ErrorCodes.MissingSectionSlot section slot) c})
                | _, None -> Invalid (seq {yield inv (ErrorCodes.UnknownComponentTemplate template) c})
                | Some (sType, sSize), Some (tType, tSize) ->
                    if sType == tType && sSize == tSize then OK else Invalid (seq {yield inv (ErrorCodes.MismatchedComponentAndSlot slot sSize template tSize) c})

            let defaultTemplates = [ "DEFAULT_COLONIZATION_SECTION"; "DEFAULT_CONSTRUCTION_SECTION"]
            let validateSection (s : Node) =
                let section = s.TagText "template"
                if defaultTemplates |> List.contains section then OK else
                    let sectionFound = sectionInfo |> Map.tryFind section
                    match sectionFound with
                    | None -> Invalid (seq {yield inv (ErrorCodes.UnknownSectionTemplate section) s})
                    | Some smap ->
                        s.Childs "component" <&!&> validateComponent section smap

            let validateDesign (d : Node) =
                d.Childs "section" <&!&> validateSection

            ship_designs <&!&> validateDesign

    let validateMixedBlocks : StructureValidator =
        fun _ es ->

            let fNode = (fun (x : Node) children ->
                if (x.LeafValues |> Seq.isEmpty |> not && (x.Leaves |> Seq.isEmpty |> not || x.Children |> Seq.isEmpty |> not)) |> not
                then children
                else Invalid (seq {yield inv ErrorCodes.MixedBlock x}) <&&> children
                )
            let fCombine = (<&&>)
            es.All <&!&> foldNode2 fNode fCombine OK


    let validateSolarSystemInitializers : StructureValidator =
        fun os es ->
            let inits = es.AllOfTypeChildren EntityType.SolarSystemInitializers |> List.filter (fun si -> not(si.Key == "random_list"))
            let starclasses =
                 es.AllOfTypeChildren EntityType.StarClasses @ os.AllOfTypeChildren EntityType.StarClasses
                |> List.map (fun sc -> if sc.Key == "random_list" then sc.TagText "name" else sc.Key)
            let fNode =
                fun (x : Node) ->
                    match x.Has "class", starclasses |> List.contains (x.TagText "class") with
                    |true, true -> OK
                    |false, _ -> Invalid (seq {yield inv (ErrorCodes.CustomError "This initializer is missing a class" Severity.Error) x})
                    |_, false -> Invalid (seq {yield inv (ErrorCodes.CustomError (sprintf "The star class %s does not exist" (x.TagText "class")) Severity.Error) x})
            inits <&!&> fNode

    let validatePlanetKillers : StructureValidator =
        fun os es ->
            let planetkillers = es.AllOfTypeChildren EntityType.ComponentTemplates |> List.filter (fun ct -> ct.TagText "type" = "planet_killer")
            let onactions = (os.AllOfTypeChildren EntityType.OnActions @ es.AllOfTypeChildren EntityType.OnActions) |> List.map (fun c -> c.Key)
            let scriptedtriggers = (os.AllOfTypeChildren EntityType.ScriptedTriggers @ es.AllOfTypeChildren EntityType.ScriptedTriggers) |> List.map (fun c -> c.Key)
            let inner (node : Node) =
                let key = node.TagText "key"
                let on_action = "on_destroy_planet_with_" + key
                let trigger = "can_destroy_planet_with_" + key
                if List.exists ((==) on_action) onactions then OK else Invalid (seq {yield inv (ErrorCodes.PlanetKillerMissing (sprintf "Planet killer %s is missing on_action %s" key on_action)) node})
                <&&>
                (if List.exists ((==) trigger) scriptedtriggers then OK else Invalid (seq {yield inv (ErrorCodes.PlanetKillerMissing (sprintf "Planet killer %s is missing scripted trigger %s" key trigger)) node}))
            planetkillers <&!&> inner

    let validateAnomaly210 : StructureValidator =
        fun _ es ->
            let anomalies = es.GlobMatchChildren("**/anomalies/*.txt")
            let fNode =
                fun (x : Node) -> if x.Key == "anomaly" || x.Key == "anomaly_category" then Invalid (seq {yield inv ((ErrorCodes.CustomError "This style of anomaly was removed with 2.1.0, please see vanilla for details") Severity.Error) x}) else OK
            anomalies <&!&> fNode


    let validateIfElse210 : StructureValidator =
        fun _ es ->
            let codeBlocks = (es.AllEffects |> List.map (fun n -> n :> Node))// @ (es.AllTriggers |> List.map (fun n -> n :> Node))
            let fNode =
                (fun (x : Node) children ->
                    if x.Key == "limit" || x.Key == "modifier" then OK else
                    let res = if x.Key == "if" && x.Has "else" && not(x.Has "if") then Invalid (seq {yield inv ErrorCodes.DeprecatedElse x}) else OK
                    let res2 = if x.Key == "else_if" && x.Has "else" && not(x.Has "if") then Invalid (seq {yield inv ErrorCodes.DeprecatedElse x}) else OK
                    let res3 = if x.Key == "if" && x.Has "else" && x.Has "if" then Invalid (seq {yield inv ErrorCodes.AmbiguousIfElse x}) else OK
                    (res <&&> res2 <&&> res3) <&&> children
                )
            codeBlocks <&!&> (foldNode2 fNode (<&&>) OK)

    let validateIfElse : StructureValidator =
        fun _ es ->
            let codeBlocks = (es.AllEffects |> List.map (fun n -> n :> Node))
            let fNode =
                (fun (x : Node) children ->
                    if x.Key == "if" && x.Has "else" && not(x.Has "if") then
                        children
                    else
                        let nodes = x.Children |> List.map (fun n -> n.Key)
                                                |> List.filter (fun n -> n == "if" || n == "else" || n == "else_if")
                        let checkNext (prevWasIf : bool) (key : string) =
                            match prevWasIf with
                            |true -> (key == "if" || key == "else_if"), None
                            |false ->
                                match key with
                                |y when y == "if" -> true, None
                                |y when y == "else" || y == "else_if" ->
                                    false, Some (Invalid (seq {yield inv ErrorCodes.IfElseOrder x}))
                                |_ -> false, None
                        let _, res = nodes |> List.fold (fun (s, (r : ValidationResult option)) n -> if r.IsSome then s, r else checkNext s n) (false, None)
                        match res with |None -> children |Some r -> r <&&> children
                )
            codeBlocks <&!&> (foldNode2 fNode (<&&>) OK)