using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TouchNStars.Utility;
using TouchNStars.Server;
using Settings = TouchNStars.Properties.Settings;
using System.Collections.Generic;
using System.Windows;

namespace TouchNStars {

    public class Mediators(
        IDeepSkyObjectSearchVM DeepSkyObjectSearchVM,
        IImageDataFactory ImageDataFactory,
        IFramingAssistantVM framingAssistantVM,
        IProfileService profile,
        IGuiderMediator guider,
        IMessageBroker broker) {

        public readonly IDeepSkyObjectSearchVM DeepSkyObjectSearchVM = DeepSkyObjectSearchVM;
        public readonly IImageDataFactory ImageDataFactory = ImageDataFactory;
        public readonly IFramingAssistantVM FramingAssistantVM = framingAssistantVM;
        public readonly IProfileService Profile = profile;
        public readonly IGuiderMediator Guider = guider;
        public readonly IMessageBroker MessageBroker = broker;
    }

    [Export(typeof(IPluginManifest))]
    public class TouchNStars : PluginBase, INotifyPropertyChanged {
        private TouchNStarsServer server;

        public static Mediators Mediators { get; private set; }
        public static string PluginId { get; private set; }

        internal static Communicator Communicator { get; private set; }

        private static TouchNStars instance;


        [ImportingConstructor]
        public TouchNStars(IProfileService profileService,
                    IDeepSkyObjectSearchVM DeepSkyObjectSearchVM,
                    IImageDataFactory imageDataFactory,
                    IFramingAssistantVM framingAssistantVM,
                    IGuiderMediator guider,
                    IMessageBroker broker) {
            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }

            instance = this;

            PluginId = this.Identifier;
            Mediators = new Mediators(DeepSkyObjectSearchVM,
                            imageDataFactory,
                            framingAssistantVM,
                            profileService,
                            guider,
                            broker);

            UpdateDefaultPortCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => {
                Port = CachedPort;
                CachedPort = Port; // This may look useless, but that way the visibility only changes when cachedPort changes and not when the user enters a new port
            });

            Communicator = new Communicator();

            SetHostNames();

            if (AppEnabled) {
                CachedPort = CoreUtility.GetNearestAvailablePort(Port);
                server = new TouchNStarsServer(CachedPort);
                server.Start();
                ShowNotificationIfPortChanged();
            }
        }

        public CommunityToolkit.Mvvm.Input.RelayCommand UpdateDefaultPortCommand { get; set; }

        private int cachedPort = -1;
        public int CachedPort {
            get => cachedPort;
            set {
                cachedPort = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CachedPort)));
                PortVisibility = ((CachedPort != Port) && AppEnabled) ? Visibility.Visible : Visibility.Hidden;
                SetHostNames();
            }
        }

        private Visibility portVisibility = Visibility.Hidden;
        public Visibility PortVisibility {
            get => portVisibility;
            set {
                portVisibility = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PortVisibility)));
            }
        }
        public static int GetCachedPort() {
            return instance.CachedPort;
        }

        private void ShowNotificationIfPortChanged() {
            if (CachedPort != Port) {
                Notification.ShowInformation("Touch 'N' Stars launched on a different port: " + CachedPort);
            }
        }


        public override Task Teardown() {
            server.Stop();
            Communicator.Dispose();
            return base.Teardown();
        }

        public bool UseAccessControlHeader {
            get => Settings.Default.UseAccessHeader;
            set {
                Settings.Default.UseAccessHeader = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool AppEnabled {
            get {
                return Settings.Default.AppEnabled;
            }
            set {
                Settings.Default.AppEnabled = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();

                if (value) {
                    CachedPort = CoreUtility.GetNearestAvailablePort(Port);
                    server = new TouchNStarsServer(CachedPort);
                    server.Start();
                    SetHostNames();
                    Notification.ShowSuccess("Touch 'N' Stars started!");
                    ShowNotificationIfPortChanged();
                } else {
                    server.Stop();
                    Notification.ShowSuccess("Touch 'N' Stars stopped!");
                    server = null;
                    CachedPort = -1;
                }
            }
        }

        public int Port {
            get {
                return Settings.Default.Port;
            }
            set {
                Settings.Default.Port = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string LocalAdress {
            get => Settings.Default.LocalAdress;
            set {
                Settings.Default.LocalAdress = value;
                NINA.Core.Utility.CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalAdress)));
            }
        }

        public string LocalNetworkAdress {
            get => Settings.Default.LocalNetworkAdress;
            set {
                Settings.Default.LocalNetworkAdress = value;
                NINA.Core.Utility.CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalNetworkAdress)));
            }
        }

        public string HostAdress {
            get => Settings.Default.HostAdress;
            set {
                Settings.Default.HostAdress = value;
                NINA.Core.Utility.CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HostAdress)));
            }
        }

        private void SetHostNames() {
            Dictionary<string, string> dict = CoreUtility.GetLocalNames();

            LocalAdress = $"http://{dict["LOCALHOST"]}:{Port}/";
            LocalNetworkAdress = $"http://{dict["IPADRESS"]}:{Port}/";
            HostAdress = $"http://{dict["HOSTNAME"]}:{Port}/";
        }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
