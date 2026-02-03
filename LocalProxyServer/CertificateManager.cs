using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LocalProxyServer
{
    public class CertificateManager
    {
        private const string CaName = "LocalProxyServer-CA";
        private const string ServerName = "localhost";
        private const string CrlDistributionPointsOid = "2.5.29.31";

        private static bool HasCrlDistributionPoints(X509Certificate2 cert)
        {
            return cert.Extensions[CrlDistributionPointsOid] != null;
        }

        private static bool IsSignedBy(X509Certificate2 cert, X509Certificate2 issuer)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.CustomTrustStore.Add(issuer);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        }

        /// <summary>
        /// Gets or creates server certificate. When crlDistributionUrl is not empty, the certificate will include a CRL distribution point,
        /// for Windows Schannel to complete revocation checking and avoid CRYPT_E_NO_REVOCATION_CHECK errors.
        /// </summary>
        public static X509Certificate2 GetOrCreateServerCertificate(string? crlDistributionUrl = null)
        {
            var rootCa = GetOrCreateRootCa(crlDistributionUrl);
            return GetOrCreateServerCert(rootCa, crlDistributionUrl);
        }

        /// <summary>
        /// Gets the installed Root CA from CurrentUser\My store (for issuing CRLs).
        /// </summary>
        public static X509Certificate2? GetRootCa()
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var existing = store.Certificates.Find(X509FindType.FindBySubjectName, CaName, false);
            return existing.Count > 0 ? existing[0] : null;
        }

        /// <summary>
        /// Uses Root CA to issue an empty CRL (DER format) for the CRL distribution endpoint to return.
        /// </summary>
        public static byte[] BuildEmptyCrl(X509Certificate2 rootCa)
        {
            if (!rootCa.HasPrivateKey)
                throw new InvalidOperationException("Root CA must have private key to sign CRL.");
            var builder = new CertificateRevocationListBuilder();
            return builder.Build(
                rootCa,
                System.Numerics.BigInteger.One,
                DateTimeOffset.UtcNow.AddDays(7),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1,
                null);
        }

        private static X509Certificate2 GetOrCreateRootCa(string? crlDistributionUrl)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var existing = store.Certificates.Find(X509FindType.FindBySubjectName, CaName, false);
            if (existing.Count > 0)
            {
                var current = existing[0];
                // CRL is enabled but existing CA has no CRL distribution point → Remove old cert and regenerate with CRL DP
                if (!string.IsNullOrEmpty(crlDistributionUrl) && !HasCrlDistributionPoints(current))
                {
                    Console.WriteLine("Replacing existing CA with a new one that includes CRL distribution point (for Windows revocation check).");
                    store.Remove(current);
                    TryRemoveFromTrustedRoot(current);
                }
                else
                {
                    return current;
                }
            }

            // Create Root CA
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={CaName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, false, 0, true));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                    false));

            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            if (!string.IsNullOrEmpty(crlDistributionUrl))
            {
                request.CertificateExtensions.Add(
                    CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(
                        new[] { crlDistributionUrl }, critical: false));
            }

            var cert = request.CreateSelfSigned(
                DateTimeOffset.Now.AddDays(-1),
                DateTimeOffset.Now.AddYears(10));

            // Mark as exportable and persistent
            var pfx = cert.Export(X509ContentType.Pfx);
            var finalCert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

            store.Add(finalCert);
            InstallToTrustedRoot(finalCert);

            return finalCert;
        }

        private static void TryRemoveFromTrustedRoot(X509Certificate2 cert)
        {
            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                    foreach (var c in found) store.Remove(c);
                }
            }
            catch { /* May require administrator privileges */ }
            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                    foreach (var c in found) store.Remove(c);
                }
            }
            catch { }
        }

        private static void InstallToTrustedRoot(X509Certificate2 cert)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                var existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                if (existing.Count == 0)
                {
                    store.Add(cert);
                    Console.WriteLine("Root CA installed to Trusted Root Certification Authorities.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not install Root CA to LocalMachine store. Try running as administrator. Error: {ex.Message}");
                // Try CurrentUser as fallback (some apps might respect it)
                try
                {
                    using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(cert);
                    Console.WriteLine("Root CA installed to CurrentUser Trusted Root store.");
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"Warning: Could not install Root CA to CurrentUser store either: {ex2.Message}");
                }
            }
        }

        private static X509Certificate2 GetOrCreateServerCert(X509Certificate2 rootCa, string? crlDistributionUrl)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var existing = store.Certificates.Find(X509FindType.FindBySubjectName, ServerName, false);
            foreach (var c in existing)
            {
                if (c.Issuer != rootCa.Subject || !IsSignedBy(c, rootCa)) continue;
                // CRL is enabled but existing server cert has no CRL distribution point → Remove and regenerate
                if (!string.IsNullOrEmpty(crlDistributionUrl) && !HasCrlDistributionPoints(c))
                {
                    store.Remove(c);
                    break;
                }
                return c;
            }

            // Create Server Cert
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={ServerName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            if (!string.IsNullOrEmpty(crlDistributionUrl))
            {
                request.CertificateExtensions.Add(
                    CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(
                        new[] { crlDistributionUrl }, critical: false));
            }

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Auth
                    false));

            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            request.CertificateExtensions.Add(
                X509AuthorityKeyIdentifierExtension.CreateFromCertificate(rootCa, true, false));

            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddDnsName(ServerName);
            subjectAlternativeName.AddDnsName("127.0.0.1");
            request.CertificateExtensions.Add(subjectAlternativeName.Build());

            // Create serial number
            byte[] serialNumber = new byte[8];
            RandomNumberGenerator.Fill(serialNumber);

            var cert = request.Create(
                rootCa,
                DateTimeOffset.Now.AddDays(-1),
                DateTimeOffset.Now.AddYears(5),
                serialNumber);

            // Copy with private key
            var finalCert = cert.CopyWithPrivateKey(rsa);
            
            // Export and import to make it persistent and usable by SslStream
            var pfx = finalCert.Export(X509ContentType.Pfx);
            var persistentCert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

            store.Add(persistentCert);
            return persistentCert;
        }
    }
}
