using System.IO;
using System.Text.Json;
using DlutTimetable.Contracts;

namespace DlutTimetable.AuthWorker;

internal static class ObservationSnapshot
{
    public static string PathName=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DLUTDesktopTimetable","logs","auth-observation-live.json");
    public static async Task SaveAsync(ObservationReport report,string? destination=null)
    {
        destination??=PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary,JsonSerializer.SerializeToUtf8Bytes(new
                {WorkerPid=Environment.ProcessId,Observation=report},new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temporary,destination,true);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
