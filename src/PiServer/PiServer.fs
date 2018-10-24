open Microsoft.Extensions.DependencyInjection
open Giraffe
open Saturn
open FSharp.Control.Tasks.ContextInsensitive
open System
open System.IO
open Microsoft.AspNetCore.NodeServices
open Thoth.Json.Net
open ServerCore.Domain
open System.Threading.Tasks
open System.Diagnostics
open System.Xml
open System.Reflection
open GeneralIO
open Elmish


let log =
    let log4netConfig = XmlDocument()
    log4netConfig.Load(File.OpenRead("log4net.config"))
    let repo = log4net.LogManager.CreateRepository(Assembly.GetEntryAssembly(), typeof<log4net.Repository.Hierarchy.Hierarchy>)
    log4net.Config.XmlConfigurator.Configure(repo, log4netConfig.["log4net"]) |> ignore

    let log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType)
    log


type PlayList = {
    Uri : string
    MediaFiles: string []
    Position : int
}

type Model = {
    PlayList : PlayList option
    FirmwareUpdateInterval : TimeSpan
    UserID : string
    TagServer : string
    Volume : float
    RFID : string option
    YoutubeLinks : Map<string,string[]>
    MediaPlayerProcess : Process option
}

type Msg =
| VolumeUp
| VolumeDown
| NewRFID of string
| RFIDRemoved
| NewTag of Tag
| MusicStopped of unit
| Play of PlayList
| PlayYoutube of string
| NextMediaFile
| PreviousMediaFile
| StartMediaPlayer
| Started of Process
| FinishPlaylist
| DiscoverYoutube of string * bool
| NewYoutubeMediaFiles of string * string [] * bool
| Err of exn

let init () : Model = {
    PlayList = None
    FirmwareUpdateInterval = TimeSpan.FromHours 1.
    UserID = "9bb2b109-bf08-4342-9e09-f4ce3fb01c0f" // TODO: load from some config
    TagServer = "https://audio-hub.azurewebsites.net" // TODO: load from some config
    Volume = 0.5 // TODO: load from webserver
    RFID = None
    YoutubeLinks = Map.empty
    MediaPlayerProcess = None
}

let getMusikPlayerProcesses() = Process.GetProcessesByName("omxplayer.bin")


let resolveRFID (model:Model,token:string) = task {
    use webClient = new System.Net.WebClient()
    let url = sprintf @"%s/api/tags/%s/%s" model.TagServer model.UserID token
    let! result = webClient.DownloadStringTaskAsync(System.Uri url)

    match Decode.fromString Tag.Decoder result with
    | Error msg -> return failwith msg
    | Ok tag -> return tag
}


let killMusikPlayer() = task {
    for p in getMusikPlayerProcesses() do
        if not p.HasExited then
            try
                log.InfoFormat "stopping omxplaxer"
                let killP = new System.Diagnostics.Process()
                let startInfo = System.Diagnostics.ProcessStartInfo()
                startInfo.FileName <- "sudo"
                startInfo.Arguments <- "kill -9 " + p.Id.ToString()
                killP.StartInfo <- startInfo
                let _ = killP.Start()

                while not killP.HasExited do
                    do! Task.Delay 10
                log.InfoFormat "stopped"
            with _ -> log.WarnFormat "couldn't kill omxplayer"
}



let dispatch (msg:Msg) () = ()  // TODO: message loop




let discoverYoutubeLink (youtubeURL:string) = task {
    log.InfoFormat("Starting youtube-dl -g {0}", youtubeURL)
    let lines = System.Collections.Generic.List<_>()
    let proc = new Process ()
    let startInfo = new ProcessStartInfo()
    startInfo.FileName <- "sudo"
    startInfo.Arguments <- sprintf "youtube-dl -g \"%s\"" youtubeURL
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.CreateNoWindow <- true
    proc.StartInfo <- startInfo

    proc.Start() |> ignore
    while not proc.StandardOutput.EndOfStream do
        let! line = proc.StandardOutput.ReadLineAsync()
        lines.Add line

    let lines = Seq.toArray lines
    let links =
        lines
        |> Array.filter (fun x -> x.Contains "&mime=audio")

    for vLink in links do
        log.InfoFormat("Youtube audio link detected: {0}", vLink)
    return links
}

