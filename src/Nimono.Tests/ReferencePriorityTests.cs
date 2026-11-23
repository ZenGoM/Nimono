using System.Text.Json;
using Nimono;

namespace Nimono.Tests;

/// <summary>利用者が決めた優先順位で基準画像を選ぶことを確かめる。</summary>
[TestClass]
public class ReferencePriorityTests
{
    private static readonly DateTime Day = new(2024, 5, 1, 12, 0, 0);

    private static ReferencePriorityRule On(ReferencePriorityKey key, bool preferHigher) => new(key, true, preferHigher);

    private static string PickFrom(
        IReadOnlyDictionary<string, ImageFacts> facts, params ReferencePriorityRule[] rules) =>
        ReferencePriority.Pick(facts.Keys.ToList(), rules, p => facts[p]);

    [TestMethod]
    public void NoRules_PicksFirstFound()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            ["b.jpg"] = new(FoundIndex: 1, 0, 100, Day, Day, 10),
            ["a.jpg"] = new(FoundIndex: 0, 0, 1, Day, Day, 1),
            ["c.jpg"] = new(FoundIndex: 2, 0, 999, Day, Day, 99),
        };

        Assert.AreEqual("a.jpg", PickFrom(facts));
        Assert.AreEqual("a.jpg", ReferencePriority.Pick(facts.Keys.ToList(), ReferencePriority.DefaultRules(), p => facts[p]),
            "初期設定はどれも無効なので見つかった順");
    }

    [TestMethod]
    public void EachKey_FollowsItsDirection()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            ["small_old_top.jpg"] = new(0, FolderIndex: 0, Pixels: 100, ImageDate: Day, ModifiedDate: Day, FileSize: 10, FileName: "a.jpg"),
            ["big_new_bottom.jpg"] = new(1, FolderIndex: 1, Pixels: 900, ImageDate: Day.AddDays(1), ModifiedDate: Day.AddDays(1), FileSize: 90, FileName: "b.jpg"),
        };

        foreach (var key in Enum.GetValues<ReferencePriorityKey>())
        {
            Assert.AreEqual("big_new_bottom.jpg", PickFrom(facts, On(key, preferHigher: true)), $"{key} 大きいほう");
            Assert.AreEqual("small_old_top.jpg", PickFrom(facts, On(key, preferHigher: false)), $"{key} 小さいほう");
        }
    }

    [TestMethod]
    public void LaterRule_DecidesOnlyWhenEarlierRulesTie()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            ["a.jpg"] = new(0, 0, Pixels: 1000, Day, Day, FileSize: 10),
            ["b.jpg"] = new(1, 0, Pixels: 1000, Day, Day, FileSize: 50), // 解像度は同じ、サイズが大きい
            ["c.jpg"] = new(2, 0, Pixels: 500, Day, Day, FileSize: 99),  // サイズは最大だが解像度が低い
        };

        Assert.AreEqual("b.jpg", PickFrom(facts,
            On(ReferencePriorityKey.Resolution, true),
            On(ReferencePriorityKey.FileSize, true)));
        Assert.AreEqual("c.jpg", PickFrom(facts,
            On(ReferencePriorityKey.FileSize, true),
            On(ReferencePriorityKey.Resolution, true)), "並びを入れ替えると先の条件が優先される");
    }

    [TestMethod]
    public void DisabledRule_IsIgnored()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            ["a.jpg"] = new(0, 0, 100, Day, Day, 10),
            ["b.jpg"] = new(1, 0, 900, Day, Day, 90),
        };

        Assert.AreEqual("a.jpg", PickFrom(facts, new ReferencePriorityRule(ReferencePriorityKey.Resolution, false, true)));
    }

    [TestMethod]
    public void AllRulesTie_FallsBackToFoundOrder()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            ["late.jpg"] = new(5, 0, 100, Day, Day, 10),
            ["early.jpg"] = new(2, 0, 100, Day, Day, 10),
        };

        Assert.AreEqual("early.jpg", PickFrom(facts,
            On(ReferencePriorityKey.Resolution, true),
            On(ReferencePriorityKey.FileSize, true),
            On(ReferencePriorityKey.ModifiedDate, false)));
    }

    [TestMethod]
    public void UnknownValue_LosesInBothDirections()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            ["unreadable.webp"] = new(0, null, Pixels: null, null, Day, 10),
            ["known.jpg"] = new(1, 0, Pixels: 100, Day, Day, 10),
        };

        Assert.AreEqual("known.jpg", PickFrom(facts, On(ReferencePriorityKey.Resolution, true)));
        Assert.AreEqual("known.jpg", PickFrom(facts, On(ReferencePriorityKey.Resolution, false)));
        Assert.AreEqual("known.jpg", PickFrom(facts, On(ReferencePriorityKey.FolderOrder, true)));
    }

    [TestMethod]
    public void Dates_DifferingOnlyBelowOneSecond_AreTreatedAsSame()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            ["copy.jpg"] = new(1, 0, 100, Day, Day.AddMilliseconds(300), 10),
            ["original.jpg"] = new(0, 0, 100, Day, Day, 10),
        };

        // 新しいほうを優先しても、秒未満の差は同じとみなして見つかった順になる
        Assert.AreEqual("original.jpg", PickFrom(facts, On(ReferencePriorityKey.ModifiedDate, true)));
    }

    [TestMethod]
    public void CompareFileNames_FollowsExplorerNameOrder()
    {
        string[] expected =
        [
            "IMG_2.jpg",       // 数字は数として比べる（2 < 10）
            "IMG_10.jpg",
            "NEC_0733.jpg",    // 拡張子を除いた名前で比べるため、コピーの「(1)」より前
            "NEC_0733(1).jpg",
            "NEC_0733(2).jpg",
            "nec_0734.JPG",    // 大文字小文字は区別しない
        ];
        var shuffled = Enumerable.Reverse(expected).ToList();

        shuffled.Sort(ReferencePriority.CompareFileNames);

        CollectionAssert.AreEqual(expected, shuffled);
    }

    [TestMethod]
    public void FileName_AscendingPrefersOriginalOverNumberedCopies()
    {
        var facts = new Dictionary<string, ImageFacts>
        {
            [@"C:\a\NEC_0733(1).jpg"] = new(0, 0, 100, Day, Day, 10, "NEC_0733(1).jpg"),
            [@"C:\b\NEC_0733.jpg"] = new(1, 0, 100, Day, Day, 10, "NEC_0733.jpg"),
            [@"C:\c\NEC_0733(2).jpg"] = new(2, 0, 100, Day, Day, 10, "NEC_0733(2).jpg"),
        };

        Assert.AreEqual(@"C:\b\NEC_0733.jpg", PickFrom(facts, On(ReferencePriorityKey.FileName, false)));
        Assert.AreEqual(@"C:\c\NEC_0733(2).jpg", PickFrom(facts, On(ReferencePriorityKey.FileName, true)));
    }

    [TestMethod]
    public void FolderIndexOf_UsesFirstMatchingFolderAndWholeFolderNames()
    {
        var folders = new[] { @"C:\Photos\Best", @"C:\Photos", @"D:\" };

        Assert.AreEqual(0, ReferencePriority.FolderIndexOf(@"C:\Photos\Best\a.jpg", folders), "入れ子は並びで先のフォルダー");
        Assert.AreEqual(1, ReferencePriority.FolderIndexOf(@"c:\photos\sub\a.jpg", folders), "大文字小文字は区別しない");
        Assert.AreEqual(1, ReferencePriority.FolderIndexOf(@"C:\Photos\Bestshots\a.jpg", folders), "名前の前半が同じだけの別フォルダー");
        Assert.AreEqual(2, ReferencePriority.FolderIndexOf(@"D:\x\a.jpg", folders), "ドライブ直下");
        Assert.IsNull(ReferencePriority.FolderIndexOf(@"E:\a.jpg", folders));
    }

    [TestMethod]
    public void Normalize_RemovesDuplicatesAndAddsMissingKeysDisabled()
    {
        var rules = ReferencePriority.Normalize(
        [
            new(ReferencePriorityKey.FileSize, true, false),
            new(ReferencePriorityKey.FileSize, true, true), // 重複は先のものだけ残す
            new((ReferencePriorityKey)99, true, true),      // 知らない項目は捨てる
        ]);

        Assert.AreEqual(Enum.GetValues<ReferencePriorityKey>().Length, rules.Count);
        Assert.AreEqual(ReferencePriorityKey.FileSize, rules[0].Key);
        Assert.IsFalse(rules[0].PreferHigher);
        Assert.IsTrue(rules.Skip(1).All(r => !r.Enabled), "足した項目は無効");
        CollectionAssert.AllItemsAreUnique(rules.Select(r => r.Key).ToList());
    }

    [TestMethod]
    public void Rules_RoundTripThroughJsonWithKeyNames()
    {
        var rules = new List<ReferencePriorityRule> { new(ReferencePriorityKey.Resolution, true, true) };

        var json = JsonSerializer.Serialize(rules);
        StringAssert.Contains(json, "\"Resolution\"", "設定ファイルで読めるよう項目名で保存する");

        var back = JsonSerializer.Deserialize<List<ReferencePriorityRule>>(json)!;
        Assert.AreEqual(ReferencePriorityKey.Resolution, back[0].Key);
        Assert.IsTrue(back[0].Enabled);
        Assert.IsTrue(back[0].PreferHigher);
    }

    [TestMethod]
    public void Apply_PutsPickedReferenceFirst_AndOrdersOthersBySimilarityThenFoundOrder()
    {
        // pHash: low と high は同じハッシュ、other は 8 ビット違い
        const ulong hash = 0x00FF00FF00FF00FFUL;
        var hashes = new Dictionary<string, ulong>
        {
            ["low.jpg"] = hash,
            ["other.jpg"] = hash ^ 0xFFUL,
            ["high.jpg"] = hash,
            ["twin.jpg"] = hash,
        };
        var group = new ImageGroup(1, hashes.Keys.ToList(), hashes.ToDictionary(kv => kv.Key, _ => 1.0), hashes);
        var facts = new Dictionary<string, ImageFacts>
        {
            ["low.jpg"] = new(0, 0, Pixels: 100, Day, Day, 10),
            ["other.jpg"] = new(1, 0, Pixels: 100, Day, Day, 10),
            ["high.jpg"] = new(3, 0, Pixels: 900, Day, Day, 10),
            ["twin.jpg"] = new(2, 0, Pixels: 100, Day, Day, 10),
        };

        var applied = ReferencePriority.Apply(group, [On(ReferencePriorityKey.Resolution, true)], p => facts[p]);

        // 基準 → 類似度 100%（見つかった順: low=0, twin=2）→ 類似度の低い other
        CollectionAssert.AreEqual(new[] { "high.jpg", "low.jpg", "twin.jpg", "other.jpg" }, applied.Paths.ToList());
        Assert.AreEqual(1.0, applied.Similarities["low.jpg"]);
        Assert.IsTrue(applied.Similarities["other.jpg"] < 1.0);
    }
}
