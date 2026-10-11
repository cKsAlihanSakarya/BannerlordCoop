using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Coop.Core.Server.Services.Admin;

/// <summary>
/// Wire format of the local server admin pipe. A client connects, writes one request line,
/// reads one JSON response line, and disconnects.
/// </summary>
/// <remarks>
/// Requests are plain text so the server needs no JSON parser:
/// <list type="bullet">
/// <item><c>status</c>: process, campaign, shutdown phase and players.</item>
/// <item><c>unstuck &lt;controller id&gt;</c>: runs <c>coop.unstuck</c> for that player.</item>
/// <item><c>shutdown &lt;seconds&gt;</c>: runs <c>coop.server.shutdown &lt;seconds&gt;</c>.</item>
/// <item><c>cancel</c>: runs <c>coop.server.shutdown cancel</c>.</item>
/// <item><c>exit</c>: closes the server process cleanly, once the shutdown has saved or while no
/// campaign is loaded.</item>
/// </list>
/// </remarks>
public static class ServerAdminProtocol
{
    /// <summary>Bumped when a request or response changes shape.</summary>
    public const int Version = 1;

    public const string PipeNamePrefix = "BannerlordCoop.ServerAdmin.";

    /// <summary>Longest request line accepted; anything longer is refused unread.</summary>
    public const int MaxRequestLength = 512;

    /// <summary>The pipe a server listening on <paramref name="port"/> serves.</summary>
    public static string GetPipeName(int port) =>
        PipeNamePrefix + port.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string line, out ServerAdminRequest request, out string error)
    {
        request = default;
        error = null;

        if (line == null)
        {
            error = "Empty request.";
            return false;
        }

        line = line.Trim();
        if (line.Length == 0)
        {
            error = "Empty request.";
            return false;
        }

        if (line.Length > MaxRequestLength)
        {
            error = "Request is too long.";
            return false;
        }

        int space = line.IndexOf(' ');
        string op = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
        string argument = space < 0 ? null : line.Substring(space + 1).Trim();
        if (argument != null && argument.Length == 0) argument = null;

        switch (op)
        {
            case "status":
            case "cancel":
            case "exit":
                if (argument != null)
                {
                    error = $"'{op}' takes no argument.";
                    return false;
                }
                request = new ServerAdminRequest(op, null);
                return true;
            case "unstuck":
                if (argument == null)
                {
                    error = "'unstuck' needs a controller id.";
                    return false;
                }
                request = new ServerAdminRequest(op, argument);
                return true;
            case "shutdown":
                if (argument == null ||
                    !int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    error = "'shutdown' needs a whole number of seconds.";
                    return false;
                }
                request = new ServerAdminRequest(op, argument);
                return true;
            default:
                error = $"Unknown request '{op}'.";
                return false;
        }
    }

    /// <summary>A response with only <c>ok</c> and a message.</summary>
    public static string Message(bool ok, string message) =>
        new JsonObjectWriter().Bool("ok", ok).String("message", message).ToString();
}

public readonly struct ServerAdminRequest
{
    public ServerAdminRequest(string op, string argument)
    {
        Op = op;
        Argument = argument;
    }

    /// <summary>Lower-case operation: status, unstuck, shutdown, cancel or exit.</summary>
    public string Op { get; }

    /// <summary>The controller id or seconds, or null.</summary>
    public string Argument { get; }
}

/// <summary>Writes one flat JSON object; values are escaped per RFC 8259.</summary>
public sealed class JsonObjectWriter
{
    private readonly StringBuilder builder = new StringBuilder("{");
    private bool first = true;

    public JsonObjectWriter String(string name, string value)
    {
        Name(name);
        AppendString(builder, value);
        return this;
    }

    public JsonObjectWriter Bool(string name, bool value)
    {
        Name(name);
        builder.Append(value ? "true" : "false");
        return this;
    }

    public JsonObjectWriter Number(string name, long value)
    {
        Name(name);
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>Appends an array of objects, each already written by its own writer.</summary>
    public JsonObjectWriter Array(string name, IEnumerable<JsonObjectWriter> items)
    {
        Name(name);
        builder.Append('[');
        bool firstItem = true;
        foreach (var item in items)
        {
            if (!firstItem) builder.Append(',');
            firstItem = false;
            builder.Append(item.ToString());
        }
        builder.Append(']');
        return this;
    }

    public JsonObjectWriter Object(string name, JsonObjectWriter value)
    {
        Name(name);
        builder.Append(value.ToString());
        return this;
    }

    public override string ToString() => builder.ToString() + "}";

    private void Name(string name)
    {
        if (!first) builder.Append(',');
        first = false;
        AppendString(builder, name);
        builder.Append(':');
    }

    internal static void AppendString(StringBuilder builder, string value)
    {
        if (value == null)
        {
            builder.Append("null");
            return;
        }

        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    // Line and paragraph separators are escaped too, so one response stays one line.
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
