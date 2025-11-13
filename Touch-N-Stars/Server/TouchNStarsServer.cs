using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using TouchNStars.Properties;
using System.Threading.Tasks;
using System.Text;

namespace TouchNStars.Server {
    public class TouchNStarsServer {
        private Thread serverThread;
        private Thread broadcastThread;
        private CancellationTokenSource apiToken;
        private CancellationTokenSource broadcastToken;
        public WebServer WebServer;

        private readonly List<string> appEndPoints = ["equipment", "camera", "autofocus", "mount", "guider", "sequence", "settings", "seq-mon", "flat", "dome", "logs", "switch", "flats", "stellarium", "settings", "rotator", "filterwheel", "plugin1", "plugin2", "plugin3", "plugin4", "plugin5", "plugin6", "plugin7", "plugin8", "plugin9"];

        private int port;
        private const int BROADCAST_PORT = 37020;
        private const string PLUGIN_IDENTIFIER = "NINA-TouchNStars";
        
        public TouchNStarsServer(int port) => this.port = port;

        public void CreateServer() {
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string webAppDir = Path.Combine(assemblyFolder, "app");

            WebServer = new WebServer(o => o
                .WithUrlPrefix($"http://*:{port}")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new CustomHeaderModule());

            foreach (string endPoint in appEndPoints) {
                WebServer = WebServer.WithModule(new RedirectModule("/" + endPoint, "/")); // redirect all reloads of the app to the root
            }
            WebServer = WebServer.WithWebApi("/api", m => m.WithController<Controller>()); // Register the controller, which will be used to handle all the api requests which were previously in server.py
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
                    
                    // Start UDP broadcast
                    StartBroadcast();
                }
            } catch (Exception ex) {
                Logger.Error($"failed to start web server: {ex}");
            }
        }

        public void Stop() {
            try {
                // Stop UDP broadcast
                StopBroadcast();
                
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

        private void StartBroadcast() {
            try {
                Logger.Info("Starting Touch-N-Stars network discovery broadcast");
                broadcastToken = new CancellationTokenSource();
                broadcastThread = new Thread(() => BroadcastTask(broadcastToken.Token)) {
                    Name = "Touch-N-Stars Broadcast Thread",
                    IsBackground = true
                };
                broadcastThread.Start();
            } catch (Exception ex) {
                Logger.Error($"Failed to start broadcast: {ex}");
            }
        }

        private void StopBroadcast() {
            try {
                broadcastToken?.Cancel();
                broadcastThread?.Join(1000);
                Logger.Info("Touch-N-Stars network discovery broadcast stopped");
            } catch (Exception ex) {
                Logger.Error($"Failed to stop broadcast: {ex}");
            }
        }

        private void BroadcastTask(CancellationToken token) {
            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, BROADCAST_PORT);

            string hostname = Dns.GetHostName();
            string ipAddress = Utility.CoreUtility.GetIPv4Address();
            
            while (!token.IsCancellationRequested) {
                try {
                    // Create broadcast message with plugin identifier, port, hostname, and IP
                    string message = $"{PLUGIN_IDENTIFIER}|Port:{port}|Host:{hostname}|IP:{ipAddress}";
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    
                    udpClient.Send(data, data.Length, broadcastEndpoint);
                    Logger.Trace($"Broadcast sent: {message}");
                    
                    // Wait 5 seconds before next broadcast or until cancellation
                    token.WaitHandle.WaitOne(5000);
                } catch (Exception ex) when (!token.IsCancellationRequested) {
                    Logger.Error($"Error during broadcast: {ex}");
                    token.WaitHandle.WaitOne(5000);
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