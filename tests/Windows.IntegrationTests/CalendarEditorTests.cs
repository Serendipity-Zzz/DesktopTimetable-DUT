using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class CalendarEditorTests
{
    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static ValidatedDataset Source()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"DlutTimetable.slnx")))root=root.Parent;
        return DatasetJson.Parse(File.ReadAllBytes(Path.Combine(root!.FullName,"fixtures/dlut/synthetic-term.json")));
    }
    private static T Find<T>(DependencyObject root,string id) where T:DependencyObject
    {
        if(root is T control && AutomationProperties.GetAutomationId(root)==id)return control;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {try{return Find<T>(VisualTreeHelper.GetChild(root,i),id);}catch(KeyNotFoundException){}}
        throw new KeyNotFoundException(id);
    }
    private static void Click(Window window,string id)=>Find<Button>(window,id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [Fact]
    public void EditorRejectsInvalidInputThenSavesValidCalendarWithoutChangingSource()=>Sta(()=>
    {
        var source=Source();CalendarAdjustment? saved=null;var writes=0;
        var window=new CalendarEditorWindow(source,null,value=>{saved=value;writes++;return Task.FromResult<string?>(null);}){Left=-5000,Top=-5000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();
            Find<DatePicker>(window,"CalendarMonday").SelectedDate=new(2026,9,1);
            Click(window,"CalendarSave");Assert.Equal(0,writes);Assert.Contains("周一",Find<TextBlock>(window,"CalendarStatus").Text);
            Find<DatePicker>(window,"CalendarMonday").SelectedDate=new(2026,9,7);Find<TextBox>(window,"CalendarWeekCount").Text="20";
            var rows=((IEnumerable<CalendarEditorWindow.PeriodDraft>)Find<DataGrid>(window,"CalendarPeriods").ItemsSource).ToArray();
            var end=rows[0].End;rows[0].End="25:00";
            Click(window,"CalendarSave");Assert.Equal(0,writes);Assert.Contains("HH:mm",Find<TextBlock>(window,"CalendarStatus").Text);
            rows[0].End=end;Click(window,"CalendarSave");Assert.Equal(1,writes);Assert.NotNull(saved);
            Assert.Equal(20,saved.WeekCount);Assert.Equal(new DateOnly(2026,8,31),source.Value.Term.FirstTeachingMonday);
            Assert.Equal(CoverageStatus.NotFetched,TimetableProjector.Week(source,19,saved).Coverage);
        }
        finally {window.Close();}
    });
    [Fact]
    public void CancelDoesNotSaveAndRestoreExplicitlyRemovesTheLocalPreference()=>Sta(()=>
    {
        var source=Source();var writes=0;
        Task<string?> Save(CalendarAdjustment? value){Assert.Null(value);writes++;return Task.FromResult<string?>(null);}
        var window=new CalendarEditorWindow(source,null,Save){Left=-5000,Top=-5000,ShowActivated=false,ShowInTaskbar=false};
        window.Show();window.UpdateLayout();Click(window,"CalendarCancel");Assert.Equal(0,writes);
        window=new(source,null,Save){Left=-5000,Top=-5000,ShowActivated=false,ShowInTaskbar=false};
        try {window.Show();window.UpdateLayout();Click(window,"CalendarReset");Assert.Equal(1,writes);}finally{window.Close();}
    });
    [Fact]
    public void WeekViewAcceptsExtendedCalendarButKeepsUnfetchedWeeksDistinct()=>Sta(()=>
    {
        var source=Source();var value=new CalendarAdjustment(source.Value.AccountId,source.Value.Term.Id,new(2026,9,7),20,source.Value.Periods);
        var view=new TimetableView();view.SetDataset(source,19,value);
        Assert.Contains("本周数据未获取",AutomationProperties.GetItemStatus(view));
        view.SetWeek(1);Assert.Contains("第 1 周",AutomationProperties.GetName(view));
    });
}
