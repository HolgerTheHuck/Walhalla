using System.Text.RegularExpressions;

namespace WalhallaSql.PgWire;

public static class PgWireTrace
{
    public static bool Enabled { get; set; }
    public static string? TraceFile { get; set; }

    private static void Write(string line)
    {
        Console.WriteLine(line);
        if (TraceFile != null)
        {
            try { System.IO.File.AppendAllText(TraceFile, line + Environment.NewLine); }
            catch { /* best-effort */ }
        }
    }

    public static void StartupPacket(int requestCode, int packetLength)
    {
        if (!Enabled)
            return;

        Write($"[PGWIRE][STARTUP][IN ] code={requestCode} len={packetLength}");
    }

    public static void Frontend(char messageType, int messageLength, int payloadLength)
    {
        if (!Enabled)
            return;

        Write($"[PGWIRE][IN ] type={messageType} len={messageLength} payload={payloadLength}");
    }

    public static void Backend(char messageType, int messageLength, int payloadLength)
    {
        if (!Enabled)
            return;

        Write($"[PGWIRE][OUT] type={messageType} len={messageLength} payload={payloadLength}");
    }

    public static void RawOutbound(char marker, int bytes)
    {
        if (!Enabled)
            return;

        Write($"[PGWIRE][OUT] raw={marker} bytes={bytes}");
    }

    public static void Sql(string phase, string sql)
    {
        if (!Enabled)
            return;

        var compact = Regex.Replace(sql ?? string.Empty, "\\s+", " ").Trim();
        Write($"[PGWIRE][SQL][{phase}] {compact}");
    }

    public static void Virtual(string bucket, string normalizedSql, int rows)
    {
        if (!Enabled)
            return;

        var compact = normalizedSql ?? string.Empty;
        if (compact.Length > 160)
            compact = compact[..160] + "...";

        Write($"[PGWIRE][VIRTUAL] bucket={bucket} rows={rows} sql={compact}");
    }
}
