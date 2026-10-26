using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

public sealed class SceneLayoutTests
{
    [Theory]
    [InlineData(EdgeDirection.Left)] [InlineData(EdgeDirection.Top)] [InlineData(EdgeDirection.Right)] [InlineData(EdgeDirection.Bottom)]
    public void ConfiguredStripsAndAdjacentPanelRespectAllEdgesAndOffsets(EdgeDirection edge)
    {
        var area=new DipRect(-1600,-100,1280,900);
        foreach(var thickness in new[]{4d,6d,12d})foreach(var length in new[]{48d,64d,120d})foreach(var offset in new[]{0d,0.5,1d})
        {
            var scene=new SceneSnapshot(TimetableState.EdgeDocked,EdgePhase.Collapsed,new(new(0,0),1.2),new(0,0),edge,offset,WindowLevel.Topmost)
                {StripThickness=thickness,StripLength=length};
            var strip=SceneLayout.Strip(scene,area);var panel=SceneLayout.Panel(scene,area);
            var vertical=edge is EdgeDirection.Left or EdgeDirection.Right;
            Assert.Equal(thickness,vertical?strip.Width:strip.Height);
            Assert.Equal(length,vertical?strip.Height:strip.Width);
            foreach(var box in new[]{strip,panel})
            {
                Assert.True(box.X>=area.X && box.Y>=area.Y);
                Assert.True(box.X+box.Width<=area.X+area.Width+0.0001 && box.Y+box.Height<=area.Y+area.Height+0.0001);
            }
            Assert.Equal(5d/3,panel.Width/panel.Height,12);
            Assert.True(edge switch {
                EdgeDirection.Left=>strip.X+strip.Width==panel.X,
                EdgeDirection.Right=>Math.Abs(panel.X+panel.Width-strip.X)<0.0001,
                EdgeDirection.Top=>strip.Y+strip.Height==panel.Y,
                _=>Math.Abs(panel.Y+panel.Height-strip.Y)<0.0001});
        }
    }
    [Theory]
    [InlineData(EdgeDirection.Left)] [InlineData(EdgeDirection.Top)] [InlineData(EdgeDirection.Right)] [InlineData(EdgeDirection.Bottom)]
    public void DockedPanelAndVisibleStripStayInsideNegativeCoordinateWorkArea(EdgeDirection edge)
    {
        var area = new DipRect(-1600, -100, 1280, 900);
        var state = new SceneSnapshot(TimetableState.EdgeDocked, EdgePhase.Collapsed, new(new(0, 0), 1.2), new(10, 10), edge, 0.75, WindowLevel.Desktop);
        var panel = SceneLayout.Panel(state, area); var strip = SceneLayout.Strip(state, area);
        foreach (var r in new[] { panel, strip })
        {
            Assert.True(r.X >= area.X && r.Y >= area.Y);
            Assert.True(r.X + r.Width <= area.X + area.Width + 0.0001);
            Assert.True(r.Y + r.Height <= area.Y + area.Height + 0.0001);
        }
        Assert.Equal(5d / 3, panel.Width / panel.Height, 12);
        Assert.True(edge is EdgeDirection.Left or EdgeDirection.Right ? strip.Width == 6 && strip.Height == 64 : strip.Width == 64 && strip.Height == 6);
    }
    [Fact]
    public void OrbGeometryCannotReplaceExpandedSizeOrLeaveTheWorkArea()
    {
        var geometry = new TimetableGeometry(new(80, 80), 1);
        var scene = new SceneSnapshot(TimetableState.Ball, EdgePhase.Collapsed, geometry, new(-5000, 8000), EdgeDirection.Right, 0.5, WindowLevel.Desktop);
        var ball = SceneLayout.Ball(scene, new(0, 0, 1920, 1080));
        Assert.Equal(44, ball.Width); Assert.Equal(0, ball.X); Assert.Equal(1036, ball.Y);
        Assert.Equal(geometry, scene.Expanded);
    }
}
