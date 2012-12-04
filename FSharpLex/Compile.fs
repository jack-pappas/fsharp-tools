﻿(*
Copyright (c) 2012, Jack Pappas
All rights reserved.

This code is provided under the terms of the 2-clause ("Simplified") BSD license.
See LICENSE.TXT for licensing details.
*)

//
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FSharpLex.Compile

open System.Diagnostics
open SpecializedCollections
open Graph
open Regex
open Ast


/// DFA compilation state.
[<DebuggerDisplay(
    "States = {Transitions.VertexCount}, \
     Final States = {FinalStates.Count}, \
     Transition Sets = {Transitions.EdgeSetCount}")>]
type private CompilationState = {
    //
    Transitions : LexerDfaGraph;
    /// Final (accepting) DFA states.
    FinalStates : Set<DfaStateId>;
    /// Maps regular vectors to the DFA state representing them.
    RegularVectorToDfaState : Map<RegularVector, DfaStateId>;
    /// Maps a DFA state to the regular vector it represents.
    DfaStateToRegularVector : Map<DfaStateId, RegularVector>;
}

/// Functional operators related to the CompilationState record.
/// These operators are designed to adhere to either the Reader or State monads.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module private CompilationState =
    /// Empty compilation state.
    let empty = {
        Transitions = LexerDfaGraph.empty;
        FinalStates = Set.empty;
        RegularVectorToDfaState = Map.empty;
        DfaStateToRegularVector = Map.empty; }

    //
    let inline tryGetDfaState regVec (compilationState : CompilationState) =
        Map.tryFind regVec compilationState.RegularVectorToDfaState

    //
    let inline getRegularVector dfaState (compilationState : CompilationState) =
        Map.find dfaState compilationState.DfaStateToRegularVector

    //
    let createDfaState regVec (compilationState : CompilationState) =
        // Preconditions
        // TODO

        Debug.Assert (
            not <| Map.containsKey regVec compilationState.RegularVectorToDfaState,
            "The compilation state already contains a DFA state for this regular vector.")

        /// The DFA state representing this regular vector.
        let (dfaState : DfaStateId), transitions =
            LexerDfaGraph.createVertex compilationState.Transitions

        // Add the new DFA state to the compilation state.
        let compilationState =
            { compilationState with
                Transitions = transitions;
                FinalStates =
                    // A regular vector represents a final state iff it is nullable.
                    if RegularVector.isNullable regVec then
                        Set.add dfaState compilationState.FinalStates
                    else
                        compilationState.FinalStates;
                RegularVectorToDfaState =
                    Map.add regVec dfaState compilationState.RegularVectorToDfaState;
                DfaStateToRegularVector =
                    Map.add dfaState regVec compilationState.DfaStateToRegularVector;
                 }

        // Return the new DFA state and the updated compilation state.
        dfaState, compilationState

