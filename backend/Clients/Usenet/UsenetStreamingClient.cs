using Microsoft.Extensions.Logging;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    public UsenetStreamingClient(ConfigManager configManager, WebsocketManager websocketManager, ILoggerFactory loggerFactory)
        : base(CreateDownloadingNntpClient(configManager, websocketManager, loggerFactory))
    {
        // when config changes, create a new MultiProviderClient to use instead.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            // update the connection-pool according to the new config
            var newUsenetClient = CreateDownloadingNntpClient(configManager, websocketManager, loggerFactory);
            ReplaceUnderlyingClient(newUsenetClient);
        };
    }

    private static DownloadingNntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ILoggerFactory loggerFactory
    )
    {
        var multiProviderClient = CreateMultiProviderClient(configManager, websocketManager);
        var logger = loggerFactory.CreateLogger<DownloadingNntpClient>();
        return new DownloadingNntpClient(multiProviderClient, configManager, logger);
    }

    private static MultiProviderNntpClient CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index)
            ))
            .ToList();
        return new MultiProviderNntpClient(providerClients);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            onConnectionPoolChanged
        );
        return new MultiConnectionNntpClient(connectionPool, connectionDetails.Type);
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = new ConnectionPool<INntpClient>(
            maxConnections,
            connectionFactory,
            connectionValidator: ValidateConnectionAsync
        );
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    /// <summary>
    /// Validates that a pooled NNTP connection is still alive by sending a DATE command.
    /// </summary>
    private static async ValueTask<bool> ValidateConnectionAsync(INntpClient client, CancellationToken ct)
    {
        try
        {
            await client.DateAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    )
    {
        var connection = new BaseNntpClient();
        var host = connectionDetails.Host;
        var port = connectionDetails.Port;
        var useSsl = connectionDetails.UseSsl;
        var user = connectionDetails.User;
        var pass = connectionDetails.Pass;
        await connection.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);
        await connection.AuthenticateAsync(user, pass, ct).ConfigureAwait(false);
        return connection;
    }
}