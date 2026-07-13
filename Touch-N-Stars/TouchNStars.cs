using NINA.Astrometry.Interfaces;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.Logic;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text;
using TouchNStars.Utility;
using TouchNStars.Server;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Models;
using Settings = TouchNStars.Properties.Settings;
using System.Windows;

namespace TouchNStars {

    public class Mediators(
        IDeepSkyObjectSearchVM DeepSkyObjectSearchVM,
        IImageDataFactory ImageDataFactory,
        IFramingAssistantVM framingAssistantVM,
        IProfileService profile,
        IGuiderMediator guider,
        ITelescopeMediator telescope,
        IMessageBroker broker,
        ISequenceMediator sequence,
        ICameraMediator camera,
        IImagingMediator imaging,
        IImageSaveMediator imageSaveMediator,
        IImageHistoryVM imageHistory,
        IFilterWheelMediator filterWheel,
        IFlatDeviceMediator flatDevice,
        ITwilightCalculator twilightCalculator,
        ISymbolBroker symbolBroker,
        IFocuserMediator focuser) {

        public readonly IDeepSkyObjectSearchVM DeepSkyObjectSearchVM = DeepSkyObjectSearchVM;
        public readonly IImageDataFactory ImageDataFactory = ImageDataFactory;
        public readonly IFramingAssistantVM FramingAssistantVM = framingAssistantVM;
        public readonly IProfileService Profile = profile;
        public readonly IGuiderMediator Guider = guider;
        public readonly ITelescopeMediator Telescope = telescope;
        public readonly IMessageBroker MessageBroker = broker;
        public readonly ISequenceMediator Sequence = sequence;
        public readonly ICameraMediator Camera = camera;
        public readonly IImagingMediator Imaging = imaging;
        public readonly IImageSaveMediator ImageSaveMediator = imageSaveMediator;
        public readonly IImageHistoryVM ImageHistory = imageHistory;
        public readonly IFilterWheelMediator FilterWheel = filterWheel;
        public readonly IFlatDeviceMediator FlatDevice = flatDevice;
        public readonly ITwilightCalculator TwilightCalculator = twilightCalculator;
        public readonly ISymbolBroker SymbolBroker = symbolBroker;
        public readonly IFocuserMediator Focuser = focuser;
    }

    [Export(typeof(IPluginManifest))]
    public class TouchNStars : PluginBase, INotifyPropertyChanged {
        private const string MdnsServiceType = "_touchnstars._tcp.";
        private const string MdnsInstancePrefix = "touchnstars_";

        // Machines sharing a hostname (e.g. identical Pi images) would otherwise advertise
        // colliding mDNS instance/host names, hiding all but one instance from discovery.
        private static readonly Lazy<string> mdnsMachineId = new(ComputeMdnsMachineId);

        private TouchNStarsServer server;
        private MdnsBroadcaster mdnsBroadcaster;

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
                    ITelescopeMediator telescope,
                    IMessageBroker broker,
                    ISequenceMediator sequence,
                    ICameraMediator camera,
                    IImagingMediator imagingMediator,
                    IImageSaveMediator imageSaveMediator,
                    IImageHistoryVM imageHistoryVM,
                    IFilterWheelMediator filterWheelMediator,
                    IFlatDeviceMediator flatDeviceMediator,
                    ITwilightCalculator twilightCalculator,
                    ISymbolBroker symbolBroker,
                    IFocuserMediator focuserMediator) {
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
                            telescope,
                            broker,
                            sequence,
                            camera,
                            imagingMediator,
                            imageSaveMediator,
                            imageHistoryVM,
                            filterWheelMediator,
                            flatDeviceMediator,
                            twilightCalculator,
                            symbolBroker,
                            focuserMediator);

            UpdateDefaultPortCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => {
                Port = CachedPort;
                CachedPort = Port; // This may look useless, but that way the visibility only changes when cachedPort changes and not when the user enters a new port
            });

            Communicator = new Communicator();

            // Pre-populate INDI JSON files on startup instead of first INDI API request.
            try {
                INDIDriverRegistry.PrepareDriverFiles(force: true);
            } catch (Exception ex) {
                Logger.Warning($"Failed to prepare INDI driver files during startup: {ex.Message}");
            }

            SetHostNames();

            if (AppEnabled) {
                CachedPort = CoreUtility.GetNearestAvailablePort(Port);
                server = new TouchNStarsServer(CachedPort);
                server.Start();
                ShowNotificationIfPortChanged();
                RefreshMdnsAdvertisement();
            }

            // Handle ToastNotifications culture issues on German systems  
            try {
                var enCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                System.Globalization.CultureInfo.CurrentUICulture = enCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = enCulture;
                System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = enCulture;
            } catch (Exception ex) {
                Logger.Debug($"Could not set UI culture to en-US: {ex.Message}");
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
                RaisePropertyChanged(nameof(MdnsServiceInstance));
                RefreshMdnsAdvertisement();
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
                try {
                    Notification.ShowInformation("Touch 'N' Stars launched on a different port: " + CachedPort);
                } catch (Exception ex) {
                    Logger.Warning($"Failed to show port notification: {ex.Message}");
                }
            }
        }


        public override Task Teardown() {
            server?.Stop();
            StopMdnsAdvertisement();
            mdnsBroadcaster?.Dispose();
            mdnsBroadcaster = null;
            Communicator.Dispose();
            Server.Controllers.PHD2Controller.CleanupPHD2Service();
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
                    try {
                        INDIDriverRegistry.PrepareDriverFiles(force: true);
                    } catch (Exception ex) {
                        Logger.Warning($"Failed to prepare INDI driver files when enabling app: {ex.Message}");
                    }

                    CachedPort = CoreUtility.GetNearestAvailablePort(Port);
                    server = new TouchNStarsServer(CachedPort);
                    server.Start();
                    SetHostNames();
                    RefreshMdnsAdvertisement();
                    try {
                        Notification.ShowSuccess("Touch 'N' Stars started!");
                    } catch (Exception ex) {
                        Logger.Warning($"Failed to show startup notification: {ex.Message}");
                    }
                    ShowNotificationIfPortChanged();
                } else {
                    server?.Stop();
                    StopMdnsAdvertisement();
                    try {
                        Notification.ShowSuccess("Touch 'N' Stars stopped!");
                    } catch (Exception ex) {
                        Logger.Warning($"Failed to show shutdown notification: {ex.Message}");
                    }
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

        public string InstanceName {
            get {
                return Settings.Default.InstanceName;
            }
            set {
                Settings.Default.InstanceName = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(MdnsServiceInstance));
                RefreshMdnsAdvertisement();
            }
        }

        public string MdnsServiceInstance => BuildMdnsInstanceName();

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

        private void RefreshMdnsAdvertisement() {
            if (!AppEnabled || server == null || CachedPort <= 0) {
                StopMdnsAdvertisement();
                return;
            }

            try {
                IPAddress address = ResolveMdnsAddress();
                var txtProperties = new Dictionary<string, string> {
                    ["instanceName"] = BuildMdnsDisplayName(),
                    ["instanceId"] = mdnsMachineId.Value,
                    ["port"] = CachedPort.ToString()
                };
                if (address != null) {
                    txtProperties["ip"] = address.ToString();
                }

                mdnsBroadcaster ??= new MdnsBroadcaster(MdnsServiceType);
                mdnsBroadcaster.StartOrUpdate(MdnsServiceInstance, CachedPort, address, txtProperties);
            } catch (Exception ex) {
                Logger.Error($"Failed to advertise Touch 'N' Stars via mDNS: {ex}");
            }
        }

        private void StopMdnsAdvertisement() {
            try {
                mdnsBroadcaster?.Stop();
            } catch (Exception ex) {
                Logger.Warning($"Failed to stop mDNS advertisement: {ex.Message}");
            }
        }

        private string BuildMdnsInstanceName() {
            return $"{MdnsInstancePrefix}{BuildMdnsDisplayName()}-{mdnsMachineId.Value}";
        }

        private string BuildMdnsDisplayName() {
            string suffix = SanitizeInstanceSuffix(GetInstanceNameOrDefault());

            if (!HasCustomInstanceName()) {
                int offset = GetPortOffset();
                if (offset > 0) {
                    suffix = $"{suffix}_{offset}";
                }
            }

            return suffix;
        }

        private static string ComputeMdnsMachineId() {
            try {
                var macs = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                    .Select(nic => nic.GetPhysicalAddress()?.ToString())
                    .Where(mac => !string.IsNullOrEmpty(mac))
                    .Distinct()
                    .OrderBy(mac => mac, StringComparer.Ordinal)
                    .ToList();

                if (macs.Count > 0) {
                    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", macs)));
                    return Convert.ToHexString(hash.AsSpan(0, 3)).ToLowerInvariant();
                }
            } catch (Exception ex) {
                Logger.Debug($"Failed to derive mDNS machine id from network interfaces: {ex.Message}");
            }

            return Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private string GetInstanceNameOrDefault() {
            string candidate = (InstanceName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate)) {
                candidate = Environment.MachineName ?? "default";
            }

            if (candidate.StartsWith(MdnsInstancePrefix, StringComparison.OrdinalIgnoreCase)) {
                candidate = candidate.Substring(MdnsInstancePrefix.Length);
            }

            return candidate;
        }

        private static string SanitizeInstanceSuffix(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return "default";
            }

            var builder = new StringBuilder(value.Length);
            foreach (char character in value) {
                if (character <= 0x7F && (char.IsLetterOrDigit(character) || character == '-' || character == '_')) {
                    builder.Append(character);
                } else if (char.IsWhiteSpace(character) || character == '.' || character == ':' || character == ',') {
                    builder.Append('-');
                }
            }

            string sanitized = builder.ToString().Trim('-');
            return string.IsNullOrEmpty(sanitized) ? "default" : sanitized;
        }

        private IPAddress ResolveMdnsAddress() {
            try {
                Dictionary<string, string> names = CoreUtility.GetLocalNames();
                if (names.TryGetValue("IPADRESS", out string ipString) && IPAddress.TryParse(ipString, out IPAddress ip)) {
                    return ip;
                }
            } catch (Exception ex) {
                Logger.Debug($"Failed to resolve advertised IPv4 address from CoreUtility: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(LocalNetworkAdress) && Uri.TryCreate(LocalNetworkAdress, UriKind.Absolute, out Uri uri) && IPAddress.TryParse(uri.Host, out IPAddress parsed)) {
                return parsed;
            }

            return null;
        }

        private bool HasCustomInstanceName() {
            return !string.IsNullOrWhiteSpace(Settings.Default.InstanceName);
        }

        private int GetPortOffset() {
            if (Port <= 0 || CachedPort <= 0) {
                return 0;
            }

            int offset = CachedPort - Port;
            return offset > 0 ? offset : 0;
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