//
let private transitions regularVector universe (transitionsFromCurrentDfaState, unvisitedTransitionTargets, compilationState) derivativeClass =
    // Ignore empty derivative classes.
    if CharSet.isEmpty derivativeClass then
        transitionsFromCurrentDfaState,
        unvisitedTransitionTargets,
        compilationState
    else
        // The derivative of the regular vector w.r.t. the chosen element.
        let regularVector' =
            // Choose an element from the derivative class; any element
            // will do (which is the point behind the derivative classes).
            let derivativeClassElement = CharSet.minElement derivativeClass

            regularVector
            // Compute the derivative of the regular vector
            |> RegularVector.derivative derivativeClassElement
            // Canonicalize the derivative vector.
            // THIS IS EXTREMELY IMPORTANT -- this algorithm absolutely
            // will not work without this step.
            |> RegularVector.canonicalize universe

        (*  If the derivative of the regular vector represents the 'error' state,
            ignore it. Instead of representing the error state with an explicit state
            and creating transition edges to it, we will just handle it in the
            back-end (code-generation phase) by transitioning to the error state
            whenever we see an input which is not explicitly allowed.
            This greatly reduces the number of edges in the transition graph. *)
        if RegularVector.isEmpty regularVector' then
            transitionsFromCurrentDfaState,
            unvisitedTransitionTargets,
            compilationState
        else
            let targetDfaState, unvisitedTransitionTargets, compilationState =
                let maybeDfaState =
                    CompilationState.tryGetDfaState regularVector' compilationState

                match maybeDfaState with
                | Some targetDfaState ->
                    targetDfaState,
                    unvisitedTransitionTargets,
                    compilationState
                | None ->
                    // Create a DFA state for this regular vector.
                    let newDfaState, compilationState =
                        CompilationState.createDfaState regularVector' compilationState

                    // Add this new DFA state to the set of unvisited states
                    // targeted by transitions from the current DFA state.
                    let unvisitedTransitionTargets =
                        Set.add newDfaState unvisitedTransitionTargets

                    newDfaState,
                    unvisitedTransitionTargets,
                    compilationState

            //
            let transitionsFromCurrentDfaState =
                Map.add derivativeClass targetDfaState transitionsFromCurrentDfaState

            transitionsFromCurrentDfaState,
            unvisitedTransitionTargets,
            compilationState

//
let rec private createDfa universe pending compilationState =
    // If there are no more pending states, we're finished compiling.
    if Set.isEmpty pending then
        compilationState
    else
        //
        let currentState = Set.minElement pending
        let pending = Set.remove currentState pending

        //
        let regularVector = CompilationState.getRegularVector currentState compilationState

        // If this regular vector represents the error state, there's nothing to do
        // for it -- just continue processing the worklist.
        if RegularVector.isEmpty regularVector then
            createDfa universe pending compilationState
        else
            /// The approximate set of derivative classes of the regular vector,
            /// representing transitions out of the DFA state representing it.
            let derivativeClasses = RegularVector.derivativeClasses regularVector universe

            // For each DFA state (regular vector) targeted by a transition (derivative class),
            // add the DFA state to the compilation state (if necessary), then add an edge
            // to the transition graph from this DFA state to the target DFA state.
            let transitionsFromCurrentDfaState, unvisitedTransitionTargets, compilationState =
                ((Map.empty, Set.empty, compilationState), derivativeClasses)
                ||> Set.fold (transitions regularVector universe)

            // Add any newly-created, unvisited states to the
            // set of states which still need to be visited.
            let pending = Set.union pending unvisitedTransitionTargets

            let compilationState =
                { compilationState with
                    Transitions =
                        // Add the unvisited transition targets to the transition graph.
                        (compilationState.Transitions, transitionsFromCurrentDfaState)
                        ||> Map.fold (fun transitions derivativeClass target ->
                            LexerDfaGraph.addEdges currentState target derivativeClass transitions); }

            // Continue processing recursively.
            createDfa universe pending compilationState

/// Lexer compilation options.
type CompilationOptions = {
    /// Enable unicode support in the lexer.
    Unicode : bool;
}

/// A deterministic finite automaton (DFA) implementing a lexer rule.
[<DebuggerDisplay(
    "States = {Transitions.VertexCount}, \
     Transitions = {Transitions.EdgeSetCount}")>]
type LexerRuleDfa = {
    /// The transition graph of the DFA.
    Transitions : LexerDfaGraph;
    //
    RuleClauseFinalStates : Set<DfaStateId>[];
    /// For each accepting state of the DFA, specifies the
    /// index of the rule-clause accepted by the state.
    RuleAcceptedByState : Map<DfaStateId, RuleClauseIndex>;
    /// The initial state of the DFA.
    InitialState : DfaStateId;
}

/// A compiled lexer rule.
type CompiledRule = {
    /// The DFA compiled from the patterns of the rule clauses.
    Dfa : LexerRuleDfa;
    /// The semantic actions to be executed when the
    /// rule clauses are matched.
    RuleClauseActions : CodeFragment[];
}

