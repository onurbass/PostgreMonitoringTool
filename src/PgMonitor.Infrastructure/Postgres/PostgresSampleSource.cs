using System.Diagnostics;
using Npgsql;
using PgMonitor.Core.Model;

namespace PgMonitor.Infrastructure.Postgres;

/// <summary>Timing of the last successful sample, for the connection-check screen.</summary>
public sealed record QueryTimings(TimeSpan Identity, TimeSpan Activity);

/// <summary>
/// Live source: runs only <see cref="SqlCatalog"/> statements on one read-only connection.
/// Collection problems never throw; they come back as failed samples with a safe description in <see cref="LastError"/>.
/// </summary>
public sealed class PostgresSampleSource : ISampleSource, IAsyncDisposable
{
    private readonly PostgresConnectionSettings _settings;
    private readonly Func<CancellationToken, ValueTask<string?>> _password;
    private readonly TimeProvider _time;
    private NpgsqlDataSource? _dataSource;
    private NpgsqlConnection? _connection;

    /// <param name="settings">Host, port, user (no password).</param>
    /// <param name="password">Returns the password at connect time (Credential Manager or memory).</param>
    /// <param name="time">Used only to timestamp failed samples; successful samples use the server's now().</param>
    public PostgresSampleSource(PostgresConnectionSettings settings, Func<CancellationToken, ValueTask<string?>> password, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(password);
        _settings = settings;
        _password = password;
        _time = time ?? TimeProvider.System;
    }

    public SourceKind Kind => SourceKind.Live;

    public ServerCapabilities? Capabilities { get; private set; }

    public QueryTimings? LastTimings { get; private set; }

    /// <summary>Safe description of the last failure (no message text, no host).</summary>
    public string? LastError { get; private set; }

    public async Task<ClusterSample> CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            ServerIdentity identity;
            DateTimeOffset sampledAt;
            await using (var cmd = Command(connection, SqlCatalog.ServerIdentity))
            await using (var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                await r.ReadAsync(cancellationToken).ConfigureAwait(false);
                identity = PgRowMapper.MapIdentity(r, out sampledAt);
            }
            var identityTime = sw.Elapsed;

            sw.Restart();
            var sessions = new List<SessionRow>();
            await using (var cmd = Command(connection, SqlCatalog.Activity))
            await using (var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sessions.Add(PgRowMapper.MapSession(r));
                }
            }
            LastTimings = new QueryTimings(identityTime, sw.Elapsed);
            LastError = null;

            return new ClusterSample
            {
                SampledAt = sampledAt,
                Sessions = sessions,
                Visibility = PgRowMapper.Classify(sessions),
                Server = identity,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown, not a collection failure
        }
#pragma warning disable CA1031 // Any driver failure becomes a failed sample; the monitor loop must keep running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var (kind, description) = PgErrorMapper.Map(ex);
            LastError = description;
            await ResetConnectionAsync().ConfigureAwait(false);
            return ClusterSample.Failed(_time.GetUtcNow(), kind);
        }
    }

    /// <summary>Opens a connection if needed, then proves it is read-only and reads capabilities.</summary>
    private async Task<NpgsqlConnection> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is { State: System.Data.ConnectionState.Open })
        {
            return _connection;
        }
        await ResetConnectionAsync().ConfigureAwait(false);

        if (_dataSource is null)
        {
            var password = await _password(ct).ConfigureAwait(false);
            _dataSource = NpgsqlDataSource.Create(_settings.BuildConnectionString(password));
        }

        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            string readOnly;
            await using (var cmd = Command(connection, SqlCatalog.ReadOnlyCheck))
            {
                readOnly = Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
            if (!string.Equals(readOnly, "on", StringComparison.OrdinalIgnoreCase))
            {
                throw new ReadOnlyNotEnforcedException();
            }

            await using (var cmd = Command(connection, SqlCatalog.Capabilities))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                await r.ReadAsync(ct).ConfigureAwait(false);
                Capabilities = PgRowMapper.MapCapabilities(r, readOnly);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _connection = connection;
        return connection;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
#pragma warning disable CA2100 // Only constants from SqlCatalog are ever used here.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        return cmd;
    }

    private async Task ResetConnectionAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    /// <summary>Forget the cached password/data source (after the user changes credentials).</summary>
    public async Task ResetCredentialsAsync()
    {
        await ResetConnectionAsync().ConfigureAwait(false);
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
            _dataSource = null;
        }
    }

    public async ValueTask DisposeAsync() => await ResetCredentialsAsync().ConfigureAwait(false);
}
