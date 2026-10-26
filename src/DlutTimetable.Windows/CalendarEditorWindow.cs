using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows;

public sealed class CalendarEditorWindow : Window
{
    public sealed class PeriodDraft(int index,string start,string end)
    {public int Index{get;}=index;public string Start{get;set;}=start;public string End{get;set;}=end;}
    private readonly ObservableCollection<PeriodDraft> rows=[];
    private readonly DatePicker monday=new(){Width=160};
    private readonly TextBox weeks=new(){Width=72,VerticalContentAlignment=VerticalAlignment.Center};
    private readonly DataGrid table=new(){AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,CanUserSortColumns=false,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0,RowHeight=29,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal};
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)};
    private readonly ValidatedDataset source;
    private readonly Func<CalendarAdjustment?,Task<string?>> save;
    private bool saving;
    public CalendarEditorWindow(ValidatedDataset source,CalendarAdjustment? current,Func<CalendarAdjustment?,Task<string?>> save)
    {
        this.source=source;this.save=save;current?.ValidateFor(source);
        Title="校历与节次";Width=580;Height=630;MinWidth=500;MinHeight=500;
        Background=TimetableView.Frozen("#F4F4F6");Foreground=TimetableView.Frozen("#232327");FontFamily=new("Segoe UI, Microsoft YaHei UI");FontSize=13;
        var root=new DockPanel{Margin=new(22)};Content=root;
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        header.Children.Add(new TextBlock{Text="校历与节次",FontSize=23,FontWeight=FontWeights.Medium,Margin=new(0,0,0,14)});
        header.Children.Add(new TextBlock{Text="按课程周次重新计算日期；单日课表保留明确日期。本机设置不会改动学校数据。",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,14)});
        var fields=new WrapPanel{Margin=new(0,0,0,16)};header.Children.Add(fields);
        AddField(fields,"第一教学周的周一",monday);AddField(fields,"教学周数",weeks);
        monday.SelectedDate=(current?.FirstTeachingMonday??source.Value.Term.FirstTeachingMonday).ToDateTime(TimeOnly.MinValue);
        weeks.Text=(current?.WeekCount??source.Value.Term.WeekCount).ToString(CultureInfo.InvariantCulture);
        AutomationProperties.SetAutomationId(monday,"CalendarMonday");AutomationProperties.SetAutomationId(weeks,"CalendarWeekCount");
        foreach(var period in current?.Periods??source.Value.Periods)rows.Add(new(period.Index,period.Start.ToString("HH:mm",CultureInfo.InvariantCulture),period.End.ToString("HH:mm",CultureInfo.InvariantCulture)));
        table.ItemsSource=rows;
        table.HorizontalGridLinesBrush=TimetableView.Frozen("#E7E7ED");table.BorderBrush=TimetableView.Frozen("#DADAE2");
        table.RowBackground=TimetableView.Frozen("#FFFFFF");table.AlternatingRowBackground=TimetableView.Frozen("#FAFAFC");
        table.Columns.Add(new DataGridTextColumn{Header="节次",Binding=new Binding(nameof(PeriodDraft.Index)),IsReadOnly=true,Width=64});
        table.Columns.Add(new DataGridTextColumn{Header="开始 · HH:mm",Binding=new Binding(nameof(PeriodDraft.Start)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        table.Columns.Add(new DataGridTextColumn{Header="结束 · HH:mm",Binding=new Binding(nameof(PeriodDraft.End)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        AutomationProperties.SetAutomationId(table,"CalendarPeriods");AutomationProperties.SetAutomationId(status,"CalendarStatus");
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(status);
        var editActions=new WrapPanel{Margin=new(0,10,0,0)};footer.Children.Add(editActions);
        Button(editActions,"添加节次","CalendarAddPeriod",()=>{if(rows.Count<24)rows.Add(new(rows.Count+1,"",""));});
        Button(editActions,"删除末节","CalendarRemovePeriod",()=>{if(rows.Count>1)rows.RemoveAt(rows.Count-1);});
        var actions=new WrapPanel{Margin=new(0,12,0,0)};footer.Children.Add(actions);
        Button? apply=null;
        apply=Button(actions,"保存本机设置","CalendarSave",async ()=>await SaveAsync(false));
        Button(actions,"恢复课表原值","CalendarReset",async ()=>await SaveAsync(true));
        Button(actions,"取消","CalendarCancel",Close);
        root.Children.Add(table);
        WindowFrame.Apply(this);
        async Task SaveAsync(bool restore)
        {
            if(saving)return;
            CalendarAdjustment? candidate=null;
            try
            {
                table.CommitEdit(DataGridEditingUnit.Cell,true);table.CommitEdit(DataGridEditingUnit.Row,true);
                if(!restore)
                {
                    if(monday.SelectedDate is not {} date || !int.TryParse(weeks.Text.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var count))
                        throw new DatasetValidationException(["请选择周一并填写 1—60 的教学周数"]);
                    var periods=ImmutableArray.CreateBuilder<Period>();
                    foreach(var row in rows)
                    {
                        if(!TimeOnly.TryParseExact(row.Start?.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start)
                            || !TimeOnly.TryParseExact(row.End?.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end))
                            throw new DatasetValidationException([$"第 {row.Index} 节时间须使用 HH:mm 格式"]);
                        periods.Add(new(row.Index,start,end));
                    }
                    candidate=new(source.Value.AccountId,source.Value.Term.Id,DateOnly.FromDateTime(date),count,periods.ToImmutable());candidate.ValidateFor(source);
                }
                saving=true;if(apply is not null)apply.IsEnabled=false;
                var error=await this.save(candidate);
                if(error is null)Close();else status.Text=error;
            }
            catch(DatasetValidationException ex){status.Text=string.Join("；",ex.Errors);}
            finally {saving=false;if(apply is not null)apply.IsEnabled=true;}
        }
    }
    private static void AddField(Panel panel,string label,FrameworkElement input)
    {var field=new StackPanel{Margin=new(0,0,22,0)};field.Children.Add(new TextBlock{Text=label,Margin=new(0,0,0,6)});field.Children.Add(input);panel.Children.Add(field);}
    private static Button Button(Panel panel,string text,string id,Action action)
    {var button=new Button{Content=text,Padding=new(12,7,12,7),Margin=new(0,0,8,0)};AutomationProperties.SetAutomationId(button,id);button.Click+=(_,_)=>action();panel.Children.Add(button);return button;}
}
