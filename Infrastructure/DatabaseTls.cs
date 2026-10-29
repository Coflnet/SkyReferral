using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Core.Security;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Coflnet.Sky.Referral.Infrastructure;

internal static class DatabaseTls
{
    internal static string ConnectionString(IConfiguration configuration)
    {
        var options = new MySqlConnectionStringBuilder(configuration["DB_CONNECTION"]
            ?? throw new InvalidOperationException("DB_CONNECTION must be configured."));
        options.ConnectionTimeout = options.ConnectionTimeout == 0 ? 10 : Math.Min(options.ConnectionTimeout, 10);
        var section = configuration.GetSection("DB:TLS");
        if (section.Exists())
        {
            foreach (var key in new[] { "CA_CERT_PATH", "CERT_PATH", "KEY_PATH" })
                if (string.IsNullOrWhiteSpace(section[key]))
                    throw new InvalidOperationException($"DB:TLS:{key} must be configured.");
            options.ConnectionLifeTime = options.ConnectionLifeTime == 0 ? 300 : Math.Min(options.ConnectionLifeTime, 300);
            // MySqlConnector 2.6.2 can soften VerifyFull failures with MariaDB password-bound TLS.
            // Required + the unconditional strict callback rejects CA/name errors before authentication.
            options.SslMode = MySqlSslMode.Required;
            options.SslCa = options.SslCert = options.SslKey = "";
            // Native PEM/PFX/store identities override the physical-connection provider.
            options.CertificateFile = options.CertificatePassword = options.CertificateThumbprint = "";
            options.CertificateStoreLocation = MySqlCertificateStoreLocation.None;
        }
        return options.ConnectionString;
    }
}

internal sealed class DatabaseTlsInterceptor(PemTlsOptions credentials) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Prepare(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Prepare(connection);
        return new(result);
    }

    private void Prepare(DbConnection connection)
    {
        var mysql = (MySqlConnection)connection;
        mysql.RemoteCertificateValidationCallback = credentials.ValidateServerCertificate;
        mysql.ProvideClientCertificatesCallback = certificates =>
        {
            credentials.RegisterClientIntermediates();
            certificates.Add(credentials.GetCertificate());
            return ValueTask.CompletedTask;
        };
    }
}