//
[<RequireQualifiedAccess>]
module private EncodingCharSet =
    open System

    //
    let ascii =
        CharSet.ofRange Char.MinValue (char Byte.MaxValue)

    //
    let unicode =
        CharSet.ofRange Char.MinValue Char.MaxValue


//
[<RequireQualifiedAccess>]
module private Unicode =
    /// Maps each UnicodeCategory to the set of characters in the category.
    let categoryCharSet =
        // OPTIMIZE : If this takes "too long" to compute on-the-fly, we could pre-compute
        // the category sets and implement code which recreates the CharSets from the intervals
        // in the CharSets (not the individual values, which would be much slower).
        let table = System.Collections.Generic.Dictionary<_,_> (30)
        for i = 0 to 65535 do
            /// The Unicode category of this character.
            let category = System.Char.GetUnicodeCategory (char i)

            // Add this character to the set for this category.
            table.[category] <-
                match table.TryGetValue category with
                | true, charSet ->
                    CharSet.add (char i) charSet
                | false, _ ->
                    CharSet.singleton (char i)

        // TODO : Assert that the table contains an entry for every UnicodeCategory value.
        // Otherwise, exceptions will be thrown at run-time if we try to retrive non-existent entries.

        // Convert the dictionary to a Map
        (Map.empty, table)
        ||> Seq.fold (fun categoryMap kvp ->
            Map.add kvp.Key kvp.Value categoryMap)

//
let private rulePatternsToDfa (rulePatterns : RegularVector) (options : CompilationOptions) : LexerRuleDfa =
    // Preconditions
    if Array.isEmpty rulePatterns then
        invalidArg "rulePatterns" "The rule must contain at least one (1) pattern."

    // Determine which "universe" to use when compiling this
    // pattern based on the compilation settings.
    let universe =
        if options.Unicode then
            EncodingCharSet.unicode
        else
            EncodingCharSet.ascii

    // The initial DFA compilation state.
    let initialDfaStateId, compilationState =
        // Canonicalize the patterns before creating a state for them.
        let rulePatterns = RegularVector.canonicalize universe rulePatterns

        CompilationState.empty
        |> CompilationState.createDfaState rulePatterns

    // Compile the DFA.
    let compilationState =
        let initialPending = Set.singleton initialDfaStateId
        createDfa universe initialPending compilationState

    //
    let rulesAcceptedByDfaState =
        (Map.empty, compilationState.FinalStates)
        ||> Set.fold (fun map finalDfaStateId ->
            let acceptedRules : Set<RuleClauseIndex> =
                // Get the regular vector represented by this DFA state.
                compilationState.DfaStateToRegularVector
                |> Map.find finalDfaStateId
                // Determine which lexer rules are accepted by this regular vector.
                |> RegularVector.acceptingElementsTagged
                
            Map.add finalDfaStateId acceptedRules map)

    (* TODO :   Add code here to generate warnings about overlapping rules. *)

    /// Maps final (accepting) DFA states to the rule clause they accept.
    let ruleAcceptedByDfaState =
        rulesAcceptedByDfaState
        // Disambiguate overlapping patterns by choosing the rule-clause with the
        // lowest index -- i.e., the rule which was declared earliest in the lexer definition.
        |> Map.map (fun _ -> Set.minElement)

    // TODO : Is this code still needed? If not, discard it.
    let ruleAcceptingStates =
        let ruleAcceptingStates = Array.create (Array.length rulePatterns) Set.empty

        rulesAcceptedByDfaState
        |> Map.iter (fun finalDfaStateId acceptedRules ->
            Debug.Assert (
                not <| Set.isEmpty acceptedRules,
                sprintf "DFA state '%i' is marked as a final state but does not accept any rules." (int finalDfaStateId))

            acceptedRules
            |> Set.iter (fun acceptedRuleIndex ->
                ruleAcceptingStates.[int acceptedRuleIndex] <-
                    ruleAcceptingStates.[int acceptedRuleIndex]
                    |> Set.add finalDfaStateId))

        ruleAcceptingStates

    // Create a LexerDfa record from the compiled DFA.
    {   Transitions = compilationState.Transitions;
        RuleClauseFinalStates = ruleAcceptingStates;
        RuleAcceptedByState = ruleAcceptedByDfaState;
        InitialState = initialDfaStateId; }


