﻿module Barb.Representation

open System
open System.Collections.Generic
open System.Reflection

type BarbException (message, offset: uint32, length: uint32) = 
    inherit Exception (message)
    member t.Offset = offset
    member t.Length = length

type BarbSettings = 
    {
        BindGlobalsWhenReducing: bool
        FailOnCatchAll: bool
        Namespaces: string Set
        AdditionalBindings: IDictionary<string,obj>
    }
    with static member Default = 
            { 
                BindGlobalsWhenReducing = true
                FailOnCatchAll = true
                AdditionalBindings = [] |> dict
                Namespaces = [null; ""; "System"; "Microsoft.FSharp"; "Microsoft.FSharp.Collections"; "Barb.Lib"; "Barb.Lib.TopLevel"] |> Set.ofList
            }

type MethodSig = ((obj array -> obj) * Type array) list

// Mutable so we can update the bindings with itself for easy recursion.
type LambdaRecord = { Params: string list; mutable Bindings: Bindings; Contents: ExprRep }

and InvokableExpr =
    | AppliedMultiMethod of (obj * MethodInfo list) list
    | AppliedMethod of obj * MethodInfo list    

and ExprTypes = 
    (* Units *)
    | Unit
    | Invoke
    | New
    | InvokableExpr of InvokableExpr
    | AppliedProperty of obj * PropertyInfo
    | AppliedMultiProperty of (obj * PropertyInfo) list
    | AppliedInvoke of int * string // where int is the collection depth to perform the invocation, 0 is top level    
    | AppliedIndexedProperty of obj * PropertyInfo list
    | FieldGet of FieldInfo list
    | Obj of obj
    | Prefix of (obj -> obj)    
    | Postfix of (obj -> obj)
    | Infix of int * (obj -> obj -> obj) 
    | IndexArgs of ExprRep array
    | Unknown of string
    (* Multi-Subexpression Containers *)
    | SubExpression of ExprRep list
    | Tuple of ExprRep array
    | ArrayBuilder of ExprRep array
    | SetBuilder of ExprRep array
    // Bound Value: Name, Bound Expression, Scope
    | BVar of string * ExprRep * ExprRep
    | Lambda of LambdaRecord
    | IfThenElse of ExprRep * ExprRep * ExprRep
    | Generator of ExprRep * ExprRep * ExprRep
    | And of ExprRep * ExprRep
    | Or of ExprRep * ExprRep
    (* Tags *)
    // Returned by a .NET call of some kind
    | Returned of obj
    // Has no Unknowns
    | Resolved of ExprTypes
    // Has Unknowns
    | Unresolved of ExprTypes

and ExprRep =
    {
        Offset: uint32
        Length: uint32
        Expr: ExprTypes
    }
    with override t.ToString() = sprintf "{ Off = %i; Len = %i; %A }" t.Offset t.Length t.Expr

and BindingContents = 
    | ComingLater
    /// Offset -> Length -> ExprRep
    | Existing of (uint32 -> uint32 -> ExprRep)

and Bindings = (String, BindingContents) Map 

type BarbData = 
    {
        InputType: Type 
        OutputType: Type
        Contents: ExprRep list
        Settings: BarbSettings
    }
    with static member Default = { InputType = typeof<unit>; OutputType = typeof<unit>; Contents = []; Settings = BarbSettings.Default }

let exprRepListOffsetLength (exprs: ExprRep seq) =
    let offsets = exprs |> Seq.map (fun e -> e.Offset)
    let max = offsets |> Seq.max 
    let min = offsets |> Seq.min
    min, max - min

let listToSubExpression (exprs: ExprRep list) =
    let offset, length = exprRepListOffsetLength exprs
    { Offset = offset; Length = length; Expr = SubExpression exprs }

let rec exprExistsInRep (pred: ExprTypes -> bool)  (rep: ExprRep) =
    exprExists pred rep.Expr
and exprExists (pred: ExprTypes -> bool) (expr: ExprTypes) =
    match expr with
    | _ when pred expr -> true 
    | SubExpression (repList) -> repList |> List.exists (exprExistsInRep pred)
    | Tuple (repArray) -> repArray |> Array.exists (exprExistsInRep pred)
    | IndexArgs (repArray) -> repArray |> Array.exists (exprExistsInRep pred)
    | BVar (name, rep, scopeRep) -> 
        exprExistsInRep pred rep || exprExistsInRep pred scopeRep
    | Lambda (lambda) -> exprExistsInRep pred (lambda.Contents)
    | IfThenElse (ifRep, thenRep, elseRep) ->
        ifRep |> exprExistsInRep pred || thenRep |> exprExistsInRep pred || elseRep |> exprExistsInRep pred
    | Generator (fromRep, incRep, toRep) -> [fromRep; incRep; toRep] |> List.exists (exprExistsInRep pred) 
    // The two tagged cases
    | Resolved (rep) -> exprExists pred rep
    | Unresolved (expr) -> exprExists pred expr
    // Nothing found
    | _ -> false

let wrapResolved (rep: ExprRep) = { rep with Expr = Resolved rep.Expr }
let wrapUnresolved (rep: ExprRep) = { rep with Expr = Unresolved rep.Expr }

let (|ResolvedTuple|_|) (v: ExprTypes) =
    match v with 
    | Resolved(Tuple tc) -> 
        tc |> Array.map (fun ex -> ex.Expr) 
        |> Array.map (function | Obj v -> v | other -> failwith (sprintf "Resolved tuple should only contian objects: %A" other))
        |> Some
    | _ -> None

let (|ResolvedIndexArgs|_|) (v: ExprTypes) =
    match v with 
    | Resolved(IndexArgs tc) -> 
        tc |> Array.map (fun ex -> ex.Expr) 
        |> Array.map (function | Obj v -> v | other -> failwith (sprintf "Resolved index args should only contian objects: %A" other))
        |> Some
    | _ -> None


let wrapExistingBinding expr = (fun off len -> {Offset = off; Length = len; Expr = expr}) |> Existing