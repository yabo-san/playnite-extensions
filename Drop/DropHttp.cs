using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace DropPlaynite
{
    /// <summary>
    /// The one place that talks HTTP. HttpWebRequest, because that is the API net462
    /// actually has (HttpClient's modern shape is not there); TLS 1.2 forced the same
    /// way <c>Shared/ArchiveOrgClient.cs</c> does, since Drop instances behind a
    /// tunnel refuse anything older and the failure otherwise reads as a dropped
    /// connection.
    ///
    /// <c>auth</c> is the full header value, or null: <c>JWT &lt;id&gt; &lt;jwt&gt;</c> for
    /// client calls, <c>Bearer &lt;token&gt;</c> for admin calls. This class never builds
    /// one; <see cref="DropAuth"/> does.
    /// </summary>
    internal static class DropHttp
    {
        private const string UserAgent = "playnite-drop";

        public static string Get(string baseUrl, string path, string auth, int timeoutMs = 60000)
        {
            var request = Create(Join(baseUrl, path), "GET", auth, timeoutMs);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        public static string Post(string baseUrl, string path, string auth, object body, int timeoutMs = 60000)
        {
            var request = Create(Join(baseUrl, path), "POST", auth, timeoutMs);
            request.ContentType = "application/json";
            var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body ?? new { }));
            request.ContentLength = payload.Length;
            using (var s = request.GetRequestStream())
            {
                s.Write(payload, 0, payload.Length);
            }
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Opens a streaming GET; the caller owns the response. Used for chunk bodies,
        /// which are hundreds of megabytes and must never be buffered as a string.
        /// </summary>
        public static HttpWebResponse OpenStream(string absoluteUrl, string auth, int timeoutMs = 120000)
        {
            var request = Create(absoluteUrl, "GET", auth, timeoutMs);
            request.ReadWriteTimeout = timeoutMs;
            return (HttpWebResponse)request.GetResponse();
        }

        /// <summary>
        /// Reads the body of a failed request so the notification says what the server
        /// said ("Invalid token", "Not allowed to authorize this client") instead of
        /// "The remote server returned an error: (403) Forbidden".
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex is WebException web && web.Response is HttpWebResponse resp)
            {
                string body = null;
                try
                {
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                }
                catch { /* the status line is still useful */ }
                var detail = string.IsNullOrWhiteSpace(body) ? "" : ": " + (body.Length > 200 ? body.Substring(0, 200) : body);
                return "HTTP " + (int)resp.StatusCode + " " + resp.StatusDescription + detail;
            }
            return ex.Message;
        }

        private static HttpWebRequest Create(string url, string method, string auth, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = UserAgent;
            request.Timeout = timeoutMs;
            request.Accept = "application/json";
            if (!string.IsNullOrEmpty(auth))
            {
                request.Headers["Authorization"] = auth;
            }
            return request;
        }

        private static string Join(string baseUrl, string path)
        {
            return (baseUrl ?? string.Empty).TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        }
    }
}
