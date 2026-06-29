using EmbedIO.WebSockets;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.INDI;
using NINA.INDI.Devices;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TouchNStars.Server.Models;

namespace TouchNStars.Server;

/// <summary>
/// WebSocket for manual (press-and-hold) mount slewing, served INDI-direct from the
/// Touch-N-Stars plugin so it can drive the mount by direction only. The slew rate is set
/// separately through POST /api/indi/mount/slew-rate (the capability model); this socket never
/// touches the rate, which keeps a chosen discrete step (e.g. "Guide") from being overwritten on
/// every keepalive message.
///
/// Protocol: the client sends { "direction": "north|south|east|west|stop" } on press and repeats
/// it as a keepalive (~every 800 ms); it sends "stop" on release. A server-side dead-man watchdog
/// stops all motion if no message arrives within the timeout, so a dropped connection or dead tab
/// cannot leave the mount slewing.
/// </summary>
public class MountControlSocket : WebSocketModule
{
    private static readonly TimeSpan DeadManTimeout = TimeSpan.FromSeconds(1.8);

    private static readonly object _lock = new object();
    private static DateTime _lastMoveAt;
    private static string _lastDirection = string.Empty;

    public MountControlSocket(string urlPath) : base(urlPath, true)
    {
    }

    protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
    {
        ApiResponse response;
        try
        {
            var message = Encoding.GetString(buffer);
            var json = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            string direction = json != null && json.TryGetValue("direction", out var d) ? d?.ToString()?.ToLowerInvariant() : null;

            var mount = INDIClient.Instance.GetRegisteredDevice<INDITelescope>();
            if (mount == null)
            {
                response = Error("No INDI mount is currently connected");
            }
            else
            {
                response = HandleDirection(mount, direction);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"MountControlSocket error: {ex}");
            response = Error(ex.Message);
        }

        await context.WebSocket.SendAsync(Encoding.GetBytes(JsonSerializer.Serialize(response)), true);
    }

    private ApiResponse HandleDirection(INDITelescope mount, string direction)
    {
        switch (direction)
        {
            case "north":
                mount.MoveAxisDirection(TelescopeAxes.Secondary, 1);
                break;
            case "south":
                mount.MoveAxisDirection(TelescopeAxes.Secondary, -1);
                break;
            case "east":
                mount.MoveAxisDirection(TelescopeAxes.Primary, 1);
                break;
            case "west":
                mount.MoveAxisDirection(TelescopeAxes.Primary, -1);
                break;
            case "stop":
                StopAll(mount);
                return new ApiResponse { Success = true, Response = "Stopped Move", StatusCode = 200, Type = "MountControl" };
            default:
                return Error("Invalid direction");
        }

        DateTime scheduledAt;
        lock (_lock)
        {
            _lastMoveAt = DateTime.UtcNow;
            _lastDirection = direction;
            scheduledAt = _lastMoveAt;
        }
        ScheduleDeadManStop(mount, scheduledAt);

        return new ApiResponse { Success = true, Response = "Moving", StatusCode = 200, Type = "MountControl" };
    }

    /// <summary>
    /// Stops all motion if no newer move message arrived after <paramref name="scheduledAt"/>.
    /// The timestamp acts as a token: each move records a fresh <see cref="_lastMoveAt"/>, so a
    /// later message invalidates this scheduled stop.
    /// </summary>
    private static void ScheduleDeadManStop(INDITelescope mount, DateTime scheduledAt)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(DeadManTimeout + TimeSpan.FromMilliseconds(200));
            try
            {
                lock (_lock)
                {
                    if (_lastMoveAt != scheduledAt)
                    {
                        return; // a newer move superseded this one
                    }
                    if (DateTime.UtcNow - _lastMoveAt >= DeadManTimeout)
                    {
                        StopAll(mount);
                        _lastDirection = string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"MountControlSocket dead-man stop failed: {ex}");
            }
        });
    }

    private static void StopAll(INDITelescope mount)
    {
        mount.MoveAxisDirection(TelescopeAxes.Primary, 0);
        mount.MoveAxisDirection(TelescopeAxes.Secondary, 0);
    }

    private static ApiResponse Error(string error)
        => new ApiResponse { Success = false, Error = error, StatusCode = 400, Type = "Error" };
}
