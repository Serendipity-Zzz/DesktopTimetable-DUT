using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Threading;
using DlutTimetable.Application;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Windows;

public sealed class NativeHostGroup : IDisposable
{
    private readonly WindowStateController scene;
    private readonly Action<AppCommandKind> command;
    private readonly Dispatcher dispatcher;
    private readonly HwndSource observer;
    private static int CurrentSession(){using var process=System.Diagnostics.Process.GetCurrentProcess();return process.SessionId;}
    private readonly PresentationAvailability availability=new(CurrentSession());
    internal nint ObserverHandle=>observer.Handle;
    internal bool SessionNotificationsRegistered {get;private set;}
    internal bool PresentationPaused=>availability.Paused;
    private readonly DesktopShellPlacement shellPlacement;
    private DesktopLayer panel = null!, strip = null!, ball = null!;
    private BallTransitionSurface morph=null!;
    private DipRect lastPanelPhysical, controlPanelPhysical;
    private DesktopLayer[] buttons = [];
    private List<DesktopLayer> all = [];
    private readonly TimetableView view;
    private readonly LiveBackdropSurface liveBackdrop=new();
    private readonly LiveLiquidEdge liquidEdge;
    private bool panelDragging;
    private readonly ScaleTransform ballScale=new(1,1);
    public RefractionStatistics RefractionStatistics=>liquidEdge.Statistics;
    internal nint RefractionTextureForProof=>liquidEdge.RenderedTexture;
    internal IReadOnlyList<nint> RefractionSourcesForProof=>liquidEdge.SourceWindows;
    public MaterialController Material { get; }
    private bool disposed, applying, recoveryQueued, placementQueued, projectionPending;
    private WindowLevel? lastLevel;
    private RectangleGeometry? clip;
    private long motionGeneration = -1;
    public string? DisplayId { get; private set; }
    public string? BallDisplayId { get; private set; }
    public TimetableView View => view;
    public bool ReduceMotion { get; set; }
    public event Action<Exception>? Failed;
    public event Action<string>? MotionDiagnostic;

