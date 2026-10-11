using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PolarAlignLive.Remote {

    /// <summary>
    /// Minimal HTTP server (plain TcpListener, so no admin rights or URL reservation are needed) that serves the phone web page
    /// and a tiny JSON API: GET /api/state, POST /api/cmd (body "name|arg"). Every API call needs the PIN in the X-Pin header.
    /// </summary>
    internal sealed class RemoteServer {

        private readonly Func<string> getState;
        private readonly Func<string, string, string> runCommand;
        private readonly Func<string> getPin;
        private TcpListener listener;
        private CancellationTokenSource cts;

        public string LastError { get; private set; }
        public bool Running => listener != null;

        public RemoteServer(Func<string> getState, Func<string, string, string> runCommand, Func<string> getPin) {
            this.getState = getState;
            this.runCommand = runCommand;
            this.getPin = getPin;
        }

        public bool Start(int port) {
            Stop();
            try {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                listener = l;
                cts = new CancellationTokenSource();
                LastError = null;
                var token = cts.Token;
                Task.Run(() => AcceptLoop(l, token));
                return true;
            } catch (Exception ex) {
                LastError = ex.Message;
                listener = null;
                return false;
            }
        }

        public void Stop() {
            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            listener = null;
            cts = null;
        }

        private async Task AcceptLoop(TcpListener l, CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); } catch { break; }
                _ = Task.Run(() => Handle(c));
            }
        }

        private static int HeaderEnd(byte[] b, int len) {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        private async Task Handle(TcpClient c) {
            using (c) {
                try {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8))) {
                        var s = c.GetStream();
                        var buf = new byte[16384];
                        int len = 0, hdrEnd = -1;
                        while (hdrEnd < 0 && len < buf.Length) {
                            int n = await s.ReadAsync(buf, len, buf.Length - len, timeout.Token);
                            if (n <= 0) return;
                            len += n;
                            hdrEnd = HeaderEnd(buf, len);
                        }
                        if (hdrEnd < 0) return;

                        string head = Encoding.ASCII.GetString(buf, 0, hdrEnd);
                        string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        string[] first = lines[0].Split(' ');
                        string method = first[0];
                        string path = first.Length > 1 ? first[1] : "/";
                        int q = path.IndexOf('?');
                        if (q >= 0) path = path.Substring(0, q);

                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 1; i < lines.Length; i++) {
                            int k = lines[i].IndexOf(':');
                            if (k > 0) headers[lines[i].Substring(0, k).Trim()] = lines[i].Substring(k + 1).Trim();
                        }

                        string body = "";
                        if (method == "POST") {
                            int contentLength = 0;
                            if (headers.TryGetValue("Content-Length", out var cl)) int.TryParse(cl, out contentLength);
                            if (contentLength < 0 || contentLength > 2048) { await Send(s, 413, "text/plain", "too large", timeout.Token); return; }
                            var bodyBytes = new byte[contentLength];
                            int have = Math.Min(contentLength, len - (hdrEnd + 4));
                            if (have > 0) Array.Copy(buf, hdrEnd + 4, bodyBytes, 0, have);
                            while (have < contentLength) {
                                int n = await s.ReadAsync(bodyBytes, have, contentLength - have, timeout.Token);
                                if (n <= 0) return;
                                have += n;
                            }
                            body = Encoding.UTF8.GetString(bodyBytes);
                        }

                        if (method == "GET" && (path == "/" || path == "/index.html")) {
                            await Send(s, 200, "text/html; charset=utf-8", RemotePage.Html, timeout.Token);
                            return;
                        }
                        if (path == "/favicon.ico") { await Send(s, 204, "text/plain", "", timeout.Token); return; }

                        if (path == "/api/state" || path == "/api/cmd") {
                            string pin = getPin();
                            headers.TryGetValue("X-Pin", out var given);
                            if (!string.IsNullOrEmpty(pin) && !string.Equals(pin, given ?? "", StringComparison.Ordinal)) {
                                await Send(s, 401, "text/plain", "pin", timeout.Token);
                                return;
                            }
                            if (path == "/api/state" && method == "GET") {
                                await Send(s, 200, "application/json", getState(), timeout.Token);
                                return;
                            }
                            if (path == "/api/cmd" && method == "POST") {
                                int bar = body.IndexOf('|');
                                string name = bar >= 0 ? body.Substring(0, bar) : body;
                                string arg = bar >= 0 ? body.Substring(bar + 1) : "";
                                await Send(s, 200, "text/plain", runCommand(name.Trim(), arg.Trim()), timeout.Token);
                                return;
                            }
                        }
                        await Send(s, 404, "text/plain", "not found", timeout.Token);
                    }
                } catch { /* a dropped phone connection must never affect NINA */ }
            }
        }

        private static async Task Send(NetworkStream s, int code, string type, string body, CancellationToken ct) {
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            string reason = code == 200 ? "OK" : code == 204 ? "No Content" : code == 401 ? "Unauthorized" : code == 404 ? "Not Found" : "Error";
            string head = "HTTP/1.1 " + code + " " + reason + "\r\n" +
                          "Content-Type: " + type + "\r\n" +
                          "Content-Length: " + data.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length, ct);
            if (data.Length > 0) await s.WriteAsync(data, 0, data.Length, ct);
            await s.FlushAsync(ct);
        }
    }
}
