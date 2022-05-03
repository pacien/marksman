module Marksman.Server

open System
open System.Collections.Generic
open System.IO
open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.Logging
open FSharpPlus.GenericBuilders

open Marksman.Misc
open Marksman.Parser
open Marksman.Domain
open Microsoft.FSharp.Control

type ClientDescription =
    { info: ClientInfo option
      caps: ClientCapabilities }
    member this.IsVSCode: bool =
        this.info
        |> Option.exists (fun x -> x.Name = "Visual Studio Code")

module ClientDescription =
    let fromParams (par: InitializeParams) : ClientDescription =
        let caps =
            par.Capabilities
            |> Option.defaultValue
                { Workspace = None
                  TextDocument = None
                  Experimental = None }

        { info = par.ClientInfo; caps = caps }

type State =
    { client: ClientDescription
      folders: Map<PathUri, Folder>
      revision: int
      diagnostic: Map<PathUri, array<PathUri * array<Diagnostic>>> }

module State =
    let logger =
        LogProvider.getLoggerByName "State"

    let tryFindFolder (uri: PathUri) (state: State) : option<Folder> =
        let root =
            state.folders
            |> Map.tryFindKey (fun root _ -> uri.AbsolutePath.StartsWith(root.AbsolutePath))

        root
        |> Option.map (fun root -> state.folders[root])

    let findFolder (uri: PathUri) (state: State) : Folder =
        tryFindFolder uri state
        |> Option.defaultWith (fun _ -> failwith $"Expected folder now found: {uri}")

    let tryFindDocument (uri: PathUri) (state: State) : option<Document> =
        tryFindFolder uri state
        |> Option.map (Folder.tryFindDocument uri)
        |> Option.flatten

    let updateFoldersFromLsp (added: WorkspaceFolder []) (removed: WorkspaceFolder []) (state: State) : State =
        logger.trace (
            Log.setMessage "Updating workspace folders"
            >> Log.addContext "numAdded" added.Length
            >> Log.addContext "numRemoved" removed.Length
        )

        let removedUris =
            removed
            |> Array.map (fun f -> PathUri(Uri(f.Uri)))

        let mutable newFolders = state.folders

        for uri in removedUris do
            newFolders <- Map.remove uri newFolders

        let addedFolders =
            seq {
                for f in added do
                    let rootUri = PathUri.fromString f.Uri

                    let folder = Folder.tryLoad f.Name rootUri

                    match folder with
                    | Some folder -> yield rootUri, folder
                    | _ -> ()
            }

        for uri, folder in addedFolders do
            newFolders <- Map.add uri folder newFolders

        { state with folders = newFolders }

    let updateDocument (newDocument: Document) (state: State) : State =
        let folder =
            findFolder newDocument.path state

        let newContent =
            folder.documents
            |> Map.add newDocument.path newDocument

        let newFolder =
            { folder with documents = newContent }

        let newFolders =
            state.folders |> Map.add newFolder.root newFolder

        { state with folders = newFolders }

    let removeDocument (path: PathUri) (state: State) : State =
        let folder = findFolder path state

        let newFolder =
            Folder.removeDocument path folder

        { state with folders = Map.add folder.root newFolder state.folders }

    let findCompletionCandidates (pos: Position) (uri: PathUri) (state: State) : array<CompletionItem> =
        tryFindFolder uri state
        |> Option.map (Folder.findCompletionCandidates pos uri)
        |> Option.defaultValue [||]

let extractWorkspaceFolders (par: InitializeParams) : Map<string, PathUri> =
    match par.WorkspaceFolders with
    | Some folders ->
        folders
        |> Array.map (fun { Name = name; Uri = uri } -> name, Uri(uri) |> PathUri)
        |> Map.ofArray
    | _ ->
        let rootPath =
            par.RootUri
            |> Option.orElse par.RootPath
            |> Option.defaultWith (fun () -> failwith $"No folders configured in workspace: {par}")

        let rootUri = Uri(rootPath) |> PathUri

        let rootName =
            Path.GetFileName(rootUri.AbsolutePath)

        Map.ofList [ rootName, rootUri ]

let readWorkspace (roots: Map<string, PathUri>) : list<Folder> =
    seq {
        for KeyValue (name, root) in roots do
            match Folder.tryLoad name root with
            | Some folder -> yield folder
            | _ -> ()
    }
    |> List.ofSeq

