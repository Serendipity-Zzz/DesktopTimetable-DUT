using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DlutTimetable.Desktop;

internal static class SettingsChrome
{
    private static Brush Ink(string hex){var brush=(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;brush.Freeze();return brush;}
    public static void Apply(Window window)
    {
        window.Background=Ink("#F4F4F6");window.Foreground=Ink("#232327");window.FontFamily=new("Segoe UI, Microsoft YaHei UI");
        var tabs=new Style(typeof(TabControl));
        tabs.Setters.Add(new Setter(TabControl.TabStripPlacementProperty,Dock.Left));
        var items=new FrameworkElementFactory(typeof(StackPanel));items.SetValue(StackPanel.OrientationProperty,Orientation.Vertical);
        tabs.Setters.Add(new Setter(ItemsControl.ItemsPanelProperty,new ItemsPanelTemplate(items)));
        var root=new FrameworkElementFactory(typeof(DockPanel));root.SetValue(DockPanel.LastChildFillProperty,true);
        var nav=new FrameworkElementFactory(typeof(ItemsPresenter));nav.SetValue(DockPanel.DockProperty,Dock.Left);
        nav.SetValue(FrameworkElement.WidthProperty,136d);nav.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,14,0));root.AppendChild(nav);
        var sheet=new FrameworkElementFactory(typeof(Border));sheet.SetValue(Border.CornerRadiusProperty,new CornerRadius(12));
        sheet.SetValue(Border.BackgroundProperty,Brushes.White);sheet.SetValue(Border.BorderBrushProperty,Ink("#E5E5E9"));sheet.SetValue(Border.BorderThicknessProperty,new Thickness(0.7));
        var page=new FrameworkElementFactory(typeof(ContentPresenter));page.Name="PART_SelectedContentHost";page.SetBinding(ContentPresenter.ContentProperty,new Binding(nameof(TabControl.SelectedContent)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});sheet.AppendChild(page);root.AppendChild(sheet);
        tabs.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(TabControl)){VisualTree=root}));window.Resources.Add(typeof(TabControl),tabs);

        var tab=new Style(typeof(TabItem));tab.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Left));
        var row=new FrameworkElementFactory(typeof(Border));row.Name="Row";row.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));
        row.SetValue(Border.PaddingProperty,new Thickness(12,10,8,10));row.SetValue(FrameworkElement.MarginProperty,new Thickness(0,2,0,2));row.SetValue(Border.BackgroundProperty,Brushes.Transparent);
        var header=new FrameworkElementFactory(typeof(ContentPresenter));header.Name="Caption";header.SetBinding(ContentPresenter.ContentProperty,new Binding(nameof(HeaderedContentControl.Header)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});row.AppendChild(header);
        var rowTemplate=new ControlTemplate(typeof(TabItem)){VisualTree=row};
        var selected=new Trigger{Property=TabItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Border.BackgroundProperty,Ink("#E0EAFB"),"Row"));
        selected.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty,Ink("#245CA6"),"Caption"));rowTemplate.Triggers.Add(selected);
        tab.Setters.Add(new Setter(Control.TemplateProperty,rowTemplate));window.Resources.Add(typeof(TabItem),tab);

        var button=new Style(typeof(Button));button.Setters.Add(new Setter(Control.BackgroundProperty,Ink("#F5F5F7")));
        button.Setters.Add(new Setter(Control.BorderBrushProperty,Ink("#DEDEE3")));button.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0.7)));
        var body=new FrameworkElementFactory(typeof(Border));body.Name="Body";body.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));
        foreach(var property in new[]{Border.BackgroundProperty,Border.BorderBrushProperty,Border.BorderThicknessProperty,Border.PaddingProperty})
            body.SetBinding(property,new Binding(property.Name){RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        var caption=new FrameworkElementFactory(typeof(ContentPresenter));caption.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);body.AppendChild(caption);
        var buttonTemplate=new ControlTemplate(typeof(Button)){VisualTree=body};
        var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,Ink("#EDEDF1"),"Body"));buttonTemplate.Triggers.Add(hover);
        var pressed=new Trigger{Property=ButtonBase.IsPressedProperty,Value=true};pressed.Setters.Add(new Setter(Border.BackgroundProperty,Ink("#DCE7FA"),"Body"));buttonTemplate.Triggers.Add(pressed);
        button.Setters.Add(new Setter(Control.TemplateProperty,buttonTemplate));window.Resources.Add(typeof(Button),button);

        var combo=new Style(typeof(ComboBox));combo.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,5,8,5)));
        combo.Setters.Add(new Setter(Control.BorderBrushProperty,Ink("#DADAE0")));combo.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0.7)));
        combo.Setters.Add(new Setter(Control.TemplateProperty,ComboTemplate()));window.Resources.Add(typeof(ComboBox),combo);

        var toggle=new Style(typeof(CheckBox));
        var line=new FrameworkElementFactory(typeof(DockPanel));line.SetValue(DockPanel.LastChildFillProperty,true);
        var track=new FrameworkElementFactory(typeof(Border));track.Name="Track";track.SetValue(DockPanel.DockProperty,Dock.Right);
        track.SetValue(FrameworkElement.WidthProperty,30d);track.SetValue(FrameworkElement.HeightProperty,18d);track.SetValue(FrameworkElement.MarginProperty,new Thickness(12,0,0,0));
        track.SetValue(Border.BackgroundProperty,Ink("#D7D7DD"));track.SetValue(Border.CornerRadiusProperty,new CornerRadius(9));
        var knob=new FrameworkElementFactory(typeof(Ellipse));knob.Name="Knob";knob.SetValue(Shape.FillProperty,Brushes.White);knob.SetValue(FrameworkElement.WidthProperty,14d);knob.SetValue(FrameworkElement.HeightProperty,14d);
        knob.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Left);knob.SetValue(FrameworkElement.MarginProperty,new Thickness(2));track.AppendChild(knob);line.AppendChild(track);
        var label=new FrameworkElementFactory(typeof(ContentPresenter));label.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);line.AppendChild(label);
        var toggleTemplate=new ControlTemplate(typeof(CheckBox)){VisualTree=line};
        var on=new Trigger{Property=ToggleButton.IsCheckedProperty,Value=true};on.Setters.Add(new Setter(Border.BackgroundProperty,Ink("#448AF2"),"Track"));on.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right,"Knob"));toggleTemplate.Triggers.Add(on);
        var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,0.45));toggleTemplate.Triggers.Add(disabled);
        toggle.Setters.Add(new Setter(Control.TemplateProperty,toggleTemplate));window.Resources.Add(typeof(CheckBox),toggle);
    }
    private static ControlTemplate ComboTemplate()
    {
        var root=new FrameworkElementFactory(typeof(Grid));
        var plate=new FrameworkElementFactory(typeof(Border));plate.Name="Plate";plate.SetValue(Border.BackgroundProperty,Brushes.White);
        plate.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));plate.SetValue(Border.BorderBrushProperty,Ink("#DADAE0"));plate.SetValue(Border.BorderThicknessProperty,new Thickness(0.7));root.AppendChild(plate);
        var toggle=new FrameworkElementFactory(typeof(ToggleButton));toggle.SetValue(UIElement.FocusableProperty,false);
        toggle.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(ComboBox.IsDropDownOpen)){RelativeSource=new(RelativeSourceMode.TemplatedParent),Mode=BindingMode.TwoWay});
        var arrowBody=new FrameworkElementFactory(typeof(Border));arrowBody.SetValue(Border.BackgroundProperty,Brushes.Transparent);
        var arrow=new FrameworkElementFactory(typeof(Path));arrow.SetValue(Path.DataProperty,Geometry.Parse("M 0,0 L 3,3 L 6,0"));arrow.SetValue(Shape.StrokeProperty,Ink("#6E6E76"));arrow.SetValue(Shape.StrokeThicknessProperty,1.1);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right);arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);arrow.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,9,0));arrowBody.AppendChild(arrow);
        toggle.SetValue(Control.TemplateProperty,new ControlTemplate(typeof(ToggleButton)){VisualTree=arrowBody});root.AppendChild(toggle);
        var selection=new FrameworkElementFactory(typeof(ContentPresenter));selection.SetValue(UIElement.IsHitTestVisibleProperty,false);
        selection.SetValue(FrameworkElement.MarginProperty,new Thickness(9,5,28,5));selection.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        selection.SetBinding(ContentPresenter.ContentProperty,new Binding(nameof(ComboBox.SelectionBoxItem)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        selection.SetBinding(ContentPresenter.ContentTemplateProperty,new Binding(nameof(ComboBox.SelectionBoxItemTemplate)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        selection.SetBinding(ContentPresenter.ContentTemplateSelectorProperty,new Binding(nameof(ItemsControl.ItemTemplateSelector)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});root.AppendChild(selection);
        var popup=new FrameworkElementFactory(typeof(Popup));popup.Name="PART_Popup";
        popup.SetValue(Popup.PlacementProperty,PlacementMode.Bottom);popup.SetValue(Popup.AllowsTransparencyProperty,true);popup.SetValue(Popup.StaysOpenProperty,false);
        popup.SetBinding(Popup.IsOpenProperty,new Binding(nameof(ComboBox.IsDropDownOpen)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        var menu=new FrameworkElementFactory(typeof(Border));menu.SetValue(Border.BackgroundProperty,Brushes.White);menu.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));
        menu.SetValue(Border.BorderBrushProperty,Ink("#DADAE0"));menu.SetValue(Border.BorderThicknessProperty,new Thickness(0.7));menu.SetValue(Border.PaddingProperty,new Thickness(4));
        menu.SetBinding(FrameworkElement.MinWidthProperty,new Binding(nameof(FrameworkElement.ActualWidth)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        var scroll=new FrameworkElementFactory(typeof(ScrollViewer));scroll.SetValue(FrameworkElement.MaxHeightProperty,240d);scroll.SetValue(ScrollViewer.CanContentScrollProperty,true);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));menu.AppendChild(scroll);popup.AppendChild(menu);root.AppendChild(popup);
        var template=new ControlTemplate(typeof(ComboBox)){VisualTree=root};
        var focus=new Trigger{Property=UIElement.IsKeyboardFocusWithinProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,Ink("#73A4EC"),"Plate"));template.Triggers.Add(focus);
        var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,0.45));template.Triggers.Add(disabled);
        return template;
    }
}
