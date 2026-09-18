using System;
using System.Net.Http;

namespace PenguinMonitor.Services
{
    /// <summary>
    /// Central HttpClient factory. Was IPv4-only for v38.19–38.22 to work around a
    /// broken AAAA record on wildwatch.co.nz (fixed at Porkbun, Jul 2026); back to
    /// default dual-stack networking.
    /// </summary>
    internal static class Http
    {
        internal static HttpClient CreateClient(TimeSpan timeout)
        {
            // Don't reuse a socket that has been sitting idle. The phone polls every 15 seconds on a
            // radio that sleeps between them, and the far end (or a carrier NAT) drops the keep-alive
            // without telling anyone — the next request onto that dead socket fails with "software
            // caused connection abort". Retiring idle connections early costs one handshake and
            // removes a failure that only ever looked like a flaky sync.
            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            var client = new HttpClient(handler) { Timeout = timeout };
            // Every client comes from here, so every request says it speaks full peng numbers
            // ("PT1039"). The API used to strip the viewing colony's prefix and re-add it on the
            // way back in; one write path that forgot to re-add it broke biometric saves. It now
            // refuses (426) a client that doesn't declare this, rather than guess which it has.
            client.DefaultRequestHeaders.Add(PengFormatHeader, "full");
            return client;
        }

        internal const string PengFormatHeader = "X-Peng-Format";

        /// <summary>426: the server won't talk to a build this old. Its body says to update from the
        /// Play Store; nothing a retry or a queue can fix, so it is carried as its own kind of
        /// failure rather than read as a flaky network.</summary>
        internal static bool IsUpgradeRequired(HttpResponseMessage resp) => (int)resp.StatusCode == 426;

        /// <summary>True for the failures that mean "the network moved", not "the request was
        /// wrong": a dropped socket, a reset, a name that didn't resolve while the radio was
        /// changing hands. Worth one quiet retry; not worth telling anyone about.</summary>
        internal static bool IsTransient(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is System.Net.Sockets.SocketException
                    || e is System.IO.IOException
                    || e is HttpRequestException
                    || e is TaskCanceledException)
                    return true;
            return false;
        }
    }

    /// <summary>The server refused this build (HTTP 426). Message is the server's own words.</summary>
    internal sealed class UpgradeRequiredException : Exception
    {
        internal UpgradeRequiredException(string message) : base(message) { }
    }
}
