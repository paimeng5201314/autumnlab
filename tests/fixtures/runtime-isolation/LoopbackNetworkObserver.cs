using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutumnRuntimeIsolationProbe
{
    // A passive, bounded test observer. Construct explicitly in an isolated native test.
    // It binds only IPv4 loopback; no Windows firewall, registry or global proxy changes.
    public sealed class LoopbackNetworkObserver : IDisposable
    {
        readonly TcpListener http = new TcpListener(IPAddress.Loopback, 0);
        readonly TcpListener transport = new TcpListener(IPAddress.Loopback, 0);
        readonly UdpClient udp;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly ConcurrentBag<Task> clients = new ConcurrentBag<Task>();
        readonly ConcurrentQueue<object> accepted = new ConcurrentQueue<object>();
        readonly ConcurrentQueue<object> connections = new ConcurrentQueue<object>();
        readonly ConcurrentQueue<object> datagrams = new ConcurrentQueue<object>();
        readonly ConcurrentQueue<string> requests = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<string> failures = new ConcurrentQueue<string>();
        readonly Task httpWorker, transportWorker, udpWorker;
        int sequence;
        public string Token { get; }
        public int HttpPort { get; }
        public int TcpPort { get; }
        // Sharing the numerical HTTP port across TCP and UDP also observes HTTP/3 /
        // WebTransport datagrams addressed to https://127.0.0.1:<HttpPort>.
        public int UdpPort => HttpPort;
        public object[] AcceptedConnections => accepted.ToArray();
        public object[] CompletedConnections => connections.ToArray();
        public object[] Datagrams => datagrams.ToArray();
        public string[] HttpRequests => requests.ToArray();
        public string[] Failures => failures.ToArray();

        public LoopbackNetworkObserver(string token)
        {
            if (token == null || !Regex.IsMatch(token, "^[a-f0-9]{32}$")) throw new ArgumentException("A new random test token is required.");
            Token = token;
            http.Start(32);
            try
            {
                HttpPort = ((IPEndPoint)http.LocalEndpoint).Port;
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, HttpPort));
                transport.Start(32);
                TcpPort = ((IPEndPoint)transport.LocalEndpoint).Port;
            }
            catch { http.Stop(); transport.Stop(); udp?.Dispose(); throw; }
            httpWorker = Accept(http, "http"); transportWorker = Accept(transport, "turn_tcp"); udpWorker = ReceiveUdp();
        }

        async Task Accept(TcpListener listener, string channel)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    int id = Interlocked.Increment(ref sequence);
                    accepted.Enqueue(new { id, channel, utc = DateTimeOffset.UtcNow.ToString("o") });
                    clients.Add(ObserveClient(client, id, channel));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { failures.Enqueue("accept:" + channel + ":" + error.GetType().Name); }
        }

        async Task ObserveClient(TcpClient client, int id, string channel)
        {
            using (client)
            {
                byte[] buffer = new byte[8192]; int bytes = 0;
                string outcome = "pending", requestLine = "", control = "";
                bool completeHeaders = false, stunMagicCookie = false;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(4000);
                    var stream = client.GetStream();
                    while (bytes < buffer.Length)
                    {
                        int count = await stream.ReadAsync(buffer.AsMemory(bytes), timeout.Token);
                        if (count == 0) break;
                        bytes += count;
                        if (bytes >= 8) stunMagicCookie = buffer[4] == 0x21 && buffer[5] == 0x12 && buffer[6] == 0xa4 && buffer[7] == 0x42;
                        string text = Encoding.ASCII.GetString(buffer, 0, bytes);
                        if (text.StartsWith("CONTROL:" + Token + ":", StringComparison.Ordinal))
                        {
                            control = text.Substring(("CONTROL:" + Token + ":").Length).Trim();
                            if (control != "before" && control != "after") control = "invalid";
                            outcome = "positive_control"; break;
                        }
                        int end = text.IndexOf("\r\n", StringComparison.Ordinal);
                        if (requestLine.Length == 0 && end > 0 && end < 1024)
                        {
                            string first = text.Substring(0, end);
                            if (Regex.IsMatch(first, @"^[A-Z]{1,16} /[^\r\n ]* HTTP/1\.[01]$"))
                            { requestLine = first; requests.Enqueue(first); }
                        }
                        completeHeaders = text.Contains("\r\n\r\n", StringComparison.Ordinal);
                        if (completeHeaders || channel == "turn_tcp" || (bytes > 0 && buffer[0] == 0x16)) break;
                    }
                    if (outcome != "positive_control")
                    {
                        outcome = bytes == 0 ? "empty_connection" : stunMagicCookie ? "stun_turn_packet" : completeHeaders ? "http_headers" : "transport_bytes";
                        if (requestLine.Length != 0 && completeHeaders)
                        {
                            // Fixed innocuous response. Any probe request reaching this listener is
                            // a failing isolation observation, regardless of later browser/CORS errors.
                            string body = "owned-isolation-observer";
                            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body);
                            await stream.WriteAsync(response, timeout.Token);
                        }
                    }
                }
                catch (OperationCanceledException) { outcome = stop.IsCancellationRequested ? "observer_stopped" : bytes == 0 ? "empty_connection_timeout" : "partial_connection_timeout"; }
                catch (Exception error) { outcome = "transport_error:" + error.GetType().Name; }
                finally
                {
                    connections.Enqueue(new { id, channel, bytes, outcome, requestLine, control, completeHeaders, stunMagicCookie, utc = DateTimeOffset.UtcNow.ToString("o") });
                }
            }
        }

        async Task ReceiveUdp()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(stop.Token);
                    var bytes = packet.Buffer;
                    string text = Encoding.ASCII.GetString(bytes);
                    string control = "";
                    if (text.StartsWith("CONTROL:" + Token + ":", StringComparison.Ordinal))
                    {
                        control = text.Substring(("CONTROL:" + Token + ":").Length).Trim();
                        if (control != "before" && control != "after") control = "invalid";
                    }
                    bool stunMagicCookie = bytes.Length >= 8 && bytes[4] == 0x21 && bytes[5] == 0x12 && bytes[6] == 0xa4 && bytes[7] == 0x42;
                    datagrams.Enqueue(new { bytes = bytes.Length, control, stunMagicCookie, utc = DateTimeOffset.UtcNow.ToString("o") });
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { failures.Enqueue("udp:" + error.GetType().Name); }
        }

        public void Dispose()
        {
            stop.Cancel(); http.Stop(); transport.Stop(); udp.Dispose();
            try { Task.WhenAll(httpWorker, transportWorker, udpWorker).GetAwaiter().GetResult(); Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            stop.Dispose();
        }
    }
}