let mkServerCaps (_pars: InitializeParams) : ServerCapabilities =
    let workspaceFoldersCaps =
        { Supported = Some true
          ChangeNotifications = Some true }

    let markdownFilePattern =
        { Glob = "**/*.md"
          Matches = Some FileOperationPatternKind.File
          Options = Some { FileOperationPatternOptions.Default with IgnoreCase = Some true } }

    let markdownFileRegistration =
        { Filters =
            [| { Scheme = None
                 Pattern = markdownFilePattern } |] }

    let workspaceFileCaps =
        { WorkspaceFileOperationsServerCapabilities.Default with
            DidCreate = Some markdownFileRegistration
            DidDelete = Some markdownFileRegistration
            // VSCode behaves weirdly when communicating file renames, so let's turn this off.
            // Anyway, when the file is renamed VSCode sends
            // - didClose on the old name, and
            // - didOpen on the new one
            // which is enough to keep the state in sync.
            DidRename = None }

    let workspaceCaps =
        { WorkspaceServerCapabilities.Default with
            WorkspaceFolders = Some workspaceFoldersCaps
            FileOperations = Some workspaceFileCaps }

    let textSyncCaps =
        { TextDocumentSyncOptions.Default with
            OpenClose = Some true
            Change = Some TextDocumentSyncKind.Incremental }

    { ServerCapabilities.Default with
        Workspace = Some workspaceCaps
        TextDocumentSync = Some textSyncCaps
        DocumentSymbolProvider = Some true
        CompletionProvider =
            Some
                { TriggerCharacters = Some [| '['; ':'; '|'; '@' |]
                  ResolveProvider = None
                  AllCommitCharacters = None }
        DefinitionProvider = Some true
        HoverProvider = Some true }

let rec headingToSymbolInfo (docUri: PathUri) (h: Heading) : SymbolInformation [] =
    let name = h.text.TrimStart([| '#'; ' ' |])
    let name = $"H{h.level}: {name}"
    let kind = SymbolKind.String

    let location =
        { Uri = docUri.Uri.OriginalString
          Range = h.range }

    let sym =
        { Name = name
          Kind = kind
          Location = location
          ContainerName = None }

    let children =
        h.children
        |> Element.pickHeadings
        |> Array.collect (headingToSymbolInfo docUri)

    Array.append [| sym |] children

let rec headingToDocumentSymbol (h: Heading) : DocumentSymbol =
    let name = h.text.TrimStart([| '#'; ' ' |])
    let kind = SymbolKind.String
    let range = h.scope
    let selectionRange = h.range

    let children =
        h.children
        |> Element.pickHeadings
        |> Array.map headingToDocumentSymbol

    { Name = name
      Detail = None
      Kind = kind
      Range = range
      SelectionRange = selectionRange
      Children = Some children }

type MarksmanClient(notSender: ClientNotificationSender, _reqSender: ClientRequestSender) =
    inherit LspClient()

    override this.TextDocumentPublishDiagnostics(par: PublishDiagnosticsParams) =
        notSender "textDocument/publishDiagnostics" (box par)
        |> Async.Ignore

type BackgroundMessage =
    | Start
    | Stop
    | EnqueueDiagnostic of PublishDiagnosticsParams

type BackgroundAgent(client: MarksmanClient) =
    let logger =
        LogProvider.getLoggerByName "BackgroundAgent"

    let agent: MailboxProcessor<BackgroundMessage> =
        MailboxProcessor.Start (fun inbox ->
            let mutable shouldStart = false
            let mutable shouldStop = false

            let diagQueue: Queue<PublishDiagnosticsParams> =
                Queue()

            let processDiagQueue () =
                async {
                    if shouldStart && not shouldStop then
                        match diagQueue.TryDequeue() with
                        | false, _ -> () // do nothing, continue processing messages
                        | true, first ->
                            logger.trace (
                                Log.setMessage "Updating document diagnostic"
                                >> Log.addContext "uri" first.Uri
                                >> Log.addContext "numEntries" first.Diagnostics.Length
                            )

                            do! client.TextDocumentPublishDiagnostics(first)
                }

            let rec processMessages () =
                async {
                    do! processDiagQueue ()
                    let! msg = inbox.Receive()

                    match msg with
                    | Start ->
                        logger.trace (Log.setMessage "Starting background agent")
                        shouldStart <- true
                    | Stop ->
                        logger.trace (Log.setMessage "Stopping background agent")
                        shouldStop <- true
                    | EnqueueDiagnostic pars -> diagQueue.Enqueue(pars)

                    do! processDiagQueue ()

                    if not shouldStop then
                        return! processMessages ()
                    else
                        ()
                }

            logger.trace (Log.setMessage "Preparing to start background agent")

            processMessages ())

    member this.EnqueueDiagnostic(par: PublishDiagnosticsParams) : unit = agent.Post(EnqueueDiagnostic par)
    member this.Start() : unit = agent.Post(Start)
    member this.Stop() : unit = agent.Post(Stop)

