using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Windows;

internal interface INativeCommandInput
{
    void SetNativePressed(bool value);
    void InvokeNativeClick();
}
internal sealed class NativeCommandButton : Button,INativeCommandInput
{
    public NativeCommandButton()=>OverridesDefaultStyle=true;
    public void SetNativePressed(bool value)=>IsPressed=value;
    public void InvokeNativeClick()=>OnClick();
}
internal sealed class NativePinButton : ToggleButton,INativeCommandInput
{
    public NativePinButton()=>OverridesDefaultStyle=true;
    public void SetNativePressed(bool value)=>IsPressed=value;
    public void InvokeNativeClick()=>OnClick();
}

internal static class CommandChrome
{
    [ThreadStatic] private static TextProtectionSolver? protection;
    public static void Attach(ButtonBase button,int index)
    {
        button.FontFamily=new("Segoe UI");button.FontWeight=FontWeights.Medium;
        button.Content=index==3?new TextBlock {Text="Pin"}:new CommandGlyph(index);
        var plate=new FrameworkElementFactory(typeof(Border));plate.Name="Plate";
        plate.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
        plate.SetValue(Border.BorderThicknessProperty,new Thickness(0.6));
        plate.SetBinding(Border.BackgroundProperty,new Binding(nameof(Control.Background)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        plate.SetBinding(Border.BorderBrushProperty,new Binding(nameof(Control.BorderBrush)){RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        var content=new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        plate.AppendChild(content);
        var template=new ControlTemplate(button.GetType()){VisualTree=plate};
        AddTrigger(UIElement.IsMouseOverProperty,Color.FromArgb(150,115,164,236));
        if(button is ToggleButton)
        {
            AddTrigger(ToggleButton.IsCheckedProperty,Color.FromArgb(190,77,144,248),fill:true);
            AddBorderTrigger(ToggleButton.IsCheckedProperty,Color.FromArgb(245,77,144,248));
        }
        AddTrigger(ButtonBase.IsPressedProperty,Color.FromArgb(220,87,148,238),fill:true);
        button.Template=template;
        Update(button,new SolidColorBrush(GlassAppearance.DarkInk));
        void AddTrigger(DependencyProperty property,Color color,bool fill=false)
        {
            var trigger=new Trigger{Property=property,Value=true};
            var brush=new SolidColorBrush(color);brush.Freeze();
            trigger.Setters.Add(new Setter(fill?Border.BackgroundProperty:Border.BorderBrushProperty,brush,"Plate"));template.Triggers.Add(trigger);
        }
        void AddBorderTrigger(DependencyProperty property,Color color)
        {
            var trigger=new Trigger{Property=property,Value=true};
            var brush=new SolidColorBrush(color);brush.Freeze();
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty,brush,"Plate"));template.Triggers.Add(trigger);
        }
    }
    public static void Update(ButtonBase button,Brush foreground,double opacity=0.88)
    {
        var lightInk=foreground is SolidColorBrush ink && TextContrast.Luminance(ink.Color)>0.5;
        var color=foreground is SolidColorBrush source?source.Color:GlassAppearance.DarkInk;
        var guard=(protection??=new()).Solve(color,opacity);
        button.Foreground=Frozen(guard.Ink);
        button.Opacity=opacity;
        button.Background=Frozen(Color.FromArgb((byte)Math.Round(guard.Opacity*255),guard.Veil.R,guard.Veil.G,guard.Veil.B));
        button.BorderBrush=Frozen(lightInk?Color.FromArgb(36,255,255,255):Color.FromArgb(20,0,0,0));
    }
    private static Brush Frozen(Color color){var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
}

internal sealed class CommandGlyph : FrameworkElement
{
    public static readonly DependencyProperty InkProperty=DependencyProperty.Register(nameof(Ink),typeof(Brush),typeof(CommandGlyph),new FrameworkPropertyMetadata(Brushes.Black,FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Ink {get=>(Brush)GetValue(InkProperty);set=>SetValue(InkProperty,value);}
    private readonly int kind;
    public CommandGlyph(int kind)
    {
        this.kind=kind;
        var owner=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(ButtonBase),1);
        SetBinding(InkProperty,new Binding(nameof(Control.Foreground)){RelativeSource=owner});
        SetBinding(WidthProperty,new Binding(nameof(Control.FontSize)){RelativeSource=owner});
        SetBinding(HeightProperty,new Binding(nameof(Control.FontSize)){RelativeSource=owner});
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth/16,ActualHeight/16));
        var pen=new Pen(Ink,1.25){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        if(kind==0)
        {
            dc.DrawEllipse(null,pen,new(8,8),4.5,4.5);dc.DrawEllipse(null,pen,new(8,8),1.6,1.6);
            for(var i=0;i<8;i++){var angle=i*Math.PI/4;dc.DrawLine(pen,new(8+Math.Cos(angle)*4.5,8+Math.Sin(angle)*4.5),new(8+Math.Cos(angle)*6,8+Math.Sin(angle)*6));}
        }
        else if(kind==1)
        {
            dc.DrawRoundedRectangle(null,pen,new(2,2.5,12,11),2,2);
            dc.DrawLine(pen,new(10.5,3),new(10.5,13));dc.DrawLine(pen,new(4,8),new(8.5,8));
            dc.DrawLine(pen,new(6.5,6),new(8.5,8));dc.DrawLine(pen,new(6.5,10),new(8.5,8));
        }
        else
        {
            dc.DrawEllipse(null,pen,new(8,8),5.5,5.5);dc.DrawEllipse(Ink,null,new(8,8),1.2,1.2);
        }
        dc.Pop();
    }
}
