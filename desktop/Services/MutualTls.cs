using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = Org.BouncyCastle.X509.X509Certificate;
using BcCertificateRequest = Org.BouncyCastle.Tls.CertificateRequest;

namespace Tunor.Desktop.Services;

/// <summary>
/// Mutual TLS to the privileged service, done in managed code and without the keychain.
///
/// .NET on macOS can hand a client certificate to the network stack only as an identity,
/// and an identity lives in a keychain. Whatever the certificate was loaded from — a PFX,
/// a PEM, memory — a key that has to sign a handshake is first put into a temporary
/// keychain with a random name and a password nobody is ever told. Every request made a
/// new one, and whenever macOS decided the app was not entitled to its own key it asked
/// for that password in a dialog. There is nothing correct to type into it.
///
/// So the handshake is done here instead, by BouncyCastle: the key is read from the PEM
/// into memory, signs one message, and no keychain is involved because none is asked.
/// The service is the same one, the protocol is the same TLS 1.3, and the trust is the
/// same as before — the server certificate must hash to the fingerprint from the invite.
/// </summary>
internal static class MutualTls
{
    /// <summary>
    /// How long one read or write on the connection may stall. A handshake that never
    /// finishes would otherwise hold a thread for as long as the service stays silent.
    /// </summary>
    private const int StallMs = 20_000;

    /// <summary>This app's certificate and the key that proves it.</summary>
    internal sealed record Identity(BcCertificate Certificate, AsymmetricKeyParameter Key)
    {
        /// <summary>Certificate, then key, as PEM — the layout the app has always stored.</summary>
        public string ToPem() =>
            PemEncoding.WriteString("CERTIFICATE", Certificate.GetEncoded()) + "\n"
            + PemEncoding.WriteString("PRIVATE KEY",
                PrivateKeyInfoFactory.CreatePrivateKeyInfo(Key).GetEncoded()) + "\n";
    }

    // ---------------------------------------------------------------- reading keys

    /// <summary>A certificate and its private key from one PEM file.</summary>
    public static Identity ReadPem(string path)
    {
        using var reader = new PemReader(new StringReader(File.ReadAllText(path)));
        BcCertificate? cert = null;
        AsymmetricKeyParameter? key = null;
        object? item;
        while ((item = reader.ReadObject()) != null)
        {
            if (item is BcCertificate c) cert = c;
            else if (item is AsymmetricKeyParameter { IsPrivate: true } k) key = k;
            else if (item is AsymmetricCipherKeyPair pair) key = pair.Private;
        }
        return cert != null && key != null
            ? new Identity(cert, key)
            : throw new InvalidDataException("в файле нет сертификата или закрытого ключа");
    }

    /// <summary>
    /// The old storage format, read without loading it into a keychain — which is the
    /// very thing loading it through .NET would do. Null if it cannot be read.
    /// </summary>
    public static Identity? ReadPfx(byte[] pfx)
    {
        // The app exported it without a password; depending on who wrote the file that
        // is either an empty one or none at all, so both are tried.
        foreach (var password in new[] { Array.Empty<char>(), null })
        {
            try
            {
                var store = new Pkcs12StoreBuilder().Build();
                store.Load(new MemoryStream(pfx), password);
                var alias = store.Aliases.FirstOrDefault(store.IsKeyEntry);
                if (alias == null) continue;
                return new Identity(store.GetCertificate(alias).Certificate, store.GetKey(alias).Key);
            }
            catch { }
        }
        return null;
    }

    // ---------------------------------------------------------------- the client

    /// <summary>
    /// The address a request to the service is sent to. The scheme is http on purpose:
    /// the TLS is done underneath, in <see cref="Client"/>, and a request that said
    /// https would be wrapped in a second layer by the handler.
    /// </summary>
    public static string Url(string address, string route) => $"http://{address}/admin/{route}";