//
let private preprocessMacro (macroId, pattern) (options : CompilationOptions) (macroEnv, badMacros) =
    //
    // OPTIMIZE : Modify this function to use a LazyList to hold the errors
    // instead of an F# list to avoid the list concatenation overhead.
    let rec preprocessMacro pattern cont =
        match pattern with
        | LexerPattern.Epsilon ->
            Choice1Of2 Regex.Epsilon

        | LexerPattern.CharacterSet charSet ->
            // Make sure all of the characters in the set are ASCII characters unless the 'Unicode' option is set.
            if options.Unicode || CharSet.forall (fun c -> int c <= 255) charSet then
                Regex.CharacterSet charSet
                |> Choice1Of2
                |> cont
            else
                ["Unicode characters may not be used in patterns unless the 'Unicode' compiler option is set."]
                |> Choice2Of2
                |> cont

        | LexerPattern.Negate r ->
            preprocessMacro r <| fun rResult ->
                match rResult with
                | (Choice2Of2 _ as err) -> err
                | Choice1Of2 r ->
                    Regex.Negate r
                    |> Choice1Of2
                |> cont

        | LexerPattern.Star r ->
            preprocessMacro r <| fun rResult ->
                match rResult with
                | (Choice2Of2 _ as err) -> err
                | Choice1Of2 r ->
                    Regex.Star r
                    |> Choice1Of2
                |> cont

        | LexerPattern.Concat (r, s) ->
            preprocessMacro r <| fun rResult ->
            preprocessMacro s <| fun sResult ->
                match rResult, sResult with
                | Choice2Of2 rErrors, Choice2Of2 sErrors ->
                    Choice2Of2 (rErrors @ sErrors)
                | (Choice2Of2 _ as err), Choice1Of2 _
                | Choice1Of2 _, (Choice2Of2 _ as err) ->
                    err
                | Choice1Of2 r, Choice1Of2 s ->
                    Regex.Concat (r, s)
                    |> Choice1Of2
                |> cont

        | LexerPattern.And (r, s) ->
            preprocessMacro r <| fun rResult ->
            preprocessMacro s <| fun sResult ->
                match rResult, sResult with
                | Choice2Of2 rErrors, Choice2Of2 sErrors ->
                    Choice2Of2 (rErrors @ sErrors)
                | (Choice2Of2 _ as err), Choice1Of2 _
                | Choice1Of2 _, (Choice2Of2 _ as err) ->
                    err
                | Choice1Of2 r, Choice1Of2 s ->
                    Regex.And (r, s)
                    |> Choice1Of2
                |> cont

        | LexerPattern.Or (r, s) ->
            preprocessMacro r <| fun rResult ->
            preprocessMacro s <| fun sResult ->
                match rResult, sResult with
                | Choice2Of2 rErrors, Choice2Of2 sErrors ->
                    Choice2Of2 (rErrors @ sErrors)
                | (Choice2Of2 _ as err), Choice1Of2 _
                | Choice1Of2 _, (Choice2Of2 _ as err) ->
                    err
                | Choice1Of2 r, Choice1Of2 s ->
                    Regex.Or (r, s)
                    |> Choice1Of2
                |> cont

        (*  Extended patterns are rewritten using the cases of LexerPattern
            which have corresponding cases in Regex. *)
        | LexerPattern.Empty ->
            Regex.CharacterSet CharSet.empty
            |> Choice1Of2
            |> cont
        
        | LexerPattern.Any ->
            Choice1Of2 Regex.Any
            |> cont

        | LexerPattern.Character c ->
            // Make sure the character is an ASCII character unless the 'Unicode' option is set.
            if options.Unicode || int c <= 255 then
                Regex.Character c
                |> Choice1Of2
                |> cont
            else
                ["Unicode characters may not be used in patterns unless the 'Unicode' compiler option is set."]
                |> Choice2Of2
                |> cont

        | LexerPattern.OneOrMore r ->
            // Rewrite r+ as rr*
            let rewritten =
                LexerPattern.Concat (r, LexerPattern.Star r)

            // Process the rewritten expression.
            preprocessMacro rewritten cont

        | LexerPattern.Optional r ->
            // Rewrite r? as (|r)
            let rewritten =
                LexerPattern.Concat (LexerPattern.Epsilon, r)

            // Process the rewritten expression.
            preprocessMacro rewritten cont

        | LexerPattern.Repetition (r, atLeast, atMost) ->
            // If not specified, the lower bound defaults to zero (0).
            let atLeast = defaultArg atLeast LanguagePrimitives.GenericZero

            // TODO : Rewrite this pattern using simpler cases.
            raise <| System.NotImplementedException "preprocessMacro"

            // Process the rewritten expression.
            //preprocessMacro rewritten cont

        (* Macro patterns *)
        | LexerPattern.Macro nestedMacroId ->
            // Make sure this macro doesn't call itself -- macros cannot be recursive.
            // NOTE : This could be handled by checking to see if this macro is already defined
            // because we don't add macros to 'macroEnv' until they're successfully preprocessed;
            // however, this separate check allows us to provide a more specific error message.
            if macroId = nestedMacroId then
                ["Recursive macro definitions are not allowed."]
                |> Choice2Of2
                |> cont
            else
                match Map.tryFind nestedMacroId macroEnv with
                | None ->
                    // Check the 'bad macros' set to avoid returning an error message
                    // for this pattern when the referenced macro contains an error.
                    if Set.contains nestedMacroId badMacros then
                        // We have to return something, so return Empty to take the place
                        // of this macro reference.
                        Choice1Of2 Regex.Empty
                        |> cont
                    else
                        Choice2Of2 [ sprintf "The macro '%s' is not defined." nestedMacroId ]
                        |> cont
                | Some nestedMacro ->
                    // Return the pattern for the nested macro so it'll be "inlined" into this pattern.
                    Choice1Of2 nestedMacro
                    |> cont

        | LexerPattern.UnicodeCategory unicodeCategory ->
            if options.Unicode then
                Regex.CharacterSet EncodingCharSet.unicode
                |> Choice1Of2
                |> cont
            else
                ["Unicode category patterns may not be used unless the 'Unicode' compiler option is set."]
                |> Choice2Of2
                |> cont

    /// Contains an error if a macro has already been defined with this name; otherwise, None.
    let duplicateNameError =
        if Map.containsKey macroId macroEnv then
            Some <| sprintf "Duplicate macro name '%s'." macroId
        else None

    // Call the function which traverses the macro pattern to validate/preprocess it.
    preprocessMacro pattern <| function
        | Choice2Of2 errors ->
            let errors =
                match duplicateNameError with
                | None -> errors
                | Some duplicateNameError ->
                    duplicateNameError :: errors

            List.rev errors
            |> List.toArray
            |> Choice2Of2

        | Choice1Of2 processedPattern ->
            // If the duplicate name error was set, return it;
            // otherwise there are no errors, so return the processed pattern.
            match duplicateNameError with
            | Some duplicateNameError ->
                [| duplicateNameError |]
                |> Choice2Of2
            | None ->
                Choice1Of2 processedPattern

