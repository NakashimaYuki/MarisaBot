using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Marisa.Plugin.Shared.MaiMaiDx;
using NUnit.Framework;

namespace Marisa.Plugin.Test;

public class MaiMaiDxScopeTest
{
    [TestCase("真", "3")]
    [TestCase("真代", "3")]
    [TestCase("彩", "3")]
    [TestCase("彩代", "3")]
    [TestCase("舞", "3,4")]
    [TestCase("东方", "3,4")]
    [TestCase("宴会场", "0,1,2,3,4")]
    [TestCase("14+", "0,1,2,3,4")]
    [TestCase("14.0", "0,1,2,3,4")]
    [TestCase("彩代14+", "0,1,2,3,4")]
    [TestCase("彩代东方14+EXP/MST", "2,3")]
    [TestCase("彩代红谱-白谱", "2,3,4")]
    [TestCase("彩代14.0-14.5红谱紫谱", "2,3")]
    public void Scope_Uses_Completion_Table_Difficulty_Defaults(string text, string expectedLevels)
    {
        var query = Parse(text);
        Assert.That(string.Join(',', query.LevelIdxes), Is.EqualTo(expectedLevels));
    }

    [Test]
    public void Scope_Accepts_All_Existing_Version_Aliases()
    {
        foreach (var (alias, versions) in PlateData.PlateVersionMap)
        {
            foreach (var text in new[] { alias, alias + "代" })
            {
                var query = Parse(text);
                var plate = query.Selectors.OfType<PlateData.Selector.Plate>().Single();
                Assert.That(plate.Versions, Is.EqualTo(versions), text);
            }
        }
    }

    [TestCase("14.0-14.5")]
    [TestCase("14.0 - 14.5")]
    [TestCase("14.0～14.5")]
    [TestCase("14.0~14.5")]
    [TestCase("14.0至14.5")]
    [TestCase("定数14.0－14.5")]
    public void Scope_Accepts_Constant_Ranges(string text)
    {
        var range = Parse(text).Selectors.OfType<PlateData.Selector.ConstantRange>().Single();
        Assert.That(range.Minimum, Is.EqualTo(14.0));
        Assert.That(range.Maximum, Is.EqualTo(14.5));
    }

    [TestCase("")]
    [TestCase("MASTER")]
    [TestCase("紫谱")]
    [TestCase("EXP/MST")]
    [TestCase("彩代SSS")]
    [TestCase("彩将")]
    [TestCase("翠楼屋")]
    [TestCase("彩代unknown")]
    [TestCase("真彩")]
    [TestCase("东方舞萌")]
    [TestCase("14+13+")]
    [TestCase("14.0 14.5")]
    [TestCase("14.5-14.0")]
    [TestCase("14.0-14.00")]
    [TestCase("14.0-15")]
    [TestCase("14.0-16.0")]
    [TestCase("14.0-14.5-14.9")]
    [TestCase("NaN")]
    [TestCase("彩代MST-EXP")]
    public void Scope_Rejects_Empty_Difficulty_Only_Or_Malformed_Queries(string text)
    {
        Assert.That(PlateData.TryParseScope(text, out var query, out var error), Is.False, text);
        Assert.That(query, Is.Null);
        Assert.That(error, Is.Not.Null);
    }

    [Test]
    public void Scope_Combines_Version_Genre_Level_Constant_And_Difficulty()
    {
        var query = Parse("彩代舞萌14+14.6-14.9紫谱/白谱");
        var actual = PlateData.SelectScopeCharts(query, Songs());

        Assert.That(actual.Select(x => (x.Song.Id, x.LevelIdx)),
            Is.EqualTo(new[] { (1001L, 3), (1003L, 4) }));
        Assert.That(PlateData.SelectScopeCharts(Parse("彩代东方14+14.0-14.5"), Songs()), Is.Empty);
    }

    [TestCase("彩代", new long[] { 1001, 1002, 1003 })]
    [TestCase("彩代舞萌", new long[] { 1001, 1003 })]
    [TestCase("彩代14+紫谱", new long[] { 1001 })]
    public void Scope_Without_Constant_Sorts_Constant_Descending(string text, long[] expectedIds)
    {
        var actual = PlateData.SelectScopeCharts(Parse(text), Songs());
        Assert.That(actual.Select(x => x.Song.Id), Is.EqualTo(expectedIds));
    }

