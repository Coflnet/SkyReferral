using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Core.Security;
using Coflnet.Sky.Referral.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using NUnit.Framework;

namespace Coflnet.Sky.Referral.Infrastructure;

public class DatabaseTlsTests
{
    [TestCase(0u, 300u)]
    [TestCase(40u, 40u)]
    [TestCase(900u, 300u)]
    public void ExplicitTlsPreservesDatabaseAuthenticationAndBoundsNewPhysicalConnections(uint lifetime, uint expected)
    {
        var configuration = Configuration(new()
        {
            ["DB_CONNECTION"] = $"Server=db-a.internal,db-b.internal;Port=3307;User ID=referral;Password=synthetic-only;Database=referral;MaximumPoolSize=23;ConnectionTimeout=90;ConnectionLifeTime={lifetime};SslMode=None;CertificateFile=old.pfx;CertificatePassword=old-pfx-only;CertificateStoreLocation=CurrentUser;CertificateThumbprint=old;SslCa=old-ca.pem;SslCert=old.pem;SslKey=old.key",
            ["DB:TLS:CA_CERT_PATH"] = "roots.pem",
            ["DB:TLS:CERT_PATH"] = "client.pem",
            ["DB:TLS:KEY_PATH"] = "client.key"
        });
        var options = new MySqlConnectionStringBuilder(DatabaseTls.ConnectionString(configuration));
        Assert.Multiple(() =>
        {
            Assert.That(options.Server, Is.EqualTo("db-a.internal,db-b.internal"));
            Assert.That(options.Port, Is.EqualTo(3307));
            Assert.That(options.UserID, Is.EqualTo("referral"));
            Assert.That(options.Password, Is.EqualTo("synthetic-only"));
            Assert.That(options.Database, Is.EqualTo("referral"));
            Assert.That(options.MaximumPoolSize, Is.EqualTo(23));
            Assert.That(options.ConnectionTimeout, Is.EqualTo(10));
            Assert.That(options.ConnectionLifeTime, Is.EqualTo(expected));
            Assert.That(options.SslMode, Is.EqualTo(MySqlSslMode.Required));
            Assert.That(options.SslCa, Is.Empty);
            Assert.That(options.SslCert, Is.Empty);
            Assert.That(options.SslKey, Is.Empty);
            Assert.That(options.CertificateFile, Is.Empty);
            Assert.That(options.CertificatePassword, Is.Empty);
            Assert.That(options.CertificateStoreLocation, Is.EqualTo(MySqlCertificateStoreLocation.None));
            Assert.That(options.CertificateThumbprint, Is.Empty);
        });
    }

    [Test]
    public void OptOutPreservesLegacyTlsAndPartialConfigurationFailsClosed()
    {
        var options = new MySqlConnectionStringBuilder(DatabaseTls.ConnectionString(Configuration(new())));
        Assert.That(options.SslMode, Is.EqualTo(MySqlSslMode.None));
        Assert.That(options.ConnectionLifeTime, Is.Zero);
        foreach (var missing in new[] { "CA_CERT_PATH", "CERT_PATH", "KEY_PATH" })
        {
            var values = new Dictionary<string, string>
            {
                ["DB:TLS:CA_CERT_PATH"] = "roots.pem", ["DB:TLS:CERT_PATH"] = "client.pem",
                ["DB:TLS:KEY_PATH"] = "client.key"
            };
            values.Remove("DB:TLS:" + missing);
            Assert.Throws<InvalidOperationException>(() => DatabaseTls.ConnectionString(Configuration(values)));
        }
    }

    [Test]
    public async Task LogicalOpensDoNoPkiIoAndPhysicalCertificateProviderFailsClosed()
    {
        using var owner = new PemTlsOptions("missing-roots", "missing-cert", "missing-key");
        var interceptor = new DatabaseTlsInterceptor(owner);
        using var connection = new MySqlConnection();
        Assert.DoesNotThrow(() => interceptor.ConnectionOpening(connection, null, default));
        Assert.That(connection.RemoteCertificateValidationCallback, Is.Not.Null);
        Assert.That(connection.ProvideClientCertificatesCallback, Is.Not.Null);
        Assert.Throws<FileNotFoundException>(() => connection.ProvideClientCertificatesCallback(new()));
        connection.RemoteCertificateValidationCallback = null;
        connection.ProvideClientCertificatesCallback = null;
        await interceptor.ConnectionOpeningAsync(connection, null, default);
        Assert.That(connection.RemoteCertificateValidationCallback, Is.Not.Null);
        Assert.Throws<FileNotFoundException>(() => connection.ProvideClientCertificatesCallback(new()));
    }

    [Test]
    public void FailedRequiredDatabaseCheckIsBoundedAndDoesNotExposeConnectionDetails()
    {
        using var database = new ReferralDbContext(new DbContextOptionsBuilder<ReferralDbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=referral;Password=synthetic-private;ConnectionTimeout=1",
                new MariaDbServerVersion(new Version(10, 11))).Options);
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DatabaseReadiness.CheckAsync(database, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.That(failure.Message, Is.EqualTo("The required database readiness check failed."));
        Assert.That(failure.InnerException, Is.Null);
    }

    private static IConfiguration Configuration(Dictionary<string, string> values)
    {
        values.TryAdd("DB_CONNECTION", "Server=legacy;User ID=referral;Password=synthetic-only;SslMode=None");
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
