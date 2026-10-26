using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class CourseDetailsTests
{
    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static string Artifacts()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"DlutTimetable.slnx")))root=root.Parent;
        var target=Path.Combine(root!.FullName,"artifacts","projection-stage");Directory.CreateDirectory(target);return target;
    }
    private static Occurrence Source()=>new(new("fixture-meeting","fixture-course","虚构课程",[],null,2,1,2,[1,3]),new(2026,9,1),new(8,0),new(9,35),false);
    private static void Save(Window window,string name)
    {
        var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        var background=new DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(window.Background,null,new(0,0,content.ActualWidth,content.ActualHeight));
        bitmap.Render(background);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(Artifacts(),name+".png"));encoder.Save(file);
    }
    [Fact]
    public void LongNamesLocationsAndTeachersRemainReadableByScrollingTheActualWindow()=>Sta(()=>
    {
        var source=Source();var name="虚构长课程"+new string('长',507);
        var occurrence=source with{HasConflict=true,Meeting=source.Meeting with{Name=name,Location=new string('室',512),TeacherNames=[new string('甲',256),new string('乙',256)]}};
        var window=new CourseDetailsWindow(occurrence){Left=-5000,Top=-5000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();
            var scroll=Assert.IsType<ScrollViewer>(window.Content);var panel=Assert.IsType<StackPanel>(scroll.Content);
            var blocks=panel.Children.OfType<TextBlock>().ToArray();
            Assert.Equal(name,blocks[0].Text);Assert.Equal(512,name.Length);
            Assert.Equal(TextWrapping.Wrap,blocks[0].TextWrapping);Assert.Equal(TextTrimming.None,blocks[0].TextTrimming);
            Assert.True(scroll.ScrollableHeight>0);Assert.True(blocks[0].ActualHeight>80);
            Save(window,"long-course-details-top");
            scroll.ScrollToEnd();window.UpdateLayout();window.UpdateLayout();
            var banner=blocks.Last();var position=banner.TransformToAncestor(scroll).TransformBounds(new(0,0,banner.ActualWidth,banner.ActualHeight));
            Assert.Contains("时间冲突",banner.Text);Assert.True(position.Top>=-2 && position.Bottom<=scroll.ActualHeight+2);
            Assert.Equal("CourseDetailsConflict",AutomationProperties.GetAutomationId(banner));
            Save(window,"long-course-details-bottom");
        }
        finally {window.Close();}
    });
    [Fact]
    public void MissingOptionalFieldsAreExplainedWithoutInventingNamesOrLocations()=>Sta(()=>
    {
        var window=new CourseDetailsWindow(Source()){Left=-5000,Top=-5000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();
            var blocks=((StackPanel)((ScrollViewer)window.Content).Content).Children.OfType<TextBlock>().ToArray();
            Assert.Contains(blocks,b=>b.Text=="地点待定");Assert.Contains(blocks,b=>b.Text=="教师待定");
            Assert.DoesNotContain(blocks,b=>b.Text.Contains("冲突",StringComparison.Ordinal));
            Save(window,"course-details-default");
        }
        finally {window.Close();}
    });
}
