[<ReflectedDefinition>]
module Ionide.Fsi

open System
open Atom
open Atom.FSharp 
open Atom.FSharp.Control

open FunScript
open FunScript.TypeScript
open FunScript.TypeScript.fs
open FunScript.TypeScript.child_process
open FunScript.TypeScript.AtomCore
open FunScript.TypeScript.text_buffer
open FunScript.TypeScript.path

// --------------------------------------------------------------------------------------
// Additional FunScript bindings
// --------------------------------------------------------------------------------------

[<AutoOpen>]
module Bindings =
    [<JSEmitInline("new FsiView()")>]
    let newFsiView () : FunScript.TypeScript.atom.ScrollView = failwith "JS"

    [<JSEmitInline("{0}.getTitle()")>]
    let getTitle (item:obj) :string = failwith "JS"

    type IWorkspace with
        [<FunScript.JSEmitInline("({0}.addOpener({1}))")>]
        member __.addOpener(cb: string -> obj) : unit = failwith "JS"
        [<FunScript.JSEmitInline("({0}.paneForItem({1}))")>]
        member __.paneForItem(o : obj) : obj = failwith "JS"

// --------------------------------------------------------------------------------------
// Manipulating the F# Interactive panel in various ways
// --------------------------------------------------------------------------------------

type SendSnippetKind = 
    | Line 
    | File
    | Selection

/// Remove any whitespace and also the specified suffix from a string
let trimEnd (suffix:string) (text:string) = 
    let text = text.Trim()
    let text = if text.EndsWith(suffix) then text.Substring(0, text.Length-suffix.Length) else text
    text.Trim()

/// Go to an opened editor with the specified file or open a new one
let private navigateToEditor file line col =
    // Try to go to an existing opened editor
    // In theory, `workspace.open` does this automatically, but in
    // reality, it does not work when the item is in another panel :(
    let mutable found = false
    for pane in unbox<IPane[]> (Globals.atom.workspace.getPanes() ) do
      for item in pane.getItems() do
        try 
          let ed = unbox<IEditor> item
          if ed.getPath() = file then 
            pane.activate()
            pane.activateItem(ed) |> ignore
            ed.setCursorBufferPosition [| line; col |] |> ignore
            found <- true
        with _ -> ()
    // If it did not exist, open a new one
    if not found then
      Globals.atom.workspace._open(file, {initialLine=line; initialColumn=col}) |> ignore

/// Removes everything currently displayed
let private clearFsiPane () =
    jq(".fsi").empty() |> ignore

/// Append panel with input or output 
let private appendFsiPanel icon cssclass expanded (input:string) =
    let pre = jq("<pre />").addClass("").text(if expanded then input else "(...)")
    let icon = jq("<span />").addClass("icon " + if expanded then "icon-chevron-down" else "icon-chevron-right")
    let padding = jq("<div class='inset-panel padded'/>").append(icon).append(pre)
    jq("<atom-panel />").addClass("top fsi-block " + cssclass)
      .click(fun _ ->
          if pre.text() = "(...)" then 
              icon.attr("class", "icon icon-chevron-down") |> ignore
              pre.text(input) |> ignore
          else 
              icon.attr("class", "icon icon-chevron-right") |> ignore
              pre.text("(...)") |> ignore
          obj() )
      .append(jq("<div class='padded'/>").append(padding))
      .appendTo(jq(".fsi")) |> ignore

/// Append input entered by the user
let private appendFsiInput input = appendFsiPanel "icon-chevron-right" "fsi-in-block" false input

/// Append output produced by FSI (typically in background)
let private appendFsiOutput res = appendFsiPanel "icon-chevron-down" "fsi-succ-block" true (trimEnd ">" res)

/// Append exception reproted by the evaluation
let private appendFsiException text = appendFsiPanel "icon-flame" "fsi-exn-block" true text

/// Append error messages or warnings to the FSI output
let private appendErrors errors = 
    let fsi = jq(".fsi")
    if Array.length errors = 0 then () else
    let block = jq("<div />").addClass("fsi-error-block").appendTo(fsi)
    for err in errors do 
        let niceSeverity = err.severity.[0].ToString().ToUpper() + err.severity.Substring(1)
        let niceMsg = trimEnd "." err.message
        jq("<div />").addClass("fsi-" + err.severity)
          .append(jq("<span class=\"inline-block highlight-" + err.severity + "\">" + niceSeverity + " #" + string err.errorNumber + "</span>"))
          .append(jq("<span class='msg'/>").text(niceMsg))
          .append(jq("<a class='loc'/>").text(" at line " + string err.startLine + " col " + string err.startColumn).click(fun _ ->
              navigateToEditor err.fileName (err.startLine-1) err.startColumn |> box ))
          .appendTo(block) |> ignore