    [Test]
    public void Scope_With_Constant_Sorts_Id_Then_Difficulty_Ascending()
    {
        var actual = PlateData.SelectScopeCharts(Parse("彩代14.0-14.9"), Songs());
        Assert.That(actual.Select(x => (x.Song.Id, x.LevelIdx)),
            Is.EqualTo(new[] { (1001L, 3), (1001L, 4), (1002L, 3), (1002L, 4), (1003L, 3), (1003L, 4) }));
    }

    [Test]
    public void Scope_Constant_Range_Includes_Endpoints_And_Rejects_Adjacent_Constants()
    {
        var range = Parse("14.0-14.5").Selectors.Single();
        var song = Songs().First();
        Assert.That(PlateData.MatchesSelector(range, 14.0, 3, song), Is.True);
        Assert.That(PlateData.MatchesSelector(range, 14.5, 3, song), Is.True);
        Assert.That(PlateData.MatchesSelector(range, 13.9, 3, song), Is.False);
        Assert.That(PlateData.MatchesSelector(range, 14.6, 3, song), Is.False);
    }

    [Test]
    public void Scope_Reuses_Completion_Table_Revival_Exclusion()
    {
        var songs = new[]
        {
            Song(146, "maimai", "舞萌", 12.0, 13.0),
            Song(1003, "maimai", "舞萌", 12.0, 13.0)
        };
        Assert.That(PlateData.SelectScopeCharts(Parse("真"), songs).Select(x => x.Song.Id),
            Is.EqualTo(new[] { 1003L }));
        Assert.That(PlateData.SelectScopeCharts(Parse("真复活曲"), songs).Select(x => x.Song.Id),
            Is.EqualTo(new[] { 146L, 146L }));
    }

    [Test]
    public void Scope_Decimal_Seven_Point_Three_Is_A_Constant()
    {
        Assert.That(Parse("7.3").Selectors.Single(), Is.TypeOf<PlateData.Selector.Constant>());
        Assert.That(PlateData.TryParse("7.3完成表", [], [], out var completion, out _), Is.True);
        Assert.That(completion!.Selectors.Single(), Is.TypeOf<PlateData.Selector.CharterAlias>());
    }

    private static PlateData.Query Parse(string text)
    {
        Assert.That(PlateData.TryParseScope(text, out var query, out var error), Is.True,
            $"{text}: {error?.Kind}/{error?.Detail}");
        return query!;
    }

    private static IReadOnlyList<MaiMaiSong> Songs() =>
    [
        Song(1003, "maimai でらっくす PRiSM PLUS", "舞萌", 14.0, 14.7),
        Song(1001, "maimai でらっくす PRiSM PLUS", "舞萌", 14.9, 14.0),
        Song(1002, "maimai でらっくす PRiSM PLUS", "东方Project", 14.5, 14.7),
        Song(900, "maimai でらっくす PRiSM", "舞萌", 14.8, 14.7)
    ];

    private static MaiMaiSong Song(long id, string version, string genre, double master, double remaster)
    {
        dynamic data = new ExpandoObject();
        data.id = id.ToString();
        data.title = $"song-{id}";
        data.type = "DX";
        dynamic info = new ExpandoObject();
        info.title = data.title;
        info.artist = "artist";
        info.genre = genre;
        info.bpm = 120;
        info.release_date = "2026-01-01";
        info.from = version;
        info.is_new = false;
        data.basic_info = info;
        var constants = new[] { 1.0, 6.0, 13.0, master, remaster };
        data.ds = constants;
        data.level = constants.Select(c => $"{Math.Floor(c)}{(c % 1 >= 0.6 ? "+" : "")}").ToArray();
        data.charts = constants.Select(_ =>
        {
            dynamic chart = new ExpandoObject();
            chart.notes = new long[] { 100, 10, 10, 10, 10 };
            chart.charter = "charter";
            return chart;
        }).ToArray();
        return new MaiMaiSong(data);
    }
}
