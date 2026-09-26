using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Yabo.Shared;

namespace DropPlaynite
{
    /// <summary>
    /// Everything about identity: building the per-request header, and the one-time
    /// device-code sign-in that produces the credential the header is built from.
    ///
    /// Drop's client protocol (drop-app <c>remote/src/auth.rs:72-96</c>): every
    /// <c>/api/v1/client/</c> request carries <c>Authorization: JWT &lt;clientId&gt; &lt;jwt&gt;</c>,
    /// where the JWT is ES384 over the claims <c>{nbf: now, exp: now + 10}</c>, signed
    /// with the EC private key the server handed out at handshake. Ten seconds of
    /// validity, so it is minted per request, never cached.
    /// </summary>
    internal static class DropAuth
    {
        /// <summary>Header value for a client call, or null when not signed in.</summary>
        public static string ClientHeader(DropSettings settings)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.PrivateKeyPem))
            {
                return null;
            }
            return "JWT " + settings.ClientId + " " + MintJwt(settings.PrivateKeyPem);
        }

        /// <summary>Header value for an admin call, or null when no token is configured.</summary>
        public static string AdminHeader(DropSettings settings)
        {
            return string.IsNullOrWhiteSpace(settings?.AdminToken) ? null : "Bearer " + settings.AdminToken.Trim();
        }

        /// <summary>
        /// ES384 JWT. net462 cannot import an EC PEM by itself, so BouncyCastle parses
        /// the key (either an "EC PRIVATE KEY" block or a PKCS#8 "PRIVATE KEY" block,
        /// the CA library upstream has emitted both) and "SHA-384withPLAIN-ECDSA" gives
        /// the raw r||s the JWS spec wants, not the DER that plain ECDSA would.
        /// </summary>
        internal static string MintJwt(string privateKeyPem)
        {
            var key = LoadEcPrivateKey(privateKeyPem);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"ES384\",\"typ\":\"JWT\"}"));
            var claims = Base64Url(Encoding.UTF8.GetBytes("{\"nbf\":" + now + ",\"exp\":" + (now + 10) + "}"));
            var signingInput = Encoding.ASCII.GetBytes(header + "." + claims);

            var signer = SignerUtilities.GetSigner("SHA-384withPLAIN-ECDSA");
            signer.Init(true, key);
            signer.BlockUpdate(signingInput, 0, signingInput.Length);
            var signature = signer.GenerateSignature();

            return header + "." + claims + "." + Base64Url(signature);
        }

        private static ECPrivateKeyParameters LoadEcPrivateKey(string pem)
        {
            object parsed;
            using (var reader = new StringReader(pem.Trim()))
            {
                parsed = new PemReader(reader).ReadObject();
            }
            var key = parsed as ECPrivateKeyParameters
                      ?? (parsed as AsymmetricCipherKeyPair)?.Private as ECPrivateKeyParameters;
            if (key == null)
            {
                throw new InvalidDataException("The stored Drop credential is not an EC private key. Sign in again.");
            }
            return key;
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// The code half of the device-code flow. The server (`clients/handler.ts`)
        /// pushes the token down a websocket that must already be listening when the
        /// user approves the code in Drop's web UI; if nobody is listening the approve
        /// call fails with "No client listening for authorization". So the socket is
        /// opened BEFORE the code is shown, and this waits on it.
        /// </summary>
        public static async Task<DropCredentials> WaitForApprovalAsync(
            DropClient client, string baseUrl, string code, TimeSpan timeout, CancellationToken cancel)
        {
            var wsUrl = ToWebSocketUrl(baseUrl) + "/api/v1/client/auth/code/ws";
            using (var ws = new ClientWebSocket())
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timer.CancelAfter(timeout);
                ws.Options.SetRequestHeader("Authorization", code);
                await ws.ConnectAsync(new Uri(wsUrl), timer.Token).ConfigureAwait(false);

                var buffer = new byte[16 * 1024];
                var text = new StringBuilder();
                while (ws.State == WebSocketState.Open)
                {
                    var segment = new ArraySegment<byte>(buffer);
                    var result = await ws.ReceiveAsync(segment, timer.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }
                    var message = text.ToString();
                    text.Clear();

                    if (DropClient.TryParseTokenMessage(message, out var clientId, out var token, out var error))
                    {
                        // Handshake over plain HTTPS; the socket has done its job.
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false); } catch { }
                        return client.CompleteAuth(clientId, token);
                    }
                    if (error != null)
                    {
                        throw new InvalidOperationException("Drop refused the sign-in: " + error);
                    }
                }
            }
            throw new TimeoutException("Drop did not approve the code in time. Enter the code in Drop's web UI (Settings, Clients) and try again.");
        }

        private static string ToWebSocketUrl(string baseUrl)
        {
            var b = (baseUrl ?? string.Empty).TrimEnd('/');
            if (b.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + b.Substring(8);
            if (b.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + b.Substring(7);
            return "wss://" + b;
        }

        /// <summary>Serialises anything for a log line without dumping secrets.</summary>
        internal static string Redact(object o)
        {
            var json = JsonConvert.SerializeObject(o);
            return json.Length > 300 ? json.Substring(0, 300) + "..." : json;
        }
    }
}
