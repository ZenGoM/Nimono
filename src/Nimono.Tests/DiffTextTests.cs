namespace Nimono.Tests;

/// <summary>
/// 基準と違う部分を赤くするための差分（共通の前後を除いた中間部分）を確かめる。
/// </summary>
[TestClass]
public class DiffTextTests
{
    [TestMethod]
    public void CharDiff_MarksOnlyTheMiddle()
    {
        var runs = DiffText.CharDiff("NEC_0635(2).jpg", "NEC_0635.jpg").ToList();

        CollectionAssert.AreEqual(
            new[] { new DiffRun("NEC_0635", false), new DiffRun("(2)", true), new DiffRun(".jpg", false) },
            runs);
    }

    [TestMethod]
    public void CharDiff_DifferentFolders()
    {
        var runs = DiffText.CharDiff(@"D:\masanoriPictures\iCloud Photos\Photos", @"D:\Pictures\2008_09").ToList();

        CollectionAssert.AreEqual(
            new[] { new DiffRun(@"D:\", false), new DiffRun(@"masanoriPictures\iCloud Photos\Photos", true) },
            runs);
    }

    [TestMethod]
    public void CharDiff_SameOrNoReference_IsNotMarked()
    {
        CollectionAssert.AreEqual(new[] { new DiffRun("a.jpg", false) }, DiffText.CharDiff("a.jpg", "a.jpg").ToList());
        CollectionAssert.AreEqual(new[] { new DiffRun("a.jpg", false) }, DiffText.CharDiff("a.jpg", null).ToList());
    }

    [TestMethod]
    public void CharDiff_TextIsPrefixOfOther_HasNoMarkedPart()
    {
        // 基準より短く、削られただけのときは赤くする文字が無い
        CollectionAssert.AreEqual(new[] { new DiffRun("abc", false) }, DiffText.CharDiff("abc", "abcd").ToList());
    }

    [TestMethod]
    public void Field_MarksWholeTextWhenDifferent()
    {
        Assert.AreEqual(new DiffRun("16 KB", true), DiffText.Field("16 KB", "17 KB"));
        Assert.AreEqual(new DiffRun("17 KB", false), DiffText.Field("17 KB", "17 KB"));
        Assert.AreEqual(new DiffRun("17 KB", false), DiffText.Field("17 KB", null));
    }
}
