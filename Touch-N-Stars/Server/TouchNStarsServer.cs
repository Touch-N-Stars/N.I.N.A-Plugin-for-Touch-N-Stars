using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace TouchNStars.Server {
    public class TouchNStarsServer {
        private Thread serverThread;
        private Thread afWatcherThread;
        private CancellationTokenSource apiToken;
        public WebServer WebServer;
        public readonly int Port = 5000;

        public void CreateServer() {
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string webAppDir = Path.Combine(assemblyFolder, "app");

            WebServer = new WebServer(o => o
                .WithUrlPrefix($"http://*:{Port}")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithWebApi("/api", m => m.WithController<Controller>()) // Register the controller, which will be used to handle all the api requests which were previously in server.py
                .WithStaticFolder("/", webAppDir, false);
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
                    // serverThread.SetApartmentState(ApartmentState.STA);
                    serverThread.Start();
                    afWatcherThread = new Thread(BackgroundWorker.MonitorLastAF) {
                        Name = "AF monitor Thread"
                    };
                    afWatcherThread.Start();
                    BackgroundWorker.ObserveGuider();
                    BackgroundWorker.MonitorLogForEvents();
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
            // string ipAdress = CoreUtility.GetLocalNames()["IPADRESS"];
            // Logger.Info($"starting web server, listening at {ipAdress}:{Port}");
            Logger.Info("Touch-N-Stars Webserver starting");

            try {
                apiToken = new CancellationTokenSource();
                server.RunAsync(apiToken.Token).Wait();
            } catch (Exception ex) {
                Logger.Error($"failed to start web server: {ex}");
                Notification.ShowError($"Failed to start web server, see NINA log for details");
            }
        }
    }
}