/// Pre-processes a list of macros from a lexer specification.
/// The macros are validated to verify correct usage, then macro
/// expansion is performed to remove any nested macros.
let private preprocessMacros macros options =
    /// Recursively processes the list of macros.
    let rec preprocessMacros macros errors (macroEnv, badMacros) =
        match macros with
        | [] ->
            // If there are any errors, return them; otherwise,
            // return the map containing the expanded macros.
            match errors with
            | [| |] ->
                assert (Set.isEmpty badMacros)
                Choice1Of2 macroEnv
            | errors ->
                Choice2Of2 (macroEnv, badMacros, errors)

        | (macroId, _ as macro) :: macros ->
            // Validate/process this macro.
            match preprocessMacro macro options (macroEnv, badMacros) with
            | Choice2Of2 macroErrors ->
                // Add this macro's identifier to the set of bad macros.
                let badMacros = Set.add macroId badMacros

                // Append the error messages to the existing error messages.
                let errors = Array.append errors macroErrors

                // Process the remaining macros.
                preprocessMacros macros errors (macroEnv, badMacros)

            | Choice1Of2 preprocessedMacroPattern ->
                // Add the processed macro pattern to the processed macro map.
                let macroEnv = Map.add macroId preprocessedMacroPattern macroEnv

                // Process the remaining macros.
                preprocessMacros macros errors (macroEnv, badMacros)

    // Reverse the macro list so the macros will be processed in
    // top-to-bottom order (i.e., as they were in the lexer
    // definition), then call the preprocessor function.
    preprocessMacros (List.rev macros) Array.empty (Map.empty, Set.empty)