/// Apend the result of running some command (Alt+Enter)
let private appendFsiResult res = 
    match res with
    | Error(errs) -> 
        appendErrors errs.details
    | Exception(exn) -> 
        appendFsiException exn.details
    | Success(succ) ->
        appendErrors succ.details.warnings
        appendFsiOutput succ.output
        if succ.details.html <> null then
            jq(".fsi").append("<div class='html'>" + succ.details.html + "</div>") |> ignore
    jq(".fsi").scrollTop(99999999.) |> ignore


/// Find the "F# Interactive" panel
let private tryFindFsiPane () = 
    let panes = unbox<IPane[]> (Globals.atom.workspace.getPanes())
    [ for pane in panes do
          for item in pane.getItems() do
              if getTitle item = "F# Interactive" then yield pane, item ]
    |> List.tryPick Some
        
/// Opens or activates the F# Interactive panel
let private openFsiPane () =
    Async.FromContinuations(fun (cont, econt, ccont) ->
        match tryFindFsiPane () with
        | Some(pane, item) ->
            // Activate FSI and then switch back
            let prevPane = Globals.atom.workspace.getActivePane()
            let prevItem = prevPane.getActiveItem()
            pane.activateItem(item) |> ignore
            pane.activate() |> ignore
            prevPane.activate() |> ignore
            prevPane.activateItem(prevItem) |> ignore
            cont ()
        | None ->
            Globals.atom.workspace
              ._open("F# Interactive", {split = "right"})
              ._done((fun ed -> cont ()) |> unbox<Function>))

/// Get the code for current file/line/selection, together with line number
let private getFsiSelection kind =
    let editor = Globals.atom.workspace.getActiveTextEditor()
    match kind with
    | File -> 1, editor.getText() 
    | Line -> 
        let line = editor.getCursorBufferPosition().row
        int line+1, editor.lineTextForBufferRow(line).Trim()
    | Selection ->
        let line = editor.getSelectedBufferRange().start.row
        int line+1, editor.getSelectedText()

/// Send the current line/file/selection to FSI
let private sendToFsi kind = async {
    let editor = Globals.atom.workspace.getActiveTextEditor()
    if isFSharpEditor editor then
        // Get selection *before* openinig FSI (it changes focus)
        let line, code = getFsiSelection kind
        do! openFsiPane()
        appendFsiInput code

        let! res = InteractiveServer.eval (editor.getPath()) line (code + ";;")
        appendFsiResult res }

// Repeatedly check for output produced in background
let private checkOutputLoop () = async {
    while InteractiveServer.isRunning() do
        if jq(".fsi").length > 0.0 then
            let! output = InteractiveServer.output()
            if output <> null && output <> "" then appendFsiOutput output
        do! Async.Sleep(500) }


// --------------------------------------------------------------------------------------
// Activate / deactivate the FSI plugin
// --------------------------------------------------------------------------------------

[<ReflectedDefinition>]
type Fsi() =
    member x.activate(state:obj) =
        // Start the F# Interactive Suave server & check for outputs
        InteractiveServer.start ()
        checkOutputLoop () |> Async.StartImmediate

        // Register command to open F# Interactive & handler that loads the FSI panel GUI
        Globals.atom.commands.add("atom-workspace", "FSI:Open", fun () ->
            Async.StartImmediate (openFsiPane ()))
        Globals.atom.workspace.addOpener(fun uri ->
            try 
                if uri.EndsWith "F# Interactive" then box (newFsiView ())
                else null
            with _ -> null)

        // Register commands that close/reset/clear the current FSI output
        Globals.atom.commands.add("atom-text-editor", "FSI:Reset-REPL", fun () ->
            InteractiveServer.reset() |> Async.StartImmediate
            clearFsiPane() )
        Globals.atom.commands.add("atom-text-editor", "FSI:Cancel-REPL", fun () ->
            InteractiveServer.cancel() |> Async.StartImmediate )
        Globals.atom.commands.add("atom-text-editor", "FSI:Clear-REPL", fun () ->
            clearFsiPane() )

        // Register commands that send some F# code to F# Interactive
        Globals.atom.commands.add("atom-text-editor", "FSI:Send-Line", fun () ->
            sendToFsi Line |> Async.StartImmediate)
        Globals.atom.commands.add("atom-text-editor", "FSI:Send-Selection", fun () ->
            sendToFsi Selection |> Async.StartImmediate)
        Globals.atom.commands.add("atom-text-editor", "FSI:Send-File", fun () ->
            sendToFsi File |> Async.StartImmediate)


    member x.deactivate() =
        // Stop the F# Interactive Suave server
        InteractiveServer.stop ()
