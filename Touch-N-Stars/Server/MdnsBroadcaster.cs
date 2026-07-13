using System;
using System.Collections.Generic;
using System.Net;
using Makaretu.Dns;
using NINA.Core.Utility;

namespace TouchNStars.Server;

/// <summary>
/// Handles mDNS advertisement lifecycle for the Touch-N-Stars service.
/// </summary>
internal sealed class MdnsBroadcaster : IDisposable
{
    private readonly string serviceType;
    private readonly object syncRoot = new();

    private MulticastService multicastService;
    private ServiceDiscovery serviceDiscovery;
    private ServiceProfile serviceProfile;
    private bool isRunning;

    public MdnsBroadcaster(string serviceType)
    {
        this.serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
    }

    public void StartOrUpdate(string instanceName, int port, IPAddress address, IReadOnlyDictionary<string, string> txtProperties = null)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            throw new ArgumentException("Instance name must be provided.", nameof(instanceName));
        }

        if (port <= 0 || port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be within the valid TCP range.");
        }

        lock (syncRoot)
        {
            EnsureStarted();
            var addresses = address != null ? new[] { address } : null;
            var profile = new ServiceProfile(instanceName, serviceType, (ushort)port, addresses);
            if (txtProperties != null)
            {
                foreach (var pair in txtProperties)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                    {
                        profile.AddProperty(pair.Key, pair.Value);
                    }
                }
            }
            UpdateAdvertisement(profile);
        }
    }

    public void Stop()
    {
        lock (syncRoot)
        {
            if (!isRunning)
            {
                return;
            }

            try
            {
                if (serviceProfile != null)
                {
                    serviceDiscovery?.Unadvertise(serviceProfile);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to unadvertise mDNS service: {ex.Message}");
            }

            DisposeDiscovery();
            DisposeMulticast();

            serviceProfile = null;
            isRunning = false;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void EnsureStarted()
    {
        if (isRunning)
        {
            return;
        }

        try
        {
            multicastService = new MulticastService();
            serviceDiscovery = new ServiceDiscovery(multicastService);
            multicastService.Start();
            isRunning = true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start mDNS multicast service: {ex}");
            DisposeDiscovery();
            DisposeMulticast();
            throw;
        }
    }

    private void UpdateAdvertisement(ServiceProfile profile)
    {
        try
        {
            if (serviceProfile != null)
            {
                serviceDiscovery?.Unadvertise(serviceProfile);
            }
            serviceProfile = profile;
            serviceDiscovery?.Advertise(serviceProfile);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to update mDNS advertisement: {ex}");
        }
    }


    private void DisposeDiscovery()
    {
        if (serviceDiscovery == null)
        {
            return;
        }

        try
        {
            serviceDiscovery.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to dispose mDNS discovery: {ex.Message}");
        }
        finally
        {
            serviceDiscovery = null;
        }
    }

    private void DisposeMulticast()
    {
        if (multicastService == null)
        {
            return;
        }

        try
        {
            multicastService.Stop();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to stop mDNS multicast: {ex.Message}");
        }
        finally
        {
            multicastService.Dispose();
            multicastService = null;
        }
    }
}