//
let private validateAndSimplifyPattern pattern (macroEnv, badMacros, options : CompilationOptions) =
    //
    // OPTIMIZE : Modify this function to use a LazyList to hold the errors
    // instead of an F# list to avoid the list concatenation overhead.
    let rec validateAndSimplify pattern cont =
        match pattern with
        | LexerPattern.Epsilon ->
            Choice1Of2 Regex.Epsilon
            |> cont

        | LexerPattern.CharacterSet charSet ->
            // Make sure all of the characters in the set are ASCII characters unless the 'Unicode' option is set.
            if options.Unicode || CharSet.forall (fun c -> int c <= 255) charSet then
                Regex.CharacterSet charSet
                |> Choice1Of2
                |> cont
            else
                ["Unicode characters may not be used in patterns unless the 'Unicode' compiler option is set."]
                |> Choice2Of2
                |> cont

        | LexerPattern.Macro macroId ->
            match Map.tryFind macroId macroEnv with
            | None ->
                // Check the 'bad macros' set to avoid returning an error message
                // for this pattern when the referenced macro contains an error.
                if Set.contains macroId badMacros then
                    // We have to return something, so return Empty to
                    // take the place of this macro reference.
                    Choice1Of2 Regex.Empty
                    |> cont
                else
                    Choice2Of2 [ sprintf "The macro '%s' is not defined." macroId ]
                    |> cont
            | Some nestedMacro ->
                // Return the pattern for the nested macro so it'll be "inlined" into this pattern.
                Choice1Of2 nestedMacro
                |> cont

        | LexerPattern.UnicodeCategory unicodeCategory ->
            if options.Unicode then
                // Return the CharSet representing this UnicodeCategory.
                match Map.tryFind unicodeCategory Unicode.categoryCharSet with
                | None ->
                    [ sprintf "Unknown or invalid Unicode category specified. (Category = %i)" <| int unicodeCategory ]
                    |> Choice2Of2
                    |> cont
                | Some charSet ->
                    Regex.CharacterSet charSet
                    |> Choice1Of2
                    |> cont
            else
                ["Unicode category patterns may not be used unless the 'Unicode' compiler option is set."]
                |> Choice2Of2
                |> cont

        | LexerPattern.Negate r ->
            validateAndSimplify r <| fun rResult ->
                match rResult with
                | (Choice2Of2 _ as err) -> err
                | Choice1Of2 r ->
                    Regex.Negate r
                    |> Choice1Of2
                |> cont

        | LexerPattern.Star r ->
            validateAndSimplify r <| fun rResult ->
                match rResult with
                | (Choice2Of2 _ as err) -> err
                | Choice1Of2 r ->
                    Regex.Star r
                    |> Choice1Of2
                |> cont

        | LexerPattern.Concat (r, s) ->
            validateAndSimplify r <| fun rResult ->
            validateAndSimplify s <| fun sResult ->
                match rResult, sResult with
                | Choice2Of2 rErrors, Choice2Of2 sErrors ->
                    Choice2Of2 (rErrors @ sErrors)
                | (Choice2Of2 _ as err), Choice1Of2 _
                | Choice1Of2 _, (Choice2Of2 _ as err) ->
                    err
                | Choice1Of2 r, Choice1Of2 s ->
                    Regex.Concat (r, s)
                    |> Choice1Of2
                |> cont

        | LexerPattern.And (r, s) ->
            validateAndSimplify r <| fun rResult ->
            validateAndSimplify s <| fun sResult ->
                match rResult, sResult with
                | Choice2Of2 rErrors, Choice2Of2 sErrors ->
                    Choice2Of2 (rErrors @ sErrors)
                | (Choice2Of2 _ as err), Choice1Of2 _
                | Choice1Of2 _, (Choice2Of2 _ as err) ->
                    err
                | Choice1Of2 r, Choice1Of2 s ->
                    Regex.And (r, s)
                    |> Choice1Of2
                |> cont

        | LexerPattern.Or (r, s) ->
            validateAndSimplify r <| fun rResult ->
            validateAndSimplify s <| fun sResult ->
                match rResult, sResult with
                | Choice2Of2 rErrors, Choice2Of2 sErrors ->
                    Choice2Of2 (rErrors @ sErrors)
                | (Choice2Of2 _ as err), Choice1Of2 _
                | Choice1Of2 _, (Choice2Of2 _ as err) ->
                    err
                | Choice1Of2 r, Choice1Of2 s ->
                    Regex.Or (r, s)
                    |> Choice1Of2
                |> cont

        (*  Extended patterns are rewritten using the cases of LexerPattern
            which have corresponding cases in Regex. *)
        | LexerPattern.Empty ->
            Regex.CharacterSet CharSet.empty
            |> Choice1Of2
            |> cont
        
        | LexerPattern.Any ->
            Choice1Of2 Regex.Any
            |> cont

        | LexerPattern.Character c ->
            // Make sure the character is an ASCII character unless the 'Unicode' option is set.
            if options.Unicode || int c <= 255 then
                Regex.Character c
                |> Choice1Of2
                |> cont
            else
                ["Unicode characters may not be used in patterns unless the 'Unicode' compiler option is set."]
                |> Choice2Of2
                |> cont

        | LexerPattern.OneOrMore r ->
            // Rewrite r+ as rr*
            let rewritten =
                LexerPattern.Concat (r, LexerPattern.Star r)

            // Process the rewritten expression.
            validateAndSimplify rewritten cont

        | LexerPattern.Optional r ->
            // Rewrite r? as (|r)
            let rewritten =
                LexerPattern.Concat (LexerPattern.Epsilon, r)

            // Process the rewritten expression.
            validateAndSimplify rewritten cont

        | LexerPattern.Repetition (r, atLeast, atMost) ->
            // If not specified, the lower bound defaults to zero (0).
            let atLeast = defaultArg atLeast LanguagePrimitives.GenericZero

            // TODO : Rewrite this pattern using simpler cases.
            raise <| System.NotImplementedException "validateAndSimplifyPattern"

            // Process the rewritten expression.
            //validateAndSimplify rewritten cont

    // Call the function which traverses the pattern to validate/preprocess it.
    validateAndSimplify pattern <| function
        | Choice2Of2 errors ->
            List.rev errors
            |> List.toArray
            |> Choice2Of2
        | Choice1Of2 processedPattern ->
            Choice1Of2 processedPattern

