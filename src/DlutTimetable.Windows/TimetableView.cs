using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using DlutTimetable.Domain;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Windows;

public sealed class TimetableView : FrameworkElement
{
    private readonly VisualCollection layers;
    private readonly DrawingVisual background = new(), foreground = new();
    private MaterialFrame? material;
    private bool liveBackdrop,liveDark;
    private Color fallbackBackground=Color.FromRgb(243,244,246),fallbackInk=GlassAppearance.DarkInk;
    private bool highContrast;
    internal void SetFallbackAppearance(bool dark,bool contrast)
    {
        var background=contrast?SystemColors.WindowColor:dark?Color.FromRgb(24,28,36):Color.FromRgb(243,244,246);
        var ink=contrast?SystemColors.WindowTextColor:dark?GlassAppearance.LightInk:GlassAppearance.DarkInk;
        if(fallbackBackground==background && fallbackInk==ink && highContrast==contrast)return;
        fallbackBackground=background;fallbackInk=ink;highContrast=contrast;
        DrawBackground();InvalidateVisual();
    }
    public bool HasLiveBackdrop=>liveBackdrop;
    internal void SetLiveBackdrop(bool enabled,bool dark=false)
    {
        if(liveBackdrop==enabled && liveDark==dark)return;
        liveBackdrop=enabled;liveDark=dark;DrawBackground();InvalidateVisual();
    }
    private DipRect viewport;
    private Brush surfaceInk=Frozen("#14171C");
    private SurfacePaint surfacePaint;
    private readonly TextProtectionSolver textProtection=new();
    public Brush ChromeForeground { get; private set; }=Frozen("#14171C");
    public double ForegroundOpacity { get; private set; }=0.88;
    internal void SetForegroundTransparency(double transparency)
    {
        var opacity=1-transparency;
        if(ForegroundOpacity==opacity)return;
        ForegroundOpacity=opacity;foreground.Opacity=opacity;ChromeChanged?.Invoke();InvalidateVisual();
    }
    public event Action? ChromeChanged;
    public bool HasMaterial => material is not null;
    public long RenderCount { get; private set; }
    protected override int VisualChildrenCount => layers.Count;
    protected override Visual GetVisualChild(int index) => layers[index];
    private readonly List<(Rect Rect, Occurrence Course)> hitAreas = [];
    private readonly List<(Rect Rect, IReadOnlyList<Occurrence> Courses)> conflictAreas = [];
    private ValidatedDataset? dataset;
    private CalendarAdjustment? calendar;
    private Term? DisplayTerm=>dataset is null?null:calendar?.Term(dataset.Value.Term)??dataset.Value.Term;
    private readonly TimeProvider clock;
    private DateTimeOffset projectionInstant;
    private WeekProjection? weekProjection;
    private TodayProjection? todayProjection;
    private bool interactive;
    public int Week { get; private set; } = 1;
    public int Page { get; private set; }
    public bool Interactive { get => interactive; set => interactive = value; }
    public event Action<Occurrence>? CourseInvoked;
    public event Action<IReadOnlyList<Occurrence>>? ConflictsInvoked;
    public event Action<int>? WeekChanged;
    private static readonly Typeface Font = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    // Semantic accent hues. These are tint inputs, never colors sampled from an already composited mockup.
    private static readonly string[] Accents = ["#4F8FF7", "#AE83E8", "#E5A851", "#65B18C", "#56AFBC", "#DB86A6"];
    public TimetableView():this(TimeProvider.System) { }
    public TimetableView(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);this.clock=clock;projectionInstant=clock.GetUtcNow();
        Width = 780; Height = 468; Focusable = false;
        layers = new(this) { background, foreground };
        foreground.Opacity=ForegroundOpacity;
    }
    public void SetMaterialViewport(DipRect value) { if (viewport == value) return; viewport = value; DrawBackground(); }
    public void SetMaterial(MaterialFrame value) { material = value; DrawBackground(); InvalidateVisual(); }
    public void ClearMaterial()
    {
        if (material is null) return;
        material = null;
        SetChrome(Brush(fallbackInk));
        // RenderOpen drops the retained DrawingVisual image even while this HWND is hidden.
        DrawBackground(); InvalidateVisual();
    }
    private Rect MaterialRect()
    {
        var outer = material!.OuterPhysicalRect;
        return new((outer.X-viewport.X)/viewport.Width*780, (outer.Y-viewport.Y)/viewport.Height*468,
            outer.Width/viewport.Width*780, outer.Height/viewport.Height*468);
    }
    private void DrawBackground()
    {
        using var dc = background.RenderOpen();
        if(liveBackdrop)return;
        dc.DrawRoundedRectangle(Brush(fallbackBackground), null, new(0,0,780,468),18,18);
        if (material is null || viewport.Width <= 0 || viewport.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(new(0,0,780,468),18,18));
        dc.DrawImage(material.Bitmap, MaterialRect()); dc.Pop();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    public void SetDataset(ValidatedDataset? value, int? selectedWeek = null,CalendarAdjustment? calendar=null)
    {
        if(calendar is not null && value is not null)calendar.ValidateFor(value);
        this.calendar=calendar;
        dataset = value; Page = 0;
        projectionInstant=clock.GetUtcNow();
        if (DisplayTerm is {} term) Week = Math.Clamp(selectedWeek ?? SchoolCalendar.TeachingWeek(term, SchoolCalendar.ShanghaiDate(projectionInstant)), 1, term.WeekCount);
        weekProjection=value is null?null:TimetableProjector.Week(value,Week,calendar);
        todayProjection=value is null?null:TimetableProjector.Today(value,projectionInstant,calendar);
        UpdateAutomationName();
        InvalidateVisual();
        if (value is not null) WeekChanged?.Invoke(Week);
    }
    public void SetWeek(int week)
    {
        if (dataset is null) return;
        Week = Math.Clamp(week, 1, DisplayTerm!.WeekCount); Page = 0;
        weekProjection=TimetableProjector.Week(dataset,Week,calendar);
        projectionInstant=clock.GetUtcNow();todayProjection=TimetableProjector.Today(dataset,projectionInstant,calendar);
        UpdateAutomationName(); InvalidateVisual(); WeekChanged?.Invoke(Week);
    }
    private int Pages => Math.Max(1, (int)Math.Ceiling((calendar?.Periods.Length??dataset?.Value.Periods.Length ?? 12) / 12d));
    private void UpdateAutomationName()
    {
        AutomationProperties.SetName(this, dataset is null ? "尚无课表" : $"第 {Week} 周课表" + (dataset.Value.Synthetic ? "，虚构示例" : ""));
        AutomationProperties.SetHelpText(this, $"第 {Page + 1}/{Pages} 页");
        AutomationProperties.SetItemStatus(this,dataset is null?"尚无课表":WeekStatus()+"；"+TodayStatus());
    }
    private string WeekStatus()=>weekProjection?.Coverage switch
    {
        CoverageStatus.NotFetched=>"本周数据未获取",
        CoverageStatus.OutsideTerm=>"不在学期内",
        CoverageStatus.PartialDay=>weekProjection.Occurrences.Count==0?"指定日期无课":"仅包含指定日期",
        _=>weekProjection?.Occurrences.Count==0?"本周无课":"本周有课"
    };
    private string TodayStatus()=>todayProjection?.Status switch
    {
        TodayScheduleStatus.OutsideTerm=>"今日不在学期内",
        TodayScheduleStatus.NoClasses=>"今日无课",
        TodayScheduleStatus.Finished=>"今日课程已结束",
        TodayScheduleStatus.InProgress=>"进行中："+todayProjection.Current[0].Meeting.Name,
        TodayScheduleStatus.Upcoming=>$"下一节 {todayProjection.Next!.Start:HH:mm} · {todayProjection.Next.Meeting.Name}",
        _=>"今日数据未获取"
    };
    private void ChangePage(int delta) { Page = Math.Clamp(Page + delta, 0, Pages - 1); UpdateAutomationName(); InvalidateVisual(); }
    public void RefreshClock()
    {
        var previousDate=SchoolCalendar.ShanghaiDate(projectionInstant);
        var previousStatus=TodayStatus();
        projectionInstant=clock.GetUtcNow();
        todayProjection=dataset is null?null:TimetableProjector.Today(dataset,projectionInstant,calendar);
        // Retain text/cards when neither today's column nor its status changed.
        if(previousDate==SchoolCalendar.ShanghaiDate(projectionInstant) && previousStatus==TodayStatus())return;
        UpdateAutomationName();InvalidateVisual();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    { if (Interactive) { ChangePage(e.Delta < 0 ? 1 : -1); e.Handled = true; } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!Interactive) return;
        var point = e.GetPosition(this);
        if (point.Y < 36)
        {
            if (point.X < 24) SetWeek(Week - 1);
            else if (point.X is > 170 and < 190) SetWeek(Week + 1);
            else if (point.X is > 530 and < 570) ChangePage(-1);
            else if (point.X is >= 570 and < 630) ChangePage(1);
        }
        else
        {
            var conflict = conflictAreas.FirstOrDefault(h => h.Rect.Contains(point));
            if (conflict.Courses is not null) { ConflictsInvoked?.Invoke(conflict.Courses); e.Handled = true; return; }
            var hit = hitAreas.FirstOrDefault(h => h.Rect.Contains(point));
            if (hit.Course is not null) CourseInvoked?.Invoke(hit.Course);
        }
        e.Handled = true;
    }
    public static Brush Frozen(string color)
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private void SetChrome(Brush value)
    {
        if(((SolidColorBrush)ChromeForeground).Color==((SolidColorBrush)value).Color)return;
        ChromeForeground=value;ChromeChanged?.Invoke();
    }
    private static Brush Brush(Color color,double opacity=1)
    { var brush=new SolidColorBrush(color){Opacity=opacity};brush.Freeze();return brush; }
    private GlassSurface Surface(DrawingContext dc,Rect bounds,Color? accent=null,bool card=false)
    {
        var hue=accent??Colors.Transparent;
        var tint=card?GlassAppearance.CourseTintOpacity:0;
        GlassSurface appearance;
        surfacePaint=default;
        if(liveBackdrop)
        {
            appearance=new(liveDark?GlassAppearance.LightInk:GlassAppearance.DarkInk,liveDark?Colors.Black:Colors.White,0,0);
            dc.DrawRoundedRectangle(Brush(liveDark?Colors.Black:Colors.White,liveDark?0.18:0.22),null,bounds,card?8:9,card?8:9);
            surfacePaint=surfacePaint.Over(liveDark?Colors.Black:Colors.White,liveDark?0.18:0.22);
            if(card)dc.DrawRoundedRectangle(Brush(hue,0.12),null,bounds,8,8);
            if(card)surfacePaint=surfacePaint.Over(hue,0.12);
        }
        else if(material is null || viewport.Width<=0 || viewport.Height<=0)
        {
            appearance=new(fallbackInk,fallbackBackground,0,TextContrast.Ratio(fallbackInk,fallbackBackground));
            if(card)dc.DrawRoundedRectangle(Brush(highContrast?fallbackBackground:TextContrast.Blend(fallbackBackground,hue,0.13)),null,bounds,8,8);
        }
        else
        {
            var image=MaterialRect();
            var area=new Rect((bounds.X-image.X)/image.Width*material.Bitmap.PixelWidth,
                (bounds.Y-image.Y)/image.Height*material.Bitmap.PixelHeight,
                bounds.Width/image.Width*material.Bitmap.PixelWidth,bounds.Height/image.Height*material.Bitmap.PixelHeight);
            appearance=GlassAppearance.Solve(material.Analysis,area,hue,tint);
            if(card)dc.DrawRoundedRectangle(Brush(hue,tint),null,bounds,8,8);
            if(appearance.VeilOpacity>0)dc.DrawRoundedRectangle(Brush(appearance.Veil,appearance.VeilOpacity),null,bounds,card?8:9,card?8:9);
            if(card)surfacePaint=surfacePaint.Over(hue,tint);
            surfacePaint=surfacePaint.Over(appearance.Veil,appearance.VeilOpacity);
        }
        surfaceInk=Brush(appearance.Foreground);
        if(liveBackdrop || material is not null)
        {
            // Protect the semantic surface once. Text is part of its card/bar,
            // rather than a separate badge for every line or number.
            var guard=textProtection.Solve(appearance.Foreground,ForegroundOpacity,surfacePaint);
            dc.DrawRoundedRectangle(Brush(guard.Veil,guard.Opacity),null,bounds,card?8:9,card?8:9);
            surfaceInk=Brush(guard.Ink);
        }
        if(card)
        {
            var darkText=TextContrast.Luminance(appearance.Foreground)<0.5;
            dc.DrawRoundedRectangle(null,new Pen(Brush(highContrast?fallbackInk:darkText?Colors.Black:Colors.White,highContrast?1:darkText?0.07:0.14),0.65),bounds,8,8);
            if(bounds.Height>16 && !highContrast)dc.DrawRoundedRectangle(Brush(hue,0.9),null,new(bounds.X+2,bounds.Y+7,2,bounds.Height-14),1,1);
        }
        return appearance;
    }
    private void Text(DrawingContext dc, string value, double x, double y, double width, double size = 12,
        Brush? brush = null, double height = 18, bool bold = false,TextAlignment alignment=TextAlignment.Left)
    {
        if (width <= 0 || height <= 0 || string.IsNullOrEmpty(value)) return;
        var ink=brush??surfaceInk;
        var font = bold ? new Typeface(Font.FontFamily, Font.Style, FontWeights.Medium, Font.Stretch) : Font;
        var text = new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            font, size, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = width, MaxTextHeight = Math.Max(height, size * 1.16), LineHeight = size * 1.16, Trimming = TextTrimming.CharacterEllipsis,TextAlignment=alignment };
        dc.DrawText(text, new(x, y));
    }
    protected override void OnRender(DrawingContext ignored)
    {
        RenderCount++;
        using var dc = foreground.RenderOpen();
        DrawBackground();
        hitAreas.Clear(); conflictAreas.Clear();
        dc.DrawRoundedRectangle(null, new Pen(highContrast?Brush(fallbackInk):Brush(Color.FromRgb(155,183,216),0.72), 0.8), new(0.5, 0.5, 779, 467), 18, 18);
        var today = SchoolCalendar.ShanghaiDate(projectionInstant);
        var d = dataset?.Value;
        var dates = d is null ? null : Enumerable.Range(1, 7).Select(day => SchoolCalendar.DateOf(DisplayTerm!, Week, day)).ToArray();
        Surface(dc,new(8,6,764,28));SetChrome(surfaceInk);
        var navigation = new Pen(surfaceInk, 1.25);
        dc.DrawLine(navigation, new(18, 14), new(13, 18)); dc.DrawLine(navigation, new(13, 18), new(18, 22));
        Text(dc, d is null ? "大工课表" : $"第 {Week} 周", 30, 10, 65, 13, bold: true);
        Text(dc, dates is null ? "等待导入" : $"{dates[0]:MM.dd}—{dates[6]:MM.dd}", 100, 12, 74, 10.5);
        dc.DrawLine(navigation, new(177, 14), new(182, 18)); dc.DrawLine(navigation, new(182, 18), new(177, 22));
        var todayAccent=highContrast?SystemColors.HighlightColor:Color.FromRgb(57,123,209);
        var todayInk=highContrast?SystemColors.HighlightTextColor:
            TextContrast.Ratio(Colors.White,todayAccent)>=TextContrast.Ratio(Colors.Black,todayAccent)?Colors.White:Colors.Black;
        dc.DrawRoundedRectangle(Brush(todayAccent),null,new(190,8,108,22),8,8);
        Text(dc,$"今天  {today:MM.dd}",194,12,100,12,Brush(todayInk),height:14,bold:true,alignment:TextAlignment.Center);
        if (Pages > 1)
        {
            Text(dc,$"{Page+1} / {Pages}",562,12,38,10.5);
            dc.DrawLine(navigation,new(547,15),new(543,18));dc.DrawLine(navigation,new(543,18),new(547,21));
            dc.DrawLine(navigation,new(611,15),new(615,18));dc.DrawLine(navigation,new(615,18),new(611,21));
        }
        const double column = 732d / 7, top = 68, rowHeight = 30;
        var names = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        Surface(dc,new(38,40,732,25));
        for (var day = 0; day < 7; day++)
        {
            var isToday = dates?[day] == today;
            var x = 38 + day * column;
            if (isToday)
            {
                var accent=highContrast?SystemColors.HighlightColor:Color.FromRgb(57,123,209);
                var ink=highContrast?SystemColors.HighlightTextColor:
                    TextContrast.Ratio(Colors.White,accent)>=TextContrast.Ratio(Colors.Black,accent)?Colors.White:Colors.Black;
                dc.DrawRoundedRectangle(Brush(accent),null,new(x+4,41,column-8,23),7,7);
                var todayLabel=dates is null?names[day]:$"今天 · {names[day]} {dates[day].Day}";
                Text(dc,todayLabel,x+5,46,column-10,10.5,Brush(ink),height:14,bold:true,alignment:TextAlignment.Center);
            }
            else
            {
                var dayLabel=dates is null?names[day]:$"{names[day]}  {dates[day].Day}";
                Text(dc,dayLabel,x+22,45,column-24,10.5,height:14);
            }
        }
        for (var col = 1; col < 7; col++) dc.DrawLine(new(Brush(((SolidColorBrush)surfaceInk).Color,0.055),0.5),new(38+col*column,top),new(38+col*column,top+360));
        Surface(dc,new(6,69,26,358));
        for (var row = 0; row < 12; row++)
        {
            var index = Page * 12 + row + 1;
            if (index <= (calendar?.Periods.Length??d?.Periods.Length ?? 12)) Text(dc, index.ToString(CultureInfo.InvariantCulture), 13, top + row * rowHeight + 8, 22, 10);
        }
        if (dataset is null)
        {
            Surface(dc,new(268,195,300,66));
            Text(dc, "尚无课表", 310, 205, 180, 18, height: 28, bold: true);
            Text(dc, "从设置导入课表，离线也可查看", 278, 237, 280, 12);
            Surface(dc,new(12,438,756,24));Text(dc, "○ 尚未连接账号", 16, 444, 400, 10);
            return;
        }
        var projection = weekProjection!;
        if(projection.Occurrences.Count==0)
        {
            Surface(dc,new(248,202,310,62));
            Text(dc,WeekStatus(),256,212,294,17,height:24,bold:true,alignment:TextAlignment.Center);
            var hint=projection.Coverage==CoverageStatus.NotFetched?"可主动刷新或导入，浏览不会自动抓取":
                projection.Coverage==CoverageStatus.PartialDay?$"仅包含 {dataset.Value.Coverage.Date:MM.dd}，其余日期未获取":"当前已获取范围内没有课程安排";
            Text(dc,hint,256,242,294,10,alignment:TextAlignment.Center);
        }
        foreach (var day in projection.Occurrences.GroupBy(o => o.Meeting.Weekday))
        {
            var remaining = day.Where(o => o.Meeting.PeriodEnd > Page * 12 && o.Meeting.PeriodStart <= (Page + 1) * 12)
                .OrderBy(o => o.Meeting.PeriodStart).ToList();
            while (remaining.Count > 0)
            {
                var group = new List<Occurrence> { remaining[0] }; remaining.RemoveAt(0);
                var end = group[0].Meeting.PeriodEnd;
                while (remaining.Count > 0 && remaining[0].Meeting.PeriodStart <= end)
                { end = Math.Max(end, remaining[0].Meeting.PeriodEnd); group.Add(remaining[0]); remaining.RemoveAt(0); }
                if (group.Count > 3)
                {
                    var start = Math.Max(group.Min(o => o.Meeting.PeriodStart), Page * 12 + 1);
                    var finish = Math.Min(end, (Page + 1) * 12);
                    var rect = new Rect(38 + (day.Key - 1) * column + 2, top + (start - Page * 12 - 1) * rowHeight + 2,
                        column - 4, (finish - start + 1) * rowHeight - 4);
                    Surface(dc,rect,(Color)ColorConverter.ConvertFromString("#E5A851"),true);
                    Text(dc, $"{group.Count} 条课程冲突\n点击查看全部", rect.X + 6, rect.Y + 5, rect.Width - 12, 11,
                        height:Math.Max(10, rect.Height - 10));
                    conflictAreas.Add((rect, group.ToArray())); continue;
                }
                for (var lane = 0; lane < group.Count; lane++)
                {
                    var o = group[lane]; var start = Math.Max(o.Meeting.PeriodStart, Page * 12 + 1); var finish = Math.Min(o.Meeting.PeriodEnd, (Page + 1) * 12);
                    var rect = new Rect(38 + (day.Key - 1) * column + lane * column / group.Count + 2,
                        top + (start - Page * 12 - 1) * rowHeight + 2, column / group.Count - 4, (finish - start + 1) * rowHeight - 4);
                    uint hash = 2166136261; foreach (var c in o.Meeting.CourseId) hash = (hash ^ c) * 16777619;
                    var color = hash % 6;
                    Surface(dc,rect,(Color)ColorConverter.ConvertFromString(Accents[color]),true);
                    var maxHeight = Math.Min(30, rect.Height - 7);
                    Text(dc, o.Meeting.Name, rect.X + 7, rect.Y + 6, rect.Width - 14, 12, height:maxHeight, bold:true);
                    if (rect.Height > 44) Text(dc, o.Meeting.Location ?? "", rect.X + 7, rect.Y + 36, rect.Width - 14, 11, height:rect.Height - 40);
                    if (o.HasConflict) Text(dc, "!", rect.Right - 11, rect.Y + 3, 9, 10);
                    hitAreas.Add((rect, o));
                }
            }
        }
        var label = projection.Coverage switch
        {
            CoverageStatus.NotFetched => "当前数据未包含该周",
            CoverageStatus.PartialDay => $"仅含 {dataset.Value.Coverage.Date:MM.dd} 数据",
            _ => dataset.Value.Synthetic ? (projection.Occurrences.Count==0?"本周无课":""):
                (projection.Occurrences.Count==0?"本周无课 · ":"")+$"更新于 {SchoolCalendar.ShanghaiTime(dataset.Value.FetchedAt):MM.dd HH:mm}"
        };
        if(dataset.Value.Synthetic)label="虚构示例"+(label.Length>0?" · "+label:"");
        Surface(dc,new(12,438,756,24));
        Text(dc, "● " + label, 16, 444, 395, 10);
        Text(dc, TodayStatus(), 430, 444, 335, 10);
    }
}
