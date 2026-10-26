namespace DlutTimetable.Windows;

// Session and power reasons are independent: a wake does not unlock a session,
// and a reconnect is not evidence that the locked desktop has been unlocked.
internal sealed class PresentationAvailability(int sessionId)
{
    public int SessionId=>sessionId;
    private bool locked,disconnected,suspended;
    public bool Paused=>locked || disconnected || suspended;
    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        if(snapshot.Locked is {} known)locked=known;
        disconnected=snapshot.Disconnected;
    }
    public bool Observe(int message,nint code,nint session)
    {
        var previous=Paused;
        if(message==0x2B1 && session==sessionId)
        {
            switch((int)code)
            {
                case 2:case 4:disconnected=true;break;
                case 1:case 3:disconnected=false;break;
                case 7:locked=true;break;
                case 8:locked=false;suspended=false;break;
            }
        }
        else if(message==0x218)
        {
            switch((int)code)
            {
                case 4:suspended=true;break;
                case 6:case 7:suspended=false;break;
                // PBT_APMRESUMEAUTOMATIC does not establish user presence.
            }
        }
        return previous!=Paused;
    }
}
