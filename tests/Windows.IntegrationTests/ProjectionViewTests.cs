using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;
using DlutTimetable.Windows;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class ProjectionViewTests
{
    private sealed class Clock(DateTimeOffset now):TimeProvider
    {
        public DateTimeOffset Now=now;
        public override DateTimeOffset GetUtcNow()=>Now;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)=>throw new InvalidOperationException("View must not start a polling timer.");
    }
    private static string Root()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"DlutTimetable.slnx")))root=root.Parent;
        return root?.FullName??throw new InvalidOperationException("Repository missing");
    }
    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static CurrentDataset Source()=>DatasetJson.Parse(File.ReadAllBytes(Path.Combine(Root(),"fixtures/dlut/synthetic-term.json"))).Value;
    [Fact]
    public void MinuteRefreshPreservesDrawingUntilCourseBoundaryOrDateChanges()=>Sta(()=>
    {
        var source=Source();var meeting=source.Meetings[0] with{Weekday=2,Weeks=[1],PeriodStart=1,PeriodEnd=2};
        var start=new DateTimeOffset(new DateTime(2026,9,1).Add(source.Periods[0].Start.ToTimeSpan()),TimeSpan.FromHours(8));
        var clock=new Clock(start.AddMinutes(-2));var view=new TimetableView(clock);
        view.SetDataset(ValidatedDataset.Create(source with{Meetings=[meeting]}),1);
        void Render()
        {
            view.Measure(new(780,468));view.Arrange(new(0,0,780,468));view.UpdateLayout();
            new RenderTargetBitmap(780,468,96,96,PixelFormats.Pbgra32).Render(view);
        }
        Render();var initial=view.RenderCount;
        clock.Now=start.AddMinutes(-1);view.RefreshClock();Render();
        Assert.Equal(initial,view.RenderCount);
        clock.Now=start;view.RefreshClock();Render();
        Assert.True(view.RenderCount>initial);Assert.Contains("进行中",AutomationProperties.GetItemStatus(view));
        var active=view.RenderCount;clock.Now=start.AddDays(1);view.RefreshClock();Render();
        Assert.True(view.RenderCount>active);Assert.Contains("今日无课",AutomationProperties.GetItemStatus(view));
    });
    [Fact]
    public void BrowsingAnotherWeekDoesNotChangeTodaysFinishedStatus()=>Sta(()=>
    {
        var source=Source();var meeting=source.Meetings[0] with{Weekday=2,Weeks=[1],PeriodStart=1,PeriodEnd=2};
        var data=ValidatedDataset.Create(source with{Meetings=[meeting]});
        var clock=new Clock(new(2026,9,1,23,0,0,TimeSpan.FromHours(8)));var view=new TimetableView(clock);
        view.SetDataset(data,1);Assert.Contains("今日课程已结束",AutomationProperties.GetItemStatus(view));
        view.SetWeek(2);
        Assert.Contains("本周无课",AutomationProperties.GetItemStatus(view));
        Assert.Contains("今日课程已结束",AutomationProperties.GetItemStatus(view));
        clock.Now=new(2026,9,2,8,0,0,TimeSpan.FromHours(8));view.RefreshClock();
        Assert.Contains("今日无课",AutomationProperties.GetItemStatus(view));
    });
    [Fact]
    public void UnknownAndConfirmedEmptyWeeksRenderDifferentStatusWithoutPolling()=>Sta(()=>
    {
        var source=Source();var clock=new Clock(new(2026,9,8,8,0,0,TimeSpan.FromHours(8)));
        var view=new TimetableView(clock);view.SetDataset(ValidatedDataset.Create(source with{Coverage=new("WEEK",[2]),Meetings=[],VerifiedEmpty=true}),2);
        var directory=Path.Combine(Root(),"artifacts/projection-stage");Directory.CreateDirectory(directory);
        void Render(string name)
        {
            view.Measure(new(780,468));view.Arrange(new(0,0,780,468));view.UpdateLayout();
            var bitmap=new RenderTargetBitmap(780,468,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,name+".png"));encoder.Save(file);
        }
        Assert.Contains("本周无课",AutomationProperties.GetItemStatus(view));Render("empty-week");
        view.SetWeek(1);Assert.Contains("本周数据未获取",AutomationProperties.GetItemStatus(view));Render("unfetched-week");
        Assert.Contains("今日无课",AutomationProperties.GetItemStatus(view));
    });
}
