using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;
using Microsoft.Win32;

namespace DlutTimetable.Desktop;

// A reviewable offline data workbench; production desktop views remain gated by G2/G3.
public sealed class WorkbenchWindow : Window
{
    private readonly SqliteStore store;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
    private readonly TextBlock title = new() { FontSize = 23, FontWeight = FontWeights.SemiBold };
    private readonly Grid table = new();
    private readonly ComboBox weeks = new() { Width = 95, Margin = new(8, 0, 8, 0) };
    private ValidatedDataset? current;
    private bool busy;
    public WorkbenchWindow(SqliteStore store)
    {
        this.store = store;
        Title = "大工课表 · 离线验证台";
        Width = 1080; Height = 800; MinWidth = 850; MinHeight = 600;
        Background = new SolidColorBrush(Color.FromRgb(240, 244, 248));
        FontFamily = new("Microsoft YaHei UI"); FontSize = 13;
        var root = new DockPanel { Margin = new(24) };
        Content = root;
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "大工课表 / 离线验证台", FontSize = 13, Foreground = Brushes.SlateGray });
        title.Text = "导入课表，开始离线查看"; header.Children.Add(title);
        header.Children.Add(new TextBlock
        {
            Text = "本科 · 开发区校区   |   当前阶段：本地数据闭环。学校数据链路、桌面四态与玻璃效果待验证。",
            Margin = new(0, 8, 0, 16), Foreground = Brushes.SlateGray
        });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 16) };
        header.Children.Add(controls);
        AddButton(controls, "导入 JSON", async () =>
        {
            var dialog = new OpenFileDialog { Filter = "标准课表 JSON|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) await RunAsync(async () =>
            {
                var parsed = await DatasetJson.ReadFileAsync(dialog.FileName);
                var imported = ValidatedDataset.Create(parsed.Value with { Source = "local-import" });
                await store.ReplaceAsync(imported);
                SetDataset(imported);
            });
        });
        AddButton(controls, "载入虚构示例", async () => await RunAsync(async () =>
        {
            var example = await DatasetJson.ReadFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-term.json"));
            // Preview only; never silently replaces a user's current stored data.
            SetDataset(example);
        }));
        AddButton(controls, "读取已保存课表", async () => await LoadCurrentAsync());
        AddButton(controls, "导出当前 JSON", async () =>
        {
            if (current is null) { status.Text = "请先导入或载入示例。"; return; }
            var dialog = new SaveFileDialog { Filter = "标准课表 JSON|*.json", FileName = "timetable.json" };
            if (dialog.ShowDialog(this) == true) await RunAsync(async () =>
            {
                await File.WriteAllBytesAsync(dialog.FileName, DatasetJson.Serialize(current));
                status.Text = "个人课表已导出；文件可能包含课程及教师信息，请自行保管。";
            });
        });
        controls.Children.Add(weeks);
        weeks.SelectionChanged += (_, _) => Render();
        AddButton(controls, "退出程序", () => { Close(); return Task.CompletedTask; });
        var footer = new Border { Padding = new(0, 14, 0, 0), Child = status };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += async (_, _) => await LoadCurrentAsync();
        Closing += (_, e) => { if (busy) { e.Cancel = true; } };
        Closed += (_, _) => System.Windows.Application.Current.Shutdown();
    }
    private static void AddButton(Panel parent, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new(12, 7, 12, 7), Margin = new(0, 0, 8, 0) };
        button.Click += async (_, _) => await action();
        parent.Children.Add(button);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or DatasetValidationException
            or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        { status.Text = "操作失败，已有课表未被清空。请检查文件格式或权限。DataOperationFailed"; }
        finally { busy = false; IsEnabled = true; }
    }
    private Task LoadCurrentAsync() => RunAsync(async () =>
    {
        current = await store.ReadAsync();
        if (current is { } data) SetDataset(data);
        else { table.Children.Clear(); title.Text = "尚无已保存课表"; status.Text = "可导入标准 JSON，或预览虚构示例。导入成功会整批替换唯一当前集合。"; }
    });
    private void SetDataset(ValidatedDataset value)
    {
        current = value;
        var data = value.Value;
        weeks.ItemsSource = Enumerable.Range(1, data.Term.WeekCount).ToArray();
        weeks.SelectedItem = Math.Clamp(SchoolCalendar.TeachingWeek(data.Term, SchoolCalendar.ShanghaiDate(DateTimeOffset.UtcNow)), 1, data.Term.WeekCount);
        Render();
    }
    private void Render()
    {
        table.Children.Clear(); table.RowDefinitions.Clear(); table.ColumnDefinitions.Clear();
        if (current is null || weeks.SelectedItem is not int week) return;
        var data = current.Value;
        var projection = TimetableProjector.Week(current, week);
        title.Text = $"{data.Term.SchoolYear} · 第 {week} 教学周" + (data.Synthetic ? "   [虚构示例]" : "");
        status.Text = $"来源：{data.Source}  |  获取时间：{SchoolCalendar.ShanghaiTime(data.FetchedAt):yyyy-MM-dd HH:mm}  |  "
            + (projection.Coverage == CoverageStatus.NotFetched ? "当前数据未包含该周，不会自动请求。"
                : projection.Coverage == CoverageStatus.PartialDay ? "仅覆盖指定日期，其余日期未获取。" : $"本周 {projection.Occurrences.Count} 条课程")
            + (data.Synthetic ? "  示例教学周和节次时间未经学校确认。" : "");
        table.ColumnDefinitions.Add(new() { Width = new(65) });
        for (var i = 0; i < 7; i++) table.ColumnDefinitions.Add(new());
        table.RowDefinitions.Add(new() { Height = new(44) });
        foreach (var _ in data.Periods) table.RowDefinitions.Add(new() { Height = new(48) });
        var names = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var day = 1; day <= 7; day++) AddCell(new TextBlock
        {
            Text = $"周{names[day - 1]}  {SchoolCalendar.DateOf(data.Term, week, day):MM/dd}",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }, 0, day);
        foreach (var p in data.Periods) AddCell(new TextBlock
        {
            Text = $"{p.Index}\n{p.Start:HH:mm}", FontSize = 11, Foreground = Brushes.SlateGray,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }, p.Index, 0);
        for (var row = 1; row <= data.Periods.Length; row++)
            for (var day = 1; day <= 7; day++) AddCell(new Border
            { BorderBrush = new SolidColorBrush(Color.FromRgb(224, 230, 236)), BorderThickness = new(0.5), Background = Brushes.White }, row, day);
        // Keep overlapping records visible by allocating a lane for each transitively overlapping group.
        foreach (var dayGroup in projection.Occurrences.GroupBy(o => o.Meeting.Weekday))
        {
            var remaining = dayGroup.OrderBy(o => o.Meeting.PeriodStart).ToList();
            while (remaining.Count > 0)
            {
                var group = new List<Occurrence> { remaining[0] }; remaining.RemoveAt(0);
                var end = group[0].Meeting.PeriodEnd;
                while (remaining.Count > 0 && remaining[0].Meeting.PeriodStart <= end)
                { end = Math.Max(end, remaining[0].Meeting.PeriodEnd); group.Add(remaining[0]); remaining.RemoveAt(0); }
                var start = group.Min(o => o.Meeting.PeriodStart);
                var lanes = new Grid();
                for (var row = start; row <= end; row++) lanes.RowDefinitions.Add(new());
                foreach (var _ in group) lanes.ColumnDefinitions.Add(new());
                for (var lane = 0; lane < group.Count; lane++)
                {
                    var o = group[lane];
                    var color = CourseColor(o.Meeting.CourseId);
                    var block = new Border { Background = new SolidColorBrush(color), CornerRadius = new(6), Margin = new(3), Padding = new(6),
                        Child = new TextBlock { Text = o.Meeting.Name + "\n" + o.Meeting.Location + (o.HasConflict ? "\n⚠ 时间冲突" : ""),
                            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.Black },
                        ToolTip = $"{o.Meeting.Name}\n{o.Meeting.Location}\n{string.Join('、', o.Meeting.TeacherNames)}\n{o.Start:HH:mm}—{o.End:HH:mm}" };
                    Grid.SetRow(block, o.Meeting.PeriodStart - start); Grid.SetRowSpan(block, o.Meeting.PeriodEnd - o.Meeting.PeriodStart + 1);
                    Grid.SetColumn(block, lane); lanes.Children.Add(block);
                }
                AddCell(lanes, start, dayGroup.Key, end - start + 1);
            }
        }
    }
    private void AddCell(UIElement element, int row, int column, int rowSpan = 1)
    { Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetRowSpan(element, rowSpan); table.Children.Add(element); }
    private static Color CourseColor(string key)
    {
        uint hash = 2166136261;
        foreach (var c in key) hash = (hash ^ c) * 16777619;
        var colors = new[] { "#DCEAF6", "#E5E1F5", "#D8EEE7", "#FAE6D4", "#F3DDE5", "#E9EDCE" };
        return (Color)ColorConverter.ConvertFromString(colors[hash % colors.Length]);
    }
}