//
let private compileRule (rule : Rule) (options : CompilationOptions) (macroEnv, badMacros) =
    let ruleClauses =
        // The clauses are provided in reverse order from the way they're
        // specified in the lexer definition, so reverse them to put them
        // in the correct order.
        // NOTE : The ordering only matters when two or more clauses overlap,
        // because then the ordering is used to decide which action to execute.
        rule.Clauses
        |> List.rev
        |> List.toArray
    
    // Validate and simplify the patterns of the rule clauses.
    let simplifiedRuleClausePatterns =
        let simplifiedRuleClausePatterns =
            ruleClauses
            |> Array.map (fun clause ->
                validateAndSimplifyPattern clause.Pattern (macroEnv, badMacros, options))

        // Put all of the "results" in one array and all of the "errors" in another.
        let results = ResizeArray<_> (Array.length simplifiedRuleClausePatterns)
        let errors = ResizeArray<_> (Array.length simplifiedRuleClausePatterns)
        simplifiedRuleClausePatterns
        |> Array.iter (function
            | Choice2Of2 errorArr ->
                errors.AddRange errorArr
            | Choice1Of2 result ->
                results.Add result)

        // If there are any errors, return them; otherwise, return the results.
        if errors.Count > 0 then
            Choice2Of2 <| errors.ToArray ()
        else
            Choice1Of2 <| results.ToArray ()

    //
    match simplifiedRuleClausePatterns with
    | Choice2Of2 errors ->
        Choice2Of2 errors
    | Choice1Of2 ruleClauseRegexes ->
        /// The DFA compiled from the rule clause patterns.
        let compiledPatternDfa = rulePatternsToDfa ruleClauseRegexes options

        // TODO : Emit warnings about any overlapping patterns.
        // E.g., "This pattern will never be matched."

        // Create a CompiledRule record from the compiled DFA.
        Choice1Of2 {
            Dfa = compiledPatternDfa;
            RuleClauseActions =
                ruleClauses
                |> Array.map (fun clause ->
                    clause.Action); }

