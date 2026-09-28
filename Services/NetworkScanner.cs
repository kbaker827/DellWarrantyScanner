using System.Collections.Concurrent;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DellWarrantyScanner.Models;

namespace DellWarrantyScanner.Services;

public class NetworkScanner
{
    public const int MaxAddresses = 65536;

    private const int PingConcurrency = 50;
    private const int WmiConcurrency = 10; // lower to avoid overloading WMI
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);

    private readonly AppSettings _settings;
    private readonly HashSet<string> _localAddresses = GetLocalIPv4Addresses();

    public NetworkScanner(AppSettings settings)
    {
        _settings = settings;
    }

    // Returns the Dell systems found, sorted by IP. If ct is cancelled the scan stops
    // early and returns what was found so far instead of throwing.
    public async Task<List<DeviceInfo>> ScanAsync(
        string ipRange,
        IProgress<(int completed, int total, string message)> progress,
        IProgress<DeviceInfo>? deviceFound,
        CancellationToken ct)
    {
        var ips = ParseIpRange(ipRange);
        var devices = new ConcurrentBag<DeviceInfo>();

        try
        {
            progress.Report((0, ips.Count, $"Pinging {ips.Count} addresses..."));

            using var pingSemaphore = new SemaphoreSlim(PingConcurrency);
            int pinged = 0;
            var pingTasks = ips.Select(async ip =>
            {
                await pingSemaphore.WaitAsync(ct);
                try
                {
                    bool alive = await PingAsync(ip, ct);
                    int done = Interlocked.Increment(ref pinged);
                    progress.Report((done, ips.Count, $"Pinging... {done}/{ips.Count}"));
                    return alive ? ip : null;
                }
                finally { pingSemaphore.Release(); }
            }).ToList();

            var aliveIps = (await Task.WhenAll(pingTasks)).OfType<string>().ToList();

            progress.Report((0, aliveIps.Count, $"Found {aliveIps.Count} live hosts. Querying WMI for Dell systems..."));

            using var wmiSemaphore = new SemaphoreSlim(WmiConcurrency);
            int queried = 0;
            var wmiTasks = aliveIps.Select(async ip =>
            {
                await wmiSemaphore.WaitAsync(ct);
                try
                {
                    var device = await QueryDeviceAsync(ip, ct);
                    if (device != null)
                    {
                        devices.Add(device);
                        deviceFound?.Report(device);
                    }
                    int done = Interlocked.Increment(ref queried);
                    progress.Report((done, aliveIps.Count, $"WMI query... {done}/{aliveIps.Count}"));
                }
                finally { wmiSemaphore.Release(); }
            }).ToList();

            await Task.WhenAll(wmiTasks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped by the user: fall through and return the partial results.
        }

        return devices.OrderBy(d => d.IpAddress, IpComparer.Instance).ToList();
    }

    private static async Task<bool> PingAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(IPAddress.Parse(ip), PingTimeout, cancellationToken: ct);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private async Task<DeviceInfo?> QueryDeviceAsync(string ip, CancellationToken ct)
    {
        var device = await Task.Run(() => QueryWmi(ip, ct), ct);
        if (device != null)
            device.Hostname = await ResolveHostnameAsync(ip, ct);
        return device;
    }

    private DeviceInfo? QueryWmi(string ip, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var options = new ConnectionOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                EnablePrivileges = true,
                Authentication = AuthenticationLevel.PacketPrivacy,
                Impersonation = ImpersonationLevel.Impersonate
            };

            // WMI rejects explicit credentials on local connections, so the scanning
            // machine itself is always queried as the current user.
            if (!_settings.UseCurrentCredentials &&
                !string.IsNullOrWhiteSpace(_settings.WmiUsername) &&
                !_localAddresses.Contains(ip))
            {
                options.Username = _settings.WmiUsername;
                options.Password = _settings.WmiPassword;
            }

            var scope = new ManagementScope($@"\\{ip}\root\cimv2", options);
            scope.Connect();

            var (manufacturer, model) = QueryFirst(scope,
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem",
                o => (o["Manufacturer"]?.ToString() ?? "", o["Model"]?.ToString() ?? ""),
                fallback: ("", ""));

            if (!manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase))
                return null;

            string serviceTag = QueryFirst(scope,
                "SELECT SerialNumber FROM Win32_BIOS",
                o => o["SerialNumber"]?.ToString() ?? "",
                fallback: "").Trim();

            var device = new DeviceInfo { IpAddress = ip, Hostname = ip, ServiceTag = serviceTag, Model = model.Trim() };
            if (serviceTag.Length == 0)
            {
                device.WarrantyStatus = WarrantyStatus.NoServiceTag;
                device.Notes = "BIOS did not report a serial number";
            }
            return device;
        }
        catch (UnauthorizedAccessException)
        {
            return new DeviceInfo
            {
                IpAddress = ip,
                Hostname = ip,
                WarrantyStatus = WarrantyStatus.WmiAccessDenied,
                Notes = "Check WMI credentials"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // WMI unavailable, RPC blocked by a firewall, or not a Windows host
        }
    }

    private static T QueryFirst<T>(ManagementScope scope, string query,
        Func<ManagementBaseObject, T> select, T fallback)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
        using var results = searcher.Get();
        foreach (var obj in results)
        {
            using (obj)
                return select(obj);
        }
        return fallback;
    }

    private static async Task<string> ResolveHostnameAsync(string ip, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip, ct);
            return entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return ip;
        }
    }

    // Accepts CIDR (192.168.1.0/24), a full range (10.0.0.1-10.0.0.50),
    // a last-octet range (10.0.0.1-50), or a single IPv4 address.
    // Throws ArgumentException for invalid input or ranges over MaxAddresses.
    public static List<string> ParseIpRange(string input)
    {
        input = input.Trim();
        uint first, last;

        if (input.Contains('/'))
        {
            var parts = input.Split('/');
            if (parts.Length != 2 ||
                !TryParseIPv4(parts[0].Trim(), out uint baseIp) ||
                !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) ||
                prefix > 32)
                throw new ArgumentException($"'{input}' is not valid CIDR notation (e.g. 192.168.1.0/24).");

            uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            uint network = baseIp & mask;
            uint broadcast = network | ~mask;

            // /31 (point-to-point) and /32 (single host) have no network/broadcast address to skip.
            (first, last) = prefix >= 31 ? (network, broadcast) : (network + 1, broadcast - 1);
        }
        else if (input.Contains('-'))
        {
            var parts = input.Split('-');
            if (parts.Length != 2 || !TryParseIPv4(parts[0].Trim(), out first))
                throw new ArgumentException($"'{input}' is not a valid IP range (e.g. 10.0.0.1-10.0.0.50).");

            string endStr = parts[1].Trim();
            if (!TryParseIPv4(endStr, out last))
            {
                if (!byte.TryParse(endStr, NumberStyles.None, CultureInfo.InvariantCulture, out byte lastOctet))
                    throw new ArgumentException($"'{input}' is not a valid IP range (e.g. 10.0.0.1-50).");
                last = (first & 0xFFFFFF00) | lastOctet;
            }

            if (first > last) (first, last) = (last, first);
        }
        else
        {
            if (!TryParseIPv4(input, out first))
                throw new ArgumentException($"'{input}' is not a valid IPv4 address.");
            last = first;
        }

        ulong count = (ulong)last - first + 1;
        if (count > MaxAddresses)
            throw new ArgumentException(
                $"That range has {count:N0} addresses; the maximum is {MaxAddresses:N0}. Please narrow it down.");

        var ips = new List<string>((int)count);
        for (ulong i = first; i <= last; i++)
            ips.Add(UintToIp((uint)i));
        return ips;
    }

    // Strict dotted-quad parser. IPAddress.TryParse also accepts forms like "10"
    // or "10.1" and IPv6, which would silently expand to unexpected ranges.
    private static bool TryParseIPv4(string s, out uint value)
    {
        value = 0;
        var octets = s.Split('.');
        if (octets.Length != 4) return false;
        foreach (var octet in octets)
        {
            if (octet.Length is 0 or > 3 ||
                !byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out byte b))
                return false;
            value = (value << 8) | b;
        }
        return true;
    }

    // Returns all IPv4 subnets on active, non-loopback adapters, as CIDR strings.
    public static List<(string Cidr, string AdapterName)> DetectLocalSubnets()
    {
        var results = new List<(string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.IPv4Mask == null) continue;

                byte[] ipBytes   = addr.Address.GetAddressBytes();
                byte[] maskBytes = addr.IPv4Mask.GetAddressBytes();

                // Skip link-local (APIPA) addresses: the adapter has no real network.
                if (ipBytes[0] == 169 && ipBytes[1] == 254) continue;

                int prefix = 0;
                foreach (var b in maskBytes)
                {
                    byte bit = 0x80;
                    while (bit > 0 && (b & bit) != 0) { prefix++; bit >>= 1; }
                }

                byte[] net = new byte[4];
                for (int i = 0; i < 4; i++) net[i] = (byte)(ipBytes[i] & maskBytes[i]);

                results.Add(($"{net[0]}.{net[1]}.{net[2]}.{net[3]}/{prefix}", ni.Name));
            }
        }
        return results;
    }

    private static HashSet<string> GetLocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return new HashSet<string>();
        }
    }

    private static string UintToIp(uint ip) =>
        $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

    private sealed class IpComparer : IComparer<string>
    {
        public static readonly IpComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x != null && y != null && TryParseIPv4(x, out uint a) && TryParseIPv4(y, out uint b))
                return a.CompareTo(b);
            return string.CompareOrdinal(x, y);
        }
    }
}
