using System.Net.Sockets;
using Npgsql;
using PgMonitor.Core.Model;

namespace PgMonitor.Infrastructure.Postgres;

/// <summary>Thrown when the server does not confirm default_transaction_read_only = on. Monitoring stops.</summary>
public sealed class ReadOnlyNotEnforcedException : Exception
{
    public ReadOnlyNotEnforcedException()
        : base("The server did not confirm default_transaction_read_only = on; refusing to monitor.")
    {
    }

    public ReadOnlyNotEnforcedException(string message)
        : base(message)
    {
    }

    public ReadOnlyNotEnforcedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Maps driver exceptions to a small set of kinds, with a description that is safe to log and show:
/// it never contains the exception message (which may carry host names, user names or SQL).
/// </summary>
public static class PgErrorMapper
{
    public static (CollectFailureKind Kind, string SafeDescription) Map(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        switch (ex)
        {
            case ReadOnlyNotEnforcedException:
                return (CollectFailureKind.PermissionDenied, "read-only mode not confirmed by server");
            case PostgresException pg:
                return (KindOf(pg.SqlState), $"PostgreSQL error SQLSTATE {pg.SqlState}");
            case TimeoutException:
                return (CollectFailureKind.Timeout, "timeout");
            case NpgsqlException { InnerException: TimeoutException }:
                return (CollectFailureKind.Timeout, "timeout");
            case NpgsqlException { InnerException: SocketException se }:
                return (CollectFailureKind.Disconnected, $"network error ({se.SocketErrorCode})");
            case NpgsqlException:
                return (CollectFailureKind.Disconnected, "connection error");
            case SocketException se:
                return (CollectFailureKind.Disconnected, $"network error ({se.SocketErrorCode})");
            default:
                return (CollectFailureKind.Unknown, ex.GetType().Name);
        }
    }

    internal static CollectFailureKind KindOf(string sqlState) => sqlState switch
    {
        "57014" => CollectFailureKind.Timeout,                     // query_canceled (statement_timeout)
        "28P01" or "28000" => CollectFailureKind.AuthFailed,       // invalid_password, invalid_authorization_specification
        "42501" => CollectFailureKind.PermissionDenied,            // insufficient_privilege
        "57P01" or "57P02" or "57P03" => CollectFailureKind.Disconnected, // admin/crash shutdown, cannot_connect_now
        _ when sqlState.StartsWith("08", StringComparison.Ordinal) => CollectFailureKind.Disconnected, // connection_exception
        "53300" => CollectFailureKind.Disconnected,                // too_many_connections
        _ => CollectFailureKind.Unknown,
    };
}