    /// <summary>
    /// A client that talks to exactly one server — the one whose certificate hashes to
    /// <paramref name="fingerprint"/> — and shows it <paramref name="identity"/>, if
    /// there is one. The daemon signs its own certificate, so the usual chain of
    /// authorities says nothing about it; the fingerprint is the whole trust, and a
    /// server that does not match is refused before a byte of the request is sent.
    ///
    /// For one request. A TLS stream that blocks cannot be watched for the server hanging
    /// up the way a pooled connection is, so a second request through the same client
    /// can find the connection already gone — measured, not guessed: 200 requests with a
    /// client each all succeed, and a shared one fails on reuse. The caller makes a new
    /// client per request and disposes it, which is also what closes the connection.
    /// </summary>
    public static HttpClient Client(string fingerprint, Identity? identity)
    {
        var pin = fingerprint.Replace(":", "");
        var handler = new SocketsHttpHandler
        {
            // The service is local. And a proxy would be handed the plain side of the
            // request, since TLS is not done by the handler: it would never see the
            // service at all.
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = (context, cancel) => new ValueTask<Stream>(
                Task.Run<Stream>(() => Connect(context.DnsEndPoint, pin, identity), cancel)),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static Stream Connect(DnsEndPoint endpoint, string pin, Identity? identity)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = StallMs,
            SendTimeout = StallMs,
        };
        try
        {
            socket.Connect(endpoint);
            var protocol = new TlsClientProtocol(new NetworkStream(socket, ownsSocket: true));
            protocol.Connect(new Client_(new BcTlsCrypto(), pin, identity));
            return protocol.Stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Client_ : DefaultTlsClient
    {
        private readonly string _pin;
        private readonly Identity? _identity;

        public Client_(TlsCrypto crypto, string pin, Identity? identity) : base(crypto)
        {
            _pin = pin;
            _identity = identity;
        }

        protected override ProtocolVersion[] GetSupportedVersions() =>
            ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

        public override TlsAuthentication GetAuthentication() => new Authentication(this);

        private sealed class Authentication : TlsAuthentication
        {
            private readonly Client_ _owner;

            public Authentication(Client_ owner) => _owner = owner;

            /// <summary>The whole of the trust: the server is the one named in the invite.</summary>
            public void NotifyServerCertificate(TlsServerCertificate server)
            {
                var der = server.Certificate.GetCertificateAt(0).GetEncoded();
                if (!Convert.ToHexString(SHA256.HashData(der))
                        .Equals(_owner._pin, StringComparison.OrdinalIgnoreCase))
                    throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            public TlsCredentials? GetClientCredentials(BcCertificateRequest request)
            {
                if (_owner._identity is not { } me) return null;

                var context = _owner.m_context;
                var crypto = (BcTlsCrypto)context.Crypto;
                // TLS 1.3 numbers every certificate message with the request it answers.
                var chain = new Certificate(request.GetCertificateRequestContext(),
                    new[] { new CertificateEntry(new BcTlsCertificate(crypto, me.Certificate.GetEncoded()), null) });
                return new BcDefaultTlsCredentialedSigner(
                    new TlsCryptoParameters(context), crypto, me.Key, chain, Scheme(request));
            }

            /// <summary>
            /// How to sign: RSA-PSS if the server takes it — TLS 1.3 allows nothing
            /// else for an RSA key — and the older PKCS#1 form if that is all it offers.
            /// </summary>
            private static SignatureAndHashAlgorithm Scheme(BcCertificateRequest request)
            {
                var offered = request.SupportedSignatureAlgorithms;
                if (offered != null)
                {
                    foreach (var scheme in new[]
                    {
                        SignatureScheme.rsa_pss_rsae_sha256,
                        SignatureScheme.rsa_pss_rsae_sha384,
                        SignatureScheme.rsa_pss_pss_sha256,
                    })
                    {
                        var candidate = SignatureScheme.GetSignatureAndHashAlgorithm(scheme);
                        if (offered.Any(o => o.Equals(candidate))) return candidate;
                    }
                    var plain = offered.FirstOrDefault(o => o.Signature == SignatureAlgorithm.rsa);
                    if (plain != null) return plain;
                }
                return new SignatureAndHashAlgorithm(Org.BouncyCastle.Tls.HashAlgorithm.sha256,
                                                     SignatureAlgorithm.rsa);
            }
        }
    }
}