/// A compiled lexer specification.
type CompiledSpecification = {
    //
    Header : CodeFragment option;
    //
    Footer : CodeFragment option;
    //
    CompiledRules : Map<RuleIdentifier, CompiledRule>;
    //
    StartRule : RuleIdentifier;
}

/// Creates pattern-matching DFAs from the lexer rules.
let lexerSpec (spec : Specification) options =
    // Validate and simplify the macros to create the macro table/environment.
    match preprocessMacros spec.Macros options with
    | Choice2Of2 (macroEnv, badMacros, errors) ->
        // TODO : Validate the rule clauses, but don't compile the rule DFAs.
        // This way we can return all applicable errors instead of just those for the macros.
        Choice2Of2 errors
    | Choice1Of2 macroEnv ->
        (* Compile the lexer rules *)
        (* TODO :   Simplify the code below using monadic operators. *)
        let ruleIdentifiers, rules =
            let ruleIdentifiers = Array.zeroCreate spec.Rules.Count
            let rules = Array.zeroCreate spec.Rules.Count

            (0, spec.Rules)
            ||> Map.fold (fun index ruleId rule ->
                ruleIdentifiers.[index] <- ruleId
                rules.[index] <- rule
                index + 1)
            |> ignore

            ruleIdentifiers, rules

        let compiledRules, compilationErrors =
            let compiledRulesOrErrors =
                rules
                |> Array.Parallel.map (fun rule ->
                    compileRule rule options (macroEnv, Set.empty))

            let compiledRules = ResizeArray<_> (Array.length rules)
            let compilationErrors = ResizeArray<_> (Array.length rules)

            compiledRulesOrErrors
            |> Array.iter (function
                | Choice1Of2 compiledRule ->
                    compiledRules.Add compiledRule
                | Choice2Of2 errors ->
                    compilationErrors.AddRange errors)

            compiledRules.ToArray (),
            compilationErrors.ToArray ()

        // If there are errors, return them; otherwise, create a
        // CompiledSpecification record from the compiled rules.
        if Array.isEmpty compilationErrors then
            Choice1Of2 {
                Header = spec.Header;
                Footer = spec.Footer;
                CompiledRules =
                    (Map.empty, ruleIdentifiers, compiledRules)
                    |||> Array.fold2 (fun map ruleId compiledRule ->
                        Map.add ruleId compiledRule map);
                StartRule = spec.StartRule; }
        else
            Choice2Of2 compilationErrors