type MarksmanServer(client: MarksmanClient) =
    inherit LspServer()
    let mutable state: option<State> = None

    let backgroundAgent =
        BackgroundAgent(client)

    let logger =
        LogProvider.getLoggerByName "MarksmanServer"

    let updateState (newState: State) : unit =
        logger.trace (Log.setMessage $"Updating state: revision {newState.revision}")

        let mutable newWorkspaceDiag = Map.empty

        for KeyValue (_, folder) in newState.folders do
            logger.trace (
                Log.setMessage $"Computing diagnostic"
                >> Log.addContext "folder" folder.name
            )

            let newFolderDiag =
                Diag.diagnosticForFolder folder

            newWorkspaceDiag <- Map.add folder.root newFolderDiag newWorkspaceDiag

            let existingFolderDiag =
                Map.tryFind folder.root newState.diagnostic
                |> Option.defaultValue [||]

            if newFolderDiag = existingFolderDiag then
                logger.trace (
                    Log.setMessage "Diagnostic didn't change"
                    >> Log.addContext "folder" folder.name
                )
            else
                logger.trace (
                    Log.setMessage $"Diagnostic changed; queueing the update"
                    >> Log.addContext "folder" folder.name
                )


                for uri, diags in newFolderDiag do
                    let publishParams =
                        { Uri = uri.Uri.OriginalString
                          Diagnostics = diags }

                    backgroundAgent.EnqueueDiagnostic(publishParams)

        let newState =
            { newState with
                revision = newState.revision + 1
                diagnostic = newWorkspaceDiag }

        state <- Some newState

        logger.trace (Log.setMessage $"Updated state: revision {newState.revision}")

    let requireState () : State =
        Option.defaultWith (fun _ -> failwith "State was not initialized") state

    override this.Initialize(par: InitializeParams) : AsyncLspResult<InitializeResult> =
        let workspaceFolders =
            extractWorkspaceFolders par

        logger.debug (
            Log.setMessage "Obtained workspace folders"
            >> Log.addContext "workspace" workspaceFolders
        )

        let folders = readWorkspace workspaceFolders

        let numNotes =
            folders |> List.sumBy (fun x -> x.documents.Count)

        logger.debug (
            Log.setMessage "Completed reading workspace folders"
            >> Log.addContext "numFolders" folders.Length
            >> Log.addContext "numNotes" numNotes
        )

        let state =
            { client = ClientDescription.fromParams par
              folders =
                folders
                |> List.map (fun x -> x.root, x)
                |> Map.ofList
              revision = 0
              diagnostic = Map.empty }

        updateState state

        let serverCaps = mkServerCaps par

        let initResult =
            { InitializeResult.Default with Capabilities = serverCaps }

        AsyncLspResult.success initResult


    override this.Initialized(_: InitializedParams) =
        backgroundAgent.Start()
        async.Return()

    override this.Shutdown() =
        backgroundAgent.Stop()
        async.Return()

    override this.TextDocumentDidChange(par: DidChangeTextDocumentParams) =
        let state = requireState ()

        let docUri =
            par.TextDocument.Uri |> Uri |> PathUri

        let doc = State.tryFindDocument docUri state

        match doc with
        | Some doc ->
            let newDoc = Document.applyLspChange par doc

            let newState =
                State.updateDocument newDoc state

            updateState newState
        | _ ->
            logger.warn (
                Log.setMessage "Document not found"
                >> Log.addContext "method" "textDocumentDidChange"
                >> Log.addContext "uri" docUri
            )

        async.Return()

    override this.TextDocumentDidClose(par: DidCloseTextDocumentParams) =
        let path =
            par.TextDocument.Uri |> Uri |> PathUri

        let state = requireState ()
        let folder = State.tryFindFolder path state

        match folder with
        | None -> ()
        | Some folder ->
            let docFromDisk =
                Document.load folder.root path

            let newState =
                match docFromDisk with
                | Some doc -> State.updateDocument doc (requireState ())
                | _ -> State.removeDocument path (requireState ())

            updateState newState

        async.Return()

    override this.TextDocumentDidOpen(par: DidOpenTextDocumentParams) =
        let state = requireState ()

        let path =
            par.TextDocument.Uri |> PathUri.fromString

        let folder = State.tryFindFolder path state

        match folder with
        | None -> ()
        | Some folder ->
            let document =
                Document.fromLspDocument folder.root par.TextDocument

            let newState =
                State.updateDocument document (requireState ())

            updateState newState

        async.Return()

    override this.WorkspaceDidChangeWorkspaceFolders(par: DidChangeWorkspaceFoldersParams) =
        let state = requireState ()

        let newState =
            State.updateFoldersFromLsp par.Event.Added par.Event.Removed state

        updateState newState
        async.Return()


    override this.WorkspaceDidCreateFiles(par: CreateFilesParams) =
        let docUris =
            par.Files
            |> Array.map (fun fc -> PathUri.fromString fc.Uri)

        let mutable newState = requireState ()

        for docUri in docUris do
            logger.trace (
                Log.setMessage "Processing file create not"
                >> Log.addContext "uri" docUri
            )

            let folder =
                State.tryFindFolder docUri newState

            match folder with
            | None -> ()
            | Some folder ->
                match Document.load folder.root docUri with
                | Some doc -> newState <- State.updateDocument doc newState
                | _ ->
                    logger.warn (
                        Log.setMessage "Couldn't load created document"
                        >> Log.addContext "uri" docUri
                    )

                    ()

        updateState newState
        async.Return()

    override this.WorkspaceDidDeleteFiles(par: DeleteFilesParams) =
        let mutable newState = requireState ()

        let deletedUris =
            par.Files
            |> Array.map (fun x -> PathUri.fromString x.Uri)

        for uri in deletedUris do
            logger.trace (
                Log.setMessage "Processing file delete not"
                >> Log.addContext "uri" uri
            )

            newState <- State.removeDocument uri newState

        updateState newState
        async.Return()

    override this.TextDocumentDocumentSymbol(par: DocumentSymbolParams) =
        let state = requireState ()

        let docUri =
            par.TextDocument.Uri |> PathUri.fromString

        match State.tryFindDocument docUri state with
        | Some doc ->
            let headings =
                Element.pickHeadings doc.elements

            let supportsHierarchy =
                monad' {
                    let! textDoc = state.client.caps.TextDocument
                    let! docSymbol = textDoc.DocumentSymbol
                    return! docSymbol.HierarchicalDocumentSymbolSupport
                }
                |> Option.defaultValue false

            let response =
                if supportsHierarchy then
                    headings
                    |> Array.map headingToDocumentSymbol
                    |> Second
                else
                    headings
                    |> Array.collect (headingToSymbolInfo docUri)
                    |> First

            AsyncLspResult.success (Some response)
        | None -> AsyncLspResult.success None

    override this.TextDocumentCompletion(par: CompletionParams) =
        let state = requireState ()
        let pos = par.Position

        let docUri =
            par.TextDocument.Uri |> PathUri.fromString

        let compCandidates =
            State.findCompletionCandidates pos docUri state

        let compList =
            if compCandidates.Length = 0 then
                None
            else
                { IsIncomplete = true
                  Items = compCandidates }
                |> Some

        AsyncLspResult.success compList

    override this.TextDocumentDefinition(par: TextDocumentPositionParams) =
        let state = requireState ()

        let docUri =
            par.TextDocument.Uri |> PathUri.fromString

        let goto =
            monad {
                let! folder = State.tryFindFolder docUri state
                let! sourceDoc = Folder.tryFindDocument docUri folder
                let! atPos = Document.elementAtPos par.Position sourceDoc
                let! ref = Element.asRef atPos

                let! destDoc, destHeading = Folder.tryFindReferenceTarget sourceDoc ref folder

                let destRange =
                    destHeading
                    |> Option.map Heading.range
                    |> Option.defaultWith destDoc.text.FullRange

                let location =
                    GotoResult.Single
                        { Uri = destDoc.path.DocumentUri
                          Range = destRange }

                location
            }

        AsyncLspResult.success goto

    override this.TextDocumentHover(par: TextDocumentPositionParams) =
        let state = requireState ()

        let docUri =
            par.TextDocument.Uri |> PathUri.fromString

        let hover =
            monad {
                let! folder = State.tryFindFolder docUri state
                let! sourceDoc = Folder.tryFindDocument docUri folder
                let! atPos = Document.elementAtPos par.Position sourceDoc
                let! ref = Element.asRef atPos

                let! destDoc, destHeading = Folder.tryFindReferenceTarget sourceDoc ref folder

                let destScope =
                    destHeading
                    |> Option.map Heading.scope
                    |> Option.defaultWith destDoc.text.FullRange

                let content =
                    destDoc.text.Substring(destScope)
                    |> markdown
                    |> MarkupContent

                let hover =
                    { Contents = content; Range = None }

                hover
            }

        AsyncLspResult.success hover

    override this.Dispose() = ()
