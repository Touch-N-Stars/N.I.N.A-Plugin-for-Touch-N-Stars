using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using NINA.Core.Utility;

namespace TouchNStars.Utility;

public static class CoreUtility {
    public static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "Logs");
    public static readonly string AfPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "AutoFocus");
    public static readonly string Hips2FitsUrl = "http://alaskybis.u-strasbg.fr/hips-image-services/hips2fits";

    public static async Task<string> GetApiUrl() {
        await TouchNStars.Communicator.GetPort();
        return $"http://localhost:{Communicator.FoundPort}/v2/api";
    }

    public static double HmsToDegrees(string hms) {
        string[] hmsParts = hms.Split(':');
        if (hmsParts.Length == 3) {
            return (double.Parse(hmsParts[0]) * 15) + (double.Parse(hmsParts[1]) * (15.0 / 60)) + (double.Parse(hmsParts[2]) * (15.0 / 3600));
        } else {
            throw new ArgumentException("HMS string must be in the format HH:MM:SS, was: " + hms);
        }
    }

    public static double DmsToDegrees(string dms) {
        int sign = dms.StartsWith('-') ? -1 : 1;
        string stripped = dms.StartsWith('-') || dms.StartsWith('+') ? dms.Substring(1) : dms;

        string[] dmsParts = stripped.Split(':');

        if (dmsParts.Length == 3) {
            return (double.Parse(dmsParts[0]) + (double.Parse(dmsParts[1]) / 60) + (double.Parse(dmsParts[2]) / 3600)) * sign;
        } else {
            throw new ArgumentException("DMS string must be in the format DD:MM:SS.s, was: " + dms);
        }
    }

    public static string SafeRead(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string content = reader.ReadToEnd();
        return content;
    }

    public static Dictionary<string, string> GetLocalNames() {
        return lazyNames.Value;
    }

    public static bool IsPortAvailable(int port) {
        bool isPortAvailable = true;

        IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
        IPEndPoint[] ipEndPoints = ipGlobalProperties.GetActiveTcpListeners();

        foreach (IPEndPoint endPoint in ipEndPoints) {
            if (endPoint.Port == port) {
                isPortAvailable = false;
                break;
            }
        }

        return isPortAvailable;
    }

    public static int GetNearestAvailablePort(int startPort) {
        using var watch = MyStopWatch.Measure();
        int port = startPort;
        while (!IsPortAvailable(port)) {
            port++;
        }
        return port;
    }

    private static readonly Lazy<Dictionary<string, string>> lazyNames = new Lazy<Dictionary<string, string>>(() => {
        var names = new Dictionary<string, string>()
        {
                { "LOCALHOST", "localhost" }
            };

        string hostName = Dns.GetHostName();
        if (!string.IsNullOrEmpty(hostName)) {
            names.Add("HOSTNAME", hostName);
        }

        string ipv4 = GetIPv4Address();
        if (!string.IsNullOrEmpty(ipv4)) {
            names.Add("IPADRESS", ipv4);
        }

        return names;
    });

    public static string GetIPv4Address() {
        string localIP;
        try {
            using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
            localIP = endPoint.Address.ToString();
        } catch (Exception) {
            localIP = "127.0.0.1";
        }

        return localIP;
    }
}
