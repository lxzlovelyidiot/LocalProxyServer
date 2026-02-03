using System.Net;
using System.Text;

namespace LocalProxyServer
{
    /// <summary>
    /// Simple HTTP service that provides CRL file download on specified port,
    /// for clients like Windows Schannel to complete certificate revocation checking.
    /// </summary>
    public class CrlServer
    {
        private readonly int _port;
        private readonly byte[] _crlDer;
        private readonly string _path;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _runTask;

        public CrlServer(int port, byte[] crlDer, string path = "/crl.der")
        {
            _port = port;
            _crlDer = crlDer;
            _path = path.TrimEnd('/') switch { "" => "/", var p => p };
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _runTask = RunAsync(_cts.Token);
            Console.WriteLine($"CRL distribution server started at http://127.0.0.1:{_port}{_path}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _runTask?.GetAwaiter().GetResult(); } catch { }
        }

        private async Task RunAsync(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(cancel).ConfigureAwait(false);
                    _ = HandleRequestAsync(context);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancel.IsCancellationRequested)
                        Console.WriteLine($"CRL server error: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";
                if (path == "" || path == "/") path = "/";
                var wantPath = _path.TrimEnd('/');
                if (wantPath == "") wantPath = "/";
                if (!path.Equals(wantPath, StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 404;
                    var notFound = Encoding.UTF8.GetBytes("Not Found");
                    response.ContentLength64 = notFound.Length;
                    response.ContentType = "text/plain";
                    await response.OutputStream.WriteAsync(notFound).ConfigureAwait(false);
                }
                else
                {
                    response.StatusCode = 200;
                    response.ContentType = "application/pkix-crl";
                    response.ContentLength64 = _crlDer.Length;
                    response.AddHeader("Content-Disposition", "attachment; filename=\"crl.der\"");
                    await response.OutputStream.WriteAsync(_crlDer).ConfigureAwait(false);
                }
            }
            finally
            {
                response.Close();
            }
        }
    }
}
