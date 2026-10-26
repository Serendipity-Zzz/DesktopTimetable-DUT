using System.Collections.Immutable;
using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

internal static class Samples
{
    public static CurrentDataset Dataset => new("1.0", true, "fictional-account", "fixture-1", "mock-not-live",
        new("autumn-2026", "2026-2027", "SAMPLE", new(2026, 8, 31), "Asia/Shanghai", 18),
        new("TERM", Enumerable.Range(1, 18).ToImmutableArray()), new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero),
        Enumerable.Range(1, 14).Select(i => new Period(i, new(7 + i, 0), new(7 + i, 45))).ToImmutableArray(),
        [new("m1", "class-a", "同名课程", ["虚构教师"], "虚构教室", 2, 1, 2, [1, 3, 5, 8])]);
}
