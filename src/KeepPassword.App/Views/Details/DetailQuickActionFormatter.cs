using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace KeepPassword.App.Views.Details;

/// <summary>仅生成可复制文本，不读写保险库，也不执行连接。</summary>
public static class DetailQuickActionFormatter
{
    public static string? ServerCommand(string? protocol, string? host, string? port, string? username)
    {
        var command = protocol?.Trim().ToLowerInvariant();
        if (command is not ("ssh" or "sftp") || FormatHost(host) is not { } address || !TryPort(port, out var portNumber))
        {
            return null;
        }

        var target = string.IsNullOrEmpty(username) ? address : username + "@" + address;
        var option = command == "ssh" ? "-p" : "-P";
        // Windows 按 PowerShell、其他平台按 POSIX shell 转义；-- 截止客户端选项。
        return command + (portNumber is null ? "" : $" {option} {portNumber}") + " -- " + QuoteShellArgument(target);
    }

    public static string? DatabaseConnection(string? driver, string? host, string? port, string? database,
        string? username, string? password, bool includePassword)
    {
        driver = driver?.Trim().ToLowerInvariant();
        database ??= "";
        username ??= "";
        password ??= "";
        if (driver == "sqlite")
        {
            if (string.IsNullOrWhiteSpace(database))
            {
                return null;
            }

            var sqlite = new DbConnectionStringBuilder { ["Data Source"] = database };
            if (includePassword && password.Length > 0)
            {
                sqlite["Password"] = password;
            }

            return sqlite.ConnectionString;
        }

        if (driver is not ("mysql" or "postgresql" or "mongodb" or "redis" or "sqlserver")
            || FormatHost(host) is not { } address || !TryPort(port, out var portNumber))
        {
            return null;
        }

        if (driver == "sqlserver")
        {
            var sql = new DbConnectionStringBuilder
            {
                ["Server"] = "tcp:" + address + (portNumber is null ? "" : "," + portNumber)
            };
            if (database.Length > 0) sql["Database"] = database;
            if (username.Length > 0) sql["User ID"] = username;
            if (includePassword && password.Length > 0) sql["Password"] = password;
            return sql.ConnectionString;
        }

        if (driver == "redis" && database.Length > 0
            && (!int.TryParse(database, NumberStyles.None, CultureInfo.InvariantCulture, out var redisDatabase) || redisDatabase < 0))
        {
            return null;
        }

        var userInfo = username.Length > 0 ? Uri.EscapeDataString(username) : "";
        if (includePassword && password.Length > 0)
        {
            userInfo += ":" + Uri.EscapeDataString(password);
        }

        if (userInfo.Length > 0) userInfo += "@";
        var endpoint = address.Replace("%", "%25", StringComparison.Ordinal) + (portNumber is null ? "" : ":" + portNumber);
        return $"{driver}://{userInfo}{endpoint}/{Uri.EscapeDataString(database)}";
    }

    private static bool TryPort(string? value, out string? port)
    {
        value = value?.Trim() ?? "";
        port = null;
        if (value.Length == 0) return true;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
        {
            return false;
        }

        port = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static string? FormatHost(string? value)
    {
        var host = value?.Trim() ?? "";
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (IPAddress.TryParse(host, out var address))
        {
            return address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + host + "]" : host;
        }

        // 端口由独立字段提供，拒绝会改变 URI/命令含义的分隔符。
        if (host.Length == 0 || host.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            return null;
        }

        return host;
    }

    private static string QuoteShellArgument(string value) => "'"
        + value.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\"'\"'", StringComparison.Ordinal) + "'";
}
