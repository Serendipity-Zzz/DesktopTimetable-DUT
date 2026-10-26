using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows;

public sealed class CourseDetailsWindow : Window
{
    public CourseDetailsWindow(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var meeting=occurrence.Meeting;
        Title="课程详情";Width=420;Height=340;MinWidth=320;MinHeight=240;
        FontFamily=new("Segoe UI, Microsoft YaHei UI");FontSize=14;
        Background=TimetableView.Frozen("#F4F4F6");Foreground=TimetableView.Frozen("#232327");
        var panel=new StackPanel{Margin=new(22)};
        var scroll=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        AutomationProperties.SetAutomationId(scroll,"CourseDetailsScroll");
        Content=scroll;
        WindowFrame.Apply(this);
        Add("CourseDetailsName",meeting.Name,21,FontWeights.SemiBold,new(0,0,0,18));
        Add("CourseDetailsTime",$"{occurrence.Date:yyyy-MM-dd}   {occurrence.Start:HH:mm}—{occurrence.End:HH:mm}\n第 {meeting.PeriodStart}—{meeting.PeriodEnd} 节");
        Add("CourseDetailsLocation",string.IsNullOrWhiteSpace(meeting.Location)?"地点待定":meeting.Location);
        Add("CourseDetailsTeachers",meeting.TeacherNames.IsDefaultOrEmpty?"教师待定":string.Join('、',meeting.TeacherNames));
        if(occurrence.HasConflict)Add("CourseDetailsConflict","时间冲突，请核对其他课程安排。",14,FontWeights.Medium);
        void Add(string id,string text,double size=14,FontWeight? weight=null,Thickness? margin=null)
        {
            var block=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=size,FontWeight=weight??FontWeights.Normal,
                Margin=margin??new Thickness(0,0,0,12)};
            AutomationProperties.SetAutomationId(block,id);panel.Children.Add(block);
        }
    }
}
