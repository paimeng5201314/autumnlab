using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace AutumnT06Security {
    public sealed class ConnectionObservation {
        public int Id { get; }
        public string Outcome { get; }
        public int BytesRead { get; }
        public bool CompleteHttpHeaders { get; }
        public string RequestLine { get; }
        public string ErrorType { get; }
        public DateTimeOffset ObservedUtc { get; }
        public ConnectionObservation(int id, string outcome, int bytesRead, bool completeHeaders, string requestLine, string errorType) {
            Id=id;Outcome=outcome;BytesRead=bytesRead;CompleteHttpHeaders=completeHeaders;RequestLine=requestLine;ErrorType=errorType;ObservedUtc=DateTimeOffset.UtcNow;
        }
    }
    public sealed class Observer : IDisposable {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly ConcurrentQueue<string> requests = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<string> failures = new ConcurrentQueue<string>();
        readonly ConcurrentBag<Task> clients = new ConcurrentBag<Task>();
        readonly ConcurrentQueue<ConnectionObservation> connections = new ConcurrentQueue<ConnectionObservation>();
        int nextConnection;
        readonly Task worker;
        public int Port { get; }
        public string[] Requests => requests.ToArray();
        public string[] Failures => failures.ToArray();
        public ConnectionObservation[] Connections => connections.ToArray();
        public Observer() { listener.Start(8); Port = ((IPEndPoint)listener.LocalEndpoint).Port; worker = Run(); }
        async Task Run() {
            try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(Handle(client, Interlocked.Increment(ref nextConnection))); } }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { failures.Enqueue(error.GetType().Name); }
        }
        async Task Handle(TcpClient client, int id) {
            using (client) {
                var bytes = new byte[4096]; int length=0; bool completeHeaders=false;
                string outcome="observer_disposed_pending_connection", requestLine="", errorType="";
                void CaptureRequestLine() {
                    if(requestLine.Length!=0 || length==0) return;
                    string text=Encoding.ASCII.GetString(bytes,0,length);
                    int end=text.IndexOf("\r\n",StringComparison.Ordinal);
                    string first=end<0 ? text : text.Substring(0,end);
                    if(first.Length<=1024 && System.Text.RegularExpressions.Regex.IsMatch(first,@"^[A-Z]{1,16} /[^\r\n ]* HTTP/1\.[01]$")) {
                        requestLine=first;requests.Enqueue(first);
                    }
                }
                try {
                    var stream = client.GetStream();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(3000);
                    while (length < bytes.Length) {
                        int count=await stream.ReadAsync(bytes.AsMemory(length),timeout.Token);
                        if(count==0) break;
                        length+=count;CaptureRequestLine();
                        completeHeaders=Encoding.ASCII.GetString(bytes,0,length).Contains("\r\n\r\n",StringComparison.Ordinal);
                        if(completeHeaders) break;
                    }
                    if(!completeHeaders) {
                        outcome=length==0 ? "empty_connection_closed" : length==bytes.Length ? "request_header_limit" : "partial_http_headers_closed";
                        return;
                    }
                    if(requestLine.Length==0) {outcome="invalid_http_header_block";return;}
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 14\r\nConnection: close\r\n\r\nowned-listener");
                    await stream.WriteAsync(response, timeout.Token);
                    outcome="http_request";
                } catch (OperationCanceledException) {
                    outcome=stop.IsCancellationRequested ? "observer_disposed_pending_connection" : completeHeaders ? "response_timeout" : length==0 ? "connection_timeout_without_http_headers" : "partial_http_headers_timeout";
                }
                catch (Exception error) {outcome="client_transport_error";errorType=error.GetType().Name;}
                finally {CaptureRequestLine();connections.Enqueue(new ConnectionObservation(id,outcome,length,completeHeaders,requestLine,errorType));}
            }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); try { worker.GetAwaiter().GetResult(); Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult(); } catch(OperationCanceledException) { } stop.Dispose(); }
    }
}
