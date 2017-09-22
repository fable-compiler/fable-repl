module Fable.REPL.FCS

open System
open System.Collections.Generic
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices
open FsAutoComplete
open Interfaces
open Fable
open Fable.AST
open Fable.Core
open Fable.State

type private CheckerImpl(c: InteractiveChecker) =
    member this.Checker = c
    interface IChecker

type private CompilerImpl(c: Compiler) =
    member this.Compiler = c
    interface IFableCompiler

type ParseResults =
    { ParseFile: FSharpParseFileResults
      CheckFile: FSharpCheckFileResults
      CheckProject: FSharpCheckProjectResults }
    interface IParseResults with
        member this.Errors = this.CheckProject.Errors |> Array.map (fun er ->
            { StartLineAlternate = er.StartLineAlternate
              StartColumn = er.StartColumn
              EndLineAlternate = er.EndLineAlternate
              EndColumn = er.EndColumn
              Message = er.Message
              IsWarning =
                match er.Severity with
                | FSharpErrorSeverity.Error -> false
                | FSharpErrorSeverity.Warning -> true
            })        

let findLongIdentsAndResidue (col, lineStr:string) =
  let lineStr = lineStr.Substring(0, col)
  match Lexer.getSymbol 0 col lineStr Lexer.SymbolLookupKind.ByLongIdent [||] with
  | Some sym ->
      match sym.Text with
      | "" -> [], ""
      | text ->
          let res = text.Split '.' |> List.ofArray |> List.rev
          if lineStr.[col - 1] = '.' then res |> List.rev, ""
          else
              match res with
              | head :: tail -> tail |> List.rev, head
              | [] -> [], ""
  | _ -> [], ""

let convertGlyph glyph =
    match glyph with
    | FSharpGlyph.Class | FSharpGlyph.Struct | FSharpGlyph.Union
    | FSharpGlyph.Type | FSharpGlyph.Typedef ->
        Glyph.Class
    | FSharpGlyph.Enum | FSharpGlyph.EnumMember ->
        Glyph.Enum
    | FSharpGlyph.Constant ->
        Glyph.Value
    | FSharpGlyph.Variable ->
        Glyph.Variable
    | FSharpGlyph.Interface ->
        Glyph.Interface
    | FSharpGlyph.Module | FSharpGlyph.NameSpace ->
        Glyph.Module
    | FSharpGlyph.Method | FSharpGlyph.OverridenMethod | FSharpGlyph.ExtensionMethod ->
        Glyph.Method
    | FSharpGlyph.Property ->
        Glyph.Property
    | FSharpGlyph.Field ->
        Glyph.Field
    | FSharpGlyph.Delegate ->
        Glyph.Function
    | FSharpGlyph.Error | FSharpGlyph.Exception ->
        Glyph.Error
    | FSharpGlyph.Event ->
        Glyph.Event

let createChecker references readAllBytes =
    InteractiveChecker.Create(List.ofArray references, readAllBytes)

let createCompiler replacements =
    Compiler(replacements = replacements)

let parseFSharpProject (checker: InteractiveChecker) fileName source =
    let parseResults, typeCheckResults, projectResults = checker.ParseAndCheckScript (fileName, source)      
    { ParseFile = parseResults
      CheckFile = typeCheckResults
      CheckProject = projectResults }

/// Get tool tip at the specified location
let getToolTipAtLocation (typeCheckResults: FSharpCheckFileResults) line col lineText =
    typeCheckResults.GetToolTipText(line, col, lineText, [], FSharpTokenTag.IDENT)

let getCompletionsAtLocation (parseResults: ParseResults) line col lineText = async {
    let longName, residue = findLongIdentsAndResidue(col - 1, lineText)
    let! decls = parseResults.CheckFile.GetDeclarationListInfo(Some parseResults.ParseFile, line, col, lineText, longName, residue, (fun () -> []))
    return decls.Items |> Array.map (fun decl ->
        { Name = decl.Name; Glyph = convertGlyph decl.Glyph })
}

let makeProjOptions (com: ICompiler) projFile =
    let projOptions: FSharpProjectOptions =
      { ProjectFileName = projFile
        SourceFiles = [| |]
        OtherOptions = [| |]
        ReferencedProjects = [| |]
        IsIncompleteTypeCheckEnvironment = false
        UseScriptResolutionRules = false
        LoadTime = DateTime.Now
        UnresolvedReferences = None
        OriginalLoadReferences = []
        ExtraProjectInfo = None
        Stamp = None }
    projOptions

let compileAst (com: Compiler) (parseResults: ParseResults) (fableCoreDir: string) fileName =
    let fableCoreDir =
        if fableCoreDir.StartsWith(".")
        then FilePath fableCoreDir
        else NonFilePath fableCoreDir
    // let errors = com.ReadAllLogs() |> Map.tryFind "error"
    // if errors.IsSome then failwith (errors.Value |> String.concat "\n")
    let projectOptions = makeProjOptions com fileName
    let project = Project(projectOptions, parseResults.CheckProject, fableCoreDir, isWatchCompile=false)
    let file: Babel.Program =
        FSharp2Fable.Compiler.transformFile com project project.CheckedProject fileName
        |> Fable2Babel.Compiler.transformFile com project
    let loc = defaultArg file.loc SourceLocation.Empty
    Babel.Program(file.fileName, loc, file.body, file.directives, com.ReadAllLogs())

type private ExportsImpl() =
    interface IFableREPL with
        member this.CreateChecker(references, readAllBytes) =
            createChecker references readAllBytes |> CheckerImpl :> IChecker
        member this.CreateCompiler(replacements) =
            let replacements = defaultArg replacements (upcast [||])
            createCompiler replacements |> CompilerImpl :> IFableCompiler
        member this.ParseFSharpProject(checker, fileName, source) =
            let c = checker :?> CheckerImpl
            parseFSharpProject c.Checker fileName source :> IParseResults
        member this.GetCompletionsAtLocation(parseResults:IParseResults, line:int, col:int, lineText:string) =
            let res = parseResults :?> ParseResults
            getCompletionsAtLocation res line col lineText
        member this.CompileToBabelJsonAst(com: IFableCompiler, parseResults:IParseResults, fableCoreDir:string, fileName:string) =
            let com = com :?> CompilerImpl
            let res = parseResults :?> ParseResults
            compileAst com.Compiler res fableCoreDir fileName |> JsInterop.toJson


let Exports: IFableREPL = upcast ExportsImpl()


