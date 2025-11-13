# Touch-N-Stars Network Discovery

## Overview

The Touch-N-Stars plugin now includes UDP broadcast functionality for automatic network discovery. This allows clients on the same network to discover the plugin and its port without manual configuration.

## Broadcast Details

- **Protocol**: UDP Broadcast
- **Port**: 37020
- **Interval**: Every 5 seconds
- **Identifier**: `NINA-TouchNStars`

## Message Format

The broadcast message uses a pipe-delimited format:

```
NINA-TouchNStars|Port:{port}|Host:{hostname}|IP:{ipAddress}
```

### Example Message

```
NINA-TouchNStars|Port:5555|Host:DESKTOP-ABC123|IP:192.168.1.100
```

## How to Listen for Broadcasts

To discover Touch-N-Stars plugin instances on your network, create a UDP listener on port 37020.

### Example C# Listener

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

class DiscoveryListener
{
    const int BROADCAST_PORT = 37020;
    
    static void Main()
    {
        using var udpClient = new UdpClient(BROADCAST_PORT);
        var remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
        
        Console.WriteLine("Listening for Touch-N-Stars broadcasts...");
        
        while (true)
        {
            byte[] data = udpClient.Receive(ref remoteEndpoint);
            string message = Encoding.UTF8.GetString(data);
            
            // Parse the message
            var parts = message.Split('|');
            if (parts.Length >= 4 && parts[0] == "NINA-TouchNStars")
            {
                string port = parts[1].Replace("Port:", "");
                string hostname = parts[2].Replace("Host:", "");
                string ip = parts[3].Replace("IP:", "");
                
                Console.WriteLine($"Found Touch-N-Stars at {ip}:{port} (Host: {hostname})");
            }
        }
    }
}
```

### Example Python Listener

```python
import socket

BROADCAST_PORT = 37020

def listen_for_broadcasts():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(('', BROADCAST_PORT))
    
    print("Listening for Touch-N-Stars broadcasts...")
    
    while True:
        data, addr = sock.recvfrom(1024)
        message = data.decode('utf-8')
        
        parts = message.split('|')
        if len(parts) >= 4 and parts[0] == 'NINA-TouchNStars':
            port = parts[1].replace('Port:', '')
            hostname = parts[2].replace('Host:', '')
            ip = parts[3].replace('IP:', '')
            
            print(f"Found Touch-N-Stars at {ip}:{port} (Host: {hostname})")

if __name__ == '__main__':
    listen_for_broadcasts()
```

## Firewall Configuration

If you're not receiving broadcasts, ensure that:

1. UDP port 37020 is allowed in your firewall (inbound)
2. Broadcast packets are not blocked on your network
3. The client and server are on the same subnet (broadcasts don't cross router boundaries by default)

## Security Considerations

- The broadcast contains only publicly accessible information (hostname, IP, port)
- No authentication credentials are transmitted
- The information is read-only and cannot be used to control the plugin
- Broadcasts are sent to the local network only (not routed beyond the subnet)

## Troubleshooting

### Not receiving broadcasts

1. Check firewall settings on both client and server
2. Verify both devices are on the same subnet
3. Some network configurations block broadcast traffic
4. Try connecting directly using the IP and port shown in the NINA plugin settings

### Multiple instances

If you have multiple instances of Touch-N-Stars running (unlikely but possible), each will broadcast its own information. The hostname and IP will help you identify which instance is which.
