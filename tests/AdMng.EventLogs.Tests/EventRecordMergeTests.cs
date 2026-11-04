using System.Collections;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs.Tests;

public class EventRecordMergeTests
{
    [Fact]
    public void OldestFirst_MergesTheFilesByTime()
    {
        var dc01 = Samples.File("DC01-Security.evtx", 0, 10, 20);
        var dc02 = Samples.File("DC02-Security.evtx", 5, 15);
        var archiwum = Samples.File("Archiwum-zabezpieczeń.evtx", 1, 30);

        var merged = EventRecordMerge.OldestFirst([dc01, dc02, archiwum]).ToList();

        Assert.Equal([0, 1, 5, 10, 15, 20, 30], merged.Select(r => (int)(r.TimeCreated - Samples.Morning).TotalMinutes));
        Assert.Equal(["DC01-Security.evtx", "Archiwum-zabezpieczeń.evtx", "DC02-Security.evtx"], merged.Take(3).Select(r => r.Host));
    }

    [Fact]
    public void OldestFirst_EqualTimes_KeepTheSourceOrderThenTheRecordNumber()
    {
        var first = new List<EventLogRecord> { Samples.Record("b.evtx", 7, Samples.Morning), Samples.Record("b.evtx", 9, Samples.Morning) };
        var second = new List<EventLogRecord> { Samples.Record("a.evtx", 1, Samples.Morning) };

        var merged = EventRecordMerge.OldestFirst([first, second]).ToList();

        Assert.Equal([("b.evtx", 7L), ("b.evtx", 9L), ("a.evtx", 1L)], merged.Select(r => (r.Host, r.RecordId)));
    }

    [Fact]
    public void OldestFirst_ASourceOutOfTimeOrder_IsStillMergedRecordByRecord()
    {
        // A clock set back on the DC (4616): the file is in record order, not in time order.
        var skewed = Samples.File("DC01.evtx", 10, 3, 12);
        var other = Samples.File("DC02.evtx", 5);

        var merged = EventRecordMerge.OldestFirst([skewed, other]).ToList();

        Assert.Equal(4, merged.Count);
        Assert.Equal([5, 10, 3, 12], merged.Select(r => (int)(r.TimeCreated - Samples.Morning).TotalMinutes));
    }

    [Fact]
    public void NewestFirst_KeepsOnlyTheNewest_NewestFirst()
    {
        var records = Samples.File("DC01.evtx", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);

        var newest = EventRecordMerge.NewestFirst(records, 3);

        Assert.Equal([9, 8, 7], newest.Select(r => (int)(r.TimeCreated - Samples.Morning).TotalMinutes));
    }

    [Fact]
    public void NewestFirst_EqualTimes_TheLaterRecordFirst_AndZeroIsEmpty()
    {
        var records = new List<EventLogRecord>
        {
            Samples.Record("DC01.evtx", 1, Samples.Morning, account: "jan.kowalski"),
            Samples.Record("DC01.evtx", 2, Samples.Morning, account: "łucja.ńczyk"),
        };

        Assert.Equal([2L, 1L], EventRecordMerge.NewestFirst(records, 5).Select(r => r.RecordId));
        Assert.Empty(EventRecordMerge.NewestFirst(records, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EventRecordMerge.NewestFirst(records, -1));
    }

    [Fact]
    public void Order_OldestFirst_StreamsUpToTheLimit()
    {
        var sources = new List<IEnumerable<EventLogRecord>> { Samples.File("DC01.evtx", 0, 4, 8), Samples.File("DC02.evtx", 2, 6) };

        Assert.Equal([0, 2, 4], EventRecordMerge.Order(sources, newestFirst: false, maxRecords: 3).Select(r => (int)(r.TimeCreated - Samples.Morning).TotalMinutes));
        Assert.Equal(5, EventRecordMerge.Order(sources, newestFirst: false, maxRecords: null).Count());
        Assert.Empty(EventRecordMerge.Order(sources, newestFirst: false, maxRecords: 0));
    }

    [Fact]
    public void Order_NewestFirst_AfterTheRead_BoundedByTheLimit()
    {
        var sources = new List<IEnumerable<EventLogRecord>> { Samples.File("DC01.evtx", 0, 4, 8), Samples.File("DC02.evtx", 2, 6) };

        Assert.Equal([8, 6], EventRecordMerge.Order(sources, newestFirst: true, maxRecords: 2).Select(r => (int)(r.TimeCreated - Samples.Morning).TotalMinutes));
        Assert.Empty(EventRecordMerge.Order(sources, newestFirst: true, maxRecords: 0));
    }

    [Fact]
    public void Order_NewestFirst_WithoutALimit_StreamsEveryRecord_SoTheCallerCountsWhatItDrops()
    {
        // E-EVTLOG-01: the reader asks newest first without a limit; a window of the newest 50 000 here dropped the rest
        // of a large exported Security.evtx without anyone counting them (the runner saw 50 000 and reported nothing).
        var minutes = Enumerable.Range(0, EventLogLimits.MaxRecordsInMemory + 7).ToArray();
        var dc01 = Samples.File("DC01-Security.evtx", [.. minutes.Where(m => m % 2 == 0)]);
        var dc02 = Samples.File("DC02-Zabezpieczenia.evtx", [.. minutes.Where(m => m % 2 == 1)]);

        var records = EventRecordMerge.Order([dc01, dc02], newestFirst: true, maxRecords: null).ToList();

        Assert.Equal(minutes.Length, records.Count);
        Assert.Equal(minutes, records.Select(r => (int)(r.TimeCreated - Samples.Morning).TotalMinutes));
    }

    [Fact]
    public void Merge_OpensTheSourcesLazily_AndDisposesThemWhenAbandoned()
    {
        var first = new TrackedSource(Samples.File("DC01.evtx", 0, 2, 4));
        var second = new TrackedSource(Samples.File("DC02.evtx", 1, 3));

        var merged = EventRecordMerge.OldestFirst([first, second]);
        Assert.Equal(0, first.Opened + second.Opened);

        using (var reader = merged.GetEnumerator())
        {
            Assert.True(reader.MoveNext());
            Assert.True(reader.MoveNext());
        }

        Assert.Equal((1, 1), (first.Opened, second.Opened));
        Assert.Equal((1, 1), (first.Disposed, second.Disposed));
    }

    [Fact]
    public void Merge_AFailingSource_DisposesEveryOpenedSource()
    {
        var healthy = new TrackedSource(Samples.File("DC01.evtx", 0, 2, 4));
        var broken = new TrackedSource(Samples.File("DC02.evtx", 1, 3), failAfter: 1);

        Assert.Throws<IOException>(() => EventRecordMerge.OldestFirst([healthy, broken]).ToList());

        Assert.Equal((1, 1), (healthy.Disposed, broken.Disposed));
    }

    /// <summary>A source that counts its enumerators and their disposal, optionally failing after some records.</summary>
    private sealed class TrackedSource(IReadOnlyList<EventLogRecord> records, int? failAfter = null) : IEnumerable<EventLogRecord>
    {
        public int Opened { get; private set; }

        public int Disposed { get; private set; }

        public IEnumerator<EventLogRecord> GetEnumerator()
        {
            Opened++;
            return Enumerate();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerator<EventLogRecord> Enumerate()
        {
            try
            {
                for (var i = 0; i < records.Count; i++)
                {
                    if (i == failAfter)
                    {
                        throw new IOException("Plik uszkodzony.");
                    }

                    yield return records[i];
                }
            }
            finally
            {
                Disposed++;
            }
        }
    }
}
