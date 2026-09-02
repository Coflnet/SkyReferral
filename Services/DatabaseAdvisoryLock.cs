using System;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Referral.Services;

internal sealed class DatabaseAdvisoryLock : IAsyncDisposable
{
    private readonly DbConnection connection;
    private readonly string name;
    private readonly bool closeConnection;

    private DatabaseAdvisoryLock(
        DbConnection connection = null,
        string name = null,
        bool closeConnection = false)
    {
        this.connection = connection;
        this.name = name;
        this.closeConnection = closeConnection;
    }

    public static async Task<DatabaseAdvisoryLock> Acquire(
        DbContext context,
        string resource)
    {
        if (!context.Database.IsMySql())
            return new();
        var connection = context.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
            await connection.OpenAsync();
        var name = "skyref:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(resource))).ToLowerInvariant()[..56];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name, 30)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1)
        {
            if (close)
                await connection.CloseAsync();
            throw new RewardProgramException(
                "The reward account is busy; retry the operation");
        }
        return new(connection, name, close);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection == null)
            return;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK(@name)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = name;
            command.Parameters.Add(parameter);
            await command.ExecuteScalarAsync();
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }
}