let update (model:Model) (msg:Msg) =
    match msg with
    | VolumeUp ->
        // //log.InfoFormat "Volume up button pressed"; if not (isNull currentAudioProcess) then currentAudioProcess.StandardInput.Write("+") |> ignore)
        { model with Volume = model.Volume + 0.1 }, Cmd.none

    | VolumeDown ->
         // log.InfoFormat "Volume down button pressed"; if not (isNull currentAudioProcess) then currentAudioProcess.StandardInput.Write("-") |> ignore)
        { model with Volume = model.Volume - 0.1 }, Cmd.none

    | NewRFID rfid ->
        log.InfoFormat("RFID/NFC: {0}", rfid)
        { model with RFID = Some rfid }, Cmd.ofTask resolveRFID (model,rfid) NewTag Err

    | RFIDRemoved ->
        { model with RFID = None }, Cmd.ofMsg FinishPlaylist

    | NewTag tag ->
        log.InfoFormat( "Object: {0}", tag.Object)
        log.InfoFormat( "Description: {0}", tag.Description)

        match tag.Action with
        | TagAction.UnknownTag ->
            log.WarnFormat "Unknown Tag"
            model, []
        | TagAction.StopMusik ->
            model, Cmd.ofTask killMusikPlayer () MusicStopped Err
        | TagAction.PlayMusik url ->
            let playList : PlayList = {
                Uri = url
                MediaFiles = [| url |]
                Position = 0
            }
            model, Cmd.batch [Cmd.ofTask killMusikPlayer () MusicStopped Err; Cmd.ofMsg (Play playList)]
        | TagAction.PlayYoutube youtubeURL ->
            model, Cmd.batch [Cmd.ofTask killMusikPlayer () MusicStopped Err; Cmd.ofMsg (PlayYoutube youtubeURL)]
        | TagAction.PlayBlobMusik _ ->
            log.ErrorFormat("Blobs links need to be converted to direct links by the tag server")
            model, Cmd.none

    | Play playList ->
        let model = { model with PlayList = Some playList }
        log.InfoFormat( "Playing new PlayList: {0}: Files: {1}", playList.Uri, playList.MediaFiles.Length)
        model, Cmd.none

    | StartMediaPlayer ->
        match model.PlayList with
        | Some playList ->
            if playList.Position < 0 || playList.Position >= playList.MediaFiles.Length then
                log.ErrorFormat("Playlist has only {0} elements. Can't play media file {1}.", playList.MediaFiles.Length , playList.Position + 1)
                model, Cmd.none
            else
                let start dispatch =
                    killMusikPlayer() |> Async.AwaitTask |> Async.RunSynchronously
                    log.InfoFormat( "Playing audio file {0} / {1}", playList.Position + 1, playList.MediaFiles.Length)
                    let p = new System.Diagnostics.Process()
                    p.EnableRaisingEvents <- true
                    p.Exited.Add (fun _ -> dispatch NextMediaFile)

                    let startInfo = System.Diagnostics.ProcessStartInfo()
                    startInfo.FileName <- "omxplayer"
                    startInfo.Arguments <- playList.MediaFiles.[playList.Position]
                    p.StartInfo <- startInfo

                    p.Start() |> ignore
                model, Cmd.none
        | None ->
            log.ErrorFormat("No playlist set")
            model, Cmd.none

    | Started p ->
        { model with MediaPlayerProcess = Some p }, Cmd.none

    | NextMediaFile ->
        match model.PlayList with
        | Some playList ->
            let playList = { playList with Position = playList.Position + 1 }
            if playList.Position <= 0 || playList.Position >= playList.MediaFiles.Length then
                model, Cmd.ofMsg FinishPlaylist
            else
                { model with PlayList = Some playList }, Cmd.ofMsg StartMediaPlayer
        | None ->
            log.ErrorFormat("No playlist set")
            model, Cmd.none

    | PreviousMediaFile ->
        match model.PlayList with
        | Some playList ->
            let playList = { playList with Position = max 0 (playList.Position - 1) }
            { model with PlayList = Some playList }, Cmd.ofMsg StartMediaPlayer
        | None ->
            log.ErrorFormat("No playlist set")
            model, Cmd.none

    | PlayYoutube youtubeURL ->
        match model.YoutubeLinks.TryGetValue youtubeURL with
        | true, mediaFiles ->
            let playList : PlayList = {
                Uri = youtubeURL
                MediaFiles = mediaFiles
                Position = 0
            }

            model, Cmd.batch [Cmd.ofTask killMusikPlayer () MusicStopped Err; Cmd.ofMsg (Play playList)]
        | _ ->
            model, Cmd.ofMsg (DiscoverYoutube (youtubeURL,true))

    | FinishPlaylist ->
        { model with PlayList = None }, Cmd.ofTask killMusikPlayer () MusicStopped Err

    | DiscoverYoutube (youtubeURL,playAfterwards) ->
        model, Cmd.ofTask discoverYoutubeLink youtubeURL (fun files -> NewYoutubeMediaFiles (youtubeURL,files,playAfterwards)) Err

    | NewYoutubeMediaFiles (youtubeURL,files,playAfterwards) ->
        let model = { model with YoutubeLinks = model.YoutubeLinks |> Map.add youtubeURL files }
        if playAfterwards then
            model, Cmd.ofMsg (PlayYoutube youtubeURL)
        else
            model, Cmd.none

    | MusicStopped _ ->
        log.InfoFormat "Music stopped"
        model, Cmd.none

    | Err exn ->
        log.ErrorFormat( "Error: {0}", exn.Message)
        model, Cmd.none