    public NativeHostGroup(WindowStateController scene, Action<AppCommandKind> command, string? displayId, string? ballDisplayId = null)
        :this(scene,command,displayId,ballDisplayId,0,0){}
    internal NativeHostGroup(WindowStateController scene,Action<AppCommandKind> command,string? displayId,string? ballDisplayId,
        nint controlledSource,uint controlledProcess)
    {
        this.scene = scene; this.command = command; DisplayId = displayId; BallDisplayId = ballDisplayId ?? displayId;
        dispatcher = Dispatcher.CurrentDispatcher;
        shellPlacement = new(QueuePlacement);
        view = new TimetableView();
        view.ChromeChanged+=()=> {foreach(var layer in buttons)CommandChrome.Update((ButtonBase)layer.Content,view.ChromeForeground,view.ForegroundOpacity);};
        Material = new(view);
        liquidEdge=new(null,controlledSource,controlledProcess);
        liquidEdge.Failed+=()=>Material.ReportRefractionFailure();
        liquidEdge.Changed+=()=>Material.UpdateRefractionStatus();
        Material.UseLiveBackdrop((bounds,parameters)=>
        {
            var owner=scene.Current.EffectiveLevel==WindowLevel.Desktop?NativeMethods.FindWindow("Progman",null):0;
            var topmost=scene.Current.EffectiveLevel==WindowLevel.Topmost;
            liveBackdrop.Show(bounds,parameters,bounds.Width/780,owner,topmost);
            if(parameters.Kind==MaterialKind.LiquidEdge && !panelDragging && scene.Current.BallMotion==BallMotionPhase.None
                && scene.Current.EdgePhase is not (EdgePhase.Opening or EdgePhase.Closing))liquidEdge.TryShow(bounds,parameters,owner,topmost);
            else {liquidEdge.Dispose();if(parameters.Kind!=MaterialKind.LiquidEdge)liquidEdge.ResetFailure();}
        },()=>{liquidEdge.Dispose();liveBackdrop.Dispose();},()=>liquidEdge.Statistics);
        CreateLayers();
        observer = new HwndSource(new HwndSourceParameters("Dlut shell observer")
            { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = 0x80 });
        observer.AddHook(Observe);
        EnsureSessionNotifications();
        scene.SetPresentationPaused(availability.Paused);
        scene.Changed += OnScene;
    }
    private void CreateLayers()
    {
        panel = new("周课表", new Viewbox { Stretch = Stretch.Fill, Child = view });
        var labels = new[] { "设置", "侧边隐藏", "收成悬浮球", "固定课表 / 允许拖动" };
        var actions = new[] { AppCommandKind.OpenSettings, AppCommandKind.DockToEdge, AppCommandKind.CollapseToBall, AppCommandKind.TogglePin };
        buttons = Enumerable.Range(0, 4).Select(i =>
        {
            ButtonBase button = i == 3 ? new NativePinButton() : new NativeCommandButton();
            button.FontSize = i == 3 ? 11 : 14;CommandChrome.Attach(button,i);
            CommandChrome.Update(button,view.ChromeForeground,view.ForegroundOpacity);
            button.Padding = new(0); button.Focusable = false;
            button.ToolTip=labels[i];
            AutomationProperties.SetName(button, labels[i]); AutomationProperties.SetHelpText(button, labels[i]);
            var action = actions[i];
            if (button is ToggleButton pin)
            {
                pin.Checked += (_, _) => { if (!applying) command(action); };
                pin.Unchecked += (_, _) => { if (!applying) command(action); };
            }
            else button.Click += (_, _) => command(action);
            return new DesktopLayer("按钮" + i, button);
        }).ToArray();
        strip = new("侧边条", new Border { Background = TimetableView.Frozen("#789C9D"), CornerRadius = new(3) });
        var icon = new Border { Background = TimetableView.Frozen("#E8F0EF"), BorderBrush = TimetableView.Frozen("#8AA6A8"),
            BorderThickness = new(1), CornerRadius = new(22), Child = new TextBlock
            { Text = "▦", FontSize = 22, Foreground = TimetableView.Frozen("#315763"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        icon.RenderTransform=ballScale;icon.RenderTransformOrigin=new(0.5,0.5);
        ball = new("悬浮球", icon) { IsBall = true, DragEnabled = true };
        AutomationProperties.SetName(icon, "单击展开课表，拖动移动悬浮球");
        morph=new(TimeProvider.System);
        morph.Frame+=ApplyBallFrame;
        morph.Completed+=generation=>
        {
            MotionDiagnostic?.Invoke($"BALL_COMPLETE request={generation} current={scene.Current.Generation} phase={scene.Current.BallMotion} applying={applying}");
            if(!disposed)scene.CompleteBallMotion(generation);
        };
        morph.Trace+=value=>MotionDiagnostic?.Invoke(value);
        morph.Failed+=ex=> { scene.StabilizeBallMotion();Failed?.Invoke(ex); };
        all = [panel, .. buttons, strip, ball, morph.Layer];
        foreach (var layer in all)
        {
            layer.MouseEnter += (_, _) => QueueHover(); layer.MouseLeave += (_, _) => QueueHover();
            layer.MouseRegionChanged += () => { if (!disposed) scene.Hover(PointerInside()); };
            layer.ShellChanged += QueueRecovery;
            layer.GestureTrace+=value=>MotionDiagnostic?.Invoke($"NATIVE {layer.Role} {value} state={scene.Current.State} motion={scene.Current.BallMotion}");
            layer.CaptureChanged += value =>
            {
                if(layer==panel && !value && panelDragging){panelDragging=false;QueuePlacement();}
                if(scene.Current.BallMotion==BallMotionPhase.None)scene.SetCaptured(value);
                else if((layer==ball && morph.ControlIsBall) || (layer==buttons[2] && !morph.ControlIsBall))morph.SetControlCaptured(value);
            };
        }
        panel.Dragged += (x, y, stable) => Move(x, y, false, stable);
        ball.Dragged += (x, y, stable) => Move(x, y, true, stable);
        ball.BallClicked += () => scene.OrbClick();
    }
    private void Move(int x, int y, bool orb, bool stable)
    {
        if(!orb)panelDragging=!stable;
        var display = DisplayLayout.AtPhysical(x, y);
        var current = scene.Current;
        if (orb)
        {
            BallDisplayId = display.Id;
            var center = new DipPoint(x / display.DpiScale + current.BallDiameter / 2, y / display.DpiScale + current.BallDiameter / 2);
            scene.SetGeometry(current.Expanded, center, stable);
        }
        else
        {
            DisplayId = display.Id;
            scene.SetGeometry(new(new(x / display.DpiScale, y / display.DpiScale), current.Expanded.Scale), current.BallCenter, stable);
        }
    }
    private void OnScene(SceneSnapshot state, bool stable)
    {
        if(disposed)return;
        if(applying){projectionPending=true;return;}
        try { Apply(); } catch (Exception ex) { Failed?.Invoke(ex); }
    }
    public void Show() => Apply();
    public void ConfigureMaterial(MaterialParameters parameters)
    {
        if(disposed || scene.Current.Exiting)return;
        var previousBackdrop=liveBackdrop.Handle;var previousEdge=liquidEdge.Handle;
        Material.Configure(parameters);
        var state=scene.Current;
        if(applying || state.BallMotion!=BallMotionPhase.None || state.EdgePhase is EdgePhase.Opening or EdgePhase.Closing)
        {QueuePlacement();return;}
        if(availability.Paused || !state.PanelVisible){Material.Release();return;}
        if(panel.Handle==0 || !panel.IsVisible){Apply();return;}
        var display=DisplayLayout.Select(DisplayId);var rect=SceneLayout.Panel(state,display.WorkArea);
        Material.Request(display,Physical(rect,display),rect.Width/780);
        // Optical changes update retained surfaces. Only newly created HWNDs
        // need placement; unchanged foreground/buttons keep their Z-order.
        if(liveBackdrop.Visible && previousBackdrop!=liveBackdrop.Handle)
            NativeMethods.Check(NativeMethods.SetWindowPos(liveBackdrop.Handle,panel.Handle,0,0,0,0,
                NativeMethods.NoActivate|NativeMethods.NoMove|NativeMethods.NoSize),"Place new live material");
        if(previousEdge!=liquidEdge.Handle)liquidEdge.PlaceBehind(panel.Handle);
        Material.UpdateRefractionStatus();
    }
    public void RetryMaterial()
    {
        if(disposed || scene.Current.Exiting)return;
        liquidEdge.ResetFailure();Material.InvalidateSource();Apply();
    }
    public bool TryRecoverDesktop()
    {
        if(disposed || applying || scene.Current.Exiting)return false;
        try
        {
            EnsureSessionNotifications();
            liquidEdge.ResetFailure();Material.InvalidateSource();scene.StabilizeBallMotion();
            if(all.Any(w=>w.IsClosed || w.Handle!=0 && !NativeMethods.IsWindow(w.Handle)))Rebuild();
            var display=DisplayLayout.Select(DisplayId);DisplayId=display.Id;
            var current=scene.Current;
            if(current.State==TimetableState.EdgeDocked)
                scene.Configure(current.Edge,current.EdgeOffset,current.EdgeLevel,current.Expanded.Scale);
            else scene.SetGeometry(current.Expanded.Fit(display.WorkArea),current.BallCenter,true);
            Apply();return true;
        }
        catch(Exception ex){Failed?.Invoke(ex);return false;}
    }
    public bool SaveOwnFrame(string path)
    {
        if(!panel.IsVisible || !scene.Current.CommandsVisible || scene.Current.BallMotion!=BallMotionPhase.None)return false;
        var drawing=new DrawingVisual();
        using(var dc=drawing.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(view){Stretch=Stretch.Fill},null,new(0,0,780,468));
            var widths=new[]{24d,24,24,36};var offset=0d;
            for(var i=0;i<buttons.Length;i++)
            {
                if(buttons[i].IsVisible)dc.DrawRectangle(new VisualBrush((Visual)buttons[i].Content){Stretch=Stretch.Fill},null,new(650+offset,10,widths[i],24));
                offset+=widths[i]+4;
            }
        }
        var bitmap=new RenderTargetBitmap(1170,702,144,144,PixelFormats.Pbgra32);bitmap.Render(drawing);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(path);encoder.Save(output);return true;
    }
    public static void ApplyPopupLevel(Window popup, WindowLevel level)
    {
        popup.Topmost = level == WindowLevel.Topmost;
        if (!popup.IsVisible || level != WindowLevel.Topmost) return;
        var handle = new WindowInteropHelper(popup).Handle;
        NativeMethods.Check(NativeMethods.SetWindowPos(handle, -1, 0, 0, 0, 0,
            NativeMethods.NoActivate | NativeMethods.NoMove | NativeMethods.NoSize), "Project explicit popup level");
    }
    private nint Observe(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if(disposed)return 0;
        if(availability.Observe(message,wparam,lparam))
        {
            scene.SetPresentationPaused(availability.Paused);
            if(availability.Paused)Apply();
            else {view.RefreshClock();QueueRecovery();}
        }
        if(message==0x218){handled=true;return 1;}
        if(message==0x1A || message==0x7E)MotionDiagnostic?.Invoke($"SYSTEM_MESSAGE observer kind={message:X} value={wparam}");
        if(!SessionNotificationsRegistered && message==0x1A)EnsureSessionNotifications();
        if (message == 0x1A || message == 0x7E) Material.InvalidateSource();
        if (message == NativeMethods.RegisterWindowMessage("TaskbarCreated") || message == 0x7E || message == 0x1A) QueueRecovery();
        return 0;
    }
    private void EnsureSessionNotifications()
    {
        if(SessionNotificationsRegistered)return;
        SessionNotificationsRegistered=WTSRegisterSessionNotification(observer.Handle,0);
        // Registration precedes the snapshot so a lock already in effect at
        // background startup is observed even if this process missed its event.
        if(SessionNotificationsRegistered && NativeSessionSnapshot.Read(availability.SessionId) is {} snapshot)
        {
            availability.ApplySnapshot(snapshot);
            scene.SetPresentationPaused(availability.Paused);
        }
    }
    private void QueueHover() => dispatcher.BeginInvoke(() => { if (!disposed) scene.Hover(PointerInside()); }, DispatcherPriority.Input);
    public bool PointerInside()
    {
        if(availability.Paused)return false;
        NativeMethods.GetCursorPos(out var point);
        var hit = NativeMethods.WindowFromPoint(point);
        return all.Any(w => w.Handle != 0 && w.IsVisible && w.Handle == hit);
    }
    private void QueueRecovery()
    {
        if (disposed || recoveryQueued || applying) return;
        recoveryQueued = true;
        dispatcher.BeginInvoke(() =>
        {
            recoveryQueued = false;
            TryRecoverDesktop();
        }, DispatcherPriority.Background);
    }
    private void Rebuild()
    {
        liveBackdrop.Dispose();
        morph.Stop();
        CancelMotion();
        if (panel.Content is Viewbox box) box.Child = null;
        foreach (var layer in all)
        {
            layer.Content = null;
            if (!layer.IsClosed) layer.Close();
        }
        all.Clear(); CreateLayers(); lastLevel = null;
    }
    private void QueuePlacement()
    {
        if(disposed || placementQueued)return;
        placementQueued=true;
        dispatcher.BeginInvoke(()=>
        {
            placementQueued=false;
            if(disposed)return;
            try { Apply(); }catch(Exception ex){Failed?.Invoke(ex);}
        },DispatcherPriority.Background);
    }
    private static DipRect Physical(DipRect rect,DisplayLayout display)=>new(rect.X*display.DpiScale,rect.Y*display.DpiScale,rect.Width*display.DpiScale,rect.Height*display.DpiScale);
    private void PositionPhysical(DesktopLayer layer,DipRect rect,nint owner)
    {
        layer.Ensure();layer.SetOwner(owner);
        if(!layer.IsVisible)
        {
            var display=DisplayLayout.AtPhysical((int)Math.Round(rect.X),(int)Math.Round(rect.Y));
            layer.Width=rect.Width/display.DpiScale;layer.Height=rect.Height/display.DpiScale;
            layer.Opacity=0;layer.Show();
        }
        // One native geometry update per presentation frame. WM_SIZE updates WPF's size;
        // setting Width, then Height, then SetWindowPos caused three resize/layout paths.
        NativeMethods.Check(NativeMethods.SetWindowPos(layer.Handle,0,(int)Math.Round(rect.X),(int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width),(int)Math.Round(rect.Height),NativeMethods.NoActivate|NativeMethods.NoZOrder),"Position physical motion layer");
    }
    private void ApplyBallFrame(BallMotionPose pose)
    {
        if(disposed || scene.Current.Exiting || scene.Current.BallMotion==BallMotionPhase.None)return;
        var owner=NativeMethods.FindWindow("Progman",null);
        morph.Layer.SetTransparent(true);PositionPhysical(morph.Layer,morph.HostBounds,owner);
        liveBackdrop.FollowMotion(pose);
        morph.Layer.Opacity=1;
        // The live retarget control stays fully opaque; only the non-interactive orb fades.
        // Updating a layered control's whole-window alpha during a press can lose native input.
        ball.Opacity=morph.ControlIsBall ? 1 : pose.BallOpacity;
        if(ballScale.ScaleX!=pose.BallScale){ballScale.ScaleX=pose.BallScale;ballScale.ScaleY=pose.BallScale;}
    }
    private void Position(DesktopLayer layer, DipRect rect, DisplayLayout display, nint owner)
    {
        layer.Ensure(); layer.SetOwner(owner);
        layer.Width = rect.Width; layer.Height = rect.Height;
        if (!layer.IsVisible) { layer.Opacity = 0; layer.Show(); }
        NativeMethods.Check(NativeMethods.SetWindowPos(layer.Handle, 0, (int)Math.Round(rect.X * display.DpiScale),
            (int)Math.Round(rect.Y * display.DpiScale), (int)Math.Round(rect.Width * display.DpiScale),
            (int)Math.Round(rect.Height * display.DpiScale), NativeMethods.NoActivate | NativeMethods.NoZOrder), "Position desktop layer");
    }
    private void Apply()
    {
        if (disposed || applying) return;
        applying = true;
        try
        {
            var state = scene.Current;
            if (state.Exiting || availability.Paused)
            { morph.Stop();Material.Release(); CancelMotion();view.Interactive=false;panelDragging=false;
                if(availability.Paused)scene.StabilizeBallMotion();foreach (var w in all) w.Hide();return; }
            var display = DisplayLayout.Select(DisplayId); DisplayId = display.Id;
            var ballDisplay = DisplayLayout.Select(BallDisplayId); BallDisplayId = ballDisplay.Id;
            var level = state.EffectiveLevel;
            if (lastLevel == WindowLevel.Topmost && level != WindowLevel.Topmost)
                foreach (var w in all.Where(w => w.Handle != 0 && NativeMethods.IsWindow(w.Handle)))
                    NativeMethods.Check(NativeMethods.SetWindowPos(w.Handle, -2, 0, 0, 0, 0,
                        NativeMethods.NoActivate | NativeMethods.NoMove | NativeMethods.NoSize), "Remove TOPMOST before exit");
            var owner = level == WindowLevel.Topmost ? 0 : NativeMethods.FindWindow("Progman", null);
            if (level == WindowLevel.Desktop && owner == 0) throw new InvalidOperationException("桌面宿主暂不可用，可从托盘恢复。");
            if (owner != 0) shellPlacement.Attach(owner);
            // Hidden layers are part of the group too. Leaving them ownerless after S3 lets the shell
            // choose a hidden command HWND when Win+D restores desktop focus.
            foreach (var layer in all.Where(w => w.Handle != 0 && NativeMethods.IsWindow(w.Handle))) layer.SetOwner(owner);
            var active = new List<DesktopLayer>();
            var rect = SceneLayout.Panel(state, display.WorkArea);
            var ballRect=SceneLayout.Ball(state,ballDisplay.WorkArea);
            var moving=state.BallMotion!=BallMotionPhase.None;
            if(moving)
            {
                var toBall=state.BallMotion==BallMotionPhase.ToBall;
                var source=toBall && lastPanelPhysical.Width>0 ? lastPanelPhysical : Physical(rect,display);
                if(!morph.Running)
                {
                    controlPanelPhysical=source;
                    // Prepare the transparent foreground before taking its own texture, including
                    // expansion from a stable ball where the previous surface has been released.
                    Material.Request(display,source,source.Width/(780*display.DpiScale));
                }
                morph.Prepare(()=>BallTransitionSurface.Capture(view,state.Expanded.Scale),source,Physical(ballRect,ballDisplay),
                    toBall,state.Generation,ReduceMotion || !SystemParameters.ClientAreaAnimation);
                morph.Layer.SetTransparent(true);PositionPhysical(morph.Layer,morph.HostBounds,owner);
                active.Add(morph.Layer);
            }
            else morph.Stop();
            if (state.PanelVisible && !moving)
            {
                Position(panel, rect, display, owner); panel.SetTransparent(state.State == TimetableState.Locked);
                panel.DragEnabled = state.State == TimetableState.Movable;
                panel.ContentScale = rect.Width / 780;
                view.Interactive = state.State == TimetableState.Movable || state.State == TimetableState.EdgeDocked && state.CommandsVisible;
                active.Add(panel);
                lastPanelPhysical=Physical(rect,display);
                Material.Request(display, new(rect.X*display.DpiScale,rect.Y*display.DpiScale,
                    rect.Width*display.DpiScale,rect.Height*display.DpiScale),rect.Width/780);
            }
            else if(moving)
            {
                panel.Hide();CancelMotion();panel.DragEnabled=false;view.Interactive=false;
            }
            else
            {
                panel.Hide();CancelMotion();view.Interactive=false;
                Material.Release();
            }
            if (state.CommandsVisible)
            {
                var factor = rect.Width / 780;
                var offset = 0d; var widths = new[] { 24d, 24, 24, 36 };
                for (var i = 0; i < 4; i++)
                {
                    Position(buttons[i], new(rect.X + (650 + offset) * factor, rect.Y + 10 * factor,
                        widths[i] * factor, 24 * factor), display, owner);
                    ((ButtonBase)buttons[i].Content).FontSize = (i == 3 ? 11 : 14) * factor;
                    if (i == 3)
                    {
                        var pin = (ToggleButton)buttons[i].Content;
                        pin.IsChecked = state.State == TimetableState.Locked;
                        var help=state.State switch
                        {
                            TimetableState.Locked=>"允许拖动课表（当前固定）",
                            TimetableState.Movable=>"固定课表（当前允许拖动）",
                            _=>"回到原展开位置并固定（退出侧边模式）"
                        };
                        pin.ToolTip=help;AutomationProperties.SetHelpText(pin,help);
                    }
                    offset += widths[i] + 4; active.Add(buttons[i]);
                }
            }
            else
            {
                // Keep the active retarget control's HWND visible throughout its click sequence.
                // Hide/show during the first mouse-up can discard WPF's second rapid press.
                for(var i=0;i<buttons.Length;i++)
                    if(!moving || morph.ControlIsBall || i!=2)buttons[i].Hide();
                if(moving && !morph.ControlIsBall)
                {
                    var factor=controlPanelPhysical.Width/780;
                    PositionPhysical(buttons[2],new(controlPanelPhysical.X+706*factor,controlPanelPhysical.Y+10*factor,24*factor,24*factor),owner);
                    active.Add(buttons[2]);
                }
            }
            if (state.State == TimetableState.EdgeDocked)
            { Position(strip, SceneLayout.Strip(state, display.WorkArea), display, owner); active.Add(strip); }
            else strip.Hide();
            if (state.State == TimetableState.Ball || moving)
            {
                Position(ball,ballRect,ballDisplay,owner);ball.SetTransparent(moving && !morph.ControlIsBall);
                ball.DragEnabled=!moving;active.Add(ball);
            }
            else ball.Hide();
            if(!moving){ballScale.ScaleX=1;ballScale.ScaleY=1;}
            // The layer order is applied only on projection events, never by a recurring Z-order timer.
            // Above the actual icon host, below the nearest ordinary application. HWND_BOTTOM alone
            // can leave a visible ball under SHELLDLL_DefView after Show Desktop changes shell order.
            var ownHandles=all.Select(w=>w.Handle).ToHashSet();ownHandles.Add(liveBackdrop.Handle);ownHandles.Add(liquidEdge.Handle);ownHandles.Add(liquidEdge.CaptureHandle);
            var anchor = level == WindowLevel.Topmost ? -1 : shellPlacement.Anchor(ownHandles);
            foreach (var w in level == WindowLevel.Topmost ? active : active.AsEnumerable().Reverse())
            {
                NativeMethods.Check(NativeMethods.SetWindowPos(w.Handle, anchor, 0, 0, 0, 0,
                    NativeMethods.NoActivate | NativeMethods.NoMove | NativeMethods.NoSize), "Apply desktop group level");
                if (level != WindowLevel.Topmost) anchor = w.Handle;
            }
            if(liveBackdrop.Visible)
                NativeMethods.Check(NativeMethods.SetWindowPos(liveBackdrop.Handle,moving?morph.Layer.Handle:panel.Handle,0,0,0,0,
                    NativeMethods.NoActivate|NativeMethods.NoMove|NativeMethods.NoSize),"Place live glass behind foreground");
            liquidEdge.PlaceBehind(moving?morph.Layer.Handle:panel.Handle);
            Material.UpdateRefractionStatus();
            foreach (var w in active) if(!moving || w!=ball && w!=panel)w.Opacity = 1;
            if(moving)morph.Activate();
            lastLevel = level;
            if (state.State == TimetableState.EdgeDocked && state.EdgePhase is EdgePhase.Opening or EdgePhase.Closing)
                AnimateEdge(state, rect);
            else if (state.EdgePhase is EdgePhase.Expanded or EdgePhase.WaitingClose) { CancelMotion(); panel.Clip = null; }
        }
        finally
        {
            applying=false;
            if(projectionPending){projectionPending=false;QueuePlacement();}
        }
    }
    private void AnimateEdge(SceneSnapshot state, DipRect bounds)
    {
        if (motionGeneration == state.Generation) return;
        var full = new Rect(0, 0, bounds.Width, bounds.Height);
        var empty = state.Edge switch
        {
            EdgeDirection.Left => new Rect(0, 0, 0, bounds.Height),
            EdgeDirection.Right => new Rect(bounds.Width, 0, 0, bounds.Height),
            EdgeDirection.Top => new Rect(0, 0, bounds.Width, 0),
            _ => new Rect(0, bounds.Height, bounds.Width, 0)
        };
        var current = clip?.Rect ?? (state.EdgePhase == EdgePhase.Opening ? empty : full);
        if(clip is null)
        {
            clip=new RectangleGeometry(current);
            clip.Changed+=(_,_)=>
            {
                if(clip is null || !liveBackdrop.Visible)return;
                var value=clip.Rect;
                liveBackdrop.Reveal(value.Left/panel.Width,value.Top/panel.Height,value.Right/panel.Width,value.Bottom/panel.Height);
            };
        }
        clip.BeginAnimation(RectangleGeometry.RectProperty, null); clip.Rect = current;
        panel.Clip = clip; motionGeneration = state.Generation;
        var animation = new RectAnimation(current, state.EdgePhase == EdgePhase.Opening ? full : empty,
            TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation && !ReduceMotion ? 210 : 100))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var generation = state.Generation;
        animation.Completed += (_, _) => { if (!disposed) scene.CompleteMotion(generation); };
        clip.BeginAnimation(RectangleGeometry.RectProperty, animation);
    }
    private void CancelMotion()
    { clip?.BeginAnimation(RectangleGeometry.RectProperty, null); clip = null; motionGeneration = -1;liveBackdrop.Reveal(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        scene.Changed -= OnScene; CancelMotion();
        morph.Stop();
        Material.Dispose();
        shellPlacement.Dispose();
        foreach (var window in all) if (!window.IsClosed) window.Close();
        all.Clear();
        if(SessionNotificationsRegistered){WTSUnRegisterSessionNotification(observer.Handle);SessionNotificationsRegistered=false;}
        observer.RemoveHook(Observe); observer.Dispose();
    }
    [System.Runtime.InteropServices.DllImport("wtsapi32.dll",SetLastError=true)]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint window,uint flags);
    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint window);
}
