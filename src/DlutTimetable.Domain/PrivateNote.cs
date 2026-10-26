namespace DlutTimetable.Domain;

// Identity comes from the normalized source meeting, never a course display name or location.
public sealed record PrivateNoteKey(string AccountId,string TermId,string MeetingId)
{
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(AccountId) || AccountId.Length>256 || AccountId.Contains('\0')
            || string.IsNullOrWhiteSpace(TermId) || TermId.Length>256 || TermId.Contains('\0')
            || string.IsNullOrWhiteSpace(MeetingId) || MeetingId.Length>512 || MeetingId.Contains('\0'))
            throw new ArgumentException("私人备注缺少有效的账号、学期或课程安排标识。");
    }
}

public sealed record PrivateNote(PrivateNoteKey Key,string Text,DateTimeOffset UpdatedAt)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Key);Key.Validate();
        if(string.IsNullOrEmpty(Text) || Text.Length>4096 || Text.Contains('\0') || UpdatedAt==default)
            throw new ArgumentException("私人备注内容或更新时间无效。");
    }
}