let runFirmwareUpdate() =
    let p = new Process()
    let startInfo = new ProcessStartInfo()
    startInfo.WorkingDirectory <- "/home/pi/firmware/"
    startInfo.FileName <- "sudo"
    startInfo.Arguments <- "sh update.sh"
    startInfo.RedirectStandardOutput <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    p.StartInfo <- startInfo
    p.Start() |> ignore

let mutable nextFirmwareCheck = DateTimeOffset.MinValue

let firmwareTarget = System.IO.Path.GetFullPath "/home/pi/firmware"

let checkFirmware (model:Model) = task {
    use webClient = new System.Net.WebClient()
    System.Net.ServicePointManager.SecurityProtocol <-
        System.Net.ServicePointManager.SecurityProtocol |||
          System.Net.SecurityProtocolType.Tls11 |||
          System.Net.SecurityProtocolType.Tls12

    let url = sprintf @"%s/api/firmware" model.TagServer
    let! result = webClient.DownloadStringTaskAsync(System.Uri url)

    match Decode.fromString Firmware.Decoder result with
    | Error msg ->
        log.ErrorFormat("Decoder error: {0}", msg)
        return failwith msg
    | Ok firmware ->
        try
            nextFirmwareCheck <- DateTimeOffset.UtcNow.Add model.FirmwareUpdateInterval
            log.InfoFormat("Latest firmware on server: {0}", firmware.Version)
            let serverVersion = Paket.SemVer.Parse firmware.Version
            let localVersion = Paket.SemVer.Parse ReleaseNotes.Version
            if serverVersion > localVersion then
                let localFileName = System.IO.Path.GetTempFileName().Replace(".tmp", ".zip")
                log.InfoFormat("Starting download of {0}", firmware.Url)
                do! webClient.DownloadFileTaskAsync(firmware.Url,localFileName)
                log.InfoFormat "Download done."

                if System.IO.Directory.Exists firmwareTarget then
                    System.IO.Directory.Delete(firmwareTarget,true)
                System.IO.Directory.CreateDirectory(firmwareTarget) |> ignore
                System.IO.Compression.ZipFile.ExtractToDirectory(localFileName, firmwareTarget)
                System.IO.File.Delete localFileName
                runFirmwareUpdate()
                while true do
                    log.InfoFormat "Running firmware update."
                    do! Task.Delay 3000
                    ()
            else
                if System.IO.Directory.Exists firmwareTarget then
                    System.IO.Directory.Delete(firmwareTarget,true)
                log.InfoFormat( "Firmware {0} is uptodate.", ReleaseNotes.Version)
        with
        | exn ->
            log.ErrorFormat("Upgrade error: {0}", exn.Message)
}

