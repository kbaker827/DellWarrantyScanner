using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DellWarrantyScanner.Models;

namespace DellWarrantyScanner.Services;

public class NetworkScanner
{
    private readonly AppSettings _settings;

    public NetworkScanner(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<List<DeviceInfo>> ScanAsync(
        string ipRange,
        IProgress<(int completed, int total, string message)> progress,
        CancellationToken ct)
    {
        var ips = ParseIpRange(ipRange);
        if (ips.Count == 0)
            throw new ArgumentException($"Could not parse IP range: {ipRange}");

        progress.Report((0, ips.Count, $"Pinging {ips.Count} addresses..."));

        // Ping all IPs concurrently
        var semaphore = new SemaphoreSlim(50, 50);
        int pinged = 0;
        var pingTasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                bool alive = await PingAsync(ip, ct);
                int done = Interlocked.Increment(ref pinged);
                progress.Report((done, ips.Count, $"Pinging... {done}/{ips.Count}"));
                return alive ? ip : null;
            }
            finally { semaphore.Release(); }
        });

        var pingResults = await Task.WhenAll(pingTasks);
        var aliveIps = pingResults.Where(ip => ip != null).Cast<string>().ToList();

        progress.Report((0, aliveIps.Count, $"Found {aliveIps.Count} live hosts. Querying WMI for Dell systems..."));

        // WMI query live hosts concurrently (lower concurrency to avoid WMI overload)
        var wmiSemaphore = new SemaphoreSlim(10, 10);
        int queried = 0;
        var devices = new System.Collections.Concurrent.ConcurrentBag<DeviceInfo>();

        var wmiTasks = aliveIps.Select(async ip =>
        {
            await wmiSemaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var device = await QueryWmiAsync(ip, ct);
                if (device != null)
                    devices.Add(device);
                int done = Interlocked.Increment(ref queried);
                progress.Report((done, aliveIps.Count, $"WMI query... {done}/{aliveIps.Count}"));
            }
            finally { wmiSemaphore.Release(); }
        });

        await Task.WhenAll(wmiTasks);

        return devices.OrderBy(d => d.IpAddress, new IpComparer()).ToList();
    }

    private async Task<bool> PingAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1000);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private async Task<DeviceInfo?> QueryWmiAsync(string ip, CancellationToken ct)
    {
        string hostname = await ResolveHostnameAsync(ip);

        return await Task.Run(() =>
        {
            try
            {
                var options = new ConnectionOptions
                {
                    Timeout = TimeSpan.FromSeconds(10),
                    EnablePrivileges = true,
                    Authentication = AuthenticationLevel.PacketPrivacy,
                    Impersonation = ImpersonationLevel.Impersonate
                };

                if (!_settings.UseCurrentCredentials && !string.IsNullOrWhiteSpace(_settings.WmiUsername))
                {
                    options.Username = _settings.WmiUsername;
                    options.Password = _settings.WmiPassword;
                }

                var scope = new ManagementScope($@"\\{ip}\root\cimv2", options);
                scope.Connect();

                string manufacturer = "";
                string model = "";
                string serviceTag = "";

                using (var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Manufacturer, Model FROM Win32_ComputerSystem")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                        model = obj["Model"]?.ToString() ?? "";
                        break;
                    }
                }

                // Only continue if it's a Dell
                if (!manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase))
                    return null;

                using (var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT SerialNumber FROM Win32_BIOS")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        serviceTag = obj["SerialNumber"]?.ToString() ?? "";
                        break;
                    }
                }

                return new DeviceInfo
                {
                    IpAddress = ip,
                    Hostname = hostname,
                    ServiceTag = serviceTag,
                    Model = model,
                    WarrantyStatus = "Pending"
                };
            }
            catch (ManagementException)
            {
                return null; // WMI unavailable or not Windows
            }
            catch (UnauthorizedAccessException)
            {
                return new DeviceInfo
                {
                    IpAddress = ip,
                    Hostname = hostname,
                    WarrantyStatus = "WMI Access Denied",
                    Notes = "Check WMI credentials"
                };
            }
            catch
            {
                return null;
            }
        }, ct);
    }

    private async Task<string> ResolveHostnameAsync(string ip)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip);
            return entry.HostName;
        }
        catch { return ip; }
    }

    public static List<string> ParseIpRange(string input)
    {
        var ips = new List<string>();
        input = input.Trim();

        // CIDR notation: 192.168.1.0/24
        if (input.Contains('/'))
        {
            var parts = input.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[1], out int prefix) && prefix is >= 0 and <= 32)
            {
                if (IPAddress.TryParse(parts[0], out var baseIp))
                {
                    uint baseInt = IpToUint(baseIp);
                    uint mask = prefix == 0 ? 0 : (0xFFFFFFFF << (32 - prefix));
                    uint network = baseInt & mask;
                    uint broadcast = network | ~mask;
                    // Skip network and broadcast addresses
                    for (uint i = network + 1; i < broadcast; i++)
                        ips.Add(UintToIp(i));
                }
            }
            return ips;
        }

        // Range: 192.168.1.1-192.168.1.254 or 192.168.1.1-254
        if (input.Contains('-'))
        {
            var parts = input.Split('-');
            if (parts.Length == 2)
            {
                if (IPAddress.TryParse(parts[0].Trim(), out var startIp))
                {
                    string endStr = parts[1].Trim();
                    IPAddress? endIp;
                    if (!IPAddress.TryParse(endStr, out endIp))
                    {
                        // Short form: last octet only
                        var startParts = parts[0].Trim().Split('.');
                        if (startParts.Length == 4)
                            IPAddress.TryParse($"{startParts[0]}.{startParts[1]}.{startParts[2]}.{endStr}", out endIp);
                    }
                    if (endIp != null)
                    {
                        uint start = IpToUint(startIp);
                        uint end = IpToUint(endIp);
                        for (uint i = start; i <= end; i++)
                            ips.Add(UintToIp(i));
                    }
                }
            }
            return ips;
        }

        // Single IP
        if (IPAddress.TryParse(input, out _))
            ips.Add(input);

        return ips;
    }

    // Returns all non-loopback IPv4 subnets on active adapters, as CIDR strings.
    public static List<(string Cidr, string AdapterName)> DetectLocalSubnets()
    {
        var results = new List<(string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel) continue;

            var props = ni.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.IPv4Mask == null) continue;

                byte[] maskBytes = addr.IPv4Mask.GetAddressBytes();
                byte[] ipBytes   = addr.Address.GetAddressBytes();

                // Count prefix bits
                int prefix = 0;
                foreach (var b in maskBytes)
                {
                    byte bit = 0x80;
                    while (bit > 0 && (b & bit) != 0) { prefix++; bit >>= 1; }
                }

                // Network address
                byte[] net = new byte[4];
                for (int i = 0; i < 4; i++) net[i] = (byte)(ipBytes[i] & maskBytes[i]);

                string cidr = $"{net[0]}.{net[1]}.{net[2]}.{net[3]}/{prefix}";
                results.Add((cidr, ni.Name));
            }
        }
        return results;
    }

    private static uint IpToUint(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static string UintToIp(uint ip) =>
        $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

    private class IpComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null || y == null) return string.Compare(x, y);
            if (IPAddress.TryParse(x, out var ipX) && IPAddress.TryParse(y, out var ipY))
            {
                byte[] bx = ipX.GetAddressBytes();
                byte[] by = ipY.GetAddressBytes();
                for (int i = 0; i < 4; i++)
                {
                    int cmp = bx[i].CompareTo(by[i]);
                    if (cmp != 0) return cmp;
                }
                return 0;
            }
            return string.Compare(x, y);
        }
    }
}
