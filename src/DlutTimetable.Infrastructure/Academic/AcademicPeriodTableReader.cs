using System.Collections.Immutable;
using System.Text.Json;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure.Academic;

public static class AcademicPeriodTableReader
{
    // Input is an explicitly selected timingUnits array, never a guessed result path.
    public static ImmutableArray<Period> Read(ReadOnlyMemory<byte> json)
    {
        if(json.Length>WorkerProtocol.MaxMessageBytes)throw new AcademicReadException(AcademicReadFailure.TooLarge);
        try
        {
            using var document=JsonDocument.Parse(json,new(){MaxDepth=8});
            var evidence=AcademicPeriodEvidence.ReadCompleteArray(document.RootElement)
                ??throw new AcademicReadException(AcademicReadFailure.InvalidShape);
            return evidence.Select(unit=>new Period(unit.Index,new(unit.StartTime/100,unit.StartTime%100),
                new(unit.EndTime/100,unit.EndTime%100))).ToImmutableArray();
        }
        catch(JsonException){throw new AcademicReadException(AcademicReadFailure.InvalidJson);}
    }
}