// let executeStartupActions (model:Model) = task {
//     try
//         use webClient = new System.Net.WebClient()
//         let url = sprintf @"%s/api/startup" model.TagServer
//         let! result = webClient.DownloadStringTaskAsync(System.Uri url)

//         match Decode.fromString (Decode.list TagAction.Decoder) result with
//         | Error msg -> return failwith msg
//         | Ok actions ->
//             log.InfoFormat("Actions: {0}", sprintf "%A" actions)
//             for t in actions do
//                 let! _ = executeAction t
//                 ()
//     with
//     | exn ->
//         log.ErrorFormat("Startup error: {0}", exn.Message)
// }

// let discoverAllYoutubeLinks (model:Model) = task {
//     while true do
//         try
//             use webClient = new System.Net.WebClient()
//             let url = sprintf  @"%s/api/usertags/%s" model.TagServer model.UserID
//             let! result = webClient.DownloadStringTaskAsync(System.Uri url)

//             match Decode.fromString TagList.Decoder result with
//             | Error msg -> return failwith msg
//             | Ok list ->
//                 for tag in list.Tags do
//                     match tag.Action with
//                     | TagAction.PlayYoutube youtubeURL ->
//                         let! vlinks = discoverYoutubeLink youtubeURL
//                         youtubeLinks.AddOrUpdate(youtubeURL,vlinks,Func<_,_,_>(fun _ _ -> vlinks)) |> ignore
//                     | _ -> ()
//         with
//         | exn ->
//             log.ErrorFormat("Youtube discovering error: {0}", exn.Message)
//         do! Task.Delay(TimeSpan.FromMinutes 60.)
// }

let webApp = router {
    get "/version" (fun next ctx -> task {
        return! text ReleaseNotes.Version next ctx
    })
}

let configureSerialization (services:IServiceCollection) =
    services.AddNodeServices(fun x -> x.InvocationTimeoutMilliseconds <- 2 * 60 * 60 * 1000)
    services

let builder = application {
    url "http://0.0.0.0:8086/"
    use_router webApp
    memory_cache
    service_config configureSerialization
    use_gzip
}

let rfidLoop dispatch model = task {
    use app = builder.Build()
    app.Start()

    log.InfoFormat("PiServer {0} started.", ReleaseNotes.Version)

    do! checkFirmware model
    // do! executeStartupActions model
    // do! discoverAllYoutubeLinks model

    let nodeServices = app.Services.GetService(typeof<INodeServices>) :?> INodeServices

    use _nextButton = new Button(Unosquare.RaspberryIO.Pi.Gpio.Pin07, fun () -> dispatch NextMediaFile)
    use _previousButton = new Button(Unosquare.RaspberryIO.Pi.Gpio.Pin01, fun () -> dispatch PreviousMediaFile)
    use _volumeDownButton = new Button(Unosquare.RaspberryIO.Pi.Gpio.Pin26, fun () -> dispatch VolumeDown)
    use _volumeUpButton = new Button(Unosquare.RaspberryIO.Pi.Gpio.Pin25, fun () -> dispatch VolumeUp)


    while true do
        let! token = nodeServices.InvokeExportAsync<string>("./read-tag", "read", "tag")

        if String.IsNullOrEmpty token then
            if nextFirmwareCheck < DateTimeOffset.UtcNow then
                let! _ = checkFirmware model
                ()
            else
                let! _ = Task.Delay(TimeSpan.FromSeconds 0.5)
                ()
        else
            dispatch (NewRFID token)
            let mutable waiting = true
            while waiting do
                do! Task.Delay(TimeSpan.FromSeconds 0.5)

                let! newToken = nodeServices.InvokeExportAsync<string>("./read-tag", "read", "tag")
                if newToken <> token then
                    // recheck in 2 seconds to make it a bit more stable
                    let! _ = Task.Delay(TimeSpan.FromSeconds 2.)
                    let! newToken = nodeServices.InvokeExportAsync<string>("./read-tag", "read", "tag")
                    if newToken <> token then
                        log.InfoFormat("RFID/NFC {0} was removed from reader", token)
                        dispatch RFIDRemoved
                        waiting <- false
}

let app = Program(log,init,update)

let model = init()

(rfidLoop app.Dispatch model).Wait()