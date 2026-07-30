using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using TouchNStars.Properties;
using System.Threading.Tasks;
using System.Text;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Services;

namespace TouchNStars.Server {
    public class TouchNStarsServer {
        private Thread serverThread;
        private CancellationTokenSource apiToken;
        public WebServer WebServer;

        private readonly List<string> appEndPoints = ["equipment", "camera", "autofocus", "mount", "guider", "sequence", "settings", "seq-mon", "flat", "dome", "logs", "switch", "flats", "stellarium", "settings", "rotator", "filterwheel", "bahtinov", "plugin1", "plugin2", "plugin3", "plugin4", "plugin5", "plugin6", "plugin7", "plugin8", "plugin9", "plugin10", "plugin11", "plugin12", "plugin13", "plugin14", "plugin15", "plugin16", "plugin17", "plugin18", "plugin19", "plugin20", "plugin21", "plugin22", "plugin23", "plugin24", "plugin25", "plugin26", "plugin27", "plugin28", "plugin29", "plugin30", "plugin31", "plugin32", "plugin33", "plugin34", "plugin35", "plugin36", "plugin37", "plugin38", "plugin39", "plugin40", "plugin41", "plugin42", "plugin43", "plugin44", "plugin45", "plugin46", "plugin47", "plugin48", "plugin49", "plugin50", "plugin51", "plugin52", "plugin53", "plugin54", "plugin55", "plugin56", "plugin57", "plugin58", "plugin59"];

        private int port;
        public TouchNStarsServer(int port) => this.port = port;

        public void CreateServer() {
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string webAppDir = Path.Combine(assemblyFolder, "app");
            string userLandscapesDir =
                StellariumLandscapeService.ResolvePersistentLandscapesRoot(createIfMissing: true);

            // Suppress EmbedIO verbose logging by unregistering the logger
            try { Swan.Logging.Logger.UnregisterLogger<Swan.Logging.ConsoleLogger>(); } catch { }

            WebServer = new WebServer(o => o
                .WithUrlPrefix($"http://*:{port}")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new CustomHeaderModule());

            foreach (string endPoint in appEndPoints) {
                WebServer = WebServer.WithModule(new RedirectModule("/" + endPoint, "/")); // redirect all reloads of the app to the root
            }
            WebServer = WebServer.WithWebApi("/api", m => m
                .WithController<AutofocusController>()   // Autofocus control
                .WithController<DialogController>()      // Dialog endpoints
                .WithController<PHD2Controller>()        // PHD2 guiding endpoints
                .WithController<TelescopiusController>() // Telescopius PIAAPI proxy
                .WithController<MessageBoxController>()  // TNS MessageBox management
                .WithController<SystemController>()      // System control (shutdown/restart)
                .WithController<SettingsController>()    // Settings management
                .WithController<FavoritesController>()   // Favorites management
                .WithController<TargetSearchController>() // NGC search and target pictures
                .WithController<FramingController>()     // Framing Assistant control
                .WithController<MetricsController>()     // System metrics
                .WithController<UtilityController>()    // Logs, version, api-port
                .WithController<BahtinovController>()    // Bahtinov mask analysis
                .WithController<INDIController>()        // INDI driver management
                .WithController<ProxyController>()       // Generic proxy for external URLs
                .WithController<FilesystemController>()
                .WithController<FitsAnalysisController>()
                .WithController<StellariumLandscapeController>());
            WebServer = WebServer.WithStaticFolder(
                StellariumLandscapeService.UserLandscapesRoute,
                userLandscapesDir,
                false);
            WebServer = WebServer.WithStaticFolder("/", webAppDir, false); // Register the static folder, which will be used to serve the web app
        }

        public void Start() {
            try {
                Logger.Debug("Creating Touch-N-Stars Webserver");
                CreateServer();
                Logger.Info("Starting Touch-N-Stars Webserver");
                if (WebServer != null) {
                    serverThread = new Thread(() => APITask(WebServer)) {
                        Name = "Touch-N-Stars API Thread"
                    };
                    serverThread.Start();
                    BackgroundWorker.MonitorLogForEvents();
                    BackgroundWorker.MonitorLastAF();
                }
            } catch (Exception ex) {
                Logger.Error($"failed to start web server: {ex}");
            }
        }

        public void Stop() {
            try {
                apiToken?.Cancel();
                WebServer?.Dispose();
                WebServer = null;
                BackgroundWorker.Cleanup();
            } catch (Exception ex) {
                Logger.Error($"failed to stop API: {ex}");
            }
        }

        // [STAThread]
        private void APITask(WebServer server) {
            Logger.Info("Touch-N-Stars Webserver starting");

            try {
                apiToken = new CancellationTokenSource();
                server.RunAsync(apiToken.Token).Wait();
            } catch (Exception ex) {
                Logger.Error($"failed to start web server: {ex}");
                try {
                    Notification.ShowError($"Failed to start web server, see NINA log for details");
                } catch (Exception notificationEx) {
                    Logger.Warning($"Failed to show error notification: {notificationEx.Message}");
                }
            }
        }
    }

    internal class CustomHeaderModule : WebModuleBase {
        internal CustomHeaderModule() : base("/") {
        }

        protected override async Task OnRequestAsync(IHttpContext context) {
            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Suppress-Toast-404, X-Requested-With");
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");

            if (context.Request.HttpVerb == HttpVerbs.Options) {
                context.Response.StatusCode = 200;
                await context.SendStringAsync(string.Empty, "text/plain", Encoding.UTF8); 
                return;
            }
        }

        public override bool IsFinalHandler => false;
    }
}
