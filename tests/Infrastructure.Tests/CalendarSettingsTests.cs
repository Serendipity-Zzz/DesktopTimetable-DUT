using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Tests;

public sealed class CalendarSettingsTests
{
    [Fact]
    public async Task LocalCalendarPersistsSeparatelyAndCanBeReset()
    {
        var path=Path.Combine(Path.GetTempPath(),"dlut-calendar-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            using var store=new SettingsStore(path);
            var value=new CalendarAdjustment("fictional-account","fictional-term",new(2026,8,31),20,[new(1,new(8,0),new(8,45))]);
            await store.SaveAsync(new(){Scale=1.2,CalendarAdjustment=value});
            using var reopened=new SettingsStore(path);var settings=(await reopened.LoadAsync()).Settings!;
            Assert.Equal(value.FirstTeachingMonday,settings.CalendarAdjustment!.FirstTeachingMonday);Assert.Equal(1.2,settings.Scale);
            await reopened.SaveAsync(settings with{CalendarAdjustment=null});
            Assert.Null((await reopened.LoadAsync()).Settings!.CalendarAdjustment);
        }
        finally {File.Delete(path);}
    }
    [Fact]
    public async Task InvalidCalendarCannotOverwriteSavedPreferences()
    {
        var path=Path.Combine(Path.GetTempPath(),"dlut-calendar-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            using var store=new SettingsStore(path);await store.SaveAsync(new(){Scale=1.2});var before=await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<ArgumentException>(()=>store.SaveAsync(new(){CalendarAdjustment=new("a","t",new(2026,9,1),20,[])}));
            Assert.Equal(before,await File.ReadAllBytesAsync(path));
        }
        finally {File.Delete(path);}
    }
}
