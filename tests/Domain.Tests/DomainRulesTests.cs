using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

public sealed class DomainRulesTests
{
    [Theory]
    [InlineData(1, 1)] [InlineData(2, 0)] [InlineData(3, 1)] [InlineData(4, 0)] [InlineData(8, 1)] [InlineData(9, 0)]
    public void DiscreteWeeksRemainDiscrete(int week, int count) =>
        Assert.Equal(count, TimetableProjector.Week(ValidatedDataset.Create(Samples.Dataset), week).Occurrences.Count);

    [Fact]
    public void DistinctClassesWithSameNameAreNotMergedAndConflictsRemainVisible()
    {
        var d = Samples.Dataset;
        d = d with { Meetings = [d.Meetings[0], d.Meetings[0] with { Id = "m2", CourseId = "class-b" }] };
        var view = TimetableProjector.Week(ValidatedDataset.Create(d), 1);
        Assert.Equal(2, d.GetCourses().Count());
        Assert.Equal(2, view.Occurrences.Count);
        Assert.All(view.Occurrences, o => Assert.True(o.HasConflict));
    }
    [Theory]
    [InlineData(0)] [InlineData(19)] [InlineData(-1)]
    public void RejectsInvalidWeeks(int invalid)
    {
        var d = Samples.Dataset;
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with
            { Meetings = [d.Meetings[0] with { Weeks = [invalid] }] }));
    }
    [Fact]
    public void ValidatesCoverageAndVerifiedEmpty()
    {
        var d = Samples.Dataset;
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with { Meetings = [] }));
        Assert.Empty(ValidatedDataset.Create(d with { Meetings = [], VerifiedEmpty = true }).Value.Meetings);
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with { Coverage = new("TERM", [1]) }));
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d, "other-account"));
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d, expectedTerm: "other-term"));
    }
    [Fact]
    public void SupportsFourPeriodSpanAndFourteenPeriodSchedule()
    {
        var d = Samples.Dataset;
        var view = TimetableProjector.Week(ValidatedDataset.Create(d with
            { Meetings = [d.Meetings[0] with { PeriodStart = 9, PeriodEnd = 12 }] }), 1);
        var occurrence = Assert.Single(view.Occurrences);
        Assert.Equal(new TimeOnly(19, 45), occurrence.End);
        Assert.Equal(4, occurrence.Meeting.PeriodEnd - occurrence.Meeting.PeriodStart + 1);
        var twelve = ValidatedDataset.Create(d with { Periods = [.. d.Periods.Take(12)] });
        Assert.Equal(12, twelve.Value.Periods.Length);
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with
            { Meetings = [d.Meetings[0] with { PeriodEnd = 15 }] }));
    }
    [Fact]
    public void MissingParametersAndMalformedRecordsAreRejected()
    {
        var d = Samples.Dataset;
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with { Periods = [] }));
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with { Term = d.Term with { FirstTeachingMonday = new(2026, 9, 1) } }));
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with { Meetings = [null!] }));
        Assert.Throws<DatasetValidationException>(() => ValidatedDataset.Create(d with { Periods = [null!] }));
    }
    [Fact]
    public void ShanghaiCalendarIgnoresHostTimezoneAndFloorsBeforeTerm()
    {
        Assert.Equal(new DateOnly(2026, 10, 23), SchoolCalendar.ShanghaiDate(new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero)));
        Assert.Equal(0, SchoolCalendar.TeachingWeek(Samples.Dataset.Term, new(2026, 8, 30)));
        Assert.Equal(2, SchoolCalendar.TeachingWeek(Samples.Dataset.Term, new(2026, 9, 7)));
        Assert.Equal(7, SchoolCalendar.Weekday(new(2026, 9, 6)));
    }
    [Fact]
    public void DayCoverageDoesNotClaimWholeWeekOrAnotherDay()
    {
        var d = Samples.Dataset with { Coverage = new("DAY", [1], new(2026, 9, 1)) };
        // Limit all meeting weeks to that actual batch coverage.
        d = d with { Meetings = [d.Meetings[0] with { Weeks = [1] }] };
        var data = ValidatedDataset.Create(d);
        Assert.Equal(CoverageStatus.PartialDay, TimetableProjector.Week(data, 1).Coverage);
        Assert.Equal(CoverageStatus.NotFetched, TimetableProjector.Week(data, 2).Coverage);
        Assert.False(TimetableProjector.Today(data, new(2026, 9, 2, 8, 0, 0, TimeSpan.FromHours(8))).IsCovered);
        Assert.True(TimetableProjector.Today(data, new(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8))).IsCovered);
    }
    [Fact]
    public void CurrentCourseUsesExclusiveEndAndFindsNext()
    {
        var data = ValidatedDataset.Create(Samples.Dataset);
        Assert.Single(TimetableProjector.Today(data, new(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8))).Current);
        Assert.Empty(TimetableProjector.Today(data, new(2026, 9, 1, 9, 45, 0, TimeSpan.FromHours(8))).Current);
        Assert.NotNull(TimetableProjector.Today(data, new(2026, 9, 1, 7, 59, 0, TimeSpan.FromHours(8))).Next);
    }
    [Theory]
    [InlineData(0.8)] [InlineData(1)] [InlineData(1.2)]
    public void GeometryRetainsRatioAndFitsNegativeCoordinateMonitor(double scale)
    {
        var geometry = new TimetableGeometry(new(-3000, 1000), scale).Fit(new(-1280, -50, 1280, 720));
        Assert.Equal(5d / 3, geometry.Width / geometry.Height, 12);
        Assert.InRange(geometry.TopLeft.X, -1280, -geometry.Width);
        Assert.InRange(geometry.TopLeft.Y, -50, 670 - geometry.Height);
    }
    [Fact]
    public void TinyDisplayShrinksUniformlyAndRejectsNonfiniteGeometry()
    {
        var geometry = new TimetableGeometry(new(0, 0), 1.2).Fit(new(0, 0, 390, 234));
        Assert.Equal(0.5, geometry.Scale);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimetableGeometry(new(0, 0), double.NaN));
    }
}
