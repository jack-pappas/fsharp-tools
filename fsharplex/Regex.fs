﻿(*
Copyright (c) 2012, Jack Pappas
All rights reserved.

This code is provided under the terms of the 2-clause ("Simplified") BSD license.
See LICENSE.TXT for licensing details.
*)

//
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FSharpLex.Regex

open LanguagePrimitives
open SpecializedCollections

(*  Turn off warning about uppercase variable identifiers;
    some variables in the code below are named using the
    F# backtick syntax so they can use names which closely
    match those in the paper. *)
#nowarn "49"


/// <summary>A regular expression.</summary>
/// <remarks>This type includes some cases which are normally referred to as "extended"
/// regular expressions. However, only those cases which are still closed under boolean
/// operations are included here, so the lanugage it represents must still be a regular
/// language.</remarks>
type Regex =
    /// The empty string.
    | Epsilon
    /// A set of characters.
    | CharacterSet of CharSet
    /// Negation.
    | Negate of Regex
    /// Kleene *-closure.
    /// The specified Regex will be matched zero (0) or more times.
    | Star of Regex
    /// Concatenation. A Regex immediately followed by another Regex.
    | Concat of Regex * Regex
    /// Choice between two regular expressions (i.e., boolean OR).
    | Or of Regex * Regex
    /// Boolean AND of two regular expressions.
    | And of Regex * Regex

    (* TODO :   Remove these -- we'll leave them in ExtendedRegex for convenience
                but they can all be handled by the CharacterSet case here. *)
    /// The empty language.
    | Empty
    /// Any character except for newline ('\n').
    | Any
    /// A character.
    | Character of char

    //
    static member private IsNullableImpl regex cont =
        match regex with
        | Epsilon
        | Star _ ->
            cont true
        | Empty
        | Any
        | Character _
        | CharacterSet _ ->
            cont false
        | Negate regex ->
            Regex.IsNullableImpl regex (cont >> not)
        | Concat (a, b)
        | And (a, b) ->
            // IsNullable(a) && IsNullable(b)
            Regex.IsNullableImpl a <| fun a ->
                if not a then false
                else
                    Regex.IsNullableImpl b cont
        | Or (a, b) ->
            // IsNullable(a) || IsNullable(b)
            Regex.IsNullableImpl a <| fun a ->
                if a then true
                else
                    Regex.IsNullableImpl b cont

    /// Determines if a specified Regex is 'nullable',
    /// i.e., it accepts the empty string (epsilon).
    static member IsNullable (regex : Regex) =
        Regex.IsNullableImpl regex id

    //
    static member private DerivativeImpl wrtSymbol regex cont =
        match regex with
        | Empty
        | Epsilon ->
            cont Empty
        | Any ->
            raise <| System.NotImplementedException "DerivativeImpl"
        | Character c ->
            if c = wrtSymbol then Epsilon else Empty
            |> cont
        | CharacterSet charSet ->
            if CharSet.contains wrtSymbol charSet then Epsilon else Empty
            |> cont
        | Negate r ->
            Regex.DerivativeImpl wrtSymbol r <| fun r' ->
                Negate r'
                |> cont
        | Star r as ``r*`` ->
            Regex.DerivativeImpl wrtSymbol r <| fun r' ->
                Concat (r', ``r*``)
                |> cont
        | Concat (r, s) ->
            Regex.DerivativeImpl wrtSymbol r <| fun r' ->
            Regex.DerivativeImpl wrtSymbol s <| fun s' ->
                let ``nu(r)`` = if Regex.IsNullable r then Epsilon else Empty
                Or (Concat (r', s),
                    Concat (``nu(r)``, s'))
                |> cont        
        | Or (r, s) ->
            Regex.DerivativeImpl wrtSymbol r <| fun r' ->
            Regex.DerivativeImpl wrtSymbol s <| fun s' ->
                Or (r', s')
                |> cont
        | And (r, s) ->
            Regex.DerivativeImpl wrtSymbol r <| fun r' ->
            Regex.DerivativeImpl wrtSymbol s <| fun s' ->
                And (r', s')
                |> cont        

    /// Computes the derivative of a Regex with respect to a specified symbol.
    static member Derivative symbol regex =
        Regex.DerivativeImpl symbol regex id

    /// Given a CharSet from a CharacterSet case, returns the simplest Regex
    /// representing the CharSet.
    static member inline private SimplifyCharacterSet (charSet : CharSet) =
        match CharSet.count charSet with
        | 0 ->
            Empty
        | 1 ->
            CharSet.minElement charSet
            |> Character
        | _ ->
            CharacterSet charSet

    /// Implementation of the canonicalization function.
    static member private CanonicalizeImpl (regex : Regex) (charUniverse : CharSet) (cont : Regex -> Regex) =
        match regex with
        | Empty
        | Epsilon
        | Any
        | Character _ as regex ->
            cont regex

        | CharacterSet charSet as charSetRegex ->
            match CharSet.count charSet with
            | 0 ->
                Empty
            | 1 ->
                CharSet.minElement charSet
                |> Character
            | _ ->
                charSetRegex
            |> cont

        | Negate Empty ->
            cont Any
        | Negate Any ->
            cont Empty        
        | Negate (Character c) ->
             let anyMinusChar = CharSet.remove c charUniverse
             Regex.SimplifyCharacterSet anyMinusChar
             |> cont
        | Negate (CharacterSet charSet) ->
             let anyMinusCharSet = CharSet.difference charUniverse charSet
             Regex.SimplifyCharacterSet anyMinusCharSet
             |> cont
        | Negate (Negate regex) ->
            Regex.CanonicalizeImpl regex charUniverse cont
        | Negate _ as notRegex ->
            // This regex is canonical
            cont notRegex

        | Star (Star _ as ``r*``) ->
            Regex.CanonicalizeImpl ``r*`` charUniverse cont
        | Star Epsilon
        | Star Empty ->
            cont Epsilon
        | Star (Or (Epsilon, r))
        | Star (Or (r, Epsilon)) ->
            Regex.CanonicalizeImpl r charUniverse <| fun r' ->
                Star r'
                |> cont
        | Star _ as ``r*`` ->
            // This regex is canonical
            cont ``r*``

        | Concat (r, Concat (s, t)) ->
            // Rewrite the expression so it's left-associative.
            let regex = Concat (Concat (r, s), t)
            Regex.CanonicalizeImpl regex charUniverse cont        
        | Concat (r, s) ->
            Regex.CanonicalizeImpl r charUniverse <| fun r' ->
            Regex.CanonicalizeImpl s charUniverse <| fun s' ->
                // Try to simplify the expression, using the canonicalized components.
                match r', s' with
                | Empty, _
                | _, Empty ->
                    cont Empty
                | Epsilon, regex
                | regex, Epsilon ->
                    Regex.CanonicalizeImpl regex charUniverse cont
                | r', s' ->
                    Concat (r', s')
                    |> cont

        | Or (Empty, r)
        | Or (r, Empty) ->
            Regex.CanonicalizeImpl r charUniverse cont
        | Or (Any, _)
        | Or (_, Any) ->
            cont Any
        | Or (r, Or (s, t)) ->
            // Rewrite the expression so it's left-associative.
            let regex = Or (Or (r, s), t)
            Regex.CanonicalizeImpl regex charUniverse cont
        | Or (r, s) ->
            Regex.CanonicalizeImpl r charUniverse <| fun r' ->
            Regex.CanonicalizeImpl s charUniverse <| fun s' ->
                // Try to simplify the expression, using the canonicalized components.
                match r', s' with
                | r', s' when r' = s' ->
                    r'
                | (Character c1 as charRegex), Character c2 ->
                    if c1 = c2 then charRegex
                    else
                        CharSet.empty
                        |> CharSet.add c1
                        |> CharSet.add c2
                        |> CharacterSet

                | Character c, CharacterSet charSet
                | CharacterSet charSet, Character c ->
                    CharSet.add c charSet
                    |> CharacterSet

                | CharacterSet charSet1, CharacterSet charSet2 ->
                    // 'Or' is the disjunction (union) of two Regexes.
                    let charSetUnion = CharSet.union charSet1 charSet2

                    // Return the simplest Regex for the union set.
                    Regex.SimplifyCharacterSet charSetUnion

                | r', s' ->
                    // Sort the components before returning; this takes care
                    // of the symmetry rule (r | s) = (s | r) so the
                    // "approximately equal" relation is simply handled by
                    // F#'s structural equality.
                    if r' < s' then Or (r', s')
                    else Or (s', r')
                |> cont
        
        | And (Empty, _)
        | And (_, Empty) ->
            cont Empty
        | And (Any, r)
        | And (r, Any) ->
            Regex.CanonicalizeImpl r charUniverse cont
        | And (r, And (s, t)) ->
            // Rewrite the expression so it's left-associative.
            let regex = And (And (r, s), t)
            Regex.CanonicalizeImpl regex charUniverse cont
        | And (r, s) ->
            Regex.CanonicalizeImpl r charUniverse <| fun r' ->
            Regex.CanonicalizeImpl s charUniverse <| fun s' ->
                // Try to simplify the expression, using the canonicalized components.
                match r', s' with
                | r', s' when r' = s' ->
                    r'
                | (Character c1 as charRegex), Character c2 ->
                    if c1 = c2 then charRegex
                    else
                        // TODO : Emit a warning to TraceListener?
                        // The 'And' case represents a conjunction (intersection) of two Regexes;
                        // since the characters are different, the intersection must be empty.
                        Empty

                | Character c, CharacterSet charSet
                | CharacterSet charSet, Character c ->
                    CharSet.add c charSet
                    |> CharacterSet

                | CharacterSet charSet1, CharacterSet charSet2 ->
                    // 'And' is the conjunction (intersection) of two Regexes.
                    let charSetIntersection = CharSet.intersect charSet1 charSet2

                    // Return the simplest Regex for the intersection set.
                    Regex.SimplifyCharacterSet charSetIntersection

                | r', s' ->
                    // Sort the components before returning; this takes care
                    // of the symmetry rule (r & s) = (s & r) so the
                    // "approximately equal" relation is simply handled by
                    // F#'s structural equality.
                    if r' < s' then And (r', s')
                    else And (s', r')
                |> cont

    /// Creates a new Regex in canonical form from the given Regex.
    static member Canonicalize (regex : Regex, universe) : Regex =
        // Preconditions
        if CharSet.isEmpty universe then
            invalidArg "universe" "The character universe (set of all characters used in the lexer) is empty."
        
        Regex.CanonicalizeImpl regex universe id

    /// Computes a conservative approximation of the intersection of two sets of
    /// derivative classes. This is needed when computing the set of derivative
    /// classes for a compound regular expression ('And', 'Or', and 'Concat').
    static member IntersectionOfDerivativeClasses (``C(r)``, ``C(s)``) =
        (Set.empty, ``C(r)``)
        ||> Set.fold (fun intersections el1 ->
            (intersections, ``C(s)``)
            ||> Set.fold (fun intersections el2 ->
                // The intersection of the two elements (character sets)
                let elementIntersection = CharSet.intersect el1 el2

                // Add the intersection to the set of intersections
                // (if it's already in the set, no error is thrown).
                Set.add elementIntersection intersections))

    //
    static member private DerivativeClassesImpl regex universe cont =
        match regex with
        | Epsilon
        | Empty ->
            Set.singleton universe
            |> cont
        | Any ->
            Set.singleton universe
            |> Set.add CharSet.empty
            |> cont
        | Character c ->
            Set.singleton (CharSet.singleton c)
            |> Set.add (CharSet.remove c universe)
            |> cont
        | CharacterSet charSet ->
            Set.singleton charSet
            |> Set.add (CharSet.difference universe charSet)
            |> cont
        | Negate r
        | Star r ->
            Regex.DerivativeClassesImpl r universe cont
        | Concat (r, s) ->
            if not <| Regex.IsNullable r then
                Regex.DerivativeClassesImpl r universe cont
            else
                Regex.DerivativeClassesImpl r universe <| fun ``C(r)`` ->
                Regex.DerivativeClassesImpl s universe <| fun ``C(s)`` ->
                    Regex.IntersectionOfDerivativeClasses (``C(r)``, ``C(s)``)
                    |> cont
        | Or (r, s)
        | And (r, s) ->
            Regex.DerivativeClassesImpl r universe <| fun ``C(r)`` ->
            Regex.DerivativeClassesImpl s universe <| fun ``C(s)`` ->
                Regex.IntersectionOfDerivativeClasses (``C(r)``, ``C(s)``)
                |> cont

    /// Computes an approximate set of derivative classes for the specified Regex.
    static member DerivativeClasses (regex : Regex, universe) =
        // Preconditions
        if CharSet.isEmpty universe then
            invalidArg "universe" "The character universe (set of all characters used in the lexer) is empty."
        
        Regex.DerivativeClassesImpl regex universe id

/// An array of regular expressions.
// Definition 4.3.
type RegularVector = Regex[]

/// Functional programming operators related to the RegularVector type.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RegularVector =
    /// Compute the derivative of a regular vector
    /// with respect to the given symbol.
    let inline derivative symbol (regVec : RegularVector) : RegularVector =
        Array.map (Regex.Derivative symbol) regVec

    /// Determines if the regular vector is nullable,
    /// i.e., it accepts the empty string (epsilon).
    let inline isNullable (regVec : RegularVector) =
        // A regular vector is nullable iff any of
        // the component regexes are nullable.
        Array.exists Regex.IsNullable regVec

    /// The indices of the element expressions (if any)
    /// that accept the empty string (epsilon).
    let acceptingElements (regVec : RegularVector) =
        /// The indices of the expressions accepting the empty string.
        let mutable accepting = Set.empty

        let len = Array.length regVec
        for i = 0 to len - 1 do
            if Regex.IsNullable regVec.[i] then
                accepting <- Set.add i accepting

        // Return the computed set of indices.
        accepting

    /// Determines if a regular vector is an empty vector. Note that an
    /// empty regular vector is *not* the same thing as an empty array.
    let inline isEmpty (regVec : RegularVector) =
        // A regular vector is empty iff all of it's entries
        // are equal to the empty character set.
        regVec
        |> Array.forall (function
            | Empty -> true
            | CharacterSet charSet ->
                CharSet.isEmpty charSet
            | _ -> false)

    /// Compute the approximate set of derivative classes of a regular vector.
    let derivativeClasses (regVec : RegularVector) universe =
        // Preconditions
        if Array.isEmpty regVec then
            invalidArg "regVec" "The regular vector does not contain any regular expressions."
        elif CharSet.isEmpty universe then
            invalidArg "universe" "The character universe (set of all characters used in the lexer) is empty."

        regVec
        // Compute the approximate set of derivative classes
        // for each regular expression in the vector.
        |> Array.map (fun r ->
            Regex.DerivativeClasses (r, universe))
        // By Definition 4.3, the approximate set of derivative classes of a regular vector
        // is the intersection of the approximate sets of derivative classes of it's elements.
        |> Array.reduce (
            FuncConvert.FuncFromTupled Regex.IntersectionOfDerivativeClasses)

