using System.Net;
using System.Net.Sockets;

namespace LocalProxyServer
{
    internal static class TcpClientConnector
    {
        public static async Task<TcpClient> ConnectAsync(string host, int port, AddressFamily? preferredAddressFamily)
        {
            ArgumentNullException.ThrowIfNull(host);

            if (IPAddress.TryParse(host, out var ipAddress))
            {
                var client = CreateClient(ipAddress.AddressFamily);
                await client.ConnectAsync(ipAddress, port);
                return client;
            }

            if (preferredAddressFamily is null || preferredAddressFamily == AddressFamily.Unspecified)
            {
                var client = CreateDualModeClient();
                await client.ConnectAsync(host, port);
                return client;
            }

            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Length == 0)
            {
                throw new InvalidOperationException($"No IP addresses found for host '{host}'.");
            }

            var selectedAddress = SelectAddress(addresses, preferredAddressFamily.Value);
            var selectedClient = CreateClient(selectedAddress.AddressFamily);
            await selectedClient.ConnectAsync(selectedAddress, port);
            return selectedClient;
        }

        private static TcpClient CreateDualModeClient()
        {
            if (!Socket.OSSupportsIPv6)
            {
                return new TcpClient(AddressFamily.InterNetwork);
            }

            var client = new TcpClient(AddressFamily.InterNetworkV6);
            client.Client.DualMode = true;
            return client;
        }

        private static TcpClient CreateClient(AddressFamily addressFamily)
        {
            var client = new TcpClient(addressFamily);
            if (addressFamily == AddressFamily.InterNetworkV6)
            {
                client.Client.DualMode = true;
            }

            return client;
        }

        private static IPAddress SelectAddress(IPAddress[] addresses, AddressFamily preferredAddressFamily)
        {
            var preferred = Array.Find(addresses, address => address.AddressFamily == preferredAddressFamily);
            if (preferred != null)
            {
                return preferred;
            }

            var fallbackFamily = preferredAddressFamily == AddressFamily.InterNetworkV6
                ? AddressFamily.InterNetwork
                : AddressFamily.InterNetworkV6;
            var fallback = Array.Find(addresses, address => address.AddressFamily == fallbackFamily);
            if (fallback != null)
            {
                return fallback;
            }

            return addresses[0];
        }
    }
}
