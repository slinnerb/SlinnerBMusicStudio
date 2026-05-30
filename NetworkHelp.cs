using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace SlinnerBMusicStudio;

// Helpers for the embedded-host ("lobby") flow: find this PC's addresses, try
// to open the router port via UPnP, and pack an address+code into one invite
// string friends can paste.
internal static class NetworkHelp
{
    // The LAN IP other machines on the same network would use to reach us.
    public static string? GetLanIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530); // no packets sent; just selects the outbound interface
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    // The public IP (for friends on the internet). Best-effort; null if offline.
    public static async Task<string?> GetPublicIpAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            return IPAddress.TryParse(ip, out _) ? ip : null;
        }
        catch { return null; }
    }

    // --- invite encoding --------------------------------------------------
    // "SBMS1:" + base64url("http://addr:port|CODE"). Friends paste this one token.

    private const string InvitePrefix = "SBMS1:";

    public static string MakeInvite(string address, string code)
    {
        var raw = Encoding.UTF8.GetBytes($"{address}|{code}");
        var b64 = Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return InvitePrefix + b64;
    }

    public static bool TryParseInvite(string text, out string address, out string code)
    {
        address = ""; code = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (!text.StartsWith(InvitePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var b64 = text[InvitePrefix.Length..].Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            int bar = raw.IndexOf('|');
            if (bar <= 0) return false;
            address = raw[..bar];
            code = raw[(bar + 1)..];
            return !string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(code);
        }
        catch { return false; }
    }

    // --- UPnP (best-effort) ----------------------------------------------
    // Asks the router (Internet Gateway Device) to forward an external TCP port
    // to this PC. Returns true if the router accepted the mapping.

    public static async Task<bool> TryOpenPortAsync(int port, string internalIp)
    {
        try
        {
            var controlUrl = await DiscoverIgdControlUrlAsync();
            if (controlUrl == null) return false;
            return await AddPortMappingAsync(controlUrl.Value.controlUrl, controlUrl.Value.serviceType, port, internalIp);
        }
        catch { return false; }
    }

    private static async Task<(string controlUrl, string serviceType)?> DiscoverIgdControlUrlAsync()
    {
        const string multicast = "239.255.255.250";
        const int ssdpPort = 1900;
        var search =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {multicast}:{ssdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        string? location = null;
        using (var udp = new UdpClient())
        {
            udp.Client.ReceiveTimeout = 2500;
            var data = Encoding.ASCII.GetBytes(search);
            var ep = new IPEndPoint(IPAddress.Parse(multicast), ssdpPort);
            await udp.SendAsync(data, data.Length, ep);

            var deadline = Environment.TickCount + 2500;
            while (Environment.TickCount < deadline)
            {
                try
                {
                    var receiveTask = udp.ReceiveAsync();
                    if (await Task.WhenAny(receiveTask, Task.Delay(2500)) != receiveTask) break;
                    var resp = Encoding.ASCII.GetString(receiveTask.Result.Buffer);
                    foreach (var line in resp.Split("\r\n"))
                    {
                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        {
                            location = line["LOCATION:".Length..].Trim();
                            break;
                        }
                    }
                    if (location != null) break;
                }
                catch { break; }
            }
        }
        if (location == null) return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var xml = await http.GetStringAsync(location);

        // Find a WAN connection service and its controlURL.
        string[] wanTypes =
        {
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1"
        };
        foreach (var type in wanTypes)
        {
            int idx = xml.IndexOf(type, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int ctrlIdx = xml.IndexOf("<controlURL>", idx, StringComparison.OrdinalIgnoreCase);
            if (ctrlIdx < 0) continue;
            int start = ctrlIdx + "<controlURL>".Length;
            int end = xml.IndexOf("</controlURL>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) continue;
            var ctrlPath = xml[start..end].Trim();

            var baseUri = new Uri(location);
            var controlUrl = new Uri(baseUri, ctrlPath).ToString();
            return (controlUrl, type);
        }
        return null;
    }

    private static async Task<bool> AddPortMappingAsync(string controlUrl, string serviceType, int port, string internalIp)
    {
        var soap =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:AddPortMapping xmlns:u=\"{serviceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            "<NewProtocol>TCP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{internalIp}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            "<NewPortMappingDescription>SlinnerB Music Studio</NewPortMappingDescription>" +
            "<NewLeaseDuration>0</NewLeaseDuration>" +
            "</u:AddPortMapping>" +
            "</s:Body></s:Envelope>";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        using var content = new StringContent(soap, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPACTION", $"\"{serviceType}#AddPortMapping\"");
        using var resp = await http.PostAsync(controlUrl, content);
        return resp.IsSuccessStatusCode;
    }
}
